-- Continuous aggregates.
--
-- The dashboard charts rollups, not raw samples. Without these, "last 24 hours across the
-- fleet" scans every row; with them it reads a materialized view that Timescale keeps
-- current incrementally.

CREATE MATERIALIZED VIEW IF NOT EXISTS telemetry_1m
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 minute', time) AS bucket,
    device_id,
    site,
    count(*)            AS samples,
    avg(temp_c)         AS temp_c_avg,
    max(temp_c)         AS temp_c_max,
    avg(humidity_pct)   AS humidity_pct_avg,
    avg(voltage_v)      AS voltage_v_avg,
    min(voltage_v)      AS voltage_v_min,
    avg(rssi_dbm)       AS rssi_dbm_avg,
    min(rssi_dbm)       AS rssi_dbm_min
FROM telemetry
GROUP BY bucket, device_id, site
WITH NO DATA;

-- Refresh trails one minute behind so a bucket is only materialized once it is closed,
-- and covers the last hour on each run to absorb late arrivals from a reconnecting device.
SELECT add_continuous_aggregate_policy('telemetry_1m',
    start_offset      => INTERVAL '1 hour',
    end_offset        => INTERVAL '1 minute',
    schedule_interval => INTERVAL '1 minute',
    if_not_exists     => TRUE);

-- Fleet-wide rollup for the dashboard's headline numbers, derived from the per-device
-- aggregate rather than from raw telemetry.
CREATE MATERIALIZED VIEW IF NOT EXISTS fleet_1m
WITH (timescaledb.continuous) AS
SELECT
    time_bucket('1 minute', bucket) AS bucket,
    site,
    count(DISTINCT device_id) AS devices_reporting,
    sum(samples)              AS samples,
    avg(temp_c_avg)           AS temp_c_avg,
    min(voltage_v_min)        AS voltage_v_min
FROM telemetry_1m
GROUP BY time_bucket('1 minute', bucket), site
WITH NO DATA;

SELECT add_continuous_aggregate_policy('fleet_1m',
    start_offset      => INTERVAL '1 hour',
    end_offset        => INTERVAL '2 minutes',
    schedule_interval => INTERVAL '1 minute',
    if_not_exists     => TRUE);

-- Lifecycle. Configured now rather than bolted on later: unbounded growth is the default
-- failure mode of telemetry storage, and the policies are part of what makes the retention
-- story real.
SELECT add_retention_policy('telemetry', INTERVAL '30 days', if_not_exists => TRUE);
SELECT add_retention_policy('telemetry_1m', INTERVAL '365 days', if_not_exists => TRUE);

ALTER TABLE telemetry SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'device_id',
    timescaledb.compress_orderby   = 'time DESC'
);

SELECT add_compression_policy('telemetry', INTERVAL '7 days', if_not_exists => TRUE);
