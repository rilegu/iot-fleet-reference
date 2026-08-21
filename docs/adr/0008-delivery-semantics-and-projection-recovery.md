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
| ingest → TimescaleDB (status, events) | at-least-once, idempotent write | Primary key `(device_id, boot_id, seq)`; redelivery is a no-op. |
| ingest → TimescaleDB (telemetry) | at-most-once, atomic batch | See *Why telemetry is not deduplicated* below. |
| ingest → NATS JetStream | at-least-once, publish-confirmed | Ingest does not acknowledge the broker until JetStream confirms. |
| NATS → API projection | at-least-once, idempotent apply | Redelivery must not corrupt state. |
| API → dashboard | coalesced, lossy by design | Deltas are last-write-wins per device; a dropped frame is superseded by the next. |

**The system is at-least-once everywhere duplicates can occur, and every consumer of those
streams is idempotent.** That combination yields effectively-once *state* without requiring
exactly-once *delivery*, which no distributed system provides honestly.

### Why telemetry is not deduplicated

Telemetry is the one hop that does not get an idempotency key, for three reasons.

**A hypertable unique index must include the partitioning column.** The key cannot be
`(device_id, seq)`; TimescaleDB requires `(device_id, boot_id, seq, time)`. That is a
constraint of the storage chosen in [ADR-0003](0003-telemetry-storage.md), not a matter of
preference.

**There is no duplicate source to defend against.** Telemetry is published at QoS 0, so the
broker never redelivers it. Status and events — the streams the broker *does* redeliver —
carry real primary keys and are genuinely idempotent. The guarantee is enforced exactly
where it is load-bearing.

**It would cost most where it buys least.** Telemetry outweighs every other table by orders
of magnitude, so a unique index taxes write throughput and storage across the whole fleet in
order to defend against nothing.

Instead, each telemetry batch is written inside a single transaction. A partially applied
batch therefore cannot exist, which is the property that actually matters: a retry after a
failed write cannot double-insert rows that already committed.

This is achievable rather than impossible, and the decision should be re-opened if the
premises change. `received_at` is stamped when the message arrives, not when it is written,
so a retried batch carries identical `time` values and `(device_id, boot_id, seq, time)`
*would* collide correctly. Implementing it means giving up `COPY` for `INSERT ... ON
CONFLICT` or a staging-table merge, and paying for an index on the largest table. If
telemetry ever moves to QoS 1, that trade flips and this section should be revisited.

### Consequences of that choice, stated plainly

Two behaviours follow from it, and both are deliberate rather than accidental:

- **A telemetry batch that fails to write is discarded, not retried.** This is consistent
  with at-most-once delivery. It is counted in `write_failures` and must stay visible:
  loss that nothing records is the failure this document exists to prevent.
- **A batch may reach the database but fail to reach the log.** Those samples are in
  history but absent from the stream, so a projection built purely from replay will not
  have them until it queries history. Acceptable for a lossy sampled signal; it would not
  be acceptable for status or events, which is why those are published one at a time and
  acknowledged only after both writes succeed.

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

1. **Seed the floor from TimescaleDB.** One `DISTINCT ON (device_id)` query over
   `device_status` yields the last known identity and presence of every device that has ever
   reported. Seeded devices are marked as a baseline rather than a live observation, so the
   first real message for each does not register as a sequence gap.
2. **Replay the log from the durable consumer's position.** The consumer's acknowledgement
   floor *is* the checkpoint: JetStream stores it server-side, so a restart resumes exactly
   where the previous instance stopped acknowledging.
3. **Serve traffic once caught up**; report `not ready` until then, so a restarting instance
   never serves a partial fleet.

There is no separate checkpoint table, and none is needed. A durable consumer already
records a per-consumer position, and the database already holds device state — writing a
third copy on a timer would add a component whose staleness has to be reasoned about,
to duplicate what two existing components track exactly.

**Why the seed exists at all.** The stream has a finite `MaxAge`. Replay alone recovers any
device that reported inside the retention window, which in normal operation is the entire
fleet. A device silent for longer than that has no messages left to replay and would be
absent from the projection entirely. The database has no such horizon, so it supplies the
floor and the log brings it current.

The seed reads presence and identity only, never telemetry: metrics arrive from the log
within a second, and a latest-row-per-device query against the hypertable costs far more
than the bounded query over `device_status`.

Because apply is idempotent, seeding and then over-replaying is harmless — which is what
makes this cheap enough to do unconditionally on every start. A failed seed is logged and
degrades to replay-only rather than preventing startup.

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
produce it. Implemented as `tools/chaos_test.py` and run in CI.

**One mechanism these tests made concrete.** The backpressure table above says QoS 1 flow
control is achieved by ingest stopping reading and letting the broker push back. In
practice that limit is the broker's in-flight window — twenty unacknowledged messages by
default — after which Mosquitto simply stops delivering to that client. So the number of
messages ingest can hold unacknowledged during an outage is bounded by broker configuration
rather than by anything in this repository, and raising `max_inflight_messages` is what
widens it. Worth knowing before tuning: a larger window means more redelivery after a crash,
not more throughput.

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

## Revision history

Projection recovery originally specified a bespoke checkpoint table in TimescaleDB holding
device state plus a stream sequence, written periodically. Implementing it showed the
checkpoint was redundant: JetStream already tracks a durable consumer's acknowledgement
position, and the database already holds device state. What the checkpoint was really
protecting against — a device silent longer than the stream's retention — is closed by
seeding from `device_status` at startup, with no third copy of the state to keep current.

The delivery table originally specified a single `ingest → TimescaleDB` hop with duplicate
writes collapsing on `(device_id, seq)`. Implementing it showed that a hypertable cannot
carry that key, and that telemetry has no duplicate source to justify paying for one. The
decision is unchanged — at-least-once with idempotent consumers — but it is now stated per
stream rather than uniformly, and the telemetry trade-off is written down instead of being
an accident of the implementation.

## Notes

At 1000 devices none of this machinery is load-bearing. Its justification is the 10 000
device stress profile and the failure modes it makes visible. That commitment is what makes
[ADR-0002](0002-ingest-api-split.md) sound rather than speculative — the two decisions stand
or fall together.
