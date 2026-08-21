// Command ingest consumes device messages from the broker, validates them against the
// schemas in contracts/, writes them to TimescaleDB, and forwards them verbatim to a
// durable log for the API to project.
//
// It owns the broker connection so that restarting the API cannot drop device sessions or
// create a telemetry gap.
package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/rilegu/iot-fleet-reference/contracts"
	"github.com/rilegu/iot-fleet-reference/telemetry"
)

type config struct {
	broker      string
	databaseURL string
	natsURL     string
	httpAddr    string
	sites       []string
}

func main() {
	cfg := parseConfig()

	log := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(log)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// Tracing is optional and degrades quietly: a collector being unreachable must not stop
	// the pipeline ingesting, because observability failing is not an outage of the thing
	// being observed.
	shutdownTracing, err := telemetry.Setup(ctx, telemetry.ConfigFromEnv("fleet-ingest"))
	if err != nil {
		log.Warn("continuing without tracing", "err", err)
		shutdownTracing = func(context.Context) error { return nil }
	}
	defer func() {
		flushCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		if err := shutdownTracing(flushCtx); err != nil {
			log.Warn("flushing traces failed", "err", err)
		}
	}()

	registry := NewRegistry()
	metrics := NewIngestMetrics(registry)

	validator, err := contracts.NewValidator()
	if err != nil {
		fatal(log, "compiling schemas", err)
	}
	log.Info("schemas compiled", "kinds", validator.Kinds())

	store, err := NewStore(ctx, cfg.databaseURL)
	if err != nil {
		fatal(log, "connecting to database", err)
	}
	defer store.Close()
	log.Info("connected to database")

	bus, err := NewBus(ctx, cfg.natsURL)
	if err != nil {
		fatal(log, "connecting to event log", err)
	}
	defer bus.Close()
	log.Info("event log ready", "stream", StreamName)

	in := NewIngest(cfg.broker, cfg.sites, validator, store, bus, metrics, log)

	srv := startHTTP(cfg.httpAddr, in, registry, log)
	defer func() {
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()
		_ = srv.Shutdown(shutdownCtx)
	}()

	if err := in.Run(ctx); err != nil {
		fatal(log, "ingest stopped", err)
	}
	log.Info("stopped")
}

// startHTTP exposes liveness, readiness and counters. Counters are the only way to see
// drops, and a drop nobody can see is the failure this pipeline most wants to avoid.
func startHTTP(addr string, in *Ingest, reg *prometheus.Registry, log *slog.Logger) *http.Server {
	mux := http.NewServeMux()

	// Prometheus alongside the hand-rolled JSON rather than replacing it. The JSON needs
	// no infrastructure to read, which is what makes it useful during a local failure.
	telemetry.ServeMetrics(mux, reg)

	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
		fmt.Fprintln(w, "ok")
	})

	mux.HandleFunc("/readyz", func(w http.ResponseWriter, _ *http.Request) {
		if err := in.Healthy(); err != nil {
			http.Error(w, err.Error(), http.StatusServiceUnavailable)
			return
		}
		w.WriteHeader(http.StatusOK)
		fmt.Fprintln(w, "ready")
	})

	mux.HandleFunc("/stats", func(w http.ResponseWriter, _ *http.Request) {
		c := in.counters
		w.Header().Set("Content-Type", "application/json")
		_ = json.NewEncoder(w).Encode(map[string]int64{
			"telemetry_received": c.TelemetryReceived.Load(),
			"status_received":    c.StatusReceived.Load(),
			"event_received":     c.EventReceived.Load(),
			"invalid":            c.Invalid.Load(),
			"dead_lettered":      c.DeadLettered.Load(),
			"telemetry_dropped":  c.TelemetryDropped.Load(),
			"batches_written":    c.BatchesWritten.Load(),
			"write_failures":     c.WriteFailures.Load(),
			"publish_failures":   c.PublishFailures.Load(),
			"queue_depth":        c.QueueDepth.Load(),
			"unacknowledged":     c.Unacknowledged.Load(),
		})
	})

	srv := &http.Server{Addr: addr, Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	go func() {
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("http server stopped", "err", err)
		}
	}()
	log.Info("http listening", "addr", addr)
	return srv
}

func parseConfig() config {
	var cfg config
	var sites string

	flag.StringVar(&cfg.broker, "broker", envStr("INGEST_BROKER", "tcp://localhost:1883"), "broker URL")
	flag.StringVar(&cfg.databaseURL, "database-url", envStr("INGEST_DATABASE_URL", "postgres://fleet:fleet@localhost:5432/fleet"), "TimescaleDB connection URL")
	flag.StringVar(&cfg.natsURL, "nats-url", envStr("INGEST_NATS_URL", "nats://localhost:4222"), "NATS URL")
	flag.StringVar(&cfg.httpAddr, "http-addr", envStr("INGEST_HTTP_ADDR", ":9101"), "address for health and stats endpoints")
	flag.StringVar(&sites, "sites", envStr("INGEST_SITES", ""), "comma-separated sites this instance owns; empty means the whole fleet")
	flag.Parse()

	for _, s := range strings.Split(sites, ",") {
		if s = strings.TrimSpace(s); s != "" {
			cfg.sites = append(cfg.sites, s)
		}
	}
	return cfg
}

func fatal(log *slog.Logger, msg string, err error) {
	log.Error(msg, "err", err)
	os.Exit(1)
}

func envStr(key, def string) string {
	if v, ok := os.LookupEnv(key); ok && v != "" {
		return v
	}
	return def
}
