package contracts

import (
	"strings"
	"testing"
)

const validEnvelope = `"device_id":"dev-000042","site":"site-01","boot_id":"b3f1a9c47d2e5810","seq":7,"ts":"2026-08-20T19:12:33.123Z","traceparent":"00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01"`

func validTelemetry() string {
	return `{"schema":"telemetry/1",` + validEnvelope + `,"metrics":{"temp_c":42.31,"humidity_pct":51.2,"voltage_v":12.08,"rssi_dbm":-67,"uptime_s":84321}}`
}

func newValidator(t *testing.T) *Validator {
	t.Helper()
	v, err := NewValidator()
	if err != nil {
		t.Fatalf("compiling embedded schemas: %v", err)
	}
	return v
}

func TestSchemasCompile(t *testing.T) {
	v := newValidator(t)
	for _, kind := range []string{KindTelemetry, KindStatus, KindEvent} {
		if err := v.Validate(kind, map[string]any{}); err == nil {
			t.Errorf("%s: empty object should not validate", kind)
		}
	}
	// envelope.json is referenced, never validated against directly.
	if err := v.Validate("envelope", map[string]any{}); err == nil {
		t.Error("envelope should not be exposed as a validatable kind")
	}
}

func TestValidTelemetryAccepted(t *testing.T) {
	v := newValidator(t)
	if err := v.ValidateBytes(KindTelemetry, []byte(validTelemetry())); err != nil {
		t.Fatalf("valid telemetry rejected: %v", err)
	}
}

func TestTelemetryRejections(t *testing.T) {
	v := newValidator(t)

	cases := map[string]string{
		"missing schema":        strings.Replace(validTelemetry(), `"schema":"telemetry/1",`, "", 1),
		"wrong schema id":       strings.Replace(validTelemetry(), "telemetry/1", "telemetry/2", 1),
		"status schema id":      strings.Replace(validTelemetry(), "telemetry/1", "status/1", 1),
		"missing metrics":       `{"schema":"telemetry/1",` + validEnvelope + `}`,
		"missing one metric":    `{"schema":"telemetry/1",` + validEnvelope + `,"metrics":{"temp_c":1,"humidity_pct":2,"voltage_v":3,"rssi_dbm":-4}}`,
		"humidity out of range": strings.Replace(validTelemetry(), `"humidity_pct":51.2`, `"humidity_pct":142`, 1),
		"rssi positive":         strings.Replace(validTelemetry(), `"rssi_dbm":-67`, `"rssi_dbm":12`, 1),
		"unknown metric":        strings.Replace(validTelemetry(), `"uptime_s":84321`, `"uptime_s":84321,"pressure":9`, 1),
		"unknown top level":     strings.Replace(validTelemetry(), `"metrics":`, `"colour":"red","metrics":`, 1),
		"bad device id":         strings.Replace(validTelemetry(), "dev-000042", "Dev 42!", 1),
		"bad boot id":           strings.Replace(validTelemetry(), "b3f1a9c47d2e5810", "nothex", 1),
		"bad traceparent":       strings.Replace(validTelemetry(), "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", "not-a-traceparent", 1),
	}

	for name, payload := range cases {
		t.Run(name, func(t *testing.T) {
			if err := v.ValidateBytes(KindTelemetry, []byte(payload)); err == nil {
				t.Errorf("expected rejection, payload was accepted:\n  %s", payload)
			}
		})
	}
}

// The Last Will is composed at connect time and cannot carry a real sequence number. The
// schema encodes that carve-out explicitly so a consumer can rely on it.
func TestStatusWillRules(t *testing.T) {
	v := newValidator(t)

	will := `{"schema":"status/1","device_id":"dev-000042","site":"site-01","boot_id":"b3f1a9c47d2e5810","seq":0,"ts":"2026-08-20T19:12:33.123Z","online":false,"reason":"lwt","fw_version":"1.4.2","model":"acme-sensor-v2"}`
	if err := v.ValidateBytes(KindStatus, []byte(will)); err != nil {
		t.Fatalf("valid will rejected: %v", err)
	}

	live := strings.NewReplacer(`"seq":0`, `"seq":1`, `"reason":"lwt"`, `"reason":"connect"`, `"online":false`, `"online":true`).Replace(will)
	if err := v.ValidateBytes(KindStatus, []byte(live)); err != nil {
		t.Fatalf("valid connect status rejected: %v", err)
	}

	bad := map[string]string{
		"seq 0 without lwt reason": strings.Replace(will, `"reason":"lwt"`, `"reason":"connect"`, 1),
		"lwt reporting online":     strings.Replace(will, `"online":false`, `"online":true`, 1),
		"lwt with real seq":        strings.Replace(will, `"seq":0`, `"seq":9`, 1),
		"unknown reason":           strings.Replace(will, `"reason":"lwt"`, `"reason":"exploded"`, 1),
	}
	for name, payload := range bad {
		t.Run(name, func(t *testing.T) {
			if err := v.ValidateBytes(KindStatus, []byte(payload)); err == nil {
				t.Errorf("expected rejection, payload was accepted:\n  %s", payload)
			}
		})
	}
}

func TestEventSchema(t *testing.T) {
	v := newValidator(t)

	ok := `{"schema":"event/1",` + validEnvelope + `,"kind":"brownout","severity":"warning","detail":"supply dipped","metric":"voltage_v","value":10.7}`
	if err := v.ValidateBytes(KindEvent, []byte(ok)); err != nil {
		t.Fatalf("valid event rejected: %v", err)
	}

	bad := map[string]string{
		"unknown kind":     strings.Replace(ok, `"kind":"brownout"`, `"kind":"gremlins"`, 1),
		"unknown severity": strings.Replace(ok, `"severity":"warning"`, `"severity":"spicy"`, 1),
		"missing kind":     strings.Replace(ok, `"kind":"brownout",`, "", 1),
	}
	for name, payload := range bad {
		t.Run(name, func(t *testing.T) {
			if err := v.ValidateBytes(KindEvent, []byte(payload)); err == nil {
				t.Errorf("expected rejection, payload was accepted:\n  %s", payload)
			}
		})
	}
}

func TestMalformedJSONRejected(t *testing.T) {
	v := newValidator(t)
	if err := v.ValidateBytes(KindTelemetry, []byte(`{"schema":`)); err == nil {
		t.Error("truncated JSON should be rejected")
	}
}
