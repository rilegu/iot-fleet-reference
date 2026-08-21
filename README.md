# iot-fleet-reference

A reference implementation of an IoT fleet management system, end to end: a simulated fleet
of a thousand embedded devices publishing MQTT telemetry, a distributed ingest and API tier
that turns that stream into queryable fleet state, and a live operations dashboard —
implemented several times over, once in each of five UI frameworks, against a single shared
contract.

It exists to answer two questions that most telemetry examples skip:

1. **How do you keep a dashboard responsive when a thousand devices are all reporting at
   once?** Pushing raw telemetry to a UI implies around a million row-updates per second and
   no framework survives it. The answer is a server-side state projection and a coalesced
   delta protocol.
2. **How do you build a telemetry pipeline that stays correct when things crash?**
   Ingest and API are separate services with a durable log between them, so duplicate
   delivery, reordering, and process death mid-message are real failure modes with real
   answers — specified, implemented, and verified by a chaos suite in CI.

Along the way it doubles as a controlled comparison: the same dashboard, the same data, the
same load, built in **Blazor, WinUI 3, WPF, Qt/QML, and Electron**, with Flutter added last
to prove that a new UI framework requires no backend change.

## Status

The system works end to end: a simulated fleet publishes over MQTT, ingest validates every
payload against the schemas in `contracts/` and writes it to TimescaleDB and a durable NATS
JetStream log, the API projects that log into live fleet state served over REST and a
coalesced WebSocket channel, and four dashboards — Blazor, WinUI 3, WPF and Electron — render
a thousand live devices from it. The comparison has started to pay: the XAML pair run the same
ViewModels unchanged and differ only in dialect and dispatcher, while the Electron client
shares no code with them at all and re-implements the reconciler in TypeScript against the
same tests. Qt and Flutter are not built.

**Legend:** ✅ working · 🚧 in progress · ⬜ not started

### Fleet and data path

| Component | Status | Notes |
|---|:--:|---|
| MQTT broker (Mosquitto) | ✅ | Plaintext and anonymous; TLS and ACLs not yet |
| Device simulator (Go) | ✅ | One session, client id and TCP connection per device; Last Will, retained status, seeded reproducible runs, connection flapping |
| Message contract | ✅ | JSON Schema (draft 2020-12) in [`contracts/schemas/`](contracts/schemas/), enforced at the ingest boundary and asserted against simulator output in tests |
| Scenario engine | 🚧 | Named profiles and fault injection work; lifecycle events and scenario files do not |
| Ingest service (Go) | ✅ | Schema validation, bounded queues with counted drops, batched writes, dead-lettering |
| Telemetry history (TimescaleDB) | ✅ | Hypertable, 1-minute continuous aggregates, retention and compression policies |
| Event log (NATS JetStream) | ✅ | Durable and replayable; carries the payload verbatim with ingest metadata in headers |
| Fleet API (REST + WebSocket) | ✅ | Log-driven projection with `(boot_id, seq)` ordering, replay on restart, REST queries, history from continuous aggregates |
| Snapshot/delta protocol | 🚧 | Snapshot plus coalesced per-device deltas at a client-capped cadence; field-level deltas and per-connection backpressure still to come |
| Shared .NET client state core | ✅ | Transport, reconnect with jittered backoff, frame reconciliation — consumed directly by Blazor, and by the XAML clients through ViewModels |
| Device agent (C99) | ⬜ | Constrained-device implementation of the same contract |

### Dashboards

| Client | Status | Notes |
|---|:--:|---|
| Blazor | ✅ | Virtualized grid over the shared .NET state core, device detail with history and events |
| WinUI 3 | ✅ | `x:Bind` over the shared ViewModels, `ItemsRepeater` virtualization, device detail with history and events |
| WPF | ✅ | The same ViewModels unchanged, detail panel included; only the dispatcher and XAML dialect differ |
| Qt / QML | ⬜ | |
| Electron | ✅ | React over an external store, `@tanstack/react-virtual` grid, device detail with history and events; no code shared with the .NET clients, and the same reconciliation tests |
| Flutter | ⬜ | Added last, to show a new framework needs no backend change |

