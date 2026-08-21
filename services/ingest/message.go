package main

import (
	"encoding/json"
	"fmt"
	"strings"
	"time"
)

// Kinds carried on the wire, matching the topic suffix and the schema file names.
const (
	KindTelemetry = "telemetry"
	KindStatus    = "status"
	KindEvent     = "event"
)

// Envelope is the subset of every message that ingest needs in order to route, order and
// store it. The full payload is preserved verbatim and forwarded unchanged, so this is a
// view over the message rather than a re-modelling of it.
type Envelope struct {
	Schema      string    `json:"schema"`
	DeviceID    string    `json:"device_id"`
	Site        string    `json:"site"`
	BootID      string    `json:"boot_id"`
	Seq         int64     `json:"seq"`
	TS          time.Time `json:"ts"`
	Traceparent string    `json:"traceparent"`
}

type Metrics struct {
	TempC       *float64 `json:"temp_c"`
	HumidityPct *float64 `json:"humidity_pct"`
	VoltageV    *float64 `json:"voltage_v"`
	RSSIdBm     *int     `json:"rssi_dbm"`
	UptimeS     *int64   `json:"uptime_s"`
}

type Telemetry struct {
	Envelope
	Metrics Metrics `json:"metrics"`
}

type Status struct {
	Envelope
	Online    bool   `json:"online"`
	Reason    string `json:"reason"`
	FwVersion string `json:"fw_version"`
	Model     string `json:"model"`
}

type Event struct {
	Envelope
	Kind     string   `json:"kind"`
	Severity string   `json:"severity"`
	Detail   string   `json:"detail"`
	Metric   string   `json:"metric"`
	Value    *float64 `json:"value"`
}

// Message is a validated payload plus what ingest observed about it.
type Message struct {
	Kind       string
	Topic      string
	Payload    []byte
	Envelope   Envelope
	ReceivedAt time.Time
	Retained   bool
}

// topicParts splits fleet/{site}/{device}/{kind} and reports the kind.
//
// The topic is parsed for routing only. Identity is taken from the payload, which carries
// site and device_id itself, so a mismatch between the two is a validation failure rather
// than something to reconcile.
func topicParts(topic string) (site, device, kind string, err error) {
	p := strings.Split(topic, "/")
	if len(p) != 4 || p[0] != "fleet" {
		return "", "", "", fmt.Errorf("unexpected topic shape %q", topic)
	}
	switch p[3] {
	case KindTelemetry, KindStatus, KindEvent:
	default:
		return "", "", "", fmt.Errorf("unknown message kind %q", p[3])
	}
	return p[1], p[2], p[3], nil
}

// decodeEnvelope reads the routing fields without consuming the full payload shape.
func decodeEnvelope(payload []byte) (Envelope, error) {
	var e Envelope
	if err := json.Unmarshal(payload, &e); err != nil {
		return Envelope{}, fmt.Errorf("decoding envelope: %w", err)
	}
	return e, nil
}

// Subject maps a message to its NATS subject. Site and device are included so a consumer
// can subscribe to a slice of the fleet without filtering in application code.
func (m Message) Subject() string {
	return fmt.Sprintf("fleet.%s.%s.%s", m.Kind, m.Envelope.Site, m.Envelope.DeviceID)
}
