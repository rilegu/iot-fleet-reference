# ADR-0002: Split MQTT ingest (Go) from the API tier (.NET)

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

Something must consume the broker's wildcard subscription, validate and normalize payloads,
persist telemetry, and feed live state to dashboards. That is either one process or two.

At the nominal target — 1000 devices at 1 Hz, roughly 1000 msg/s — a single ASP.NET Core
service using MQTTnet would handle the entire workload at a few percent of one core. **The
split is not forced by the nominal load**, and pretending otherwise would be the weak form
of this argument. It needs a justification tied to something the system actually does.

## Decision

Two services:

- **`fleet-ingest` (Go 1.26)** — owns the broker subscription, schema validation,
  normalization, writes to TimescaleDB, and publishes normalized events to NATS JetStream.
- **`fleet-api` (.NET 10)** — owns the fleet state projection, REST, WebSocket fan-out,
  authentication, authorization, command dispatch and audit.

They communicate only through NATS JetStream and the database.

**This decision is bound to a commitment:** the 10 000-device stress profile is a delivered
artifact, not an aspiration, and the consistency model in
[ADR-0008](0008-delivery-semantics-and-projection-recovery.md) is implemented and tested
rather than asserted. Without both, this ADR would be describing infrastructure the load
does not justify.

## Rationale

1. **The patterns are part of what this reference implements.** Projection recovery,
   idempotent event application, replay from a checkpoint, backpressure across a process
   boundary and partitioned consumers are the load-bearing patterns of any real telemetry
   pipeline. None of them exist inside a single process, so a merged design could not
   provide a working reference for them.

2. **The stress profile makes the split load-bearing.** At 10 000 devices, ingest and query
   diverge sharply: ingest scales with device count and connection churn, the API with
   operator count and subscription complexity. MQTT 3.1.1 has no shared subscriptions
   ([ADR-0005](0005-broker-and-protocol.md)), so scaling ingest requires partitioned
   consumers — a design problem with a real answer, documented in
   [ADR-0008](0008-delivery-semantics-and-projection-recovery.md).

3. **Different failure and deploy characteristics.** Deploying a UI-facing API change should
   not tear down thousands of MQTT sessions or create a telemetry gap. An ingest backlog
   should not stall dashboard queries. Independent restartability is behaviour a real fleet
   requires, and it is demonstrable in the chaos suite.

4. **JetStream provides replay.** The API restarts and resumes from its last checkpoint
   rather than losing live state or rebuilding by scanning the database. This is what makes
   an in-memory projection safe to rely on.

5. **Each language does what it is best at.** Go for high-fanout I/O and connection handling
   in a small footprint; ASP.NET Core for a typed API surface, authorization, and a shared
   object model with the .NET clients. A real division of labour rather than language
   tourism.

## Consequences

**Positive**

- Independent deploy, restart and scaling of the two tiers, verified by chaos tests.
- Ingest backpressure isolated from query latency.
- NATS is a natural seam for later consumers — alerting, rules evaluation, exports — without
  touching either existing service.
- The hard parts (idempotency, ordering, recovery, partitioning) become visible, testable
  artifacts rather than invisible assumptions.

**Negative — accepted deliberately**

- **Distributed state.** Data arrives at ingest; the projection lives in the API. This is
  the primary risk and the entire subject of
  [ADR-0008](0008-delivery-semantics-and-projection-recovery.md).
- **Two contracts.** The public contract plus an internal ingest-to-API event contract, both
  versioned. A new telemetry field touches JSON Schema, Go types, the event schema, and C#
  models.
- **Debugging spans eight hops** rather than four. Mitigated only by distributed tracing,
  which is why observability moves from phase 5 to phase 2 — it is a prerequisite for the
  split, not a finishing touch.
- **JetStream is real infrastructure**: stream configuration, retention, consumer
  acknowledgement, redelivery, duplicate handling.
- **A heavier inner loop.** Debugging `fleet-api` on the Windows host requires broker,
  database, ingest and NATS running. The `infra` Compose profile
  ([ADR-0007](0007-linux-containers-windows-dashboards.md)) exists for this.
- **Ingest is written in Go**, so any logic prototyped in C# during phase 0 is discarded.
  Phase 0 is therefore explicitly a contract spike, not an architectural starting point.

## Boundary of this decision

The split is justified by the stress profile and by the systems-design goal. If both were
removed — the project capped permanently at 1000 devices with the UI comparison as its only
deliverable — the correct design would be a single .NET service with ingest behind an
`ITelemetryIngestor` interface, and this ADR would be superseded.

That interface boundary is maintained inside `fleet-ingest` regardless, so the merge
direction stays open. It is recorded here so the reasoning is visible rather than implied.