### Cross-cutting

| Concern | Status | Notes |
|---|:--:|---|
| Projection replay on restart | ✅ | Durable log consumer; readiness withheld until the backlog is drained |
| API contract (OpenAPI + AsyncAPI) | ✅ | `contracts/openapi.yaml` and `contracts/asyncapi.yaml`; the realtime frames reference the same definitions as the REST responses |
| Contract conformance + CI | ✅ | CI builds everything, runs every test under the race detector, brings up the stack and validates the running API against the contract |
| TLS, per-device identity, broker ACLs | ⬜ | |
| API authentication, authorization, audit | ⬜ | |
| Distributed tracing (OpenTelemetry) | ✅ | One trace spans device to dashboard; the device's W3C context travels in the payload because MQTT 3.1.1 has no header for it |
| Metrics and dashboards | ✅ | Prometheus scrapes the simulator, ingest and API; Grafana and Jaeger under an `observability` profile |
| Projection checkpoint and replay | ⬜ | |
| Chaos suite | ✅ | Six scenarios in CI: kill ingest, the log or the API mid-stream, replay the whole log, reboot a device, and load the bounded queue |
| Device conformance suite (Python) | ⬜ | |
| Scale testing to 10 000 devices | ⬜ | |
| Published UI comparison | ⬜ | |

---

## How it works

The data path, end to end:

```
 1. fleet-sim runs 1000 independent device state machines, each with its own
    MQTT client id, credential and TCP connection. Devices boot, report, drift,
    fault, drop offline and reconnect according to a scenario file.
                                  |
                                  |  MQTT 3.1.1
                                  v
 2. mosquitto authenticates each device against its own credential and enforces
    an ACL: device X can publish only under fleet/{site}/X/# and can subscribe
    only to its own command topic.
                                  |
                                  v
 3. fleet-ingest subscribes across the fleet, validates every payload against
    JSON Schema, normalizes it, and writes it to two places:
       - TimescaleDB, as durable history
       - NATS JetStream, as a replayable event log
    Malformed messages are counted and dead-lettered, never trusted.
                                  |
                                  v
 4. fleet-api consumes the event log and maintains an in-memory projection of
    current fleet state. The projection is a cache, not a source of truth: on
    restart it loads its last checkpoint and replays the log forward.
                                  |
                                  v
 5. Dashboards connect over WebSocket. Each receives one snapshot, then a
    coalesced delta frame every 250 ms containing only what changed, plus
    precomputed fleet aggregates. Slow clients get fewer, larger frames rather
    than a growing backlog.
```

**Why step 5 matters.** A thousand devices at 1 Hz is a thousand messages per second *per
connected dashboard*. Delivered naively against a thousand-row grid, that is roughly 10⁶
row-renders per second. With coalescing at 4 Hz and row virtualization, a client renders
about forty rows four times a second. The load is bounded by viewport and cadence rather
than by fleet size — which is also what makes comparing the frameworks meaningful, since
none of them is drowning in transport overhead.

## Architecture

```
+- Linux containers (Docker) ---------------------------------+
|                                                             |
|   fleet-sim (Go)  --+                                       |
|   1000 devices      |                                       |
|   1 conn/device     +-- MQTT 3.1.1 -->  mosquitto           |
|                     |                   :1883 / :8883       |
|   fleet-agent-c  ---+                        |              |
|   1..5 "real" C99 devices                    v              |
|                                     fleet-ingest (Go)       |
|                                     validate - normalize    |
|                                        |          |         |
|                                        v          v         |
|                                 timescaledb    nats         |
|                                  (history)  (event log)     |
|                                        |          |         |
|                                        +----+-----+         |
|                                             v               |
|                              fleet-api (.NET 10)            |
|                              projection - REST - WS - authz |
+---------------------------------------+---------------------+
                                        | :8080
+---------------------------------------+---------------------+
| Windows host (native)                 v                     |
|                                                             |
|   WinUI 3    WPF    Qt/QML    Electron    (Flutter)         |
|      +--------+                                             |
|      Fleet.Client.Core (shared .NET state core)             |
|                                                             |
|   Blazor - served from the container, rendered in a browser |
+-------------------------------------------------------------+
```

