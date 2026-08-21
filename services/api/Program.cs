using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using FleetApi.Fleet;
using FleetApi.Realtime;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// The projection is process-wide: the log consumer writes to it and every request and
// socket reads from it.
builder.Services.AddSingleton<FleetProjection>();
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddSingleton<FleetSocket>();
builder.Services.AddHostedService<LogConsumer>();

var databaseUrl = builder.Configuration["Database:Url"]
    ?? "Host=localhost;Port=5432;Database=fleet;Username=fleet;Password=fleet";
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(databaseUrl));
builder.Services.AddSingleton<HistoryStore>();

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

// ---------------------------------------------------------------------------------------
// Health
// ---------------------------------------------------------------------------------------

app.MapGet("/healthz", () => Results.Text("ok"));

// Readiness is not liveness. A restarting instance is alive long before it has replayed the
// log, and serving a partial fleet would be worse than refusing traffic.
app.MapGet("/readyz", (ReadinessState r) =>
    r.Ready
        ? Results.Text("ready")
        : Results.Text($"not ready: log={r.LogConnected} caught_up={r.CaughtUp}", statusCode: 503));

app.MapGet("/stats", (FleetProjection p, ReadinessState r) => Results.Ok(new
{
    ready = r.Ready,
    log_connected = r.LogConnected,
    caught_up = r.CaughtUp,
    devices = p.Count,
    applied = p.Applied,
    stale_dropped = p.StaleDropped,
    aggregates = p.Aggregates(),
}));

// ---------------------------------------------------------------------------------------
// Fleet queries, served from the in-memory projection
// ---------------------------------------------------------------------------------------

app.MapGet("/api/fleet", (FleetProjection p, string? site, bool? online, int? limit) =>
{
    IEnumerable<DeviceState> devices = p.Snapshot();

    if (!string.IsNullOrEmpty(site))
        devices = devices.Where(d => d.Site == site);
    if (online is not null)
        devices = devices.Where(d => d.Online == online.Value);

    devices = devices.OrderBy(d => d.DeviceId, StringComparer.Ordinal);
    if (limit is > 0)
        devices = devices.Take(limit.Value);

    return Results.Ok(new { aggregates = p.Aggregates(), devices = devices.ToArray() });
});

app.MapGet("/api/fleet/aggregates", (FleetProjection p) => Results.Ok(p.Aggregates()));

app.MapGet("/api/devices/{deviceId}", (FleetProjection p, string deviceId) =>
{
    var device = p.Get(deviceId);
    return device is null ? Results.NotFound() : Results.Ok(device);
});

// ---------------------------------------------------------------------------------------
// History, served from continuous aggregates rather than raw telemetry
// ---------------------------------------------------------------------------------------

app.MapGet("/api/devices/{deviceId}/history", async (
    HistoryStore store, string deviceId, int? minutes, CancellationToken ct) =>
{
    var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, 60 * 24 * 7));
    return Results.Ok(await store.DeviceHistoryAsync(deviceId, window, ct));
});

app.MapGet("/api/history/fleet", async (HistoryStore store, int? minutes, CancellationToken ct) =>
{
    var window = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 1, 60 * 24 * 7));
    return Results.Ok(await store.FleetHistoryAsync(window, ct));
});

app.MapGet("/api/events", async (HistoryStore store, string? device, int? limit, CancellationToken ct) =>
{
    var take = Math.Clamp(limit ?? 50, 1, 500);
    return Results.Ok(await store.EventsAsync(device, take, ct));
});

// ---------------------------------------------------------------------------------------
// Realtime
// ---------------------------------------------------------------------------------------

app.Map("/ws/fleet", async (HttpContext ctx, FleetSocket socket, ILogger<Program> log) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("expected a websocket upgrade");
        return;
    }

    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    try
    {
        await socket.HandleAsync(ws, ctx.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // client went away
    }
    catch (WebSocketException ex)
    {
        log.LogDebug("websocket closed: {Message}", ex.Message);
    }
});

app.Run();
