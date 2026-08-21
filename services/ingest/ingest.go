package main

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"sync/atomic"
	"time"

	mqtt "github.com/eclipse/paho.mqtt.golang"
	"github.com/rilegu/iot-fleet-reference/contracts"
)

// Counters are exported over HTTP. Every drop is counted: silent loss is the failure that
// makes a dashboard confidently wrong, so nothing is discarded without being recorded.
type Counters struct {
	TelemetryReceived atomic.Int64
	StatusReceived    atomic.Int64
	EventReceived     atomic.Int64
	Invalid           atomic.Int64
	DeadLettered      atomic.Int64
	TelemetryDropped  atomic.Int64
	BatchesWritten    atomic.Int64
	WriteFailures     atomic.Int64
	PublishFailures   atomic.Int64
	QueueDepth        atomic.Int64

	// Unacknowledged counts QoS 1 messages deliberately left unacknowledged after a
	// failure, so the broker redelivers them. A rising value means the pipeline is
	// failing to persist state transitions.
	Unacknowledged atomic.Int64
}

type Ingest struct {
	broker    string
	sites     []string
	validator *contracts.Validator
	store     *Store
	bus       *Bus
	log       *slog.Logger

	// telemetryQ is bounded. Telemetry is QoS 0 and a sample of a continuous signal, so
	// when the queue fills the oldest sample is dropped and counted rather than allowing
	// unbounded growth. Status and events never pass through here: they are handled
	// synchronously so the broker is not acknowledged before they are durable.
	telemetryQ chan Message

	counters *Counters
	client   mqtt.Client
}

const (
	telemetryQueueSize = 8192
	batchSize          = 512
	batchInterval      = 250 * time.Millisecond
)

func NewIngest(broker string, sites []string, v *contracts.Validator, store *Store, bus *Bus, log *slog.Logger) *Ingest {
	return &Ingest{
		broker:     broker,
		sites:      sites,
		validator:  v,
		store:      store,
		bus:        bus,
		log:        log,
		telemetryQ: make(chan Message, telemetryQueueSize),
		counters:   &Counters{},
	}
}

// topicFilters returns the subscriptions this instance owns.
//
// MQTT 3.1.1 has no shared subscriptions, so several ingest instances cannot split one
// wildcard subscription: each would receive every message. Partitioning is therefore by
// site prefix, assigned explicitly rather than negotiated.
func (in *Ingest) topicFilters() map[string]byte {
	filters := map[string]byte{}
	if len(in.sites) == 0 {
		filters["fleet/+/+/telemetry"] = 0
		filters["fleet/+/+/status"] = 1
		filters["fleet/+/+/event"] = 1
		return filters
	}
	for _, s := range in.sites {
		filters[fmt.Sprintf("fleet/%s/+/telemetry", s)] = 0
		filters[fmt.Sprintf("fleet/%s/+/status", s)] = 1
		filters[fmt.Sprintf("fleet/%s/+/event", s)] = 1
	}
	return filters
}

func (in *Ingest) Run(ctx context.Context) error {
	opts := mqtt.NewClientOptions().
		AddBroker(in.broker).
		SetClientID("fleet-ingest").
		SetProtocolVersion(4).
		SetCleanSession(false). // resume the session so QoS 1 messages survive a restart
		SetOrderMatters(false).
		SetAutoReconnect(true).
		SetConnectRetry(true).
		SetConnectRetryInterval(2 * time.Second).
		SetKeepAlive(30 * time.Second).
		SetConnectTimeout(10 * time.Second).
		// Acknowledge QoS 1 messages by hand. With auto-acking, returning from the
		// handler acknowledges the message whether or not it was persisted, so a failed
		// write would lose a state transition silently. Ingest must not acknowledge the
		// broker until the message is durable in both the database and the log.
		SetAutoAckDisabled(true)

	opts.SetOnConnectHandler(func(c mqtt.Client) {
		filters := in.topicFilters()
		if token := c.SubscribeMultiple(filters, in.handle); token.Wait() && token.Error() != nil {
			in.log.Error("subscribe failed", "err", token.Error())
			return
		}
		in.log.Info("subscribed", "filters", len(filters), "sites", in.sites)
	})
	opts.SetConnectionLostHandler(func(_ mqtt.Client, err error) {
		in.log.Warn("broker connection lost", "err", err)
	})

	in.client = mqtt.NewClient(opts)
	if token := in.client.Connect(); token.Wait() && token.Error() != nil {
		return fmt.Errorf("connecting to broker: %w", token.Error())
	}
	in.log.Info("connected to broker", "url", in.broker)

	// One batch worker. Telemetry ordering per device is preserved because a single
	// consumer drains the queue.
	done := make(chan struct{})
	go func() {
		defer close(done)
		in.runBatchWorker(ctx)
	}()

	<-ctx.Done()
	in.client.Disconnect(500)
	<-done
	return nil
}

