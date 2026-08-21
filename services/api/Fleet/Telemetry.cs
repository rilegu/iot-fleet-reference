using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace FleetApi.Fleet;

/// <summary>
/// Instrumentation for the API.
///
/// The ActivitySource name is what appears as the instrumentation scope on every span, and
/// the meter name prefixes every metric. Both are constants because a trace filtered by
/// scope, or a dashboard querying a metric, only works if the name never drifts.
/// </summary>
public static class FleetTelemetry
{
    public const string ServiceName = "fleet-api";

    public static readonly ActivitySource Source = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    /// <summary>Messages applied to the projection, tagged by kind.</summary>
    public static readonly Counter<long> Applied =
        Meter.CreateCounter<long>("fleet.api.applied", unit: "{message}",
            description: "Messages applied to the fleet projection.");

    /// <summary>
    /// Messages rejected by the ordering rule. Non-zero is normal after a redelivery and is
    /// the metric that distinguishes a healthy retry from a broken consumer.
    /// </summary>
    public static readonly Counter<long> StaleDropped =
        Meter.CreateCounter<long>("fleet.api.stale_dropped", unit: "{message}",
            description: "Messages rejected as duplicates or reorderings.");

    public static readonly Counter<long> Gaps =
        Meter.CreateCounter<long>("fleet.api.gaps", unit: "{gap}",
            description: "Sequence jumps observed, meaning messages were lost upstream.");

    /// <summary>Delta frames sent to dashboards, and how much each carried.</summary>
    public static readonly Counter<long> FramesSent =
        Meter.CreateCounter<long>("fleet.api.frames_sent", unit: "{frame}",
            description: "Realtime frames sent, by type.");

    public static readonly Histogram<int> FrameDevices =
        Meter.CreateHistogram<int>("fleet.api.frame_devices", unit: "{device}",
            description: "Devices carried per delta frame. Rising values mean coalescing is doing more work per frame.");

    /// <summary>
    /// Time from ingest observing a message to the projection applying it, measured from the
    /// header ingest stamped. Both ends are server-side clocks; device clocks are never used
    /// because they drift and a clock_step fault moves them backwards deliberately.
    /// </summary>
    public static readonly Histogram<double> ApplyLag =
        Meter.CreateHistogram<double>("fleet.api.apply_lag", unit: "s",
            description: "Seconds between ingest receiving a message and the projection applying it.");

    private static int _connectedSockets;

    /// <summary>Currently connected dashboards. Each one costs a coalescing timer and a socket.</summary>
    public static void SocketOpened() => Interlocked.Increment(ref _connectedSockets);
    public static void SocketClosed() => Interlocked.Decrement(ref _connectedSockets);

    static FleetTelemetry()
    {
        Meter.CreateObservableGauge("fleet.api.connected_dashboards",
            () => Volatile.Read(ref _connectedSockets),
            unit: "{connection}",
            description: "Dashboards currently connected to the realtime channel.");
    }
}
