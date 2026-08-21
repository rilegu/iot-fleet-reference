using System.Text.Json.Serialization;

namespace Fleet.Client.Core;

// Wire types for the API's REST and WebSocket surface.
//
// These are hand-written for now and mirror the API's own types. Once OpenAPI and AsyncAPI
// documents exist they become generated code, which is the point of the contracts
// directory: no client should be able to drift from the server by editing a model.
//
// They are deliberately plain and immutable. The store swaps whole records rather than
// mutating them in place, so a render pass can never observe a half-applied device.

public sealed record Metrics
{
    [JsonPropertyName("temp_c")] public double TempC { get; init; }
    [JsonPropertyName("humidity_pct")] public double HumidityPct { get; init; }
    [JsonPropertyName("voltage_v")] public double VoltageV { get; init; }
    [JsonPropertyName("rssi_dbm")] public int RssiDbm { get; init; }
    [JsonPropertyName("uptime_s")] public long UptimeS { get; init; }
}

public sealed record DeviceState
{
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("site")] public string Site { get; init; } = "";
    [JsonPropertyName("boot_id")] public string BootId { get; init; } = "";
    [JsonPropertyName("online")] public bool Online { get; init; }
    [JsonPropertyName("offline_reason")] public string? OfflineReason { get; init; }
    [JsonPropertyName("fw_version")] public string? FwVersion { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("gaps")] public long Gaps { get; init; }
    [JsonPropertyName("metrics")] public Metrics? Metrics { get; init; }
    [JsonPropertyName("last_event")] public string? LastEvent { get; init; }
    [JsonPropertyName("last_event_severity")] public string? LastEventSeverity { get; init; }
    [JsonPropertyName("last_seen")] public DateTimeOffset LastSeen { get; init; }

    /// <summary>
    /// True when this device changed in the most recent delta frame. Purely a view concern:
    /// the grid uses it to flash a row, and it is cleared on the next frame.
    /// </summary>
    [JsonIgnore] public bool JustChanged { get; init; }
}

public sealed record FleetAggregates
{
    [JsonPropertyName("total")] public int Total { get; init; }
    [JsonPropertyName("online")] public int Online { get; init; }
    [JsonPropertyName("offline")] public int Offline { get; init; }
    [JsonPropertyName("alerting")] public int Alerting { get; init; }
    [JsonPropertyName("gaps")] public long Gaps { get; init; }
    [JsonPropertyName("applied")] public long Applied { get; init; }
    [JsonPropertyName("stale_dropped")] public long StaleDropped { get; init; }
    [JsonPropertyName("sites")] public int Sites { get; init; }
}

/// <summary>
/// Frames arriving on the WebSocket. A snapshot carries the whole fleet once; every frame
/// after it carries only what changed.
/// </summary>
public sealed record ServerFrame
{
    [JsonPropertyName("type")] public string Type { get; init; } = "";
    [JsonPropertyName("frame")] public long Frame { get; init; }
    [JsonPropertyName("cadence_ms")] public int CadenceMs { get; init; }
    [JsonPropertyName("devices")] public List<DeviceState>? Devices { get; init; }
    [JsonPropertyName("changed")] public List<DeviceState>? Changed { get; init; }
    [JsonPropertyName("aggregates")] public FleetAggregates? Aggregates { get; init; }
}

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

public sealed record DeviceEvent
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
