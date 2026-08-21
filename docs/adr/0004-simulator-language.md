# ADR-0004: Go for the device simulator, with a Rust pivot kept open

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

The simulator must hold 1000 concurrent MQTT sessions — one TCP connection, client ID and
credential per device, because sharing connections would make the connection-scale problem
disappear and with it most of the realism. It must run as a container image on Docker
Desktop/WSL2 and scale out to roughly 10k devices across several containers.

Candidates were Go, Rust, C# and Python. Go and Rust both handle the concurrency trivially;
C# is workable with more care and a heavier image; Python is the fastest to write but the
weakest fit for a production-grade claim at this connection count.

## Decision

Implement `devices/sim-go` in **Go 1.26**, and define the simulator by contract so a Rust
implementation can be added or substituted without touching anything else.

## Rationale

- Goroutine-per-device is the natural expression of 1000 independent device state machines,
  and 1000 connections is unremarkable for the Go runtime.
- A static binary in a `scratch` or `distroless` image keeps the container around 10-15 MB,
  which matters when running several scaled replicas on a workstation.
- Go is already installed on the target machine and has a mature MQTT 3.1.1 client.
- It leaves Rust available as a deliberate, justified addition later rather than spending
  the project's hardest-language budget on the least novel component.

## What makes the Rust pivot cheap

The simulator is not privileged. It is one implementation of a contract, and three things
define that contract:

1. **The MQTT topic and payload contract** in `contracts/` — shared with `fleet-agent-c` and
   any future implementation.
2. **The scenario schema** in `devices/scenarios/*.yaml` — device population, telemetry
   cadence and jitter, fault injection, lifecycle events, RNG seed. Declarative and
   language-neutral.
3. **The Python conformance suite** in `tools/` — asserts that any simulator produces
   schema-valid payloads, correct retained-status and LWT behaviour, correct command-ack
   correlation, and correct ACL isolation.

A `devices/sim-rust` implementation that passes the conformance suite against the same
scenario files is a drop-in replacement. Compose selects between them by image name.

## Consequences

**Positive**

- Small image, simple concurrency model, fast to iterate on device behaviour.
- The conformance suite is valuable independently: it also validates the C99 agent.

**Negative**

- Go's memory-per-connection is higher than Rust's. At 1000 sessions this lands inside the
  400 MB budget; at 10k it is the reason to scale out across containers rather than up.
- Writing the conformance suite up front is work that a single-implementation project would
  skip. It is what buys the pivot.

## Constraints to handle explicitly

- Raise `ulimit -n` in the simulator container; the default file-descriptor limit will not
  cover 1000 sockets plus overhead.
- Raise `max_connections` in Mosquitto to match.
- Windows/WSL2 ephemeral port exhaustion appears well before 10k devices, which is why
  horizontal scaling across simulator replicas is part of the design rather than a fallback.
