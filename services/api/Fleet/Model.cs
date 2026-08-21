using System.Text.Json.Serialization;

namespace FleetApi.Fleet;

/// <summary>
/// Message kinds, matching the topic suffix, the schema file names and the NATS subject
/// segment.
/// </summary>
public static class Kind
{
    public const string Telemetry = "telemetry";
    public const string Status = "status";
    public const string Event = "event";
}

public sealed class Metrics
{
    [JsonPropertyName("temp_c")] public double TempC { get; init; }
    [JsonPropertyName("humidity_pct")] public double HumidityPct { get; init; }
    [JsonPropertyName("voltage_v")] public double VoltageV { get; init; }
    [JsonPropertyName("rssi_dbm")] public int RssiDbm { get; init; }
    [JsonPropertyName("uptime_s")] public long UptimeS { get; init; }
}

/// <summary>Envelope fields shared by every message. See <c>contracts/schemas/</c>.</summary>
public abstract class Envelope
{
    [JsonPropertyName("schema")] public string Schema { get; init; } = "";
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("site")] public string Site { get; init; } = "";
    [JsonPropertyName("boot_id")] public string BootId { get; init; } = "";
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; init; }
    [JsonPropertyName("traceparent")] public string? Traceparent { get; init; }
}

public sealed class TelemetryMessage : Envelope
{
    [JsonPropertyName("metrics")] public Metrics Metrics { get; init; } = new();
}

public sealed class StatusMessage : Envelope
{
    [JsonPropertyName("online")] public bool Online { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("fw_version")] public string? FwVersion { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
}

public sealed class EventMessage : Envelope
{
    [JsonPropertyName("kind")] public string EventKind { get; init; } = "";
    [JsonPropertyName("severity")] public string Severity { get; init; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; init; }
    [JsonPropertyName("metric")] public string? Metric { get; init; }
    [JsonPropertyName("value")] public double? Value { get; init; }
}

/// <summary>
/// A device as the dashboard sees it. Immutable: the projection swaps whole records rather
/// than mutating shared state, so a reader never observes a half-applied update.
/// </summary>
public sealed record DeviceState
{
    [JsonPropertyName("device_id")] public required string DeviceId { get; init; }
    [JsonPropertyName("site")] public required string Site { get; init; }
    [JsonPropertyName("boot_id")] public string BootId { get; init; } = "";
    [JsonPropertyName("online")] public bool Online { get; init; }
    [JsonPropertyName("offline_reason")] public string? OfflineReason { get; init; }
    [JsonPropertyName("fw_version")] public string? FwVersion { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("gaps")] public long Gaps { get; init; }
    [JsonPropertyName("metrics")] public Metrics? Metrics { get; init; }

    /// <summary>Last alerting event, so the grid can show a device is unhealthy without a second query.</summary>
    [JsonPropertyName("last_event")] public string? LastEvent { get; init; }
    [JsonPropertyName("last_event_severity")] public string? LastEventSeverity { get; init; }

    /// <summary>Server-side arrival time. Device clocks are never used for ordering or age.</summary>
    [JsonPropertyName("last_seen")] public DateTimeOffset LastSeen { get; init; }

    /// <summary>
    /// True while everything known about this device came from a retained replay rather than
    /// a live message, so its sequence is a historical baseline and the next live message
    /// must not be scored against it.
    /// </summary>
    [JsonIgnore] public bool Provisional { get; init; }
}

/// <summary>Fleet-wide numbers, precomputed so every client does not derive them itself.</summary>
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
