# Contracts

> **Draft.** These shapes are being validated by the simulator and an exploratory consumer.
> They become JSON Schema, OpenAPI and AsyncAPI documents once the ingest service is built,
> at which point this file is replaced by generated artifacts and a versioning policy.
> Nothing here is frozen yet.

## Topics

```
fleet/{site}/{deviceId}/telemetry      QoS 0, device -> cloud, high rate
fleet/{site}/{deviceId}/status         QoS 1, retained, LWT-backed
fleet/{site}/{deviceId}/event          QoS 1, not yet implemented
fleet/{site}/{deviceId}/cmd            QoS 1, cloud -> device, not yet implemented
fleet/{site}/{deviceId}/cmd/ack        QoS 1, device -> cloud, not yet implemented
```

`site` and `deviceId` are both `[a-z0-9-]+`. The site segment exists so broker ACLs and
ingest partitioning can be scoped without parsing device identifiers.

## Envelope

Every message carries the same envelope fields:

| Field | Type | Purpose |
|---|---|---|
| `schema` | string | `name/major`, e.g. `telemetry/1`. Lets a consumer reject what it cannot parse. |
| `device_id` | string | Also present in the topic; duplicated so a payload is self-describing once detached from its topic. |
| `site` | string | Same reasoning. |
| `boot_id` | string | Changes on every device restart. |
| `seq` | uint64 | Monotonic per `boot_id`, starting at 1. |
| `ts` | RFC 3339 UTC | Device-local clock. **Data, never used for ordering.** |
| `traceparent` | string | W3C trace context. MQTT 3.1.1 has no user properties, so it travels in the payload. |

Ordering is by `(boot_id, seq)` — never by `ts`. Device clocks drift, devices reboot without
a real-time clock, and container clocks drift from host clocks. See
[ADR-0008](../docs/adr/0008-delivery-semantics-and-projection-recovery.md).

## `telemetry/1`

```json
{
  "schema": "telemetry/1",
  "device_id": "dev-000042",
  "site": "site-01",
  "boot_id": "b3f1a9c47d2e5810",
  "seq": 1234,
  "ts": "2026-08-20T19:12:33.123Z",
  "traceparent": "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01",
  "metrics": {
    "temp_c": 42.31,
    "humidity_pct": 51.2,
    "voltage_v": 12.08,
    "rssi_dbm": -67,
    "uptime_s": 84321
  }
}
```

Published at QoS 0: telemetry is a sample of a continuous signal, so a lost sample is
acceptable but head-of-line blocking is not.

## `status/1`

```json
{
  "schema": "status/1",
  "device_id": "dev-000042",
  "site": "site-01",
  "boot_id": "b3f1a9c47d2e5810",
  "seq": 1,
  "ts": "2026-08-20T19:10:02.004Z",
  "online": true,
  "reason": "connect",
  "fw_version": "1.4.2",
  "model": "acme-sensor-v2"
}
```

Published at QoS 1 with the retain flag, so a consumer that subscribes later immediately
learns the current state of every device without waiting for the next telemetry sample.

`reason` is one of:

| Value | Meaning |
|---|---|
| `connect` | Device came online normally |
| `shutdown` | Device disconnected gracefully; it published this itself |
| `lwt` | The broker published this on the device's behalf after the connection dropped |

### The Last Will problem

The Last Will and Testament payload is fixed **at connect time**, before any messages are
sent. It therefore cannot carry a meaningful `seq` — the device does not yet know what its
last sequence number will be.

This is resolved by setting `seq: 0` in the will and marking it `"reason": "lwt"`. Consumers
must treat `seq: 0` on a status message as *"outside the sequence"* rather than as a stale
message to be discarded by the normal `seq > current.seq` rule. Without that carve-out, the
idempotency rule in ADR-0008 would silently drop every offline transition — a device would
die and the dashboard would never notice.

Presence is therefore derived from the retained status message plus the will, never from
telemetry silence.

---

# Findings from validating this contract

Three things the contract got wrong, found by running it rather than by reading it. All
three are fixed above; they are recorded because each one constrains how the ingest service
and the fleet projection must be built.

## 1. A retained status replay is not a sequence participant

**Symptom.** On first connect the consumer reported a sequence gap for 95 of 100 devices
and a stale drop for the other 5 — then both counters froze while telemetry kept climbing.
Exactly one event per device.

**Cause.** Retained status messages are replayed by the broker when a consumer subscribes.
That status was published at device connect time and carries `seq: 1`, while the device has
since sent hundreds of telemetry messages. The consumer set its baseline from the replayed
status and then scored the next live telemetry message — at `seq: 400` or wherever the
device had reached — as a gap of 399. The 5 stale drops were the same race in the other
order: telemetry arrived first, so the replayed `seq: 1` status looked like a stale message.

**Rule.** A retained message is historical by definition. A consumer must use it to
establish a baseline for a device it has not seen, and must never let it count as a gap, count
as a stale drop, or move a known device's sequence backwards. The first *live* message after
a retained baseline is also not a gap, because the baseline did not come from the device's
previous send.

MQTT delivers the retain flag on replayed messages, so this is detectable at the ingest
boundary. Ingest must propagate that flag into the projection rather than discarding it —
if the event log drops it, this bug returns and is much harder to see.

## 2. Sequence numbers are shared across streams

`seq` is monotonic per `(device_id, boot_id)` across *all* streams, not per topic. A
consumer watching only telemetry sees `1, 2, 4, 5` whenever a status message takes sequence
3, and cannot distinguish that from loss.

Gap detection is therefore only meaningful for a consumer subscribed to every stream from a
device. This holds for `fleet-ingest`, which subscribes across the fleet, but it is a
constraint worth stating: any future partial consumer must not attempt gap detection.

The alternative — a separate sequence space per stream — was rejected because it makes
cross-stream ordering ambiguous, and ordering between a status change and the telemetry
around it is exactly what a fleet console needs to get right.

## 3. Schema identifiers have to be enforced, not documented

The first run published `"schema": ""` on every message. The field was specified here and
declared in the payload type, and still shipped empty, because nothing asserted it.

Schema validation at the ingest boundary will make `schema` a required field, and the
simulator now has a test asserting it is populated. A contract that is only written down is
not a contract.
