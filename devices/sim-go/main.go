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
	"sync"
	"syscall"
	"time"
)

type config struct {
	broker       string
	devices      int
	sites        int
	rate         time.Duration
	seed         int64
	flapPct      float64
	flapInterval time.Duration
}

func main() {
	cfg := parseConfig()

	log := slog.New(slog.NewTextHandler(os.Stdout, &slog.HandlerOptions{Level: slog.LevelInfo}))
	slog.SetDefault(log)

	log.Info("starting fleet simulator",
		"broker", cfg.broker,
		"devices", cfg.devices,
		"sites", cfg.sites,
		"rate", cfg.rate,
		"seed", cfg.seed,
	)

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	// One seeded source per device. Sharing a single *rand.Rand across goroutines would
	// need a mutex on a hot path and would make runs non-reproducible.
	seeder := rand.New(rand.NewSource(cfg.seed))

	fleet := make([]*Device, 0, cfg.devices)
	for i := 0; i < cfg.devices; i++ {
		site := fmt.Sprintf("site-%02d", i%cfg.sites)
		rng := rand.New(rand.NewSource(seeder.Int63()))
		fleet = append(fleet, NewDevice(i, site, cfg.broker, cfg.rate, rng))
	}

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
				log.Info("flapped device", "device", d.ID)
			}
		}
	}
}

// parseConfig reads flags, falling back to SIM_* environment variables so the same
// binary is configured identically from a shell and from Compose.
func parseConfig() config {
	cfg := config{}
	flag.StringVar(&cfg.broker, "broker", envStr("SIM_BROKER", "tcp://localhost:1883"), "broker URL")
	flag.IntVar(&cfg.devices, "devices", envInt("SIM_DEVICES", 100), "number of simulated devices")
	flag.IntVar(&cfg.sites, "sites", envInt("SIM_SITES", 4), "number of sites to spread devices across")
	flag.DurationVar(&cfg.rate, "rate", envDur("SIM_RATE", time.Second), "telemetry interval per device")
	flag.Int64Var(&cfg.seed, "seed", int64(envInt("SIM_SEED", 1)), "RNG seed; the same seed reproduces a run")
	flag.Float64Var(&cfg.flapPct, "flap-pct", envFloat("SIM_FLAP_PCT", 0), "percent of devices to drop ungracefully each interval")
	flag.DurationVar(&cfg.flapInterval, "flap-interval", envDur("SIM_FLAP_INTERVAL", 30*time.Second), "how often to flap devices")
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