Everything except the dashboards runs in Linux containers — which is also how real devices
and real backends run. There is exactly one platform boundary, and it is the same for every
client.

Full detail in [docs/architecture.md](docs/architecture.md).

## Design decisions

Each of these is a recorded decision with its context, alternatives and consequences:

| Decision | In short |
|---|---|
| [REST + WebSocket is the only UI boundary](docs/adr/0001-contract-first-ui-boundary.md) | Every framework has a mature HTTP and WebSocket client. SignalR would privilege .NET; gRPC complicates browsers and Qt; direct MQTT would force every client to reimplement fleet state. |
| [Ingest and API are separate services](docs/adr/0002-ingest-api-split.md) | Independent restart and scaling, and partitioned consumers at 10 000 devices. States plainly that a single process suffices at 1000. |
| [TimescaleDB for history](docs/adr/0003-telemetry-storage.md) | Hypertables and continuous aggregates precompute dashboard rollups. Telemetry and device metadata stay in one relational database. |
| [Go for the device simulator](docs/adr/0004-simulator-language.md) | A goroutine per device, a ~15 MB image, and scale-out by container. A Rust implementation can replace it by passing the same conformance suite. |
| [Mosquitto and MQTT 3.1.1](docs/adr/0005-broker-and-protocol.md) | The version real embedded clients ship. Presence via retained status plus Last Will, so a device that dies without disconnecting still goes offline. |
| [One state core, per-ecosystem idioms](docs/adr/0006-shared-client-state-core.md) | Code reuse is bounded by the runtime. .NET clients share a state core; the others share the contract and a behavioural spec, not code. |
| [Linux containers, Windows dashboards](docs/adr/0007-linux-containers-windows-dashboards.md) | Only the dashboards need Windows. One `docker compose up` brings up the entire backend and fleet. |
| [Delivery semantics and recovery](docs/adr/0008-delivery-semantics-and-projection-recovery.md) | At-least-once everywhere it matters, idempotent consumers, ordering by sequence rather than clock, checkpoint-and-replay recovery, bounded queues with declared drop policies. |

Two decisions carry most of the weight. **The contract is the coupling point** — OpenAPI,
AsyncAPI and JSON Schema in `contracts/`, from which every client's models are
generated, with CI failing on drift. That is what makes adding a UI framework a day of work.
And **ordering is by sequence number, never by timestamp** — device clocks drift, devices
reboot without a real-time clock, and container clocks drift from host clocks, so timestamps
are treated as data rather than control.

## Running it

**Requirements:** Docker with Compose. The whole backend and the Blazor dashboard run in
containers; the .NET 10 SDK is needed only to build the WinUI 3 and WPF clients, which are
Windows desktop applications and are not containerised.

From the repository root:

```bash
docker compose -f deploy/compose.yaml --profile full up -d --build
```

That brings up the broker, a simulated fleet, TimescaleDB, NATS JetStream, the ingest
service, the API and the dashboard:

```
http://localhost:8090     dashboard
http://localhost:8080     API
http://localhost:9101     ingest counters
```

