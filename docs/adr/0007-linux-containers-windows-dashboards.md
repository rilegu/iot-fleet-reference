# ADR-0007: Linux containers for the fleet and server tier; native Windows for the dashboards

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

The development and demonstration host is Windows 11 with Docker Desktop on a WSL2
backend. The dashboards target Windows. That raised a fair question: if the target OS is
Windows, is Docker/WSL2 the right choice at all, or does it add a Linux dependency the
project does not need?

The question resolves once two things are separated:

- **The product** is the dashboards. WinUI 3, WPF, Qt and Electron are native Windows
  applications.
- **The fleet and the server tier** are not the product. The simulated devices are a test
  harness; the ingest and API services are infrastructure that in any real deployment would
  run on Linux.

The deployment target was confirmed as **dashboards on Windows only**. Nothing else is
required to run on Windows.

## Decision

- **Linux containers** run the simulated fleet, the broker, the datastore, and the server
  tier: `fleet-sim`, `fleet-agent-c`, `mosquitto`, `timescaledb`, `nats`, `fleet-ingest`,
  `fleet-api`.
- **Native Windows** runs the dashboards: WinUI 3, WPF, Qt/QML, Electron, and later
  Flutter. The Blazor client is served from the Linux container and rendered in a browser
  on Windows.
- There is exactly **one platform boundary**: dashboards on Windows connecting to
  `fleet-api` over HTTP/WebSocket.

### Development inner loop

Running the whole stack in containers is the demo and CI topology, not a constraint on
day-to-day work. Two Compose profiles:

| Invocation | Contents | Use |
|---|---|---|
| `docker compose up` | broker, datastore, log, simulator, ingest | inner loop — run `fleet-api` on the Windows host with hot reload and a debugger attached |
| `docker compose --profile full up` | the above plus `fleet-api` | demo, CI, measurement runs |

The service reads the same configuration in both modes; only connection endpoints differ.
This is the standard pattern for containerized development and keeps `F5` debugging of the
.NET service available.

## Rationale

**Simulation fidelity.** Real embedded devices run embedded Linux or an RTOS, not Windows.
Simulating them as Linux processes is the more honest simulation. This matters most for
`fleet-agent-c`, which should be compiled against Linux libc because that is what a real
device agent is; an MSVC Windows build would be a less faithful reference.

**Reproducibility.** `docker compose up` brings up the entire backend and a thousand-device
fleet in one command. The native-Windows alternative is a page of installation instructions
covering Mosquitto, Postgres, TimescaleDB and Go. For a reference repository intended to be
cloned and run by other people, this is the single largest practical difference.

**Storage.** TimescaleDB is not a first-class Windows platform; Timescale dropped Windows
support for recent versions. A native-Windows server tier would force a move to plain
Postgres or SQL Server. Since the server tier is Linux, [ADR-0003](0003-telemetry-storage.md)
stands unchanged.

**Production realism.** ASP.NET Core in a Linux container is a mainstream deployment
target, including in Microsoft-centric organizations. Containerizing `fleet-api` makes the
demo topology and a plausible production topology the same thing.

**Scale-out.** `compose --scale sim=10` is how the 10 000-device stress profile is reached.
There is no comparably simple native equivalent.

**CI.** GitHub Actions Linux runners execute Compose directly, and are faster and cheaper
than Windows runners. Only the Windows dashboard builds need a Windows runner.

## Consequences

**Positive**

- One-command startup for the whole backend.
- Only one platform boundary in the system, and it is the same boundary for every dashboard
  — so it is a constant in the UI comparison rather than a variable.
- Server-side components communicate over a Docker network rather than through the WSL2
  port-forward, removing that hop from the ingest path entirely.
- The Windows-native clients are unaffected: they are ordinary desktop applications talking
  to an HTTP endpoint.

**Negative — and how each is handled**

| Cost | Mitigation |
|---|---|
| WSL2 clock can drift from the host clock, which would corrupt end-to-end latency figures | Never compare timestamps across the boundary. Telemetry carries a sequence number; latency is computed from host-side arrival time on a single clock. |
| The WSL2 port-forward sits between dashboards and `fleet-api` | It is identical for all five clients, so it cancels out of the comparison. A one-off calibration run against a host-resident API quantifies the offset for absolute figures. |
| `vmmem` reserves host RAM and releases it slowly | A `.wslconfig` memory cap ships in `deploy/`. |
| Docker Desktop requires a paid subscription for larger organizations | Free for this use. Docker Engine installed directly into the existing WSL2 Ubuntu distribution is an equivalent, licence-free fallback and is documented in `deploy/`. |
| Debugging a containerized .NET service is worse than debugging a host process | Compose omits the API by default, so the inner loop runs it on the host against containerized dependencies. |

## Alternatives rejected

- **Everything native on Windows.** Loses one-command reproducibility, scale-out,
  TimescaleDB, and simulation fidelity. Would only win if Windows-hosted
  infrastructure were itself a requirement. It is not.
- **WSL2 without Docker** (packages installed into the Ubuntu distribution). Same platform
  boundary, less isolation, no scale-out story, worse reproducibility. Strictly worse.
- **Windows containers.** The required images are Linux-only, Windows base images are far
  larger, and it would move the simulation further from real device behaviour.
