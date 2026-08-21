#!/usr/bin/env python3
"""Kill each component in turn and check the fleet state still converges.

The delivery-semantics decision record makes a series of claims — that redelivery is safe,
that a reboot is not mistaken for a stale message, that nothing is acknowledged before it
is durable. It also says those claims are not credible as assertions and are verified by
these tests. This is that verification.

The technique throughout is to publish a uniquely identifiable message *during* an outage
and then prove it arrived once the component is back. Counting rows before and after does
not work: the simulated fleet keeps publishing throughout, so any total is a moving target
and a test built on one is a test that passes for the wrong reason.

    python tools/chaos_test.py

Requires the stack running with the full profile. Every scenario restores what it broke,
including on failure, so a red run leaves a working system behind.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from typing import Any, Callable

COMPOSE = ["docker", "compose", "-f", "deploy/compose.yaml", "--profile", "full"]
API = "http://localhost:8080"
INGEST = "http://localhost:9101"

# Probe devices carry a per-run id.
#
# Scenarios assert things like "this has not arrived yet", and a row left by an earlier run
# turns that into a false failure. Unique ids make each run independent; the sweep below
# stops probes accumulating into the fleet the tests are meant to be measuring, which they
# otherwise would, because the projection is seeded from the database on startup.
PROBE_PREFIX = "chaos-probe"
RUN_ID = str(int(time.time()))


def probe_device(kind: str) -> str:
    return f"{PROBE_PREFIX}-{kind}-{RUN_ID}"


def sweep_probes() -> None:
    """Remove every probe this suite has left behind, from any run."""
    for table in ("telemetry", "device_event", "device_status", "device"):
        try:
            psql(f"DELETE FROM {table} WHERE device_id LIKE '{PROBE_PREFIX}-%'")
        except Exception:
            pass


# ------------------------------------------------------------------------------ plumbing


def run(cmd: list[str], timeout: int = 180) -> subprocess.CompletedProcess[str]:
    return subprocess.run(cmd, capture_output=True, text=True, timeout=timeout)


def compose(*args: str, timeout: int = 300) -> subprocess.CompletedProcess[str]:
    return run(COMPOSE + list(args), timeout=timeout)


def get_json(url: str, timeout: int = 15) -> Any:
    with urllib.request.urlopen(url, timeout=timeout) as response:
        return json.loads(response.read().decode("utf-8"))


def api_ready() -> bool:
    try:
        with urllib.request.urlopen(f"{API}/readyz", timeout=5) as response:
            return response.status == 200
    except Exception:
        return False


def psql(sql: str) -> str:
    """Query the database directly.

    The reference state for every comparison comes from here rather than from the API. A
    projection checked against itself proves nothing; checked against the store that did
    not produce it, a divergence is real.
    """
    result = run(["docker", "exec", "fleet-timescaledb",
                  "psql", "-U", "fleet", "-d", "fleet", "-t", "-A", "-c", sql])
    if result.returncode != 0:
        raise RuntimeError(f"psql failed: {result.stderr.strip()}")
    return result.stdout.strip()


def publish(topic: str, payload: dict[str, Any], qos: int = 1, retain: bool = False) -> None:
    cmd = ["docker", "exec", "fleet-mosquitto", "mosquitto_pub",
           "-h", "localhost", "-q", str(qos), "-t", topic, "-m", json.dumps(payload)]
    if retain:
        cmd.append("-r")
    result = run(cmd, timeout=30)
    if result.returncode != 0:
        raise RuntimeError(f"publish to {topic} failed: {result.stderr.strip()}")


def clear_retained(topic: str) -> None:
    """Publish an empty retained message, which is how MQTT deletes one.

    Without this, probe devices linger in the broker forever and every later run inherits
    them — the tests would slowly poison the fleet they are meant to be checking.
    """
    run(["docker", "exec", "fleet-mosquitto", "mosquitto_pub",
         "-h", "localhost", "-q", "1", "-r", "-t", topic, "-n"], timeout=30)


def wait_for(predicate: Callable[[], bool], timeout: float, interval: float = 2.0,
             what: str = "condition") -> bool:
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            if predicate():
                return True
        except Exception:
            pass
        time.sleep(interval)
    print(f"       timed out after {timeout:.0f}s waiting for {what}")
    return False


def status_payload(device: str, boot_id: str, seq: int, online: bool = True,
                   reason: str = "connect", fw: str = "9.9.9") -> dict[str, Any]:
    return {
        "schema": "status/1",
        "device_id": device,
        "site": "site-00",
        "boot_id": boot_id,
        "seq": seq,
        "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "online": online,
        "reason": reason,
        "fw_version": fw,
        "model": "chaos-probe",
    }


def device_in_db(device: str) -> bool:
    return psql(f"SELECT count(*) FROM device_status WHERE device_id = '{device}'") != "0"


def device_in_projection(device: str) -> dict[str, Any] | None:
    try:
        return get_json(f"{API}/api/devices/{device}")
    except urllib.error.HTTPError:
        return None
    except Exception:
        return None


# -------------------------------------------------------------------------------- results


@dataclass
class Results:
    passed: list[str] = field(default_factory=list)
    failed: list[tuple[str, str]] = field(default_factory=list)
    skipped: list[tuple[str, str]] = field(default_factory=list)

    def ok(self, name: str, detail: str = "") -> None:
        print(f"  PASS  {name}{(' — ' + detail) if detail else ''}")
        self.passed.append(name)

    def fail(self, name: str, detail: str) -> None:
        print(f"  FAIL  {name} — {detail}")
        self.failed.append((name, detail))

    # Tracked separately from passes. A scenario that did not run has verified nothing, and
    # counting it as a pass would report false assurance — the failure mode this whole
    # suite exists to prevent.
    def skip(self, name: str, detail: str) -> None:
        print(f"  SKIP  {name} — {detail}")
        self.skipped.append((name, detail))


# ------------------------------------------------------------------------------ scenarios


def scenario_kill_ingest(r: Results) -> None:
    """A QoS 1 message published while ingest is down must not be lost.

    Ingest acknowledges the broker only after both the database write and the log publish
    succeed. While it is dead nothing is acknowledged, so the broker still holds the
    message and redelivers it on the next session.
    """
    name = "ingest killed mid-stream: QoS 1 message survives"
    device = probe_device("ingest")
    topic = f"fleet/site-00/{device}/status"

    try:
        compose("kill", "fleet-ingest")
        time.sleep(3)

        publish(topic, status_payload(device, "1111111111111111", 1), qos=1, retain=True)
        time.sleep(2)

        if device_in_db(device):
            r.fail(name, "the message reached the database while ingest was down")
            return

        compose("start", "fleet-ingest")

        if wait_for(lambda: device_in_db(device), timeout=90, what="the probe to be persisted"):
            r.ok(name, "redelivered and persisted after restart")
        else:
            r.fail(name, "the message never arrived after ingest returned")
    finally:
        compose("start", "fleet-ingest")
        clear_retained(topic)


def scenario_kill_api(r: Results) -> None:
    """Killing the API must not lose messages, and the projection must rebuild.

    The API acknowledges the log only after applying. A message in flight when it dies is
    therefore redelivered, and because apply is idempotent the redelivery is harmless.
    """
    name = "API killed mid-stream: projection rebuilds complete"
    device = probe_device("api")
    topic = f"fleet/site-00/{device}/status"

    try:
        before = get_json(f"{API}/stats")["aggregates"]["total"]

        compose("kill", "fleet-api")
        time.sleep(3)

        # Ingest is still alive, so this reaches the log while the API cannot consume it.
        publish(topic, status_payload(device, "2222222222222222", 1), qos=1, retain=True)
        time.sleep(3)

        compose("start", "fleet-api")

        if not wait_for(api_ready, timeout=120, what="the API to report ready"):
            r.fail(name, "the API never became ready again")
            return

        # Readiness means the backlog is drained, so the probe must be present already
        # rather than arriving later from live traffic.
        if device_in_projection(device) is None:
            r.fail(name, "the probe is missing from the projection after recovery")
            return

        after = get_json(f"{API}/stats")["aggregates"]["total"]
        if after < before:
            r.fail(name, f"the fleet shrank across the restart: {before} then {after}")
            return

        r.ok(name, f"{after} devices, probe present, readiness withheld until caught up")
    finally:
        compose("start", "fleet-api")
        wait_for(api_ready, timeout=120, what="the API to recover")
        clear_retained(topic)


def scenario_kill_log(r: Results) -> None:
    """With the log down, ingest must refuse to acknowledge rather than drop.

    This is the case where dropping would be invisible: the database write succeeds, so
    the data is not lost, but the projection would never see it.
    """
    name = "event log killed: ingest withholds acknowledgement"
    device = probe_device("log")
    topic = f"fleet/site-00/{device}/status"

    try:
        # Start from a clean session.
        #
        # The broker only allows a bounded number of unacknowledged QoS 1 messages in
        # flight — twenty by default — and stops delivering once that window is full. That
        # is the backpressure this design relies on, but it means a previous scenario can
        # leave ingest saturated, and a saturated ingest receives nothing new, so the
        # counter this scenario watches would never move. Restarting clears the window:
        # the broker redelivers, ingest acknowledges, and the counters reset.
        compose("restart", "fleet-ingest")
        if not wait_for(lambda: get_json(f"{INGEST}/stats")["status_received"] > 0,
                        timeout=90, interval=3, what="ingest to resume consuming"):
            r.fail(name, "ingest did not resume consuming after a restart")
            return

        before = get_json(f"{INGEST}/stats")["unacknowledged"]

        compose("kill", "nats")
        time.sleep(3)

        publish(topic, status_payload(device, "3333333333333333", 1), qos=1, retain=True)

        # A publish fails by hitting its context deadline, which is ten seconds, so nothing
        # can register before then. Poll rather than sleeping a fixed amount: a fixed wait
        # shorter than the deadline is a test that always fails, and one longer than needed
        # is dead time in every run.
        def withheld_grew() -> bool:
            return get_json(f"{INGEST}/stats")["unacknowledged"] > before

        if not wait_for(withheld_grew, timeout=90, interval=3,
                        what="ingest to withhold acknowledgement"):
            r.fail(name, "ingest acknowledged messages it could not publish")
            return

        withheld = get_json(f"{INGEST}/stats")["unacknowledged"] - before

        compose("start", "nats")

        if wait_for(lambda: device_in_projection(device) is not None,
                    timeout=150, what="the probe to reach the projection"):
            r.ok(name, f"{withheld} withheld during the outage, delivered after recovery")
        else:
            r.fail(name, "the probe never reached the projection after the log returned")
    finally:
        compose("start", "nats")
        clear_retained(topic)


def scenario_redelivery(r: Results) -> None:
    """Replaying the entire log must not change the projection.

    Deleting the API's durable consumer makes it replay from the beginning of the stream,
    which is the strongest available test of idempotency: every message it has already
    applied arrives a second time.
    """
    name = "full log replay: idempotent apply leaves state unchanged"

    try:
        before = get_json(f"{API}/stats")
        before_stale = before["stale_dropped"]
        before_total = before["aggregates"]["total"]

        result = run(["docker", "run", "--rm", "--network", "iot-fleet_default",
                       "natsio/nats-box:latest", "nats", "--server", "nats://nats:4222",
                       "consumer", "rm", "FLEET", "fleet-api", "-f"], timeout=180)
        if result.returncode != 0:
            r.skip(name, f"could not remove the consumer: {result.stderr.strip()[:120]}")
            return

        compose("restart", "fleet-api")
        if not wait_for(api_ready, timeout=180, what="the API to replay and become ready"):
            r.fail(name, "the API never became ready after replay")
            return

        after = get_json(f"{API}/stats")
        after_total = after["aggregates"]["total"]

        if after_total < before_total:
            r.fail(name, f"the fleet shrank across replay: {before_total} then {after_total}")
            return

        # Replaying already-applied messages must be *rejected*, not applied twice. A
        # stale-dropped count that did not move would mean the ordering rule never fired.
        if after["stale_dropped"] <= before_stale:
            r.fail(name, "replayed messages were not rejected as stale, so apply is not idempotent")
            return

        r.ok(name, f"{after['stale_dropped'] - before_stale} duplicates rejected, fleet intact")
    finally:
        compose("start", "fleet-api")
        wait_for(api_ready, timeout=180, what="the API to recover")


def scenario_device_reboot(r: Results) -> None:
    """A rebooted device restarts its sequence and must not be discarded as stale.

    This failed for real once: the simulator derived boot ids from its seed, so a restart
    kept the old boot id while the sequence reset, and the ordering rule correctly
    discarded an entire session.
    """
    name = "device reboot: sequence reset is accepted"
    device = probe_device("reboot")
    topic = f"fleet/site-00/{device}/status"

    try:
        publish(topic, status_payload(device, "aaaaaaaaaaaaaaaa", 500), qos=1, retain=True)
        if not wait_for(lambda: (device_in_projection(device) or {}).get("seq") == 500,
                        timeout=90, what="the pre-reboot state"):
            r.fail(name, "the probe never reached the projection before the reboot")
            return

        # A new boot id with a far lower sequence. Judged on sequence alone this is stale.
        publish(topic, status_payload(device, "bbbbbbbbbbbbbbbb", 1, fw="9.9.10"),
                qos=1, retain=True)

        def rebooted() -> bool:
            state = device_in_projection(device) or {}
            return state.get("boot_id") == "bbbbbbbbbbbbbbbb" and state.get("seq") == 1

        if wait_for(rebooted, timeout=90, what="the post-reboot state"):
            r.ok(name, "new boot id accepted with seq 1")
        else:
            state = device_in_projection(device) or {}
            r.fail(name, f"still showing boot_id={state.get('boot_id')} seq={state.get('seq')}")
    finally:
        clear_retained(topic)


def scenario_bounded_queue(r: Results) -> None:
    """Under load the queue must stay bounded and any drop must be counted.

    The failure this guards against is not dropping — telemetry is lossy by contract — but
    dropping *silently*, or growing without limit until the process dies.
    """
    name = "bounded queue: depth stays capped and drops are counted"
    capacity = 8192  # telemetryQueueSize in the ingest service

    try:
        depths: list[int] = []
        for _ in range(12):
            stats = get_json(f"{INGEST}/stats")
            depths.append(stats["queue_depth"])
            time.sleep(1)

        peak = max(depths)
        if peak > capacity:
            r.fail(name, f"queue depth reached {peak}, above the {capacity} bound")
            return

        stats = get_json(f"{INGEST}/stats")
        metrics = urllib.request.urlopen(f"{INGEST}/metrics", timeout=15).read().decode()

        # Whether anything was dropped depends on load, but the counter must exist either
        # way: a drop that is not exported is a drop nobody can see.
        if "fleet_ingest_telemetry_dropped_total" not in metrics:
            r.fail(name, "the drop counter is not exported")
            return

        r.ok(name, f"peak depth {peak} of {capacity}, {stats['telemetry_dropped']} drops counted")
    except Exception as err:  # noqa: BLE001
        r.fail(name, str(err))


# ------------------------------------------------------------------------------------ run


SCENARIOS: list[tuple[str, Callable[[Results], None]]] = [
    ("device-reboot", scenario_device_reboot),
    ("bounded-queue", scenario_bounded_queue),
    ("kill-ingest", scenario_kill_ingest),
    ("kill-log", scenario_kill_log),
    ("kill-api", scenario_kill_api),
    ("redelivery", scenario_redelivery),
]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--only", help="run one scenario by name")
    parser.add_argument("--list", action="store_true", help="list scenario names")
    args = parser.parse_args()

    if args.list:
        for scenario_name, _ in SCENARIOS:
            print(scenario_name)
        return 0

    if not api_ready():
        print("the API is not ready; start the stack with --profile full first")
        return 2

    selected = [s for s in SCENARIOS if args.only is None or s[0] == args.only]
    if not selected:
        print(f"no scenario named {args.only}")
        return 2

    print("chaos scenarios\n")
    results = Results()
    for scenario_name, scenario in selected:
        print(f"  ---- {scenario_name}")
        try:
            scenario(results)
        except Exception as err:  # noqa: BLE001 - reported, never raised
            results.fail(scenario_name, f"raised {type(err).__name__}: {err}")
        print()

    sweep_probes()

    total = len(results.passed) + len(results.failed) + len(results.skipped)

    if results.failed:
        print(f"{len(results.failed)} of {total} scenarios failed:")
        for failed_name, detail in results.failed:
            print(f"  - {failed_name}: {detail}")
        return 1

    if results.skipped:
        # Reported prominently rather than buried. A suite that quietly skips is worse than
        # one that fails, because it still prints a reassuring summary.
        print(f"{len(results.passed)} passed, {len(results.skipped)} SKIPPED of {total}:")
        for skipped_name, detail in results.skipped:
            print(f"  - {skipped_name}: {detail}")
        return 1

    print(f"all {total} scenarios passed")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
