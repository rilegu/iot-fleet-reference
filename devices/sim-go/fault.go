package main

import "math/rand"

// Fault is an injectable device malfunction: what it is called on the wire, how serious it
// is, and how it perturbs the device's sensor state.
//
// Faults exist so the pipeline downstream has something to detect other than a steady
// stream of healthy samples. A fleet console that has only ever seen healthy devices has
// not been tested.
type Fault struct {
	Kind     string
	Severity string
	Detail   string

	// Metric named in the emitted event, and read to populate its value. Empty when the
	// fault is not about a single metric.
	Metric string

	// apply perturbs device state. It runs under the device's rng lock.
	apply func(d *Device, r *rand.Rand)
}

// AllFaults returns every injectable fault. Kind values must stay in step with the enum in
// contracts/schemas/event.json; the schema tests fail if they drift.
func AllFaults() []Fault {
	return []Fault{
		{
			Kind:     "sensor_fault",
			Severity: "critical",
			Detail:   "temperature sensor reading implausible value",
			Metric:   "temp_c",
			// A stuck or shorted sensor pins the reading at a rail rather than drifting.
			apply: func(d *Device, r *rand.Rand) { d.tempC = 84 + r.Float64() },
		},
		{
			Kind:     "brownout",
			Severity: "warning",
			Detail:   "supply voltage dipped below nominal",
			Metric:   "voltage_v",
			apply:    func(d *Device, r *rand.Rand) { d.voltageV = 10.6 + r.Float64()*0.3 },
		},
		{
			Kind:     "threshold_breach",
			Severity: "warning",
			Detail:   "temperature above configured limit",
			Metric:   "temp_c",
			apply:    func(d *Device, r *rand.Rand) { d.tempC = 60 + r.Float64()*15 },
		},
		{
			Kind:     "network_degraded",
			Severity: "info",
			Detail:   "signal strength degraded",
			Metric:   "rssi_dbm",
			apply:    func(d *Device, r *rand.Rand) { d.rssiDBm = -92 + r.Float64()*5 },
		},
		{
			Kind:     "clock_step",
			Severity: "warning",
			Detail:   "device clock stepped; timestamps before this point are unreliable",
			// No metric: this is exactly the condition that makes ts unusable for
			// ordering, which is why the contract orders by (boot_id, seq) instead.
			apply: func(d *Device, r *rand.Rand) { d.clockSkew = skewChoices[r.Intn(len(skewChoices))] },
		},
	}
}

// Clock steps a real device might take: a jump forward after NTP sync from a dead RTC, or
// backwards when a stale RTC is trusted over the network.
var skewChoices = []float64{-3600, -90, 45, 7200}

func pickFault(r *rand.Rand) Fault {
	faults := AllFaults()
	return faults[r.Intn(len(faults))]
}
