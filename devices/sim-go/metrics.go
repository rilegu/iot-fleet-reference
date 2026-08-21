package main

import (
	"errors"
	"log/slog"
	"net/http"
	"time"

	"github.com/prometheus/client_golang/prometheus"
	"github.com/rilegu/iot-fleet-reference/telemetry"
)

// SimMetrics reports what the fleet is doing, from the fleet's own side.
//
// Without this, the only account of how many messages were published is the ingest side's
// count of how many arrived — so a discrepancy between them would be invisible, and a
// discrepancy between the two is exactly what message loss looks like.
type SimMetrics struct {
	Published *prometheus.CounterVec
	Faults    *prometheus.CounterVec
	Flaps     prometheus.Counter

	Connected prometheus.Gauge
	Devices   prometheus.Gauge

	PublishErrors *prometheus.CounterVec
}

func NewSimMetrics(reg *prometheus.Registry) *SimMetrics {
	m := &SimMetrics{
		Published: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "sim", Name: "published_total",
			Help: "Messages published by the fleet, by kind.",
		}, []string{"kind"}),

		Faults: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "sim", Name: "faults_injected_total",
			Help: "Faults injected into devices, by kind.",
		}, []string{"kind"}),

		Flaps: prometheus.NewCounter(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "sim", Name: "flaps_total",
			Help: "Connections dropped ungracefully so the broker publishes a Last Will.",
		}),

		Connected: prometheus.NewGauge(prometheus.GaugeOpts{
			Namespace: "fleet", Subsystem: "sim", Name: "devices_connected",
			Help: "Devices currently holding an MQTT session.",
		}),

		Devices: prometheus.NewGauge(prometheus.GaugeOpts{
			Namespace: "fleet", Subsystem: "sim", Name: "devices_total",
			Help: "Devices this simulator instance is running.",
		}),

		PublishErrors: prometheus.NewCounterVec(prometheus.CounterOpts{
			Namespace: "fleet", Subsystem: "sim", Name: "publish_errors_total",
			Help: "Publishes that timed out or failed, by kind.",
		}, []string{"kind"}),
	}

	reg.MustRegister(m.Published, m.Faults, m.Flaps, m.Connected, m.Devices, m.PublishErrors)
	return m
}

// serveMetrics exposes the registry. The simulator has no other HTTP surface, so this is
// its only endpoint; it is still worth having, because a fleet that is not publishing looks
// identical to a broker that is not receiving until you can see both sides.
func serveMetrics(addr string, reg *prometheus.Registry, log *slog.Logger) *http.Server {
	mux := http.NewServeMux()
	telemetry.ServeMetrics(mux, reg)
	mux.HandleFunc("/healthz", func(w http.ResponseWriter, _ *http.Request) {
		w.WriteHeader(http.StatusOK)
	})

	srv := &http.Server{Addr: addr, Handler: mux, ReadHeaderTimeout: 5 * time.Second}
	go func() {
		if err := srv.ListenAndServe(); err != nil && !errors.Is(err, http.ErrServerClosed) {
			log.Error("metrics server stopped", "err", err)
		}
	}()
	log.Info("metrics listening", "addr", addr)
	return srv
}

// NewRegistry returns a registry carrying the process and runtime collectors.
func NewRegistry() *prometheus.Registry { return telemetry.NewRegistry() }