// handle runs on paho's callback goroutine. Acknowledgement is manual: a QoS 1 message is
// acknowledged only once it is durable, so a failure leaves it unacknowledged and the
// broker redelivers it on the next session.
func (in *Ingest) handle(_ mqtt.Client, msg mqtt.Message) {
	received := time.Now().UTC()

	_, _, kind, err := topicParts(msg.Topic())
	if err != nil {
		in.reject(msg, "topic: "+err.Error())
		return
	}

	payload := msg.Payload()
	if err := in.validator.ValidateBytes(kind, payload); err != nil {
		in.reject(msg, "schema: "+err.Error())
		return
	}

	env, err := decodeEnvelope(payload)
	if err != nil {
		in.reject(msg, "envelope: "+err.Error())
		return
	}

	m := Message{
		Kind:       kind,
		Topic:      msg.Topic(),
		Payload:    payload,
		Envelope:   env,
		ReceivedAt: received,
		Retained:   msg.Retained(),
	}

	switch kind {
	case KindTelemetry:
		in.counters.TelemetryReceived.Add(1)
		in.enqueueTelemetry(m)
		// QoS 0 carries no acknowledgement, and telemetry is at-most-once by contract.
		msg.Ack()
	case KindStatus:
		in.counters.StatusReceived.Add(1)
		in.handleDurable(msg, m, in.store.WriteStatus)
	case KindEvent:
		in.counters.EventReceived.Add(1)
		in.handleDurable(msg, m, in.store.WriteEvent)
	}
}

// handleDurable writes to the database, then to the log, and only then acknowledges the
// broker. Leaving a message unacknowledged is the whole point: the broker redelivers it,
// and both writes are idempotent, so redelivery is safe and losing it is not.
func (in *Ingest) handleDurable(raw mqtt.Message, m Message, write func(context.Context, Message) error) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	if err := write(ctx, m); err != nil {
		in.counters.WriteFailures.Add(1)
		in.counters.Unacknowledged.Add(1)
		in.log.Error("write failed, leaving unacknowledged for redelivery",
			"kind", m.Kind, "device", m.Envelope.DeviceID, "err", err)
		return
	}
	if err := in.bus.Publish(ctx, m); err != nil {
		in.counters.PublishFailures.Add(1)
		in.counters.Unacknowledged.Add(1)
		in.log.Error("publish failed, leaving unacknowledged for redelivery",
			"kind", m.Kind, "device", m.Envelope.DeviceID, "err", err)
		return
	}
	raw.Ack()
}

// enqueueTelemetry applies the bounded-queue policy: drop the oldest sample rather than
// block the broker callback or grow without limit.
func (in *Ingest) enqueueTelemetry(m Message) {
	select {
	case in.telemetryQ <- m:
		in.counters.QueueDepth.Store(int64(len(in.telemetryQ)))
	default:
		select {
		case <-in.telemetryQ:
			in.counters.TelemetryDropped.Add(1)
		default:
		}
		select {
		case in.telemetryQ <- m:
		default:
			in.counters.TelemetryDropped.Add(1)
		}
	}
}

func (in *Ingest) reject(msg mqtt.Message, reason string) {
	in.counters.Invalid.Add(1)
	// Acknowledge it. A malformed payload will still be malformed on redelivery, so
	// withholding the acknowledgement would loop it forever; the dead-letter record is
	// what preserves it for inspection.
	msg.Ack()
	// Sample: recording every rejection from a misbehaving fleet would make dead_letter
	// the highest-volume table in the database.
	if in.counters.Invalid.Load()%64 != 1 {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := in.store.WriteDeadLetter(ctx, msg.Topic(), reason, msg.Payload()); err != nil {
		in.log.Error("dead letter write failed", "err", err)
		return
	}
	in.counters.DeadLettered.Add(1)
	in.log.Warn("rejected message", "topic", msg.Topic(), "reason", reason)
}

func (in *Ingest) runBatchWorker(ctx context.Context) {
	ticker := time.NewTicker(batchInterval)
	defer ticker.Stop()

	batch := make([]Message, 0, batchSize)

	flush := func() {
		if len(batch) == 0 {
			return
		}
		writeCtx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		defer cancel()

		if err := in.store.WriteTelemetryBatch(writeCtx, batch); err != nil {
			in.counters.WriteFailures.Add(1)
			in.log.Error("telemetry batch write failed", "size", len(batch), "err", err)
		} else if err := in.bus.PublishBatch(writeCtx, batch); err != nil {
			in.counters.PublishFailures.Add(1)
			in.log.Error("telemetry batch publish failed", "size", len(batch), "err", err)
		} else {
			in.counters.BatchesWritten.Add(1)
		}
		batch = batch[:0]
	}

	for {
		select {
		case <-ctx.Done():
			flush()
			return
		case m := <-in.telemetryQ:
			batch = append(batch, m)
			in.counters.QueueDepth.Store(int64(len(in.telemetryQ)))
			if len(batch) >= batchSize {
				flush()
			}
		case <-ticker.C:
			flush()
		}
	}
}

var errNotConnected = errors.New("not connected to broker")

func (in *Ingest) Healthy() error {
	if in.client == nil || !in.client.IsConnected() {
		return errNotConnected
	}
	return nil
}
