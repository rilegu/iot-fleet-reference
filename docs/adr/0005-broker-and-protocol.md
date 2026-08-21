# ADR-0005: Mosquitto broker, MQTT 3.1.1 baseline

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

The fleet needs a broker that runs in a container on a workstation, handles 1000+
concurrent connections, supports TLS and per-device ACLs, and speaks the MQTT version real
embedded devices use.

MQTT 3.1 was named as acceptable. In practice "3.1" almost always means **3.1.1**, which is
the OASIS standard version and what nearly all embedded clients ship. Version 5 adds session
expiry, shared subscriptions, reason codes and user properties, none of which this design
depends on.

## Decision

- **Broker:** Eclipse Mosquitto.
- **Protocol baseline:** MQTT **3.1.1**, with the client abstraction written so a v5
  variant can be enabled per device later.

## Rationale

**Mosquitto**

- It is what actually runs on embedded gateways and small fleets, so the configuration in
  `deploy/mosquitto/` is transferable knowledge rather than a demo artifact.
- Small, single-binary, trivial to run in Compose.
- File-based ACLs and TLS configuration are straightforward and reviewable — which matters
  because the ACL rules are part of the reference itself, not scaffolding around it.
- 1000 connections at 1 Hz is far inside its comfortable range.

Alternatives: **EMQX** offers clustering, a built-in dashboard and richer metrics, but its
own dashboard would compete with the dashboards this project exists to build. **NanoMQ**
targets edge/embedded deployment. **VerneMQ** targets clustered scale. Any of these is a
reasonable later swap; none changes the device or API contract.

**MQTT 3.1.1**

- Maximum device-side realism and client-library compatibility, including the C99 agent.
- The features v5 adds are not load-bearing here: presence is handled by retained status
  plus LWT, and command correlation is carried in the payload (`cmdId`) rather than in v5
  response topics and correlation data.
- Keeping correlation in the payload means the C agent and any constrained client need only
  a minimal MQTT implementation.

## Consequences

**Positive**

- Broadest client compatibility across Go, C, Python and .NET.
- Broker configuration that mirrors real small-fleet deployments.
- No dependency on broker-specific features, so the broker stays swappable.

**Negative**

- No shared subscriptions, so scaling ingest horizontally would need either broker-side
  support (an EMQX swap) or partitioning the wildcard subscription by site prefix. The
  topic namespace is already site-partitioned, which makes that straightforward.
- No session expiry semantics; stale sessions are managed by broker configuration instead.

## Notes

- TLS on 8883 with a project CA is the default. Plaintext 1883 is available only under the
  `dev` Compose profile.
- `max_connections` and per-listener limits must be raised explicitly; the defaults will not
  accommodate 1000 devices. Configured in `deploy/mosquitto/`.
