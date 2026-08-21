package main

import (
	"context"
	"fmt"
	"log/slog"
	"math"
	"math/rand"
	"net"
	"net/url"
	"sync"
	"sync/atomic"
	"time"

	mqtt "github.com/eclipse/paho.mqtt.golang"
)

var firmwareVersions = []string{"1.3.7", "1.4.0", "1.4.2"}

// Device simulates one embedded device: its own MQTT session, client id, sequence
// counter and drifting sensor state. Connections are never shared between devices —
// that is what makes the connection-scale problem real.
type Device struct {
	ID     string
	Site   string
	Model  string
	FwVer  string
	BootID string

	broker string
	rate   time.Duration

	// rngMu guards rng. The device goroutine and paho's OnConnect handler both build
	// envelopes, and *rand.Rand is not safe for concurrent use.
	rngMu sync.Mutex
	rng   *rand.Rand

	seq       atomic.Uint64
	startedAt time.Time

	// faultPct is the chance per telemetry tick of injecting a fault.
	faultPct float64

	// clockSkew is seconds added to reported timestamps after a clock_step fault. It
	// deliberately makes ts unusable for ordering, which is why the contract orders by
	// (boot_id, seq) instead.
	clockSkew float64

	// sensor state, walked rather than randomised per sample so the dashboard shows
	// something that looks like a physical process
	tempC       float64
	humidityPct float64
	voltageV    float64
	rssiDBm     float64

	client mqtt.Client

	// conn holds the live TCP connection so it can be closed without sending a
	// DISCONNECT packet, which is the only way to make the broker publish the will.
	mu   sync.Mutex
	conn net.Conn
}

func NewDevice(index int, site string, broker string, rate time.Duration, faultPct float64, rng *rand.Rand) *Device {
	return &Device{
		ID:          fmt.Sprintf("dev-%06d", index),
		Site:        site,
		Model:       "acme-sensor-v2",
		FwVer:       firmwareVersions[rng.Intn(len(firmwareVersions))],
		BootID:      newBootID(),
		broker:      broker,
		rate:        rate,
		faultPct:    faultPct,
		rng:         rng,
		tempC:       18 + rng.Float64()*12,
		humidityPct: 35 + rng.Float64()*30,
		voltageV:    11.6 + rng.Float64()*0.8,
		rssiDBm:     -50 - rng.Float64()*35,
		startedAt:   time.Now(),
	}
}

func (d *Device) envelope(schema string) Envelope {
	d.rngMu.Lock()
	tp := newTraceparent(d.rng)
	d.rngMu.Unlock()
	return Envelope{
		Schema:      schema,
		DeviceID:    d.ID,
		Site:        d.Site,
		BootID:      d.BootID,
		Seq:         d.seq.Add(1),
		TS:          d.now(),
		Traceparent: tp,
	}
}

// willPayload is fixed at connect time, before the device has sent anything, so it
// cannot carry a meaningful sequence number. It uses seq 0 and reason "lwt"; consumers
// must treat seq 0 on a status message as outside the sequence rather than as a stale
// message to discard. See contracts/README.md.
func (d *Device) willPayload() []byte {
	env := Envelope{
		Schema:   SchemaStatus,
		DeviceID: d.ID,
		Site:     d.Site,
		BootID:   d.BootID,
		Seq:      0,
		TS:       nowRFC3339(),
	}
	return mustJSON(Status{
		Envelope:  env,
		Online:    false,
		Reason:    ReasonLWT,
		FwVersion: d.FwVer,
		Model:     d.Model,
	})
}

func (d *Device) Run(ctx context.Context) error {
	opts := mqtt.NewClientOptions().
		AddBroker(d.broker).
		SetClientID(d.ID).
		SetProtocolVersion(4). // MQTT 3.1.1
		SetCleanSession(true).
		SetOrderMatters(false).
		SetAutoReconnect(true).
		SetConnectRetry(true).
		SetConnectRetryInterval(2*time.Second).
		SetMaxReconnectInterval(30*time.Second).
		SetKeepAlive(30*time.Second).
		SetConnectTimeout(10*time.Second).
		SetWill(topicStatus(d.Site, d.ID), string(d.willPayload()), 1, true)

	// Capture the underlying connection so Flap can drop it without a DISCONNECT.
	opts.SetCustomOpenConnectionFn(func(uri *url.URL, _ mqtt.ClientOptions) (net.Conn, error) {
		conn, err := net.DialTimeout("tcp", uri.Host, 10*time.Second)
		if err != nil {
			return nil, err
		}
		d.mu.Lock()
		d.conn = conn
		d.mu.Unlock()
		return conn, nil
	})

	// Announce presence on every (re)connection, including automatic reconnects after
	// a flap. Doing this in the handler rather than once after Connect is what makes a
	// device recover its retained status without restarting the process.
	opts.SetOnConnectHandler(func(c mqtt.Client) {
		d.publishStatus(c, true, ReasonConnect)
	})

	d.client = mqtt.NewClient(opts)
	if token := d.client.Connect(); token.Wait() && token.Error() != nil {
		return fmt.Errorf("connect %s: %w", d.ID, token.Error())
	}

	// Spread the fleet across the publish interval instead of having every device fire
	// on the same tick, which would produce an unrealistic sawtooth at the broker.
	select {
	case <-time.After(time.Duration(d.rng.Int63n(int64(d.rate)))):
	case <-ctx.Done():
		d.shutdown()
		return nil
	}

	ticker := time.NewTicker(d.rate)
	defer ticker.Stop()

	for {
		select {
		case <-ctx.Done():
			d.shutdown()
			return nil
		case <-ticker.C:
			d.publishTelemetry()
		}
	}
}

