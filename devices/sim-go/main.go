// Command sim-go simulates a fleet of embedded devices publishing MQTT telemetry.
//
// Each device gets its own MQTT session, client id and TCP connection. Connections are
// never shared, because sharing them would make the connection-scale problem disappear
// along with most of the realism.
//
// Current scope: telemetry, retained status and Last Will. The scenario engine, fault
// injection and Prometheus metrics are not implemented yet.
package main

import (
	"context"
	"flag"
	"fmt"
	"log/slog"
	"math/rand"
	"os"
	"os/signal"
	"strings"
	"sync"
	"syscall"
	"time"
)

type config struct {
	metricsAddr  string
	profile      string
	broker       string
	devices      int
	sites        int
	rate         time.Duration
	seed         int64
	flapPct      float64
	flapInterval time.Duration
	faultPct     float64
}

func main() {
	cfg := parseConfig()

	log := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(log)

	log.Info("starting fleet simulator",
		"profile", cfg.profile,
		"broker", cfg.broker,
		"devices", cfg.devices,
		"sites", cfg.sites,
		"rate", cfg.rate,
		"seed", cfg.seed,
		"fault_pct", cfg.faultPct,
	)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// One seeded source per device. Sharing a single *rand.Rand across goroutines would
	// need a mutex on a hot path and would make runs non-reproducible.
	seeder := rand.New(rand.NewSource(cfg.seed))

	// A fleet that is not publishing looks identical to a broker that is not receiving,
	// until both sides can be seen at once.
	registry := NewRegistry()
	metrics := NewSimMetrics(registry)
	metricsSrv := serveMetrics(cfg.metricsAddr, registry, log)
	defer func() {
		shutdownCtx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()
		_ = metricsSrv.Shutdown(shutdownCtx)
	}()

	fleet := make([]*Device, 0, cfg.devices)
	for i := 0; i < cfg.devices; i++ {
		site := fmt.Sprintf("site-%02d", i%cfg.sites)
		rng := rand.New(rand.NewSource(seeder.Int63()))
		d := NewDevice(i, site, cfg.broker, cfg.rate, cfg.faultPct, rng)
		d.metrics = metrics
		fleet = append(fleet, d)
	}
	metrics.Devices.Set(float64(len(fleet)))

	var wg sync.WaitGroup
	for _, d := range fleet {
		wg.Add(1)
		go func(d *Device) {
			defer wg.Done()
			if err := d.Run(ctx); err != nil {
				log.Error("device stopped", "device", d.ID, "err", err)
			}
		}(d)

		// Stagger connection setup. A thousand simultaneous TCP handshakes is a
		// thundering herd that makes startup look like a broker fault.
		time.Sleep(2 * time.Millisecond)
	}

	log.Info("fleet connected", "devices", len(fleet))

	if cfg.flapPct > 0 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			runFlapper(ctx, fleet, cfg, rand.New(rand.NewSource(seeder.Int63())), log)
		}()
	}

	<-ctx.Done()
	log.Info("shutting down, publishing offline status")

	// Devices publish a graceful offline status on shutdown. Give them a bounded window
	// rather than waiting indefinitely on a broker that may itself be going away.
	done := make(chan struct{})
	go func() {
		wg.Wait()
		close(done)
	}()
	select {
	case <-done:
	case <-time.After(10 * time.Second):
		log.Warn("shutdown timed out waiting for devices")
	}
	log.Info("stopped")
}

// runFlapper periodically drops a percentage of devices' TCP connections without sending
// DISCONNECT, so the broker publishes their Last Will, which exercises presence detection
// without killing the container.
func runFlapper(ctx context.Context, fleet []*Device, cfg config, rng *rand.Rand, log *slog.Logger) {
	ticker := time.NewTicker(cfg.flapInterval)
	defer ticker.Stop()

	n := int(float64(len(fleet)) * cfg.flapPct / 100)
	if n < 1 {
		n = 1
	}

	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
			for i := 0; i < n; i++ {
				d := fleet[rng.Intn(len(fleet))]
				d.Flap()
				if d.metrics != nil {
					d.metrics.Flaps.Inc()
				}
				log.Info("flapped device", "device", d.ID)
			}
		}
	}
}

