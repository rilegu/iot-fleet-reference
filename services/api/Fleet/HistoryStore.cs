using System.Text.Json.Serialization;
using Npgsql;

namespace FleetApi.Fleet;

public sealed record TelemetryPoint
{
    [JsonPropertyName("bucket")] public DateTimeOffset Bucket { get; init; }
    [JsonPropertyName("samples")] public long Samples { get; init; }
    [JsonPropertyName("temp_c_avg")] public double? TempCAvg { get; init; }
    [JsonPropertyName("temp_c_max")] public double? TempCMax { get; init; }
    [JsonPropertyName("humidity_pct_avg")] public double? HumidityPctAvg { get; init; }
    [JsonPropertyName("voltage_v_avg")] public double? VoltageVAvg { get; init; }
    [JsonPropertyName("voltage_v_min")] public double? VoltageVMin { get; init; }
    [JsonPropertyName("rssi_dbm_avg")] public double? RssiDbmAvg { get; init; }
}

public sealed record FleetPoint
{
    [JsonPropertyName("bucket")] public DateTimeOffset Bucket { get; init; }
    [JsonPropertyName("site")] public string Site { get; init; } = "";
    [JsonPropertyName("devices_reporting")] public long DevicesReporting { get; init; }
    [JsonPropertyName("samples")] public long Samples { get; init; }
    [JsonPropertyName("temp_c_avg")] public double? TempCAvg { get; init; }
}

public sealed record DeviceEventRow
{
    [JsonPropertyName("received_at")] public DateTimeOffset ReceivedAt { get; init; }
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("site")] public string Site { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("severity")] public string Severity { get; init; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("metric")] public string? Metric { get; init; }
    [JsonPropertyName("value")] public double? Value { get; init; }
}

/// <summary>
/// Historical queries.
///
/// Charts read continuous aggregates, never raw telemetry. "Last 24 hours across the fleet"
/// against the raw hypertable scans every row; against the aggregate it reads a
/// materialized view Timescale keeps current incrementally.
/// </summary>
public sealed class HistoryStore
{
    private readonly NpgsqlDataSource _db;

    public HistoryStore(NpgsqlDataSource db) => _db = db;

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var cmd = _db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Seeds the projection with the last known identity and presence of every device.
    ///
    /// The durable log consumer recovers anything that happened inside the stream's
    /// retention window, but a device silent for longer than that has no messages left to
    /// replay and would be missing from the projection entirely. The database has no such
    /// horizon, so it supplies the floor and the log brings it current.
    ///
    /// Deliberately does not read telemetry: metrics arrive from the log within a second,
    /// and a latest-row-per-device query over the hypertable is far more expensive than the
    /// bounded query below.
    /// </summary>
    public async Task<IReadOnlyList<StatusMessage>> LoadPresenceSeedAsync(CancellationToken ct)
    {
        // DISTINCT ON gives the most recent row per device in one pass. Ordering is by
        // received_at, the server-side clock, never the device's own timestamp.
        const string sql = """
            SELECT DISTINCT ON (s.device_id)
                   s.device_id, s.site, s.boot_id, s.seq, s.online, s.reason,
                   COALESCE(s.fw_version, d.fw_version), COALESCE(s.model, d.model)
            FROM device_status s
            LEFT JOIN device d ON d.device_id = s.device_id
            ORDER BY s.device_id, s.received_at DESC
            """;

        await using var cmd = _db.CreateCommand(sql);
        var rows = new List<StatusMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new StatusMessage
            {
                Schema = "status/1",
                DeviceId = reader.GetString(0),
                Site = reader.GetString(1),
                BootId = reader.GetString(2),
                Seq = reader.GetInt64(3),
                Online = reader.GetBoolean(4),
                Reason = reader.IsDBNull(5) ? null : reader.GetString(5),
                FwVersion = reader.IsDBNull(6) ? null : reader.GetString(6),
                Model = reader.IsDBNull(7) ? null : reader.GetString(7),
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<TelemetryPoint>> DeviceHistoryAsync(
        string deviceId, TimeSpan window, CancellationToken ct)
    {
        const string sql = """
            SELECT bucket, samples, temp_c_avg, temp_c_max, humidity_pct_avg,
                   voltage_v_avg, voltage_v_min, rssi_dbm_avg
            FROM telemetry_1m
            WHERE device_id = $1 AND bucket >= now() - $2::interval
            ORDER BY bucket
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(deviceId);
        cmd.Parameters.AddWithValue($"{(int)window.TotalMinutes} minutes");

        var rows = new List<TelemetryPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new TelemetryPoint
            {
                Bucket = reader.GetFieldValue<DateTimeOffset>(0),
                Samples = reader.GetInt64(1),
                TempCAvg = reader.IsDBNull(2) ? null : reader.GetDouble(2),
                TempCMax = reader.IsDBNull(3) ? null : reader.GetDouble(3),
                HumidityPctAvg = reader.IsDBNull(4) ? null : reader.GetDouble(4),
                VoltageVAvg = reader.IsDBNull(5) ? null : reader.GetDouble(5),
                VoltageVMin = reader.IsDBNull(6) ? null : reader.GetDouble(6),
                RssiDbmAvg = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<FleetPoint>> FleetHistoryAsync(TimeSpan window, CancellationToken ct)
    {
        const string sql = """
            SELECT bucket, site, devices_reporting, samples, temp_c_avg
            FROM fleet_1m
            WHERE bucket >= now() - $1::interval
            ORDER BY bucket DESC, site
            """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue($"{(int)window.TotalMinutes} minutes");

        var rows = new List<FleetPoint>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new FleetPoint
            {
                Bucket = reader.GetFieldValue<DateTimeOffset>(0),
                Site = reader.GetString(1),
                DevicesReporting = reader.GetInt64(2),
                Samples = reader.GetInt64(3),
                TempCAvg = reader.IsDBNull(4) ? null : reader.GetDouble(4),
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<DeviceEventRow>> EventsAsync(
        string? deviceId, int limit, CancellationToken ct)
    {
        var sql = deviceId is null
            ? """
              SELECT received_at, device_id, site, kind, severity, detail, metric, value
              FROM device_event ORDER BY received_at DESC LIMIT $1
              """
            : """
              SELECT received_at, device_id, site, kind, severity, detail, metric, value
              FROM device_event WHERE device_id = $2 ORDER BY received_at DESC LIMIT $1
              """;

        await using var cmd = _db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(limit);
        if (deviceId is not null) cmd.Parameters.AddWithValue(deviceId);

        var rows = new List<DeviceEventRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DeviceEventRow
            {
                ReceivedAt = reader.GetFieldValue<DateTimeOffset>(0),
                DeviceId = reader.GetString(1),
                Site = reader.GetString(2),
                Kind = reader.GetString(3),
                Severity = reader.GetString(4),
                Detail = reader.IsDBNull(5) ? null : reader.GetString(5),
                Metric = reader.IsDBNull(6) ? null : reader.GetString(6),
                Value = reader.IsDBNull(7) ? null : reader.GetDouble(7),
            });
        }
        return rows;
    }
}
