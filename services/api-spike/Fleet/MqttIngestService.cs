using System.Buffers;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Formatter;

namespace ApiSpike.Fleet;

public sealed class MqttOptions
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 1883;
    public string ClientId { get; set; } = "api-spike";
}

/// <summary>
/// Exploratory consumer: subscribes across the fleet, validates loosely, and updates the
/// in-memory projection.
///
/// This class is throwaway. Ingest moves into a Go service behind a durable event log, so
/// nothing here should be extended or depended upon.
/// </summary>
public sealed class MqttIngestService : BackgroundService
{
    private const string TelemetryFilter = "fleet/+/+/telemetry";
    private const string StatusFilter = "fleet/+/+/status";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
    };

    private readonly FleetState _state;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttIngestService> _log;

    public MqttIngestService(FleetState state, IConfiguration config, ILogger<MqttIngestService> log)
    {
        _state = state;
        _log = log;
        _options = config.GetSection("Mqtt").Get<MqttOptions>() ?? new MqttOptions();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();

        client.ApplicationMessageReceivedAsync += OnMessageAsync;

        // Re-subscribe on every connection. Retained status messages replay on subscribe,
        // so a reconnect immediately re-learns the whole fleet rather than waiting for the
        // next telemetry sample from each device.
        client.ConnectedAsync += async _ =>
        {
            _log.LogInformation("connected to broker {Host}:{Port}", _options.Host, _options.Port);
            await client.SubscribeAsync(
                new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(TelemetryFilter).WithAtMostOnceQoS())
                    .WithTopicFilter(f => f.WithTopic(StatusFilter).WithAtLeastOnceQoS())
                    .Build(),
                CancellationToken.None);
            _log.LogInformation("subscribed to {Telemetry} and {Status}", TelemetryFilter, StatusFilter);
        };

        client.DisconnectedAsync += e =>
        {
            _log.LogWarning("disconnected from broker: {Reason}", e.Reason);
            return Task.CompletedTask;
        };

        var options = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithProtocolVersion(MqttProtocolVersion.V311)
            .WithClientId(_options.ClientId)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .Build();

        // Reconnect loop. The broker may not be up yet when this service starts, which is
        // normal under Compose, so a failed connect is retried rather than fatal.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                    await client.ConnectAsync(options, stoppingToken);

                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning("broker connect failed, retrying: {Message}", ex.Message);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                }
                catch (OperationCanceledException) { break; }
            }
        }

        if (client.IsConnected)
            await client.DisconnectAsync(cancellationToken: CancellationToken.None);
    }

    private Task OnMessageAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic = e.ApplicationMessage.Topic;

        try
        {
            var payload = e.ApplicationMessage.Payload.ToArray();

            if (topic.EndsWith("/telemetry", StringComparison.Ordinal))
            {
                var msg = JsonSerializer.Deserialize<TelemetryMessage>(payload, JsonOptions);
                if (msg is null || string.IsNullOrEmpty(msg.DeviceId))
                {
                    _state.CountRejected();
                    return Task.CompletedTask;
                }
                _state.ApplyTelemetry(msg);
            }
            else if (topic.EndsWith("/status", StringComparison.Ordinal))
            {
                var msg = JsonSerializer.Deserialize<StatusMessage>(payload, JsonOptions);
                if (msg is null || string.IsNullOrEmpty(msg.DeviceId))
                {
                    _state.CountRejected();
                    return Task.CompletedTask;
                }
                _state.ApplyStatus(msg, e.ApplicationMessage.Retain);
            }
        }
        catch (JsonException)
        {
            // Malformed payloads are counted and dropped, never trusted. A dead-letter
            // table and schema validation belong at the real ingest boundary.
            _state.CountRejected();
        }

        return Task.CompletedTask;
    }
}
