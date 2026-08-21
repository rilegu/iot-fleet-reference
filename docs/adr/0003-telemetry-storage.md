# ADR-0003: TimescaleDB for telemetry history

- **Status:** Accepted
- **Date:** 2026-08-20

## Context

Telemetry history has to answer two very different questions: "what is device X doing right
now" and "chart the last 24 hours across the whole fleet". Choosing a single store for both
is the classic mistake.

### What commercial IoT products and SaaS actually use

Real deployments tier their storage rather than picking one database:

| Tier | Purpose | Commonly used |
|---|---|---|
| Hot | current device state, last minutes | in-process cache, Redis, Postgres |
| Warm | queryable history, dashboard rollups | TimescaleDB, ClickHouse, InfluxDB, Azure Data Explorer, MongoDB time-series |
| Cold | archive, replay, analytics | Parquet on S3/ADLS, BigQuery |

Managed cloud pipelines follow the same shape: AWS IoT Core into Kinesis into Timestream or
S3+Athena; Azure IoT Hub into Event Hubs into Azure Data Explorer; GCP Pub/Sub into
BigQuery. At very large fleet sizes, Cassandra or ScyllaDB partitioned per device is the
traditional answer.

Among the self-hostable warm-tier options:

- **TimescaleDB** — a Postgres extension. Widely used by IoT SaaS built on Postgres.
  Hypertables, continuous aggregates, native compression, retention policies. Keeps the
  entire relational toolchain (joins to device metadata, migrations, EF Core, psql).
- **ClickHouse** — the current default for very high volume telemetry and observability
  products. Outstanding compression and scan speed, weaker for transactional metadata and
  frequent single-row updates.
- **InfluxDB** — the classic IoT/metrics TSDB, with a significant query-language
  discontinuity across major versions.

## Decision

**TimescaleDB is the warm tier.** In-process projection in `fleet-api` is the hot tier.
Optional Parquet export is the cold tier.

`fleet-ingest` writes through a `TelemetryStore` port so the warm tier is replaceable.

## Rationale

- It is genuinely what comparable commercial IoT systems use, which is the point of a
  reference implementation.
- Device metadata, fleet grouping, OTA campaigns and audit records are relational data. On
  Postgres they live in the same database as telemetry with real foreign keys, instead of
  being split across two systems.
- Continuous aggregates map exactly onto the dashboard's rollups, so "last 24 hours across
  1000 devices" reads a precomputed materialized view rather than scanning raw rows.
- Compression and retention policies are declarative and can be configured from day one,
  so storage growth is bounded by policy rather than left unmanaged.
- It runs well in a single Docker container on a developer workstation.

## Consequences

**Positive**

- One database for telemetry and metadata; one migration story; one backup story.
- Dashboard queries stay fast without hand-rolled rollup tables.
- Familiar to anyone who reads the repository.

**Negative**

- Postgres will not match ClickHouse on very high-cardinality, very high-volume scans. Well
  beyond this project's targets, but worth stating.
- TimescaleDB community edition ships under the Timescale License, not Apache-2.0. It runs
  as a separate process and does not affect this repository's own licensing, but the
  distinction should be documented in `deploy/`.

## Alternatives kept open

A ClickHouse adapter behind `TelemetryStore` is the documented path for a high-volume
variant. The same port maps onto Event Hubs into Azure Data Explorer if a cloud-hosted
variant is ever built.
