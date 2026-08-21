# Architecture

`iot-fleet-reference` is a production-grade reference implementation of an IoT fleet
management system: a simulated fleet of 1000+ embedded devices publishing MQTT telemetry
to a broker, an ingest and API tier that turns that stream into queryable fleet state, and
a set of interchangeable desktop/web dashboards built on **one shared contract**.

The distinguishing goal is that the *same* dashboard is implemented across several UI
stacks — Blazor/ASP.NET Core, WinUI 3, Qt/QML, Electron, and later Flutter — against a
frozen, language-neutral API. Adding a new UI framework must never require touching the
backend.

> **This document describes the target design, not what is built today.** It is written in
> the present tense throughout because it is a specification, not a progress report. For
> what currently runs, see the [status table in the README](../README.md#status).

## 1. Goals

| # | Goal |
|---|---|
| G1 | Simulate 1000 devices (headroom to 10k) with realistic behaviour, faults, and lifecycle |
| G2 | End-to-end production practices: TLS, per-device identity, ACLs, authn/authz, audit, observability, CI |
| G3 | A language-neutral contract that any UI framework can consume without backend changes |
| G4 | Implement each client in its ecosystem's idiomatic view-state style — MVVM in WinUI 3, signals and unidirectional flow on the web, Model/View in Qt — over one shared .NET state core |
| G5 | Use each language where it fits and record why: Go, C, Python, C#, C++/QML, TypeScript |
| G6 | Render 1000 live-updating devices at interactive frame rates |
| G7 | Produce a measured, reproducible comparison of the UI stacks under identical load |
| G8 | A correct and verifiable distributed ingest path: partitioned ingest, idempotent projection, replay-based recovery, backpressure, and a chaos suite that proves it |

## 2. Non-goals

- Multi-tenant SaaS billing, org management, or horizontal cluster deployment.
- Real hardware bring-up. The C agent is cross-compilable but not targeted at a specific MCU.
- Beating any vendor platform on features. This is a reference, not a product.

## 3. Topology

```
+- Linux containers (Docker / WSL2) --------------------------+
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
|                                  (history)  (event bus)     |
|                                        |          |         |
|                                        +----+-----+         |
|                                             v               |
|                              fleet-api (.NET 10)            |
|                              projection - REST - WS - authz |
|                              +-- REST (OpenAPI)  queries    |
|                              +-- WS   (AsyncAPI) deltas     |
+---------------------------------------+---------------------+
                                        | :8080
                          the ONE platform boundary
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

Only the dashboards run on Windows. Everything else — fleet, broker, datastore, and the
server tier — runs in Linux containers, which is both closer to how real devices and real
backends run and what makes `docker compose up` bring the entire system up in one command.
See [ADR-0007](adr/0007-linux-containers-windows-dashboards.md).

`docker compose up` starts everything except `fleet-api`, so the API can run on the Windows
host with a debugger attached during development. `docker compose --profile full up` adds
the API container and is the demo, CI and measurement topology.

There is exactly one platform boundary — dashboards to `fleet-api` — and it is identical
for every client, so it is a constant in the comparison rather than a variable.

## 4. Components

| Component | Language | Runs in | Responsibility |
|---|---|---|---|
| `fleet-sim` | Go 1.26 | Linux container | 1000 device state machines, one MQTT session each, scenario-driven faults |
| `fleet-agent-c` | C99 | Linux container | Reference device agent — constrained-device implementation of the same contract |
| `mosquitto` | — | Linux container | MQTT 3.1.1 broker, TLS, per-device ACLs |
| `timescaledb` | — | Linux container | Telemetry history, continuous aggregates, retention |
| `nats` | — | Linux container | Normalized internal event bus with replay (JetStream) |
| `fleet-ingest` | Go 1.26 | Linux container | MQTT consumer, schema validation, normalization, persistence, fan-out |
| `fleet-api` | C# / .NET 10 | Linux container, or the host during development | Fleet state projection, REST + WebSocket, authn/authz, command dispatch |
| `Fleet.Client.Core` | C# / .NET 10 | Windows | Transport, reconciliation, observable fleet state core |
| `clients/*` | varies | Windows | Dashboard implementations |
| `tools/*` | Python 3.13 | either | Contract conformance suite, load orchestration, comparison analysis |

### Why ingest and API are separate processes

Ingest and query have different failure modes, scaling curves, and deploy cadences. An API
restart must not drop thousands of MQTT sessions or lose telemetry; an ingest backlog must
not stall dashboard queries. Go handles high-fanout I/O and connection churn in a small
footprint; ASP.NET Core handles the typed API surface, authorization, and the shared object
model with the .NET clients.

**At 1000 devices a single process would suffice**, and the design says so plainly rather
than inventing a justification. The split is load-bearing at the 10 000-device stress
profile, where ingest and query diverge sharply and MQTT 3.1.1's lack of shared
subscriptions forces partitioned consumers. That profile is therefore a committed
deliverable, not an aspiration — [ADR-0002](adr/0002-ingest-api-split.md) depends on it.

Splitting creates one hard problem: telemetry arrives at `fleet-ingest` while the state
dashboards read lives in `fleet-api`. Duplicate delivery, reordering, and process death
between receive and apply are the failure modes that make split systems quietly wrong.
[ADR-0008](adr/0008-delivery-semantics-and-projection-recovery.md) specifies the guarantee
at every hop, the idempotent apply rule, checkpoint-and-replay recovery, backpressure
policy, partitioning scheme, and the chaos tests that verify all of it.

## 5. Contract-first design

The contract is the product. It lives in `contracts/` and is the only coupling point
between tiers.

```
contracts/
  openapi.yaml        REST surface: fleet query, device detail, history, commands, OTA
  asyncapi.yaml       WebSocket channel + the device-facing MQTT topics
  schemas/            JSON Schema for every payload (telemetry, status, event, command)
  codegen/            per-language generator config
```

Generated clients, never hand-written models:

| Target | Generator |
|---|---|
| C# | NSwag or Kiota |
| TypeScript | `openapi-typescript` + `zod` from JSON Schema |
| Go | `oapi-codegen` |
| Python | `datamodel-code-generator` |
| Dart | `openapi-generator` |
| C++ / Qt | `openapi-generator` (cpp-qt) or a thin hand-written layer over `QNetworkAccessManager` |

CI fails if generated code drifts from the contract, and fails if a contract change is not
accompanied by a version bump. **This is the mechanism that makes "add Flutter later" a
day of work rather than a refactor.**

## 6. Device contract (MQTT)

MQTT **3.1.1** is the baseline (see [ADR-0005](adr/0005-broker-and-protocol.md)). Topics
are namespaced by site so multi-site ACLs are natural:

```
fleet/{site}/{deviceId}/telemetry      QoS 0, device -> cloud, high rate
fleet/{site}/{deviceId}/status         QoS 1, retained, LWT-backed
fleet/{site}/{deviceId}/event          QoS 1, faults, boots, threshold crossings
fleet/{site}/{deviceId}/cmd            QoS 1, cloud -> device
fleet/{site}/{deviceId}/cmd/ack        QoS 1, device -> cloud, correlated by cmdId
```

- **Presence** is derived from a retained `status` message plus a Last Will and Testament,
  so a device that dies without a `DISCONNECT` still flips to offline. Presence is never
  inferred from telemetry silence alone.
- **Payloads** are JSON by default, with a CBOR encoding selectable per device to exercise
  the constrained-device path. Both are generated from the same JSON Schema.
- **Identity** is one MQTT client ID, one credential, and one TCP connection per device —
  no shared connections. This is what makes the connection-scale problem real.

## 7. Realtime protocol (the hard part)

Naively pushing 1000 devices x 1 Hz to a UI is 1000 messages/second per client and will
melt any of the five frameworks. The API therefore owns a **fleet state projection** and
speaks a snapshot/delta protocol:

1. Client opens the WebSocket and sends `subscribe { view, filter, fields, maxRate }`.
2. Server replies with one `snapshot` frame: current state of the matching devices.
3. Server then emits `delta` frames on a fixed cadence (default 250 ms), containing only
   fields that changed, plus precomputed fleet aggregates (online count, alerting count,
   ingest rate, p50/p95 telemetry latency).
4. Per-connection outbound queues **coalesce last-write-wins per device**. A slow client
   receives fewer, larger deltas — never a growing backlog.
5. Opening a device detail view creates a second, higher-rate subscription scoped to that
   device only.

This bounds client work by *viewport and cadence*, not by fleet size, which is what makes a
fair cross-framework comparison possible at all. Clients still virtualize their grids; the
protocol just stops the network from being the bottleneck first.

## 8. Client architecture: one state contract, five idioms

Every client obeys the same principle — **observable state lives outside the view, the view
is a projection of that state, user intent flows one way back** — but expresses it in its
own ecosystem's native idiom rather than having one pattern imposed on all five. MVVM with
two-way binding is one expression of that principle; signals and unidirectional data flow
are another. Both are represented here deliberately. See
[ADR-0006](adr/0006-shared-client-state-core.md).

`clients/dotnet/Fleet.Client.Core` is a UI-framework-free .NET 10 library holding everything that
is genuinely shareable:

- `FleetConnection` — REST + WebSocket transport, reconnect with jitter, resilience
  pipelines, snapshot/delta reconciliation.
- `FleetStore` — the observable fleet state: device collection, filtering, sorting,
  selection, derived aggregates. Exposes both a change-notification stream and immutable
  snapshots, so either binding style can sit on top.
- Command dispatch with `cmdId` correlation and optimistic/settled state.
- `Microsoft.Extensions.DependencyInjection` registration extensions.

On top of that core:

| Client | Idiom | Notes |
|---|---|---|
| **WinUI 3** | **MVVM** — `CommunityToolkit.Mvvm` ViewModels over `FleetStore`, bound with `x:Bind` | The idiomatic and effectively mandatory pattern for XAML. |
| **Blazor** | Store subscription + `InvokeAsync(StateHasChanged)` | Blazor's native idiom, not `INotifyPropertyChanged`. Reuses transport, reconciliation and query logic. |
| **Qt/QML** | Model/View — `QAbstractTableModel` + `Q_PROPERTY` | Qt's own terminology and structure. |
| **Electron** | Signals/observable store + virtualized grid | The mainstream web idiom. |
| **Flutter** | `ChangeNotifier` or Riverpod | Whichever the comparison finds cleaner. |

The reuse that matters — transport, reconnect, snapshot/delta reconciliation, filter/sort/
aggregate semantics — is shared between the two .NET clients regardless of binding style,
and is the part most likely to develop subtle divergence if duplicated.

The deliverable is a documented, comparative answer to "how does each of these ecosystems
actually manage view state in 2026, and what does each style cost", which is a more useful
result than applying one pattern uniformly.

### Sharing boundaries

"Sharing" means three different things in this repository, and conflating them causes bad
decisions. They are ranked by how much they can actually cover:

| Tier | What is shared | Scope | Cost |
|---|---|---|---|
| **Shared code** | a compiled library | one runtime only | free once written |
| **Shared contract** | models generated from OpenAPI / JSON Schema | every language | free — codegen |
| **Shared spec** | behaviour: reconciliation, filter/sort semantics, command lifecycle | every language | discipline plus tests |

Code reuse is bounded by the runtime, not by preference. Qt is C++, Electron is TypeScript,
Flutter is Dart — none of them can consume a .NET library. So the client tree groups by
runtime family:

```
clients/
  dotnet/
    Fleet.Client.Core/    transport, reconciliation, FleetStore    <- shared code
    Fleet.Client.Xaml/    MVVM ViewModels                          <- shared by XAML hosts only
    winui/                thin: XAML + DI wiring
    wpf/       (optional) thin: XAML + DI wiring
    blazor/               consumes FleetStore directly, no ViewModels
  qt/                     C++  — own implementation
  electron/               TS   — own implementation
  flutter/                Dart — own implementation
```

Adding WPF is close to free and worth doing: the same ViewModels running unchanged on both
WPF and WinUI 3 is the most direct available proof that the MVVM layer is genuinely
view-agnostic.

### Reuse is deliberately not maximised

A shared core could in principle be forced across runtimes — compiled to WebAssembly, or
exposed as a C library behind FFI. That is rejected. It is substantial work, it makes every
client non-idiomatic, and above all **it would destroy the comparison**: five thin wrappers
around one engine are not five implementations, and measuring them would say nothing about
the frameworks.

The duplication between runtime families is the deliverable, not a defect.

### Guarding the duplication

Five independent implementations of snapshot/delta reconciliation is five opportunities for
subtle, divergent bugs. This is handled the same way as the device contract — with tests
rather than trust.

`contracts/vectors/` holds **golden reconciliation vectors**: language-neutral JSON
fixtures of `(snapshot, delta stream) -> expected final state`, including reordering, gap
detection, resubscribe-after-drop, and coalescing edge cases. Every client's reconciler runs
them in its own test suite. A client that cannot reproduce the expected state fails CI.

This converts "shared spec" from an intention into something enforced, which is what makes
deliberate duplication safe.

> **Licensing note:** the Qt MQTT add-on is GPL-3.0/commercial, not LGPL. Since all clients
> talk to `fleet-api` over WebSocket (Qt WebSockets, LGPL) rather than to the broker
> directly, this repository stays cleanly Apache-2.0. Verify current Qt module licensing
> before adding any Qt add-on.

## 9. Data and storage

Tiered, mirroring commercial practice:

| Tier | Store | Retention |
|---|---|---|
| Hot — current fleet state | in-process projection in `fleet-api` | live |
| Warm — queryable history | TimescaleDB hypertable + continuous aggregates | 30 days raw, 1 year rolled up |
| Cold — archive | Parquet export on disk (optional) | indefinite |

Continuous aggregates precompute the 1 m / 5 m / 1 h rollups the dashboards actually chart,
so a "last 24 hours across 1000 devices" query does not scan raw rows. Compression and
retention policies are configured from the start, not bolted on.

`fleet-ingest` writes through a `TelemetryStore` port. A ClickHouse adapter is the
documented alternative for high-volume columnar workloads; the same port maps onto Azure
Event Hubs into Azure Data Explorer if a cloud variant is ever built. Rationale in
[ADR-0003](adr/0003-telemetry-storage.md).

## 10. Security

Not deferred to "later" — it is a stated goal, and it is what separates this from a demo.

- **Transport:** TLS on 8883 with a project CA; plaintext 1883 available only under a
  `dev` compose profile.
- **Device identity:** per-device X.509 client certificate or per-device credential issued
  by a provisioning step. No shared fleet password, ever.
- **Broker ACLs:** device `X` may publish only under `fleet/{site}/X/#` and subscribe only
  to its own command topic. Enforced in Mosquitto config, and *tested* — the conformance
  suite asserts that a device cannot impersonate another.
- **API:** OIDC/JWT bearer auth, roles `viewer` / `operator` / `admin`. Command dispatch and
  OTA campaigns require `operator` or above and write an immutable audit record.
- **Input:** every inbound payload is schema-validated at the ingest boundary. Malformed
  messages are counted, sampled to a dead-letter table, and dropped — never trusted.

## 11. Observability and operations

- OpenTelemetry traces and metrics from `fleet-ingest` and `fleet-api`; Prometheus +
  Grafana available under an `observability` compose profile.
- Structured logging with correlation IDs propagated from `cmdId` through to device ack.
- Health and readiness endpoints on both services.
- Golden-signal dashboards for the pipeline itself: ingest lag, validation failure rate,
  broker connection count, WebSocket fan-out cost.
- CI (GitHub Actions): build every component, run unit and conformance suites, bring up
  Compose, run a 200-device smoke test end to end, verify contract codegen is current.

## 12. Repository layout

```
contracts/              OpenAPI - AsyncAPI - JSON Schema (source of truth)
  vectors/              golden reconciliation vectors, run by every client
deploy/                 compose profiles, mosquitto config, TLS bootstrap, .wslconfig guidance
devices/                                                    [Linux containers]
  sim-go/               Go fleet simulator
  agent-c/              C99 reference device agent
  scenarios/            dev / demo / stress scenario definitions
services/                                                   [Linux containers]
  ingest/               Go ingest service
  api/                  .NET 10 API service
clients/                                                    [native Windows]
  dotnet/
    Fleet.Client.Core/  transport, reconciliation, FleetStore
    Fleet.Client.Xaml/  MVVM ViewModels, shared by the XAML hosts
    winui/  wpf/        thin XAML + DI wiring
    blazor/             consumes FleetStore directly
  qt/  electron/  flutter/
tools/                  Python: conformance suite, load orchestration, analysis
docs/
  architecture.md       this document
  adr/                  architecture decision records
  ui-comparison.md      measured cross-framework results
```

## 13. Scale targets and known constraints

### Scale ladder

Fleet size is a dial, not a constant. Three profiles ship in `devices/scenarios/`:

| Profile | Devices | Purpose |
|---|---|---|
| `dev` | 200 | Fast iteration. Compose comes up in seconds. |
| `demo` | 1 000 | The headline configuration and the target for all published measurements. |
| `stress` | 10 000 | Multiple simulator replicas, partitioned ingest. **A committed deliverable, not an option** — it is what makes the service split in [ADR-0002](adr/0002-ingest-api-split.md) load-bearing rather than speculative. Results are published in `docs/scale-testing.md`. |

### Why 1000 is the right headline number

1000 devices at 1 Hz is 1000 msg/s. That is a small number for every server-side component
here — a 200-byte JSON payload parses in roughly a microsecond, so validation and
normalization cost low single-digit percent of one core. Estimated footprint on a 6-core /
32 GB workstation:

| Component | Approximate cost at `demo` scale |
|---|---|
| Mosquitto, 1000 connections | ~50 MB, negligible CPU |
| Go simulator, 1000 sessions | 200-300 MB |
| TimescaleDB, batched inserts (~10/s) | ~1 GB |
| Go ingest + .NET API | ~300 MB |

The workstation ceiling is closer to 20 000-50 000 devices than to 1000. 1000 is chosen
because it is a realistic mid-size commercial fleet **and** because it is the point where
naive implementations start to fail — which is precisely what makes the exercise worth
doing. At 100 devices every naive approach works, and the design questions this repository
exists to answer never arise.

### Where the difficulty actually is

The only component that struggles at this scale is the **client render loop**, and only if
the server pushes raw messages. 1000 msg/s against a 1000-row grid implies on the order of
10^6 row-renders per second, which no framework survives.

With the coalescing described in section 7 (one delta frame per 250 ms) plus row
virtualization (~40 visible rows), the client does 4 renders of 40 rows per second. Every
candidate framework handles that comfortably. **This is a protocol design problem, not a
capacity problem**, and the fix is a few hundred lines in the API.

A consequence worth planning for: because all frameworks cope easily at 4 Hz, a
single-cadence comparison would be uninformative. The comparison in `ui-comparison.md`
therefore **sweeps the delta cadence** — 4 Hz, 10 Hz, 30 Hz, and uncoalesced — and reports
where each framework's frame time degrades. That breaking point is the interesting result.

### Targets

| Metric | Target |
|---|---|
| Devices | 1000 default, 10 000 via `compose --scale` |
| Telemetry rate | 1 Hz/device nominal, 20 Hz burst scenarios |
| Ingest throughput | 20 000 msg/s sustained, under 1 s p99 broker-to-database |
| Dashboard | 60 fps while streaming, under 300 ms cold snapshot for 1000 devices |
| Simulator footprint | under 400 MB RSS for 1000 sessions |

### Real constraints to plan for, not discover

- 1000 concurrent TCP connections needs `ulimit -n` raised in the simulator container and
  `max_connections` raised in Mosquitto. Both are set explicitly in `deploy/`.
- Windows/WSL2 ephemeral port exhaustion becomes a factor well before 10k devices; scaling
  past roughly 5k means multiple simulator containers, which the design already supports.
- Docker Desktop must have enough WSL2 memory allocated; `.wslconfig` guidance ships in
  `deploy/`.

## 14. Build order

The system is built as working vertical slices rather than as layers: each step produces
something that runs end to end, not a tier waiting for the tier beneath it. Current state is
tracked in the [status table in the README](../README.md#status).

The broad order is: freeze the contract, then the ingest path and storage, then correctness
and security, then the clients, then the device agent and conformance suite, then scale
testing, and finally the remaining UI frameworks and the published comparison.

Two orderings within that are deliberate and worth stating, because both invert what would
otherwise be natural:

- **Distributed tracing is built early, not late.** Debugging an eight-hop path without it
  is the principal risk of the ingest/API split, so it is a prerequisite for that split
  rather than a finishing touch.
- **The chaos suite lands immediately after the recovery machinery it verifies**, rather
  than at the end. Deferred to the end it would be reduced to a claim; built alongside, it
  is what makes the guarantees in
  [ADR-0008](adr/0008-delivery-semantics-and-projection-recovery.md) checkable.

One consequence worth flagging for anyone reading the history: the earliest exploratory
consumer was written in C# to validate the message contract quickly, while ingest is
specified in Go. That code is deliberately discarded rather than grown.

## 15. Extension points

The design is deliberately open at four seams:

1. **New UI framework** — implement the generated client plus the WebSocket protocol. No
   backend change. Validated by adding a Flutter client last, after the API is frozen.
2. **New device implementation** — the Go simulator, the C agent, and a future Rust
   simulator are all just implementations of the MQTT contract, validated by the same
   Python conformance suite. See [ADR-0004](adr/0004-simulator-language.md).
3. **New storage backend** — implement the `TelemetryStore` port.
4. **New transport** — a gRPC or SSE facade can sit beside REST + WebSocket without
   touching the projection.