// Flap closes the TCP connection without sending DISCONNECT, which is what a real device
// losing power or signal looks like. The broker publishes the will; paho's auto-reconnect
// brings the device back and the OnConnect handler republishes an online status.
func (d *Device) Flap() {
	d.mu.Lock()
	conn := d.conn
	d.mu.Unlock()
	if conn != nil {
		_ = conn.Close()
	}
}

// now reports the device's own clock, including any skew a clock_step fault introduced.
func (d *Device) now() string {
	d.rngMu.Lock()
	skew := d.clockSkew
	d.rngMu.Unlock()
	return time.Now().UTC().Add(time.Duration(skew) * time.Second).Format(time.RFC3339Nano)
}

// currentMetrics snapshots sensor state as it would be reported right now.
func (d *Device) currentMetrics() Metrics {
	d.rngMu.Lock()
	defer d.rngMu.Unlock()
	return Metrics{
		TempC:       round2(d.tempC),
		HumidityPct: round2(d.humidityPct),
		VoltageV:    round2(d.voltageV),
		RSSIdBm:     int(math.Round(d.rssiDBm)),
		UptimeS:     int64(time.Since(d.startedAt).Seconds()),
	}
}

// buildEvent composes the event a fault reports, reading the affected metric's current
// value so the event carries evidence rather than only a label.
func (d *Device) buildEvent(f Fault) Event {
	ev := Event{
		Envelope: d.envelope(SchemaEvent),
		Kind:     f.Kind,
		Severity: f.Severity,
		Detail:   f.Detail,
		Metric:   f.Metric,
	}
	if f.Metric != "" {
		m := d.currentMetrics()
		var v float64
		switch f.Metric {
		case "temp_c":
			v = m.TempC
		case "humidity_pct":
			v = m.HumidityPct
		case "voltage_v":
			v = m.VoltageV
		case "rssi_dbm":
			v = float64(m.RSSIdBm)
		}
		ev.Value = &v
	}
	return ev
}

// maybeInjectFault rolls for a fault, applies it to sensor state and publishes the event.
func (d *Device) maybeInjectFault() {
	if d.faultPct <= 0 {
		return
	}
	d.rngMu.Lock()
	roll := d.rng.Float64() * 100
	var f Fault
	if roll < d.faultPct {
		f = pickFault(d.rng)
		f.apply(d, d.rng)
	}
	d.rngMu.Unlock()

	if f.Kind == "" {
		return
	}
	payload := mustJSON(d.buildEvent(f))
	// QoS 1: an event describes a transition that telemetry will not repeat.
	token := d.client.Publish(topicEvent(d.Site, d.ID), 1, false, payload)
	if !token.WaitTimeout(5 * time.Second) {
		slog.Warn("event publish timed out", "device", d.ID, "kind", f.Kind)
	}
}

func (d *Device) publishTelemetry() {
	if !d.client.IsConnected() {
		return
	}
	d.walk()
	d.maybeInjectFault()
	payload := mustJSON(Telemetry{
		Envelope: d.envelope(SchemaTelemetry),
		Metrics:  d.currentMetrics(),
	})
	// QoS 0: telemetry is a sample of a continuous signal. A dropped sample is
	// acceptable; head-of-line blocking is not.
	d.client.Publish(topicTelemetry(d.Site, d.ID), 0, false, payload)
}

func (d *Device) publishStatus(c mqtt.Client, online bool, reason string) {
	payload := mustJSON(Status{
		Envelope:  d.envelope(SchemaStatus),
		Online:    online,
		Reason:    reason,
		FwVersion: d.FwVer,
		Model:     d.Model,
	})
	// QoS 1 and retained: a consumer subscribing later must immediately learn the
	// current state of every device rather than waiting for the next sample.
	token := c.Publish(topicStatus(d.Site, d.ID), 1, true, payload)
	if !token.WaitTimeout(5 * time.Second) {
		slog.Warn("status publish timed out", "device", d.ID)
	}
}

func (d *Device) shutdown() {
	if d.client != nil && d.client.IsConnected() {
		d.publishStatus(d.client, false, ReasonShutdown)
		d.client.Disconnect(250)
	}
}

// walk moves each metric by a small random step and clamps it, so values look like a
// physical process rather than uniform noise.
func (d *Device) walk() {
	d.rngMu.Lock()
	defer d.rngMu.Unlock()

	d.tempC = clamp(d.tempC+(d.rng.Float64()-0.5)*0.4, -10, 85)
	d.humidityPct = clamp(d.humidityPct+(d.rng.Float64()-0.5)*1.2, 0, 100)
	d.voltageV = clamp(d.voltageV+(d.rng.Float64()-0.5)*0.05, 10.5, 13.0)
	d.rssiDBm = clamp(d.rssiDBm+(d.rng.Float64()-0.5)*3, -95, -35)
}

func clamp(v, lo, hi float64) float64 {
	return math.Min(math.Max(v, lo), hi)
}

func round2(v float64) float64 {
	return math.Round(v*100) / 100
}
