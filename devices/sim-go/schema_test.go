package main

import (
	"math/rand"
	"testing"
	"time"

	"github.com/rilegu/iot-fleet-reference/contracts"
)

// These tests close the loop that the exploratory spike left open: the simulator's real
// output is validated against the same schema files the ingest service uses, rather than
// against a hand-maintained idea of what it emits.
//
// The spike shipped `"schema": ""` on every message precisely because the contract was
// documented and declared but never asserted anywhere.

func validator(t *testing.T) *contracts.Validator {
	t.Helper()
	v, err := contracts.NewValidator()
	if err != nil {
		t.Fatalf("compiling schemas: %v", err)
	}
	return v
}

func TestTelemetryOutputMatchesSchema(t *testing.T) {
	v := validator(t)
	d := NewDevice(42, "site-01", "tcp://localhost:1883", time.Second, 0, rand.New(rand.NewSource(1)))

	// Many samples, because the metric random walk drifts and only some values will sit
	// near the schema's declared bounds.
	for i := 0; i < 500; i++ {
		d.walk()
		payload := mustJSON(Telemetry{
			Envelope: d.envelope(SchemaTelemetry),
			Metrics:  d.currentMetrics(),
		})
		if err := v.ValidateBytes(contracts.KindTelemetry, payload); err != nil {
			t.Fatalf("emitted telemetry violates the schema at sample %d: %v\n  %s", i, err, payload)
		}
	}
}

func TestStatusOutputMatchesSchema(t *testing.T) {
	v := validator(t)
	d := NewDevice(7, "site-00", "tcp://localhost:1883", time.Second, 0, rand.New(rand.NewSource(2)))

	for _, reason := range []string{ReasonConnect, ReasonShutdown} {
		payload := mustJSON(Status{
			Envelope:  d.envelope(SchemaStatus),
			Online:    reason == ReasonConnect,
			Reason:    reason,
			FwVersion: d.FwVer,
			Model:     d.Model,
		})
		if err := v.ValidateBytes(contracts.KindStatus, payload); err != nil {
			t.Errorf("emitted %s status violates the schema: %v\n  %s", reason, err, payload)
		}
	}
}

// The will is the payload most likely to drift out of contract, because it is built on a
// different path from every other message and is only ever published by the broker.
func TestWillOutputMatchesSchema(t *testing.T) {
	v := validator(t)
	d := NewDevice(9, "site-02", "tcp://localhost:1883", time.Second, 0, rand.New(rand.NewSource(3)))

	if err := v.ValidateBytes(contracts.KindStatus, d.willPayload()); err != nil {
		t.Fatalf("emitted will violates the schema: %v\n  %s", err, d.willPayload())
	}
}

func TestEventOutputMatchesSchema(t *testing.T) {
	v := validator(t)
	d := NewDevice(3, "site-03", "tcp://localhost:1883", time.Second, 0, rand.New(rand.NewSource(4)))

	for _, f := range AllFaults() {
		payload := mustJSON(d.buildEvent(f))
		if err := v.ValidateBytes(contracts.KindEvent, payload); err != nil {
			t.Errorf("emitted %s event violates the schema: %v\n  %s", f.Kind, err, payload)
		}
	}
}
