package main

import (
	"encoding/json"
	"math/rand"
	"sync"
	"testing"
	"time"
)

func newTestDevice() *Device {
	return NewDevice(42, "site-01", "tcp://localhost:1883", time.Second, 0, rand.New(rand.NewSource(1)))
}

// TestEnvelopeConcurrent exercises the path that paho's OnConnect handler and the device
// goroutine share. Run under -race; without the mutex guarding rng this fails.
func TestEnvelopeConcurrent(t *testing.T) {
	d := newTestDevice()

	var wg sync.WaitGroup
	for i := 0; i < 8; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for j := 0; j < 200; j++ {
				_ = d.envelope(SchemaTelemetry)
				d.walk()
			}
		}()
	}
	wg.Wait()

	// 8 goroutines x 200 envelopes, each incrementing seq exactly once.
	if got := d.seq.Load(); got != 1600 {
		t.Fatalf("seq = %d, want 1600", got)
	}
}

func TestSeqIsMonotonic(t *testing.T) {
	d := newTestDevice()
	prev := uint64(0)
	for i := 0; i < 100; i++ {
		env := d.envelope(SchemaTelemetry)
		if env.Seq <= prev {
			t.Fatalf("seq went backwards: %d after %d", env.Seq, prev)
		}
		prev = env.Seq
	}
	if prev != 100 {
		t.Fatalf("final seq = %d, want 100", prev)
	}
}

// The will is composed at connect time, before the device knows its final sequence
// number, so it must carry seq 0 and be marked as a will. Consumers rely on both:
// seq 0 signals "outside the sequence" so the idempotency rule does not discard it.
func TestWillPayloadShape(t *testing.T) {
	d := newTestDevice()

	var got Status
	if err := json.Unmarshal(d.willPayload(), &got); err != nil {
		t.Fatalf("will payload is not valid JSON: %v", err)
	}

	if got.Seq != 0 {
		t.Errorf("will seq = %d, want 0", got.Seq)
	}
	if got.Online {
		t.Error("will must report the device offline")
	}
	if got.Reason != ReasonLWT {
		t.Errorf("will reason = %q, want %q", got.Reason, ReasonLWT)
	}
	if got.DeviceID != d.ID || got.Site != d.Site || got.BootID != d.BootID {
		t.Errorf("will identity = %s/%s/%s, want %s/%s/%s",
			got.Site, got.DeviceID, got.BootID, d.Site, d.ID, d.BootID)
	}
}

// Composing the will must not consume a sequence number, or the device's first real
// message would start at 2 and every consumer would see a phantom gap.
func TestWillDoesNotConsumeSeq(t *testing.T) {
	d := newTestDevice()
	_ = d.willPayload()
	if got := d.envelope(SchemaStatus).Seq; got != 1 {
		t.Fatalf("first envelope seq = %d, want 1", got)
	}
}

func TestTelemetryJSONKeys(t *testing.T) {
	d := newTestDevice()
	d.walk()
	payload := mustJSON(Telemetry{Envelope: d.envelope(SchemaTelemetry), Metrics: Metrics{TempC: 1}})

	var raw map[string]json.RawMessage
	if err := json.Unmarshal(payload, &raw); err != nil {
		t.Fatalf("telemetry is not valid JSON: %v", err)
	}
	for _, key := range []string{"device_id", "site", "boot_id", "seq", "ts", "traceparent", "metrics"} {
		if _, ok := raw[key]; !ok {
			t.Errorf("telemetry payload missing %q", key)
		}
	}
}

// Every message must identify its schema. An empty schema shipped in the first run of
// the spike; consumers cannot reject what they cannot parse without it.
func TestSchemaIsAlwaysSet(t *testing.T) {
	d := newTestDevice()

	if got := d.envelope(SchemaTelemetry).Schema; got != SchemaTelemetry {
		t.Errorf("telemetry envelope schema = %q, want %q", got, SchemaTelemetry)
	}

	var will Status
	if err := json.Unmarshal(d.willPayload(), &will); err != nil {
		t.Fatalf("will payload: %v", err)
	}
	if will.Schema != SchemaStatus {
		t.Errorf("will schema = %q, want %q", will.Schema, SchemaStatus)
	}
}

func TestTopics(t *testing.T) {
	if got, want := topicTelemetry("site-01", "dev-000042"), "fleet/site-01/dev-000042/telemetry"; got != want {
		t.Errorf("telemetry topic = %q, want %q", got, want)
	}
	if got, want := topicStatus("site-01", "dev-000042"), "fleet/site-01/dev-000042/status"; got != want {
		t.Errorf("status topic = %q, want %q", got, want)
	}
}

// A given seed must reproduce a run exactly, or scenario-driven scale tests are not
// comparable between runs.
func TestSeedIsReproducible(t *testing.T) {
	build := func() []float64 {
		seeder := rand.New(rand.NewSource(7))
		d := NewDevice(1, "site-00", "tcp://localhost:1883", time.Second, 0, rand.New(rand.NewSource(seeder.Int63())))
		out := make([]float64, 0, 10)
		for i := 0; i < 10; i++ {
			d.walk()
			out = append(out, d.tempC)
		}
		return out
	}

	a, b := build(), build()
	for i := range a {
		if a[i] != b[i] {
			t.Fatalf("same seed diverged at step %d: %v vs %v", i, a[i], b[i])
		}
	}
}

// A boot id identifies a process lifetime, not a device, so it must differ on every start
// even when the seed is fixed. When it did not, a restarted simulator kept its old boot id
// while its sequence reset to 1, and consumers discarded the whole new session as stale.
func TestBootIDIsNotReproducibleFromSeed(t *testing.T) {
	build := func() string {
		return NewDevice(1, "site-00", "tcp://localhost:1883", time.Second, 0,
			rand.New(rand.NewSource(42))).BootID
	}

	a, b := build(), build()
	if a == b {
		t.Fatalf("same seed produced the same boot id %q; a restart would be indistinguishable from a continuation", a)
	}
	if len(a) != 16 {
		t.Errorf("boot id %q is %d chars, want 16 to match the schema pattern", a, len(a))
	}
}

// The metric walk must stay reproducible even though the boot id is not.
func TestSeedStillReproducesSensorWalk(t *testing.T) {
	build := func() float64 {
		d := NewDevice(1, "site-00", "tcp://localhost:1883", time.Second, 0, rand.New(rand.NewSource(42)))
		for i := 0; i < 20; i++ {
			d.walk()
		}
		return d.tempC
	}
	if a, b := build(), build(); a != b {
		t.Fatalf("sensor walk diverged with the same seed: %v vs %v", a, b)
	}
}
