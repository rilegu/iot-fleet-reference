package main

import (
	"strings"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/rilegu/iot-fleet-reference/telemetry"
)

// IngestMetrics mirrors the counters already exposed on /stats, in a form Prometheus can scrape.
//
// The JSON endpoint stays: it is the fastest way for a person to see what the pipeline is
// doing, and it needs no infrastructure. These exist for the questions a person cannot
// answer by reading a number once — rates, trends, and whether the drop that just happened
// is new.
type IngestMetrics struct {
	Received       *prometheus.CounterVec
	Invalid        *prometheus.CounterVec
	Dropped        prometheus.Counter
	DeadLettered   prometheus.Counter
	Unacknowledged prometheus.Counter

	WriteFailures   *prometheus.CounterVec
	PublishFailures prometheus.Counter

	QueueDepth prometheus.Gauge

	// Histograms rather than averages. An average write latency hides the tail, and the
	// tail is what a stalled dashboard is made of.
	WriteDuration   *prometheus.HistogramVec
	PublishDuration prometheus.Histogram

	// IngestLag measures broker-to-database time using arrival timestamps taken on one
	// clock. Device timestamps are never used: they drift, and a clock_step fault makes
	// them move backwards on purpose.
	IngestLag prometheus.Histogram
}

func NewIngestMetrics(reg *prometheus.Registry) *IngestMetrics {
	m := &IngestMetrics{
		Received: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "messages_received_total",
			Help: "Messages accepted from the broker, by kind.",
		}, []string{"kind"}),

		Invalid: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "messages_invalid_total",
			Help: "Messages rejected at the validation boundary, by reason.",
		}, []string{"reason"}),

		Dropped: prometheus.NewCounter(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "telemetry_dropped_total",
			Help: "Telemetry samples discarded because the bounded queue was full.",
		}),

		DeadLettered: prometheus.NewCounter(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "dead_lettered_total",
			Help: "Rejected payloads sampled into the dead-letter table.",
		}),

		Unacknowledged: prometheus.NewCounter(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "unacknowledged_total",
			Help: "QoS 1 messages deliberately left unacknowledged so the broker redelivers them.",
		}),

		WriteFailures: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "write_failures_total",
			Help: "Database writes that failed, by kind.",
		}, []string{"kind"}),

		PublishFailures: prometheus.NewCounter(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "publish_failures_total",
			Help: "Publishes to the event log that failed.",
		}),

		QueueDepth: prometheus.NewGauge(prometheus.GaugeOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "telemetry_queue_depth",
			Help: "Messages waiting in the bounded telemetry queue. Sustained growth precedes drops.",
		}),

		WriteDuration: prometheus.NewHistogramVec(prometheus.HistogramOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "write_duration_seconds",
			Help:    "Time to write to the database, by kind.",
			Buckets: prometheus.ExponentialBuckets(0.001, 2, 12),
		}, []string{"kind"}),

		PublishDuration: prometheus.NewHistogram(prometheus.HistogramOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "publish_duration_seconds",
			Help:    "Time to publish to the event log and receive confirmation.",
			Buckets: prometheus.ExponentialBuckets(0.001, 2, 12),
		}),

		IngestLag: prometheus.NewHistogram(prometheus.HistogramOpts{
			Namespace: "fleet", Subsystem: "ingest", Name: "lag_seconds",
			Help:    "Broker arrival to durable write, measured on a single clock.",
			Buckets: prometheus.ExponentialBuckets(0.001, 2, 14),
		}),
	}

	reg.MustRegister(
		m.Received, m.Invalid, m.Dropped, m.DeadLettered, m.Unacknowledged,
		m.WriteFailures, m.PublishFailures, m.QueueDepth,
		m.WriteDuration, m.PublishDuration, m.IngestLag,
	)
	return m
}

// NewRegistry is re-exported so main does not need to import the telemetry package twice.
func NewRegistry() *prometheus.Registry { return telemetry.NewRegistry() }

// reasonCategory collapses a validation message to a bounded set of labels.
//
// Metric labels must have low cardinality. A schema error message contains the offending
// value, so using it directly would create a new time series per malformed payload, which
// is the classic way to bring down a metrics backend with a monitoring change.
func reasonCategory(reason string) string {
	switch {
	case strings.HasPrefix(reason, "schema"):
		return "schema"
	case strings.HasPrefix(reason, "topic"):
		return "topic"
	case strings.HasPrefix(reason, "envelope"):
		return "envelope"
	default:
		return "other"
	}
}
