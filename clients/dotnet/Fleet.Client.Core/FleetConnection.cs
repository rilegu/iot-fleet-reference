using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Client.Core;

public sealed class FleetClientOptions
{
    /// <summary>Base address of the API, for example http://localhost:8080.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// Frames per second to request. The server treats this as a ceiling it will honour by
    /// slowing down, never as a request to speed up — a client cannot make the server work
    /// harder by asking.
    /// </summary>
    public double MaxRateHz { get; set; } = 4;
}

/// <summary>
/// Transport for one client session: REST for queries, a WebSocket for live state.
///
/// One connection per client, not one shared across all viewers. That mirrors what a
/// desktop client does and keeps the framework comparison fair — each dashboard pays the
/// same transport cost rather than one of them benefiting from a shared feed.
/// </summary>
public sealed class FleetConnection : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly FleetStore _store;
    private readonly FleetClientOptions _options;
    private readonly HttpClient _http;
    private readonly ILogger _log;
    private CancellationTokenSource? _cts;
    private Task? _pump;

    public FleetConnection(FleetStore store, FleetClientOptions options, HttpClient? http = null, ILogger<FleetConnection>? log = null)
    {
        _store = store;
        _options = options;
        _http = http ?? new HttpClient();
        _http.BaseAddress = new Uri(options.BaseUrl);
        // A connection that fails silently is undiagnosable once this runs anywhere but a
        // developer's machine — in a container the log is the only window into it.
        _log = (ILogger?)log ?? NullLogger.Instance;
    }

    /// <summary>Connection state, surfaced so the UI can say so rather than silently showing stale data.</summary>
    public bool Connected { get; private set; }

    public string? LastError { get; private set; }

    public event Action? StateChanged;

    public void Start()
    {
        if (_pump is not null) return;
        _log.LogInformation("fleet connection starting against {BaseUrl}", _options.BaseUrl);
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>
    /// Reconnect loop.
    ///
    /// A dashboard that dies when the API restarts is useless during exactly the events an
    /// operator cares about, so a dropped socket is a normal condition rather than an error.
    /// Backoff is capped and jittered: without jitter, every client reconnects on the same
    /// tick after an outage and the API gets a thundering herd at the worst moment.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        var jitter = new Random();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(ct);
                attempt = 0; // a clean session resets the backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                _log.LogWarning("fleet connection failed: {Message}", ex.Message);
            }

            Connected = false;
            StateChanged?.Invoke();

            attempt = Math.Min(attempt + 1, 6);
            var backoff = TimeSpan.FromMilliseconds(
                Math.Min(500 * Math.Pow(2, attempt), 15_000) + jitter.Next(0, 400));

            try { await Task.Delay(backoff, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();

        // Derive the websocket scheme from the configured base address rather than string
        // surgery on the whole URL: replacing "http" anywhere in the string would also
        // rewrite a host that happens to contain it.
        var baseUri = new Uri(_options.BaseUrl);
        var wsUri = new UriBuilder(baseUri)
        {
            Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
            Path = "/ws/fleet",
        }.Uri;

        _log.LogInformation("opening {Uri}", wsUri);
        await socket.ConnectAsync(wsUri, ct);

        // Ask for a cadence. Sending this is optional — a client that says nothing gets
        // server defaults — but stating it makes the client's expectations explicit and is
        // what the framework comparison varies.
        var subscribe = JsonSerializer.SerializeToUtf8Bytes(
            new { type = "subscribe", max_rate_hz = _options.MaxRateHz });
        await socket.SendAsync(subscribe, WebSocketMessageType.Text, true, ct);

        Connected = true;
        LastError = null;
        _log.LogInformation("fleet connection established");
        StateChanged?.Invoke();

        // A snapshot of a thousand devices is around 300 KB, well past any sensible
        // single-read buffer, so frames are accumulated until the socket reports the end of
        // the message rather than assuming one read is one frame.
        var buffer = new byte[32 * 1024];
        var accumulator = new MemoryStream();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            accumulator.SetLength(0);

            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
                    return;
                }
                accumulator.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            ApplyFrame(accumulator.ToArray());
        }
    }

    private void ApplyFrame(byte[] payload)
    {
        var frame = JsonSerializer.Deserialize<ServerFrame>(payload, Json);
        if (frame is null) return;

        switch (frame.Type)
        {
            case "snapshot":
                _store.ApplySnapshot(frame.Devices ?? new(), frame.Aggregates, frame.CadenceMs, frame.Frame);
                break;
            case "delta":
                _store.ApplyDelta(frame.Changed ?? new(), frame.Aggregates, frame.Frame);
                break;
        }
    }

    // ---------------------------------------------------------------------------------
    // REST queries. Live state comes over the socket; these are for what the socket does
    // not carry — history and the event feed, which are read on demand rather than pushed.
    // ---------------------------------------------------------------------------------

    public async Task<IReadOnlyList<TelemetryPoint>> DeviceHistoryAsync(string deviceId, int minutes, CancellationToken ct = default) =>
        await _http.GetFromJsonAsync<List<TelemetryPoint>>(
            $"/api/devices/{Uri.EscapeDataString(deviceId)}/history?minutes={minutes}", Json, ct)
        ?? new List<TelemetryPoint>();

    public async Task<IReadOnlyList<DeviceEvent>> EventsAsync(string? deviceId = null, int limit = 50, CancellationToken ct = default)
    {
        var url = deviceId is null
            ? $"/api/events?limit={limit}"
            : $"/api/events?device={Uri.EscapeDataString(deviceId)}&limit={limit}";
        return await _http.GetFromJsonAsync<List<DeviceEvent>>(url, Json, ct) ?? new List<DeviceEvent>();
    }

    /// <summary>
    /// Cancels the pump and waits for it before releasing the token source.
    ///
    /// The order matters: disposing a CancellationTokenSource while a task still holds its
    /// token makes every later use of that token throw ObjectDisposedException, and here
    /// that surfaced as an unhandled exception during scope teardown rather than as a
    /// quiet shutdown. Each step is also guarded, because disposal must not be able to fail
    /// — it runs while something else is already going away.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* already torn down */ }

        if (_pump is not null)
        {
            try { await _pump; } catch { /* shutting down */ }
            _pump = null;
        }

        try { _cts?.Dispose(); } catch (ObjectDisposedException) { }
        _cts = null;

        Connected = false;
        _http.Dispose();
    }
}
