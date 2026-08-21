using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetApi.Fleet;

namespace FleetApi.Realtime;

/// <summary>
/// The realtime channel.
///
/// A thousand devices at 1 Hz is a thousand messages per second per connected dashboard.
/// Delivered as-is against a thousand-row grid that implies on the order of a million
/// row-renders per second, which no UI framework survives. So the server sends one snapshot
/// and then coalesced deltas at a fixed cadence: work is bounded by cadence and change
/// count, not by fleet size.
/// </summary>
public sealed class FleetSocket
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly FleetProjection _projection;
    private readonly ILogger<FleetSocket> _log;

    public FleetSocket(FleetProjection projection, ILogger<FleetSocket> log)
    {
        _projection = projection;
        _log = log;
    }

    public async Task HandleAsync(WebSocket socket, CancellationToken ct)
    {
        FleetTelemetry.SocketOpened();
        try
        {
            await PumpAsync(socket, ct);
        }
        finally
        {
            FleetTelemetry.SocketClosed();
        }
    }

    private async Task PumpAsync(WebSocket socket, CancellationToken ct)
    {
        var cadence = TimeSpan.FromMilliseconds(250);

        // A client may ask for a slower cadence, never a faster one. The server's rate is a
        // ceiling: a client cannot make the server do more work by asking.
        var subscribe = await ReadSubscribeAsync(socket, ct);
        if (subscribe?.MaxRateHz is > 0 and <= 20)
            cadence = TimeSpan.FromSeconds(1.0 / subscribe.MaxRateHz.Value);

        var frame = 0L;

        var snapshot = _projection.Snapshot().OrderBy(d => d.DeviceId, StringComparer.Ordinal).ToArray();
        await SendAsync(socket, new SnapshotFrame
        {
            Frame = ++frame,
            CadenceMs = (int)cadence.TotalMilliseconds,
            Devices = snapshot,
            Aggregates = _projection.Aggregates(),
        }, ct);
        FleetTelemetry.FramesSent.Add(1, new KeyValuePair<string, object?>("type", "snapshot"));
        FleetTelemetry.FrameDevices.Record(snapshot.Length,
            new KeyValuePair<string, object?>("type", "snapshot"));

        using var timer = new PeriodicTimer(cadence);

        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            if (!await timer.WaitForNextTickAsync(ct)) break;

            var changed = _projection.DrainDirty();
            var aggregates = _projection.Aggregates();

            // Send nothing when nothing changed. An idle fleet should cost an idle socket,
            // not a heartbeat carrying an unchanged snapshot.
            if (changed.Count == 0) continue;

            var devices = new List<DeviceState>(changed.Count);
            foreach (var id in changed)
            {
                var d = _projection.Get(id);
                if (d is not null) devices.Add(d);
            }

            await SendAsync(socket, new DeltaFrame
            {
                Frame = ++frame,
                Changed = devices,
                Aggregates = aggregates,
            }, ct);
            FleetTelemetry.FramesSent.Add(1, new KeyValuePair<string, object?>("type", "delta"));
            FleetTelemetry.FrameDevices.Record(devices.Count,
                new KeyValuePair<string, object?>("type", "delta"));
        }

        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
    }

    /// <summary>
    /// Reads an optional subscribe frame. A client that sends nothing gets server defaults,
    /// so the simplest possible client is just "connect and read".
    /// </summary>
    private async Task<SubscribeFrame?> ReadSubscribeAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[1024];
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(TimeSpan.FromMilliseconds(250));

        try
        {
            var result = await socket.ReceiveAsync(buffer, readCts.Token);
            if (result.MessageType != WebSocketMessageType.Text || result.Count == 0) return null;
            return JsonSerializer.Deserialize<SubscribeFrame>(
                Encoding.UTF8.GetString(buffer, 0, result.Count), Json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // no subscribe frame sent; defaults apply
        }
        catch (Exception ex)
        {
            _log.LogDebug("ignoring unreadable subscribe frame: {Message}", ex.Message);
            return null;
        }
    }

    private static async Task SendAsync<T>(WebSocket socket, T frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
    }
}

public sealed class SubscribeFrame
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("max_rate_hz")] public double? MaxRateHz { get; init; }
}

public sealed class SnapshotFrame
{
    [JsonPropertyName("type")] public string Type => "snapshot";
    [JsonPropertyName("frame")] public long Frame { get; init; }
    [JsonPropertyName("cadence_ms")] public int CadenceMs { get; init; }
    [JsonPropertyName("devices")] public IReadOnlyList<DeviceState> Devices { get; init; } = Array.Empty<DeviceState>();
    [JsonPropertyName("aggregates")] public FleetAggregates Aggregates { get; init; } = new();
}

public sealed class DeltaFrame
{
    [JsonPropertyName("type")] public string Type => "delta";
    [JsonPropertyName("frame")] public long Frame { get; init; }
    [JsonPropertyName("changed")] public IReadOnlyList<DeviceState> Changed { get; init; } = Array.Empty<DeviceState>();
    [JsonPropertyName("aggregates")] public FleetAggregates Aggregates { get; init; } = new();
}