Tracing and dashboards are a separate profile, off by default because they cost four more
containers and roughly a gigabyte of memory:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=otel-collector:4317 OTEL_GRPC_ENDPOINT=http://otel-collector:4317   docker compose -f deploy/compose.yaml --profile full --profile observability up -d
```

```
http://localhost:16686    traces (Jaeger)
http://localhost:9090     metrics (Prometheus)
http://localhost:3000     dashboards (Grafana)
```

A single trace spans device to dashboard. The device's W3C context travels inside the
message payload, because MQTT 3.1.1 has no header to carry it, and ingest lifts it back out
so its span joins the same trace rather than starting a new one. Without the endpoint
variables set, both services log that tracing is disabled and run exactly as before —
observability failing is not an outage of the thing being observed.

Omit `--profile full` to leave the API and dashboard out and run either on your machine with
a debugger attached:

```bash
docker compose -f deploy/compose.yaml up -d              # infrastructure only
dotnet run --project services/api                        # http://localhost:5200
dotnet run --project clients/dotnet/blazor               # http://localhost:5300
```

The API refuses traffic until it has replayed the log, so `readyz` is the signal to watch,
not `healthz` — a restarting instance is alive long before it can serve a complete fleet:

```bash
curl -s http://localhost:8080/readyz
curl -s http://localhost:8080/stats
curl -s "http://localhost:8080/api/fleet?limit=3"
curl -s "http://localhost:8080/api/devices/dev-000000/history?minutes=30"
curl -s "http://localhost:8080/api/events?limit=5"
```

`stale_dropped` counts messages the projection rejected as duplicates or reorderings, and
`gaps` counts detected sequence jumps. Both should sit near zero on a healthy pipeline.

The realtime channel is a WebSocket at `/ws/fleet`. It sends one snapshot, then coalesced
deltas containing only devices that changed:

```bash
# a client may request a slower cadence, never a faster one
# {"type":"subscribe","max_rate_hz":4}
```

Ingest exposes its own counters separately:

```bash
curl -s http://localhost:9101/stats
```

`invalid`, `telemetry_dropped`, `write_failures`, `publish_failures` and `unacknowledged`
should all stay at zero. Every drop is counted rather than silent, because silent loss is
what makes a dashboard confidently wrong.

`unacknowledged` is the one to watch: it counts QoS 1 messages ingest deliberately refused
to acknowledge because it could not persist them. Those are not lost — the broker
redelivers them on the next session — but a rising value means the pipeline is failing to
keep up with state transitions.

Querying what landed:

```bash
docker exec fleet-timescaledb psql -U fleet -d fleet -c   "SELECT count(*) FROM telemetry;"

docker exec fleet-timescaledb psql -U fleet -d fleet -c   "SELECT kind, severity, count(*) FROM device_event GROUP BY 1,2 ORDER BY 3 DESC;"

