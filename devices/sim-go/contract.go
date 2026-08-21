package main

import (
	"encoding/json"
	"fmt"
	"math/rand"
	"time"
)

// Envelope is carried by every message. See contracts/README.md.
//
// Ordering is by (BootID, Seq) and never by TS: device clocks drift, devices reboot
// without a real-time clock, and container clocks drift from host clocks.
type Envelope struct {
	Schema      string `json:"schema"`
	DeviceID    string `json:"device_id"`
	Site        string `json:"site"`
	BootID      string `json:"boot_id"`
	Seq         uint64 `json:"seq"`
	TS          string `json:"ts"`
	Traceparent string `json:"traceparent,omitempty"`
}

type Metrics struct {
	TempC       float64 `json:"temp_c"`
	HumidityPct float64 `json:"humidity_pct"`
	VoltageV    float64 `json:"voltage_v"`
	RSSIdBm     int     `json:"rssi_dbm"`
	UptimeS     int64   `json:"uptime_s"`
}

type Telemetry struct {
	Envelope
	Metrics Metrics `json:"metrics"`
}

// Schema identifiers, carried on every message so a consumer can reject what it cannot
// parse. Format is name/major; a breaking payload change bumps the major.
const (
	SchemaTelemetry = "telemetry/1"
	SchemaStatus    = "status/1"
	SchemaEvent     = "event/1"
)

// Status reasons.
const (
	ReasonConnect  = "connect"
	ReasonShutdown = "shutdown"
	ReasonLWT      = "lwt"
)

// Event is a discrete state change or fault, as opposed to a periodic sample. Published
// at QoS 1: losing one loses information telemetry will not repeat.
type Event struct {
	Envelope
	Kind     string   `json:"kind"`
	Severity string   `json:"severity"`
	Detail   string   `json:"detail,omitempty"`
	Metric   string   `json:"metric,omitempty"`
	Value    *float64 `json:"value,omitempty"`
}

type Status struct {
	Envelope
	Online    bool   `json:"online"`
	Reason    string `json:"reason,omitempty"`
	FwVersion string `json:"fw_version"`
	Model     string `json:"model"`
}

func topicTelemetry(site, deviceID string) string {
	return fmt.Sprintf("fleet/%s/%s/telemetry", site, deviceID)
}

func topicStatus(site, deviceID string) string {
	return fmt.Sprintf("fleet/%s/%s/status", site, deviceID)
}

func topicEvent(site, deviceID string) string {
	return fmt.Sprintf("fleet/%s/%s/event", site, deviceID)
}

func nowRFC3339() string {
	return time.Now().UTC().Format(time.RFC3339Nano)
}

// randomHex returns n random bytes as lowercase hex, drawn from the supplied source so
// that a given --seed reproduces an identical run.
func randomHex(r *rand.Rand, n int) string {
	const hexDigits = "0123456789abcdef"
	b := make([]byte, n*2)
	for i := range b {
		b[i] = hexDigits[r.Intn(16)]
	}
	return string(b)
}

// newTraceparent builds a W3C trace context header value. MQTT 3.1.1 has no user
// properties, so trace context has to travel inside the payload.
func newTraceparent(r *rand.Rand) string {
	return "00-" + randomHex(r, 16) + "-" + randomHex(r, 8) + "-01"
}

func mustJSON(v any) []byte {
	b, err := json.Marshal(v)
	if err != nil {
		// The payload types are closed and contain no unsupported kinds, so a failure
		// here is a programming error rather than a runtime condition.
		panic(fmt.Sprintf("marshalling payload: %v", err))
	}
	return b
}
