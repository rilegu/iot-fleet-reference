// Package telemetry wires tracing and metrics for the Go services.
//
// The pipeline crosses eight hops between a device and a dashboard. Without a trace that
// spans all of them, "why is device 412 missing" is answered by reading four sets of logs
// and guessing — which is the risk the ingest/API split was accepted knowing about, on the
// condition that this existed.
//
// Everything here degrades rather than fails. A service that cannot reach a collector must
// still ingest telemetry: observability going down is not an outage of the thing being
// observed.
package telemetry

import (
	"context"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"time"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/prometheus/client_golang/prometheus/collectors"
	"github.com/prometheus/client_golang/prometheus/promhttp"
	"go.opentelemetry.io/otel"
	"go.opentelemetry.io/otel/attribute"
	"go.opentelemetry.io/otel/exporters/otlp/otlptrace/otlptracegrpc"
	"go.opentelemetry.io/otel/propagation"
	"go.opentelemetry.io/otel/sdk/resource"
	sdktrace "go.opentelemetry.io/otel/sdk/trace"
	semconv "go.opentelemetry.io/otel/semconv/v1.26.0"
	"go.opentelemetry.io/otel/trace"
)

// Config is resolved from the environment so a service behaves the same whether it runs
// under Compose, in CI, or on a workstation with no collector at all.
type Config struct {
	ServiceName string

	// OTLPEndpoint is host:port for the collector. Empty disables tracing entirely, which
	// is the default: a developer running one service should not need a collector.
	OTLPEndpoint string

	// SampleRatio is the fraction of traces recorded. At a thousand messages a second,
	// recording every one costs more than it reveals.
	SampleRatio float64
}

func ConfigFromEnv(serviceName string) Config {
	ratio := 0.05
	if v := os.Getenv("OTEL_TRACES_SAMPLER_ARG"); v != "" {
		if _, err := fmt.Sscanf(v, "%g", &ratio); err != nil {
			slog.Warn("ignoring unparseable OTEL_TRACES_SAMPLER_ARG", "value", v)
		}
	}
	return Config{
		ServiceName:  serviceName,
		OTLPEndpoint: os.Getenv("OTEL_EXPORTER_OTLP_ENDPOINT"),
		SampleRatio:  ratio,
	}
}

// Shutdown flushes anything buffered. Callers should defer it: spans held in the batch
// processor when a process exits are simply lost, and losing precisely the spans around a
// shutdown is losing the ones most likely to explain it.
type Shutdown func(context.Context) error

// Setup installs a tracer provider and the W3C propagator.
//
// Returns a no-op shutdown when tracing is disabled, so callers need no conditional.
func Setup(ctx context.Context, cfg Config) (Shutdown, error) {
	// The propagator is installed regardless of whether an exporter exists. Trace context
	// then still flows through the pipeline, so enabling a collector later does not require
	// touching any of the code that passes context along.
	otel.SetTextMapPropagator(propagation.NewCompositeTextMapPropagator(
		propagation.TraceContext{},
		propagation.Baggage{},
	))

	if cfg.OTLPEndpoint == "" {
		slog.Info("tracing disabled", "reason", "OTEL_EXPORTER_OTLP_ENDPOINT is not set")
		return func(context.Context) error { return nil }, nil
	}

	exporter, err := otlptracegrpc.New(ctx,
		otlptracegrpc.WithEndpoint(cfg.OTLPEndpoint),
		otlptracegrpc.WithInsecure(),
	)
	if err != nil {
		return nil, fmt.Errorf("creating otlp exporter: %w", err)
	}

	// NewSchemaless, not NewWithAttributes: merging two resources that declare different
	// semantic-convention schema URLs is an error, and resource.Default() tracks whatever
	// version the SDK ships. Pinning a URL here means every SDK upgrade breaks startup for
	// no benefit, since the only attribute being added is the service name.
	res, err := resource.Merge(resource.Default(), resource.NewSchemaless(
		semconv.ServiceName(cfg.ServiceName),
	))
	if err != nil {
		return nil, fmt.Errorf("building resource: %w", err)
	}

	provider := sdktrace.NewTracerProvider(
		sdktrace.WithBatcher(exporter, sdktrace.WithBatchTimeout(5*time.Second)),
		sdktrace.WithResource(res),
		// ParentBased means a sampling decision made upstream is honoured. Without it a
		// trace would be recorded in one service and dropped in the next, producing
		// fragments that look like the pipeline lost the message.
		sdktrace.WithSampler(sdktrace.ParentBased(sdktrace.TraceIDRatioBased(cfg.SampleRatio))),
	)
	otel.SetTracerProvider(provider)

	slog.Info("tracing enabled", "endpoint", cfg.OTLPEndpoint, "sample_ratio", cfg.SampleRatio)
	return provider.Shutdown, nil
}

// Tracer returns a named tracer. Named per component so spans can be attributed without
// inspecting attributes.
func Tracer(name string) trace.Tracer { return otel.Tracer(name) }

// ContextFromTraceparent continues a trace that began on a device.
//
// MQTT 3.1.1 has no user properties, so W3C trace context travels inside the message
// envelope. This lifts it back out. An unparseable or absent value yields a context that
// starts a new trace rather than an error: a malformed header is not a reason to drop
// telemetry.
func ContextFromTraceparent(ctx context.Context, traceparent string) context.Context {
	if traceparent == "" {
		return ctx
	}
	return otel.GetTextMapPropagator().Extract(ctx, propagation.MapCarrier{
		"traceparent": traceparent,
	})
}

// TraceparentFromContext renders the current span as a W3C header value, for putting back
// onto a wire that has no header of its own.
func TraceparentFromContext(ctx context.Context) string {
	carrier := propagation.MapCarrier{}
	otel.GetTextMapPropagator().Inject(ctx, carrier)
	return carrier.Get("traceparent")
}

// Attr helpers keep attribute keys consistent across services. A trace filtered by
// device.id only works if every service spells it the same way.
func DeviceAttrs(deviceID, site, kind string) []attribute.KeyValue {
	return []attribute.KeyValue{
		attribute.String("fleet.device_id", deviceID),
		attribute.String("fleet.site", site),
		attribute.String("fleet.message_kind", kind),
	}
}

// ---------------------------------------------------------------------------------------
// Metrics
// ---------------------------------------------------------------------------------------

// NewRegistry returns a registry pre-populated with process and Go runtime collectors.
//
// Those come first because the questions asked during an incident are usually about memory
// and goroutines, and a registry containing only bespoke counters cannot answer them.
func NewRegistry() *prometheus.Registry {
	reg := prometheus.NewRegistry()
	reg.MustRegister(
		collectors.NewProcessCollector(collectors.ProcessCollectorOpts{}),
		collectors.NewGoCollector(),
	)
	return reg
}

// ServeMetrics adds a Prometheus endpoint to a mux.
func ServeMetrics(mux *http.ServeMux, reg *prometheus.Registry) {
	mux.Handle("/metrics", promhttp.HandlerFor(reg, promhttp.HandlerOpts{
		// A failing collector should show up in the scrape rather than returning a 500 that
		// looks to Prometheus like the whole service is down.
		ErrorHandling: promhttp.ContinueOnError,
	}))
}
