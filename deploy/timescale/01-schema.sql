-- Telemetry storage.
--
-- Two shapes on purpose. Telemetry is a high-volume append-only stream and lives in a
-- hypertable. Status and events are low-volume state transitions that must survive
-- redelivery unchanged, so they carry a real primary key and are written with
-- ON CONFLICT DO NOTHING.

CREATE EXTENSION IF NOT EXISTS timescaledb;

-- Device registry. Upserted from status messages; the source of truth for identity and
-- firmware, which telemetry does not carry.
CREATE TABLE IF NOT EXISTS device (
    device_id   text PRIMARY KEY,
    site        text        NOT NULL,
    model       text,
    fw_version  text,
    first_seen  timestamptz NOT NULL DEFAULT now(),
    last_seen   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS device_site_idx ON device (site);

-- Raw telemetry.
--
-- `time` is server-side arrival, never the device clock: devices drift, reboot without a
-- real-time clock, and can step their clock outright. `device_ts` keeps what the device
-- claimed, as data.
--
-- No unique key. Telemetry is published at QoS 0, so the broker never redelivers it, and
-- ingest writes each batch in a single transaction so a retry after a partial failure
-- cannot double-insert. Duplicates that do slip through are absorbed by the projection,
-- which applies (boot_id, seq) ordering.
CREATE TABLE IF NOT EXISTS telemetry (
    time         timestamptz      NOT NULL,
    device_id    text             NOT NULL,
    site         text             NOT NULL,
    boot_id      text             NOT NULL,
    seq          bigint           NOT NULL,
    device_ts    timestamptz,
    temp_c       double precision,
    humidity_pct double precision,
    voltage_v    double precision,
    rssi_dbm     integer,
    uptime_s     bigint
);

SELECT create_hypertable('telemetry', 'time', if_not_exists => TRUE);

CREATE INDEX IF NOT EXISTS telemetry_device_time_idx ON telemetry (device_id, time DESC);

-- Presence transitions. Primary key gives genuine idempotency under redelivery.
--
-- A Last Will carries seq 0 because it is composed before the device knows its final
-- sequence number, so (device_id, boot_id, 0) can legitimately repeat across a reconnect
-- cycle. `received_at` disambiguates without weakening the key for live messages.
CREATE TABLE IF NOT EXISTS device_status (
    device_id   text        NOT NULL,
    boot_id     text        NOT NULL,
    seq         bigint      NOT NULL,
    received_at timestamptz NOT NULL,
    site        text        NOT NULL,
    online      boolean     NOT NULL,
    reason      text,
    fw_version  text,
    model       text,
    device_ts   timestamptz,
    PRIMARY KEY (device_id, boot_id, seq, received_at)
);

CREATE INDEX IF NOT EXISTS device_status_recent_idx ON device_status (device_id, received_at DESC);

CREATE TABLE IF NOT EXISTS device_event (
    device_id   text        NOT NULL,
    boot_id     text        NOT NULL,
    seq         bigint      NOT NULL,
    received_at timestamptz NOT NULL,
    site        text        NOT NULL,
    kind        text        NOT NULL,
    severity    text        NOT NULL,
    detail      text,
    metric      text,
    value       double precision,
    device_ts   timestamptz,
    PRIMARY KEY (device_id, boot_id, seq)
);

CREATE INDEX IF NOT EXISTS device_event_recent_idx ON device_event (received_at DESC);

-- Payloads that failed schema validation. Sampled rather than exhaustive: under a
-- misbehaving fleet this could otherwise become the highest-volume table in the database.
CREATE TABLE IF NOT EXISTS dead_letter (
    received_at timestamptz NOT NULL DEFAULT now(),
    topic       text        NOT NULL,
    reason      text        NOT NULL,
    payload     bytea
);

CREATE INDEX IF NOT EXISTS dead_letter_recent_idx ON dead_letter (received_at DESC);
