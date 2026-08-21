using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;

namespace FleetApi.Fleet;

public sealed class LogOptions
{
    public string Url { get; set; } = "nats://localhost:4222";
    public string Stream { get; set; } = "FLEET";
    public string Consumer { get; set; } = "fleet-api";
}

/// <summary>
/// Consumes the durable log and applies it to the projection.
///
/// Acknowledgement happens after the apply, never before. A crash between the two means the
/// message is redelivered, and because apply is idempotent, redelivery is safe. The reverse
/// order would lose the update silently.
/// </summary>
public sealed class LogConsumer : BackgroundService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly FleetProjection _projection;
    private readonly LogOptions _options;
    private readonly ILogger<LogConsumer> _log;
    private readonly ReadinessState _readiness;
    private readonly HistoryStore _history;

    public LogConsumer(FleetProjection projection, IConfiguration config, ReadinessState readiness, HistoryStore history, ILogger<LogConsumer> log)
    {
        _projection = projection;
        _readiness = readiness;
        _history = history;
        _log = log;
        _options = config.GetSection("Log").Get<LogOptions>() ?? new LogOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SeedFromDatabaseAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ConsumeAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _readiness.LogConnected = false;
                _log.LogWarning("log consumer failed, retrying: {Message}", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Establishes the floor before replaying the log.
    ///
    /// Seeded devices are marked as a baseline rather than a live observation, so the first
    /// real message for each does not register as a sequence gap — the same rule that makes
    /// a retained MQTT replay safe.
    /// </summary>
    private async Task SeedFromDatabaseAsync(CancellationToken ct)
    {
        try
        {
            var seed = await _history.LoadPresenceSeedAsync(ct);
            foreach (var status in seed)
                _projection.ApplyStatus(status, retained: true);

            _readiness.DatabaseConnected = true;
            _log.LogInformation("seeded {Count} devices from the database", seed.Count);
        }
        catch (Exception ex)
        {
            // Not fatal. The log alone recovers every device active within the retention
            // window, which is all of them in normal operation; the seed only matters for
            // devices silent for longer than that.
            _readiness.DatabaseConnected = false;
            _log.LogWarning("could not seed from the database, relying on log replay alone: {Message}", ex.Message);
        }
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await using var client = new NatsClient(_options.Url);
        var js = client.CreateJetStreamContext();

        // A durable consumer with an explicit ack policy. Durable so a restart resumes from
        // the last acknowledged message rather than replaying everything or, worse, only
        // seeing what arrives after startup.
        var consumer = await js.CreateOrUpdateConsumerAsync(_options.Stream, new ConsumerConfig
        {
            DurableName = _options.Consumer,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            MaxAckPending = 4096,
            FilterSubject = "fleet.>",
        }, cancellationToken: ct);

        _readiness.LogConnected = true;
        _log.LogInformation("consuming {Stream} as {Consumer}", _options.Stream, _options.Consumer);

        // Catching up is not the same as being ready. A restarting instance must not serve
        // a partial fleet, so readiness waits until the backlog is drained.
        await consumer.RefreshAsync(ct);
        var backlog = (long)consumer.Info.NumPending;
        if (backlog > 0)
            _log.LogInformation("replaying {Backlog} messages before serving", backlog);

        var applied = 0L;

        await foreach (var msg in consumer.ConsumeAsync<byte[]>(cancellationToken: ct))
        {
            try
            {
                Apply(msg);
                await msg.AckAsync(cancellationToken: ct);

                applied++;
                if (!_readiness.CaughtUp && applied >= backlog)
                {
                    _readiness.CaughtUp = true;
                    _log.LogInformation("caught up after {Applied} messages, serving traffic", applied);
                }
            }
            catch (Exception ex)
            {
                // Leave it unacknowledged. Redelivery is safe because apply is idempotent,
                // and losing a message silently is the failure worth avoiding.
                _log.LogError("apply failed for {Subject}, leaving unacknowledged: {Message}",
                    msg.Subject, ex.Message);
            }
        }
    }

    private void Apply(INatsJSMsg<byte[]> msg)
    {
        var payload = msg.Data;
        if (payload is null || payload.Length == 0) return;

        var kind = Header(msg, "Fleet-Kind");
        // A retained replay is historical. Ingest forwards the flag precisely so the
        // projection does not treat a replayed baseline as a live message; dropping it here
        // reintroduces the phantom-gap bug recorded in contracts/README.md.
        var retained = Header(msg, "Fleet-Retained") == "true";

        switch (kind)
        {
            case Kind.Telemetry:
                var t = JsonSerializer.Deserialize<TelemetryMessage>(payload, Json);
                if (t is not null) _projection.ApplyTelemetry(t, retained);
                break;
            case Kind.Status:
                var s = JsonSerializer.Deserialize<StatusMessage>(payload, Json);
                if (s is not null) _projection.ApplyStatus(s, retained);
                break;
            case Kind.Event:
                var e = JsonSerializer.Deserialize<EventMessage>(payload, Json);
                if (e is not null) _projection.ApplyEvent(e, retained);
                break;
        }
    }

    private static string? Header(INatsJSMsg<byte[]> msg, string name) =>
        msg.Headers is not null && msg.Headers.TryGetValue(name, out var v) ? v.ToString() : null;
}

/// <summary>Readiness is separate from liveness: a process can be alive and still not fit to serve.</summary>
public sealed class ReadinessState
{
    public volatile bool LogConnected;
    public volatile bool CaughtUp;
    public volatile bool DatabaseConnected;

    public bool Ready => LogConnected && CaughtUp;
}
