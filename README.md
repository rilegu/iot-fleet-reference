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

> **Status: design phase.** The architecture and its decision records are complete;
> implementation has not started. The commands in *Running it* describe the intended
> interface, not something you can run today. Track progress against the
> [delivery phases](docs/architecture.md#14-delivery-phases).

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

> Planned interface — see the status note above.

**Requirements:** Docker with Compose. For the native dashboards, the .NET 10 SDK; Node for
Electron; Qt 6 for the Qt client.

```bash
# Bring up the broker, database, event log, ingest, API and a 1000-device fleet
docker compose --profile full up

# Open the Blazor dashboard
#   http://localhost:8080

# A smaller fleet for fast iteration
FLEET_PROFILE=dev docker compose --profile full up

# Push it: 10 000 devices across ten simulator replicas
FLEET_PROFILE=stress docker compose --profile full up --scale fleet-sim=10
```

To work on the API itself, run the infrastructure in containers and the service on your
machine with a debugger attached:

```bash
docker compose --profile infra up          # everything except fleet-api
dotnet run --project services/api
```

Native dashboards are ordinary applications pointed at the API:

```bash
dotnet run --project clients/dotnet/winui
npm start --prefix clients/electron
```

### Fleet profiles

| Profile | Devices | Purpose |
|---|---|---|
| `dev` | 200 | Fast iteration; comes up in seconds |
| `demo` | 1 000 | Default, and the configuration all published measurements use |
| `stress` | 10 000 | Multiple simulator replicas and partitioned ingest; finds the real ceilings |

## What each language is doing here

| Language | Used for | Why |
|---|---|---|
| Go | Device simulator, ingest service | A thousand concurrent MQTT sessions, small image, high-fanout I/O |
| C# / .NET 10 | API tier, shared client state core, Blazor, WinUI 3, WPF | Typed API surface, and one state core serving two different view-state idioms |
| C99 | Reference device agent | Proves the payload contract works from a genuinely constrained device |
| C++ / QML | Qt dashboard | Native desktop UI with its own Model/View idiom |
| TypeScript | Electron dashboard | The web stack, as a desktop application |
| Python | Conformance suite, load orchestration, analysis | Contract tests that any device implementation must pass |

## Repository layout

```
contracts/              OpenAPI, AsyncAPI, JSON Schema — the source of truth
  vectors/              golden reconciliation vectors, run by every client
deploy/                 compose profiles, mosquitto config, TLS bootstrap
devices/
  sim-go/               Go fleet simulator
  agent-c/              C99 reference device agent
  scenarios/            dev / demo / stress definitions, fault injection
services/
  ingest/               Go ingest service
  api/                  .NET 10 API service
clients/
  dotnet/               shared state core, XAML ViewModels, WinUI, WPF, Blazor
  qt/  electron/  flutter/
tools/                  Python: conformance suite, load orchestration, analysis
docs/                   architecture and decision records
```

## Documentation

- [Architecture](docs/architecture.md) — topology, contracts, realtime protocol, security,
  scale targets, delivery phases
- [Decision records](docs/adr/) — why the system is built this way
- Scale testing — measured ceilings and failure modes *(published at phase 7)*
- UI comparison — framework results under identical load *(published at phase 8)*

## License

Apache-2.0. See [LICENSE](LICENSE).
