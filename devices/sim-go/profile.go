package main

import (
	"fmt"
	"sort"
	"time"
)

// Profile is a named fleet shape. Profiles set defaults only: any value explicitly given
// as a flag or environment variable wins, so a profile is a starting point rather than a
// straitjacket.
type Profile struct {
	Name         string
	Devices      int
	Sites        int
	Rate         time.Duration
	FlapPct      float64
	FlapInterval time.Duration

	// FaultPct is the chance, per device per telemetry tick, of injecting a fault.
	// Small numbers matter here: at 1 Hz with 1000 devices, 0.05% is roughly one fault
	// every two seconds somewhere in the fleet.
	FaultPct float64
}

var profiles = map[string]Profile{
	// Fast iteration. Small enough that Compose is up in seconds and the logs are
	// readable, faulty enough that the interesting paths still get exercised.
	"dev": {
		Name: "dev", Devices: 200, Sites: 4, Rate: time.Second,
		FlapPct: 2, FlapInterval: 20 * time.Second, FaultPct: 0.2,
	},
	// The headline configuration, and the one all published measurements use.
	"demo": {
		Name: "demo", Devices: 1000, Sites: 8, Rate: time.Second,
		FlapPct: 1, FlapInterval: 30 * time.Second, FaultPct: 0.05,
	},
	// Finds the ceilings. Intended to be run across several simulator replicas, since a
	// single container will hit file-descriptor and ephemeral-port limits first.
	"stress": {
		Name: "stress", Devices: 10000, Sites: 32, Rate: time.Second,
		FlapPct: 0.5, FlapInterval: time.Minute, FaultPct: 0.01,
	},
}

func ProfileByName(name string) (Profile, error) {
	p, ok := profiles[name]
	if !ok {
		return Profile{}, fmt.Errorf("unknown profile %q, expected one of %v", name, ProfileNames())
	}
	return p, nil
}

func ProfileNames() []string {
	out := make([]string, 0, len(profiles))
	for k := range profiles {
		out = append(out, k)
	}
	sort.Strings(out)
	return out
}