# Dashboard rollups come from continuous aggregates, not raw scans
docker exec fleet-timescaledb psql -U fleet -d fleet -c   "SELECT bucket, site, devices_reporting, samples FROM fleet_1m ORDER BY bucket DESC LIMIT 8;"
```

A small percentage of devices are dropped ungracefully every interval so the broker
publishes their Last Will, and faults are injected into sensor readings — both are the
pipeline being exercised, not a malfunction.

### Fleet size and behaviour

The simulator resolves settings from a named profile, then environment variables, then
flags. A profile is a starting point, not a straitjacket:

```bash
SIM_PROFILE=demo docker compose -f deploy/compose.yaml up -d      # 1000 devices
SIM_DEVICES=50 SIM_RATE=200ms docker compose -f deploy/compose.yaml up -d
```

| Profile | Devices | Sites | Rate | Faults |
|---|---|---|---|---|
| `dev` | 200 | 4 | 1s | 0.2% per tick |
| `demo` | 1 000 | 8 | 1s | 0.05% per tick |
| `stress` | 10 000 | 32 | 1s | 0.01% per tick |

Individual settings — `SIM_DEVICES`, `SIM_SITES`, `SIM_RATE`, `SIM_SEED`, `SIM_FLAP_PCT`,
`SIM_FLAP_INTERVAL`, `SIM_FAULT_PCT` — override whichever profile is selected.

Watching the wire directly is often more informative than any of the above:

```bash
docker exec fleet-mosquitto mosquitto_sub -h localhost -t 'fleet/+/+/telemetry' -C 1
docker exec fleet-mosquitto mosquitto_sub -h localhost -t 'fleet/+/+/event' -C 1
```

Stopping:

```bash
docker compose -f deploy/compose.yaml down       # stop
docker compose -f deploy/compose.yaml down -v    # also drop stored telemetry and the log
```

Tests, including the race detector and the schema conformance checks:

```bash
go test -race ./...
dotnet test services/api.tests
dotnet test clients/dotnet/Fleet.Client.Core.Tests
dotnet test clients/dotnet/Fleet.Client.Xaml.Tests
npm --prefix clients/electron test
```

Whether the running API still matches its contract:

```bash
pip install pyyaml jsonschema
python tools/contract_test.py --base-url http://localhost:8080
```

Whether it survives losing each component in turn:

```bash
python tools/chaos_test.py              # all six scenarios
python tools/chaos_test.py --list       # names
python tools/chaos_test.py --only kill-api
```

Each scenario publishes a uniquely identifiable message *during* an outage and proves it
arrives afterwards. Counting rows before and after does not work: the simulated fleet keeps
publishing throughout, so any total is a moving target and a test built on one passes for
the wrong reason.

All of the above runs in CI on every push, along with a full stack build that asserts the
pipeline lost nothing and the projection detected no gaps.

### Planned interface

None of this works yet; it is the shape the Compose setup is heading toward.

```bash
docker compose --profile full up          # broker, database, event log, ingest, API
docker compose --profile infra up         # everything except the API, for debugging it on the host
FLEET_PROFILE=stress docker compose --profile full up --scale fleet-sim=10
```

### Fleet profiles

Not implemented yet — fleet size is currently set directly with `SIM_DEVICES`. The scenario
engine behind these profiles adds fault injection and lifecycle events.

| Profile | Devices | Purpose |
|---|---|---|
| `dev` | 200 | Fast iteration; comes up in seconds |
| `demo` | 1 000 | Default, and the configuration all published measurements use |
| `stress` | 10 000 | Multiple simulator replicas and partitioned ingest; finds the real ceilings |

## What each language is doing here

| Language | Used for | Why |
|---|---|---|
| Go | Device simulator, ingest service | A thousand concurrent MQTT sessions, small image, high-fanout I/O |
| C# / .NET 10 | API tier, shared client state core, Blazor, WinUI 3, WPF | Typed API surface, and one state core serving two different view-state idioms — MVVM for the XAML hosts, direct store subscription for Blazor |
| C99 | Reference device agent | Proves the payload contract works from a genuinely constrained device |
| C++ / QML | Qt dashboard | Native desktop UI with its own Model/View idiom |
| TypeScript | Electron dashboard | The web stack, as a desktop application: React, Vite and Tailwind over its own copy of the reconciler |
| Python | Conformance suite, load orchestration, analysis | Contract tests that any device implementation must pass |

## Repository layout

Directories marked *planned* do not exist yet — see [Status](#status).

```
contracts/              the source of truth every tier is generated from or checked against
  openapi.yaml          the API's REST surface
  asyncapi.yaml         the realtime channel and the device-facing MQTT topics
  schemas/              JSON Schema per device message, embedded into every Go service
  codegen/              generator configuration, and why C# validates instead
  generated/            generated client models
  vectors/              golden reconciliation vectors, run by every client   (planned)
deploy/                 compose file, mosquitto config, database schema
devices/
  sim-go/               Go fleet simulator
  agent-c/              C99 reference device agent                           (planned)
  scenarios/            fault injection and lifecycle definitions            (planned)
services/
  ingest/               Go ingest service
  api/                  .NET 10 API service
  api.tests/            projection ordering and idempotency tests
clients/
  dotnet/
    Fleet.Client.Core/  transport, reconnection, frame reconciliation
    Fleet.Client.Xaml/  MVVM ViewModels, shared by both XAML hosts
    blazor/             virtualized dashboard
    winui/  wpf/        XAML hosts running those ViewModels unchanged
    *.Tests/            reconciliation, ViewModel and detail-panel tests
  electron/             TypeScript, React and Vite; its own reconciler, same tests
  qt/  flutter/                                               (planned)
tools/                  contract_test.py; conformance suite and load orchestration  (partly planned)
docs/                   architecture and decision records
```

## Documentation

- [Architecture](docs/architecture.md) — topology, contracts, realtime protocol, security,
  scale targets, build order
- [Decision records](docs/adr/) — why the system is built this way
- [.NET clients](clients/dotnet/README.md) — the shared state core, the three .NET
  dashboards, where sharing stops, and what makes a thousand rows render
- [Electron client](clients/electron/README.md) — the web stack as a desktop app, why its
  network access lives in the main process, and the startup race that cost a snapshot
- Scale testing — measured ceilings and failure modes *(not published yet)*
- UI comparison — framework results under identical load *(not published yet)*

## License

Apache-2.0. See [LICENSE](LICENSE).
