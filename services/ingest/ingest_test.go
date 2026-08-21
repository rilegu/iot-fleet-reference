package main

import (
	"testing"
	"time"

	"github.com/prometheus/client_golang/prometheus"
)

// newTestIngest builds the struct the way the service does, minus the external
// dependencies. A fresh registry per test keeps counters isolated: a shared one would make
// assertions depend on which tests ran before.
func newTestIngest(queueSize int) *Ingest {
	return &Ingest{
		telemetryQ: make(chan Message, queueSize),
		counters:   &Counters{},
		metrics:    NewIngestMetrics(prometheus.NewRegistry()),
	}
}

func TestTopicParts(t *testing.T) {
	ok := map[string][3]string{
		"fleet/site-00/dev-000042/telemetry": {"site-00", "dev-000042", KindTelemetry},
		"fleet/site-31/dev-009999/status":    {"site-31", "dev-009999", KindStatus},
		"fleet/site-01/dev-000001/event":     {"site-01", "dev-000001", KindEvent},
	}
	for topic, want := range ok {
		site, device, kind, err := topicParts(topic)
		if err != nil {
			t.Errorf("%s: unexpected error %v", topic, err)
			continue
		}
		if site != want[0] || device != want[1] || kind != want[2] {
			t.Errorf("%s: got (%s,%s,%s), want %v", topic, site, device, kind, want)
		}
	}

	bad := []string{
		"fleet/site-00/dev-000042",             // too few segments
		"fleet/site-00/dev-000042/telemetry/x", // too many
		"other/site-00/dev-000042/telemetry",   // wrong root
		"fleet/site-00/dev-000042/cmd",         // not yet a kind ingest accepts
		"",
	}
	for _, topic := range bad {
		if _, _, _, err := topicParts(topic); err == nil {
			t.Errorf("%q: expected an error, got none", topic)
		}
	}
}

func TestSubjectIncludesSiteAndDevice(t *testing.T) {
	m := Message{
		Kind:     KindTelemetry,
		Envelope: Envelope{Site: "site-03", DeviceID: "dev-000007"},
	}
	if got, want := m.Subject(), "fleet.telemetry.site-03.dev-000007"; got != want {
		t.Errorf("Subject() = %q, want %q", got, want)
	}
}

func TestDecodeEnvelope(t *testing.T) {
	payload := []byte(`{"schema":"telemetry/1","device_id":"dev-000042","site":"site-01","boot_id":"b3f1a9c47d2e5810","seq":7,"ts":"2026-08-20T19:12:33.123Z","traceparent":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01","metrics":{}}`)

	e, err := decodeEnvelope(payload)
	if err != nil {
		t.Fatalf("decodeEnvelope: %v", err)
	}
	if e.DeviceID != "dev-000042" || e.Site != "site-01" || e.Seq != 7 {
		t.Errorf("decoded %+v, want device dev-000042 / site-01 / seq 7", e)
	}
	if e.BootID != "b3f1a9c47d2e5810" {
		t.Errorf("boot id = %q", e.BootID)
	}
	if e.TS.IsZero() {
		t.Error("ts did not decode")
	}

	if _, err := decodeEnvelope([]byte(`{"seq":`)); err == nil {
		t.Error("truncated payload should fail to decode")
	}
}

// The bounded queue is the backpressure policy from the delivery-semantics decision
// record: telemetry is a sample of a continuous signal, so when the queue is full the
// oldest sample is discarded and counted. What must never happen is unbounded growth, or
// a drop that nothing records.
func TestTelemetryQueueDropsOldestAndCounts(t *testing.T) {
	const size = 8
	in := newTestIngest(size)

	// Fill to capacity: nothing should be dropped yet.
	for i := 0; i < size; i++ {
		in.enqueueTelemetry(Message{Envelope: Envelope{Seq: int64(i)}, ReceivedAt: time.Now()})
	}
	if got := in.counters.TelemetryDropped.Load(); got != 0 {
		t.Fatalf("dropped %d while still under capacity", got)
	}
	if got := len(in.telemetryQ); got != size {
		t.Fatalf("queue holds %d, want %d", got, size)
	}

	// Overflow by a known amount.
	const over = 5
	for i := 0; i < over; i++ {
		in.enqueueTelemetry(Message{Envelope: Envelope{Seq: int64(size + i)}, ReceivedAt: time.Now()})
	}

	if got := in.counters.TelemetryDropped.Load(); got != over {
		t.Errorf("dropped counter = %d, want %d", got, over)
	}
	if got := len(in.telemetryQ); got != size {
		t.Errorf("queue grew to %d; it must stay bounded at %d", got, size)
	}

	// The oldest samples went, the newest stayed: the queue should now start at `over`.
	first := <-in.telemetryQ
	if first.Envelope.Seq != over {
		t.Errorf("oldest retained seq = %d, want %d (drop-oldest not honoured)", first.Envelope.Seq, over)
	}
}

func TestHealthyReportsDisconnected(t *testing.T) {
	in := newTestIngest(1)
	if err := in.Healthy(); err == nil {
		t.Error("a service with no broker client must not report healthy")
	}
}