// parseConfig resolves settings in three layers: a named profile supplies defaults, then
// SIM_* environment variables override it, then command-line flags override those. A
// profile is a starting point, never a straitjacket.
func parseConfig() config {
	profileName := envStr("SIM_PROFILE", "dev")
	// The profile has to be resolved before flag defaults are computed, so it is read
	// from the raw arguments rather than from the flag package.
	for i, a := range os.Args[1:] {
		if a == "--profile" || a == "-profile" {
			if i+1 < len(os.Args)-1 {
				profileName = os.Args[i+2]
			}
		} else if strings.HasPrefix(a, "--profile=") {
			profileName = strings.TrimPrefix(a, "--profile=")
		} else if strings.HasPrefix(a, "-profile=") {
			profileName = strings.TrimPrefix(a, "-profile=")
		}
	}

	p, err := ProfileByName(profileName)
	if err != nil {
		fatalf("%v", err)
	}

	cfg := config{profile: p.Name}
	flag.StringVar(&cfg.profile, "profile", p.Name, "fleet profile: "+strings.Join(ProfileNames(), ", "))
	flag.StringVar(&cfg.broker, "broker", envStr("SIM_BROKER", "tcp://localhost:1883"), "broker URL")
	flag.StringVar(&cfg.metricsAddr, "metrics-addr", envStr("SIM_METRICS_ADDR", ":9102"), "address for the metrics endpoint")
	flag.IntVar(&cfg.devices, "devices", envInt("SIM_DEVICES", p.Devices), "number of simulated devices")
	flag.IntVar(&cfg.sites, "sites", envInt("SIM_SITES", p.Sites), "number of sites to spread devices across")
	flag.DurationVar(&cfg.rate, "rate", envDur("SIM_RATE", p.Rate), "telemetry interval per device")
	flag.Int64Var(&cfg.seed, "seed", int64(envInt("SIM_SEED", 1)), "RNG seed; the same seed reproduces a run")
	flag.Float64Var(&cfg.flapPct, "flap-pct", envFloat("SIM_FLAP_PCT", p.FlapPct), "percent of devices to drop ungracefully each interval")
	flag.DurationVar(&cfg.flapInterval, "flap-interval", envDur("SIM_FLAP_INTERVAL", p.FlapInterval), "how often to flap devices")
	flag.Float64Var(&cfg.faultPct, "fault-pct", envFloat("SIM_FAULT_PCT", p.FaultPct), "percent chance per device per tick of injecting a fault")
	flag.Parse()

	if cfg.devices < 1 {
		fatalf("--devices must be at least 1, got %d", cfg.devices)
	}
	if cfg.sites < 1 {
		fatalf("--sites must be at least 1, got %d", cfg.sites)
	}
	if cfg.rate <= 0 {
		fatalf("--rate must be positive, got %s", cfg.rate)
	}
	if cfg.flapPct < 0 || cfg.flapPct > 100 {
		fatalf("--flap-pct must be between 0 and 100, got %v", cfg.flapPct)
	}
	if cfg.faultPct < 0 || cfg.faultPct > 100 {
		fatalf("--fault-pct must be between 0 and 100, got %v", cfg.faultPct)
	}
	return cfg
}

func fatalf(format string, args ...any) {
	fmt.Fprintf(os.Stderr, "sim-go: "+format+"\n", args...)
	os.Exit(2)
}

func envStr(key, def string) string {
	if v, ok := os.LookupEnv(key); ok && v != "" {
		return v
	}
	return def
}

func envInt(key string, def int) int {
	v, ok := os.LookupEnv(key)
	if !ok || v == "" {
		return def
	}
	var out int
	if _, err := fmt.Sscanf(v, "%d", &out); err != nil {
		fatalf("%s must be an integer, got %q", key, v)
	}
	return out
}

func envFloat(key string, def float64) float64 {
	v, ok := os.LookupEnv(key)
	if !ok || v == "" {
		return def
	}
	var out float64
	if _, err := fmt.Sscanf(v, "%g", &out); err != nil {
		fatalf("%s must be a number, got %q", key, v)
	}
	return out
}

func envDur(key string, def time.Duration) time.Duration {
	v, ok := os.LookupEnv(key)
	if !ok || v == "" {
		return def
	}
	out, err := time.ParseDuration(v)
	if err != nil {
		fatalf("%s must be a duration such as 500ms or 2s, got %q", key, v)
	}
	return out
}
