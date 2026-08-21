# Architecture Decision Records

Each ADR records one decision, the context that forced it, the alternatives considered, and
the consequences accepted. ADRs are immutable once accepted: a reversal is a new ADR that
supersedes the old one, not an edit.

| # | Decision | Status |
|---|---|---|
| [0001](0001-contract-first-ui-boundary.md) | Contract-first REST + WebSocket as the only UI boundary | Accepted |
| [0002](0002-ingest-api-split.md) | Split MQTT ingest (Go) from the API tier (.NET) | Accepted |
| [0003](0003-telemetry-storage.md) | TimescaleDB for telemetry history | Accepted |
| [0004](0004-simulator-language.md) | Go for the device simulator, with a Rust pivot kept open | Accepted |
| [0005](0005-broker-and-protocol.md) | Mosquitto broker, MQTT 3.1.1 baseline | Accepted |
| [0006](0006-shared-client-state-core.md) | One shared .NET state core; per-ecosystem view-state idioms | Accepted |
| [0007](0007-linux-containers-windows-dashboards.md) | Linux containers for fleet and server tier; native Windows dashboards | Accepted |
| [0008](0008-delivery-semantics-and-projection-recovery.md) | Delivery semantics and projection recovery | Accepted |

## Template

```markdown
# ADR-NNNN: Title

- **Status:** Proposed | Accepted | Superseded by ADR-NNNN
- **Date:** YYYY-MM-DD

## Context
What forced a decision. Constraints, requirements, what was already true.

## Decision
What was decided, stated plainly.

## Rationale
Why this option and not the others.

## Consequences
What this makes easy, what it makes hard, what is now expensive to change.
```
