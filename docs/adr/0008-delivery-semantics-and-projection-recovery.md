# ADR-0008: Delivery semantics and projection recovery

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

[ADR-0002](0002-ingest-api-split.md) separates ingest from the API, with NATS JetStream
between them. That decision creates the central problem of this system: **the data arrives
at one process and the fleet state that dashboards read lives in another.**

Every genuine risk introduced by the split reduces to one question — what happens when a
message is delivered twice, delivered out of order, or when a process dies between
receiving a message and acting on it. Leaving that implicit is what turns a service split
into an unreliable system. This ADR makes it explicit.

## Decision

### Delivery guarantees, stated per hop

| Hop | Guarantee | Why |
|---|---|---|
| device → broker (telemetry) | at-most-once (QoS 0) | Telemetry is a sample of a continuous signal. A dropped sample is acceptable; head-of-line blocking is not. |
| device → broker (status, events, commands, acks) | at-least-once (QoS 1) | These are state transitions. Losing one corrupts fleet state. |
| broker → ingest | at-least-once for QoS 1 topics | Follows from the above. |
| ingest → TimescaleDB | at-least-once, idempotent write | Duplicate writes collapse on `(device_id, seq)`. |
| ingest → NATS JetStream | at-least-once, publish-confirmed | Ingest does not acknowledge the broker until JetStream confirms. |
| NATS → API projection | at-least-once, idempotent apply | Redelivery must not corrupt state. |
| API → dashboard | coalesced, lossy by design | Deltas are last-write-wins per device; a dropped frame is superseded by the next. |

**The system is at-least-once everywhere it matters, and every consumer is idempotent.**
That combination yields effectively-once *state* without requiring exactly-once *delivery*,
which no distributed system provides honestly.

### Sequence numbers, not timestamps

Every device message carries a monotonically increasing per-device `seq`. Ordering,
deduplication and gap detection all key on `seq`.

Timestamps are never used for ordering. Device clocks drift, devices reboot with no RTC,
and — per [ADR-0007](0007-linux-containers-windows-dashboards.md) — the container clock may
drift from the host clock. Timestamps are data, not control.

Gap detection is therefore explicit: a jump in `seq` means messages were lost, and that is
recorded as a measurable quality signal per device rather than silently ignored.

### Idempotent projection apply

The projection applies an event only if `event.seq > current.seq` for that device. Redelivery
of an already-applied event is a no-op. Out-of-order delivery cannot move a device backwards.

Device reboots reset `seq`, so a reboot is signalled explicitly by a `boot_id` that changes
on restart. `(boot_id, seq)` is the actual ordering key. Without this, a rebooted device
sending `seq=1` would be permanently ignored — a bug worth designing out rather than
discovering.

### Projection recovery on restart

The API's in-memory projection is a cache, never the source of truth. On start:

1. Load the last projection checkpoint — device state plus the NATS stream sequence it
   reflects — from TimescaleDB.
2. Replay JetStream from that sequence forward.
3. Serve traffic once caught up; report `not ready` until then, so a restarting instance
   never serves a partial fleet.

Checkpoints are written periodically and on graceful shutdown. Because apply is idempotent,
an over-replay from a stale checkpoint is harmless — which is precisely why checkpointing
can be cheap and asynchronous.

**Failure case this closes:** the API acknowledges a JetStream message and crashes before
updating the projection. Acknowledgement happens *after* apply, so the message is
redelivered; idempotency makes redelivery safe.

### Backpressure at every stage

An unbounded queue is a memory leak with extra steps. Every stage has a bounded buffer and a
declared policy for what happens when it fills:

| Stage | Bound | Policy when full |
|---|---|---|
| broker → ingest channel | bounded | QoS 0 telemetry: drop oldest, increment a counter. QoS 1: stop reading, let the broker apply backpressure. |
| ingest → database | batched, bounded | Block the ingest channel. Never buffer without limit. |
| ingest → JetStream | bounded, publish-confirmed | Block. Do not acknowledge the broker. |
| API → WebSocket | bounded per connection | Coalesce last-write-wins per device. A slow client gets fewer, larger deltas. |

Every drop is counted and exported. **Silent loss is the failure this design most wants to
avoid**, because it is the one that makes a dashboard confidently wrong.

### Horizontal ingest partitioning

MQTT 3.1.1 has no shared subscriptions ([ADR-0005](0005-broker-and-protocol.md)), so N
ingest instances cannot simply share one wildcard subscription — each would receive every
message.

Partitioning is therefore by topic prefix. The topic namespace is already site-scoped, so
instance *i* of *N* subscribes to the sites assigned to it by a deterministic map held in
configuration:

```
ingest-0  ->  fleet/site-00/#, fleet/site-04/#, ...
ingest-1  ->  fleet/site-01/#, fleet/site-05/#, ...
```

Assignment is static and explicit rather than dynamically rebalanced. Dynamic rebalancing
needs coordination — a lock service or consensus — which is not justified here, and static
assignment is what most fleets of this size actually run.

The documented alternative is switching to a broker with shared subscriptions (EMQX, or
MQTT v5), which trades broker-level simplicity for automatic balancing.

### Trace context propagation

Distributed tracing is not optional in a split system; without it, "why is device 412
missing" spans eight hops and is undebuggable.

MQTT 3.1.1 has no user properties (that is a v5 feature), so W3C `traceparent` travels in
the message envelope alongside `seq` and `boot_id`. The trace continues across the NATS
publish via message headers, and through the API to the WebSocket frame.

One trace therefore covers device → broker → ingest → database → NATS → projection →
dashboard.

### Verification

None of the above is credible as an assertion. It is verified by chaos tests in CI:

| Test | Assertion |
|---|---|
| Kill ingest mid-stream, restart | No QoS 1 message lost; projection converges to the correct state |
| Kill the API mid-stream, restart | Projection rebuilds from checkpoint + replay and matches a reference computed from the database |
| Kill NATS, restart | Ingest blocks rather than dropping; no acknowledgement of unconfirmed messages |
| Force JetStream redelivery | Projection is unchanged — idempotency holds |
| Reboot a device (`boot_id` change, `seq` reset) | Device is not stuck; new sequence is accepted |
| Fill every bounded queue | Drops are counted and exported; no unbounded memory growth |

The reference state for these comparisons is computed independently from TimescaleDB by the
Python suite in `tools/`, so the projection is checked against something that did not
produce it.

## Consequences

**Positive**

- The split is defensible in detail rather than in principle. Every failure mode has a
  stated behaviour and a test that demonstrates it.
- Idempotent apply plus sequence ordering removes the entire class of duplicate/reorder bugs
  that otherwise make split systems flaky in ways that are hard to reproduce.
- Trace context through the whole path makes the eight-hop debugging problem tractable.
- The chaos suite turns the guarantees above into executable checks, so they stay true as
  the system changes rather than decaying into stale documentation.

**Negative**

- Substantial work that a merged design would not need at all: checkpointing, replay,
  idempotency keys, `boot_id` handling, partition maps, chaos harness. This is the real
  cost of the split and it is accepted deliberately.
- The message envelope grows (`seq`, `boot_id`, `traceparent`), which matters on constrained
  devices. The CBOR encoding path exists partly to offset this.
- Observability must be built early rather than late, because debugging the split without
  tracing is the risk this ADR exists to remove.

## Notes

At 1000 devices none of this machinery is load-bearing. Its justification is the 10 000
device stress profile and the failure modes it makes visible. That commitment is what makes
[ADR-0002](0002-ingest-api-split.md) sound rather than speculative — the two decisions stand
or fall together.
