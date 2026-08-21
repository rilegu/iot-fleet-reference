using System.Text.Json.Serialization;

namespace ApiSpike.Fleet;

/// <summary>
/// Exploratory wire types, hand-written to mirror <c>contracts/README.md</c>. They are
/// replaced by models generated from JSON Schema once the contract is formalized; nothing
/// should be built on top of them in the meantime.
/// </summary>
public abstract class Envelope
{
    [JsonPropertyName("schema")] public string? Schema { get; init; }
    [JsonPropertyName("device_id")] public string DeviceId { get; init; } = "";
    [JsonPropertyName("site")] public string Site { get; init; } = "";
    [JsonPropertyName("boot_id")] public string BootId { get; init; } = "";
    [JsonPropertyName("seq")] public ulong Seq { get; init; }
    [JsonPropertyName("ts")] public DateTimeOffset Ts { get; init; }
    [JsonPropertyName("traceparent")] public string? Traceparent { get; init; }
}

public sealed class TelemetryMessage : Envelope
{
    [JsonPropertyName("metrics")] public Metrics Metrics { get; init; } = new();
}

public sealed class Metrics
{
    [JsonPropertyName("temp_c")] public double TempC { get; init; }
    [JsonPropertyName("humidity_pct")] public double HumidityPct { get; init; }
    [JsonPropertyName("voltage_v")] public double VoltageV { get; init; }
    [JsonPropertyName("rssi_dbm")] public int RssiDbm { get; init; }
    [JsonPropertyName("uptime_s")] public long UptimeS { get; init; }
}

public sealed class StatusMessage : Envelope
{
    [JsonPropertyName("online")] public bool Online { get; init; }
    [JsonPropertyName("reason")] public string? Reason { get; init; }
    [JsonPropertyName("fw_version")] public string? FwVersion { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
}

public static class StatusReason
{
    public const string Connect = "connect";
    public const string Shutdown = "shutdown";
    public const string Lwt = "lwt";
}
