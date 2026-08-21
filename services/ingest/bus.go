package main

import (
	"context"
	"fmt"
	"strconv"
	"time"

	"github.com/nats-io/nats.go"
	"github.com/nats-io/nats.go/jetstream"
	"github.com/rilegu/iot-fleet-reference/telemetry"
)

// StreamName is the durable log the API consumes to build its projection.
const StreamName = "FLEET"

// Bus publishes validated messages to JetStream.
//
// The published message is the original payload, byte for byte, with ingest's observations
// carried in headers. That keeps one wire format rather than two: there is no separate
// internal event schema to version alongside the device contract.
type Bus struct {
	nc     *nats.Conn
	js     jetstream.JetStream
	stream jetstream.Stream
}

func NewBus(ctx context.Context, url string) (*Bus, error) {
	nc, err := nats.Connect(url,
		nats.Name("fleet-ingest"),
		nats.MaxReconnects(-1),
		nats.ReconnectWait(2*time.Second),
	)
	if err != nil {
		return nil, fmt.Errorf("connecting to nats: %w", err)
	}

	js, err := jetstream.New(nc)
	if err != nil {
		nc.Close()
		return nil, fmt.Errorf("creating jetstream context: %w", err)
	}

	// Limits rather than retention-forever: the stream exists so a restarting consumer
	// can replay recent history, not as the system of record. TimescaleDB is that.
	stream, err := js.CreateOrUpdateStream(ctx, jetstream.StreamConfig{
		Name:        StreamName,
		Description: "Validated device messages, forwarded verbatim by ingest",
		Subjects:    []string{"fleet.>"},
		Storage:     jetstream.FileStorage,
		Retention:   jetstream.LimitsPolicy,
		MaxAge:      24 * time.Hour,
		MaxBytes:    2 << 30,
		Discard:     jetstream.DiscardOld,
	})
	if err != nil {
		nc.Close()
		return nil, fmt.Errorf("creating stream: %w", err)
	}

	return &Bus{nc: nc, js: js, stream: stream}, nil
}

func (b *Bus) Close() { b.nc.Close() }

// newMsg builds the NATS message for a fleet message. Both publish paths go through it, so
// a header added here cannot be forgotten on one of them.
//
// The trace context written here is the *current* span's, not the device's raw header.
// Forwarding the device value verbatim would put the API's span in the same trace but as a
// sibling of ingest's rather than a child, so the timeline would show two unrelated spans
// instead of the handoff between them. Where there is no active span — batched telemetry,
// which is queued rather than handled inline — it falls back to the device's own value so
// the trace still reaches back to the source.
func newMsg(ctx context.Context, m Message) *nats.Msg {
	msg := &nats.Msg{
		Subject: m.Subject(),
		Data:    m.Payload,
		Header:  nats.Header{},
	}
	msg.Header.Set("Fleet-Kind", m.Kind)
	msg.Header.Set("Fleet-Device", m.Envelope.DeviceID)
	msg.Header.Set("Fleet-Site", m.Envelope.Site)
	msg.Header.Set("Fleet-Boot-Id", m.Envelope.BootID)
	msg.Header.Set("Fleet-Seq", strconv.FormatInt(m.Envelope.Seq, 10))
	msg.Header.Set("Fleet-Received-At", m.ReceivedAt.UTC().Format(time.RFC3339Nano))
	if m.Retained {
		// A retained replay is historical. Losing this flag downstream reintroduces the
		// phantom-gap bug described in contracts/README.md, so it travels with the
		// message rather than being inferred later.
		msg.Header.Set("Fleet-Retained", "true")
	}
	if tp := telemetry.TraceparentFromContext(ctx); tp != "" {
		msg.Header.Set("traceparent", tp)
	} else if m.Envelope.Traceparent != "" {
		msg.Header.Set("traceparent", m.Envelope.Traceparent)
	}
	return msg
}

// Publish sends one message and waits for the stream to confirm it.
//
// Waiting matters: ingest must not acknowledge the broker until the log has the message,
// or a crash in this window loses it with no way to notice.
func (b *Bus) Publish(ctx context.Context, m Message) error {
	if _, err := b.js.PublishMsg(ctx, newMsg(ctx, m)); err != nil {
		return fmt.Errorf("publishing %s: %w", m.Subject(), err)
	}
	return nil
}

// PublishBatch publishes asynchronously and waits for all confirmations, which is much
// faster than a synchronous round trip per message.
func (b *Bus) PublishBatch(ctx context.Context, msgs []Message) error {
	futures := make([]jetstream.PubAckFuture, 0, len(msgs))
	for _, m := range msgs {
		f, err := b.js.PublishMsgAsync(newMsg(ctx, m))
		if err != nil {
			return fmt.Errorf("queueing %s: %w", m.Subject(), err)
		}
		futures = append(futures, f)
	}

	select {
	case <-b.js.PublishAsyncComplete():
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(30 * time.Second):
		return fmt.Errorf("timed out waiting for %d publish acknowledgements", len(futures))
	}

	for _, f := range futures {
		select {
		case err := <-f.Err():
			return fmt.Errorf("publish rejected: %w", err)
		default:
		}
	}
	return nil
}
