using FleetApi.Fleet;

namespace FleetApi.Tests;

/// <summary>
/// The ordering and idempotency rules from ADR-0008. These are the properties that make the
/// ingest/API split safe, so they are asserted rather than assumed.
/// </summary>
public class FleetProjectionTests
{
    private const string Device = "dev-000042";
    private const string BootA = "aaaaaaaaaaaaaaaa";
    private const string BootB = "bbbbbbbbbbbbbbbb";

    private static TelemetryMessage Telemetry(string bootId, long seq, double temp = 20) => new()
    {
        Schema = "telemetry/1",
        DeviceId = Device,
        Site = "site-00",
        BootId = bootId,
        Seq = seq,
        Ts = DateTimeOffset.UtcNow,
        Metrics = new Metrics { TempC = temp },
    };

    private static StatusMessage Status(string bootId, long seq, bool online, string reason) => new()
    {
        Schema = "status/1",
        DeviceId = Device,
        Site = "site-00",
        BootId = bootId,
        Seq = seq,
        Ts = DateTimeOffset.UtcNow,
        Online = online,
        Reason = reason,
        FwVersion = "1.4.2",
    };

    [Fact]
    public void AppliesMessagesInSequence()
    {
        var p = new FleetProjection();
        p.ApplyTelemetry(Telemetry(BootA, 1, temp: 10));
        p.ApplyTelemetry(Telemetry(BootA, 2, temp: 20));
        p.ApplyTelemetry(Telemetry(BootA, 3, temp: 30));

        var d = p.Get(Device);
        Assert.NotNull(d);
        Assert.Equal(3, d!.Seq);
        Assert.Equal(30, d.Metrics!.TempC);
        Assert.Equal(0, p.StaleDropped);
    }

    [Fact]
    public void RedeliveryIsANoOp()
    {
        var p = new FleetProjection();
        p.ApplyTelemetry(Telemetry(BootA, 1, temp: 10));
        p.ApplyTelemetry(Telemetry(BootA, 2, temp: 20));

        // The same message arriving three more times must not change anything. This is what
        // makes at-least-once delivery safe.
        p.ApplyTelemetry(Telemetry(BootA, 2, temp: 999));
        p.ApplyTelemetry(Telemetry(BootA, 2, temp: 999));
        p.ApplyTelemetry(Telemetry(BootA, 1, temp: 999));

        var d = p.Get(Device)!;
        Assert.Equal(2, d.Seq);
        Assert.Equal(20, d.Metrics!.TempC);
        Assert.Equal(3, p.StaleDropped);
    }

    [Fact]
    public void OutOfOrderDeliveryCannotMoveADeviceBackwards()
    {
        var p = new FleetProjection();
        p.ApplyTelemetry(Telemetry(BootA, 5, temp: 50));
        p.ApplyTelemetry(Telemetry(BootA, 3, temp: 30));

        Assert.Equal(5, p.Get(Device)!.Seq);
        Assert.Equal(50, p.Get(Device)!.Metrics!.TempC);
    }

    /// <summary>
    /// A reboot resets the sequence. Without keying on boot id, a restarted device sending
    /// seq 1 would be discarded as stale forever — which is exactly what happened when the
    /// simulator derived its boot id from a fixed seed.
    /// </summary>
    [Fact]
    public void RebootResetsTheSequenceAndIsAccepted()
    {
        var p = new FleetProjection();
        p.ApplyTelemetry(Telemetry(BootA, 900, temp: 90));

        p.ApplyTelemetry(Telemetry(BootB, 1, temp: 11));

        var d = p.Get(Device)!;
        Assert.Equal(BootB, d.BootId);
        Assert.Equal(1, d.Seq);
        Assert.Equal(11, d.Metrics!.TempC);
        Assert.Equal(0, p.StaleDropped);
    }

    [Fact]
    public void GapsAreCountedNotSilentlyIgnored()
    {
        var p = new FleetProjection();
        p.ApplyTelemetry(Telemetry(BootA, 1));
        p.ApplyTelemetry(Telemetry(BootA, 2));
        p.ApplyTelemetry(Telemetry(BootA, 9)); // 3..8 lost

        Assert.Equal(1, p.Get(Device)!.Gaps);
        Assert.Equal(1, p.Aggregates().Gaps);
    }

    /// <summary>
    /// A Last Will carries seq 0 because it is composed at connect time. Applying the
    /// ordinary rule would discard every offline transition and a dying device would never
    /// be noticed.
    /// </summary>
    [Fact]
    public void LastWillIsAppliedDespiteCarryingSequenceZero()
    {
        var p = new FleetProjection();
        p.ApplyStatus(Status(BootA, 1, online: true, reason: "connect"));
        p.ApplyTelemetry(Telemetry(BootA, 400));

        p.ApplyStatus(Status(BootA, 0, online: false, reason: "lwt"));

        var d = p.Get(Device)!;
        Assert.False(d.Online);
        Assert.Equal("lwt", d.OfflineReason);
        // The will must not rewind the sequence it was never part of.
        Assert.Equal(400, d.Seq);
    }

    [Fact]
    public void TelemetryDoesNotResurrectAnOfflineDevice()
    {
        var p = new FleetProjection();
        p.ApplyStatus(Status(BootA, 1, online: true, reason: "connect"));
        p.ApplyStatus(Status(BootA, 0, online: false, reason: "lwt"));

        // In-flight telemetry can arrive after the will. Presence is owned by status
        // messages, so it must not flip the device back to online.
        p.ApplyTelemetry(Telemetry(BootA, 500));

        Assert.False(p.Get(Device)!.Online);
    }

    /// <summary>
    /// A retained replay is historical by definition. Treating it as live produced a phantom
    /// gap on nearly every device in the earlier spike.
    /// </summary>
    [Fact]
    public void RetainedReplayEstablishesABaselineWithoutCountingAGap()
    {
        var p = new FleetProjection();
        p.ApplyStatus(Status(BootA, 1, online: true, reason: "connect"), retained: true);

        // The device has been running for a while; its next live message is far ahead.
        p.ApplyTelemetry(Telemetry(BootA, 400));

        var d = p.Get(Device)!;
        Assert.Equal(400, d.Seq);
        Assert.Equal(0, d.Gaps);
        Assert.Equal(0, p.Aggregates().Gaps);
    }

    [Fact]
    public void RetainedReplayDoesNotOverwriteKnownState()
    {
        var p = new FleetProjection();
        p.ApplyStatus(Status(BootA, 10, online: true, reason: "connect"));
        p.ApplyTelemetry(Telemetry(BootA, 50));

        // A late retained replay carrying an old sequence must not rewind anything.
        p.ApplyStatus(Status(BootA, 1, online: false, reason: "lwt"), retained: true);

        var d = p.Get(Device)!;
        Assert.True(d.Online);
        Assert.Equal(50, d.Seq);
    }

    [Fact]
    public void DirtySetCoalescesRepeatedChanges()
    {
        var p = new FleetProjection();
        for (var i = 1; i <= 50; i++) p.ApplyTelemetry(Telemetry(BootA, i));

        // Fifty applies, one device: the dashboard should be told once, not fifty times.
        var dirty = p.DrainDirty();
        Assert.Single(dirty);
        Assert.Equal(Device, dirty[0]);

        // Draining clears it, so an idle cadence sends nothing.
        Assert.Empty(p.DrainDirty());
    }

    [Fact]
    public void AggregatesReflectPresenceAndAlerts()
    {
        var p = new FleetProjection();

        p.ApplyStatus(new StatusMessage
        {
            DeviceId = "dev-1", Site = "site-00", BootId = BootA, Seq = 1, Online = true, Reason = "connect",
        });
        p.ApplyStatus(new StatusMessage
        {
            DeviceId = "dev-2", Site = "site-01", BootId = BootA, Seq = 1, Online = false, Reason = "lwt",
        });
        p.ApplyEvent(new EventMessage
        {
            DeviceId = "dev-1", Site = "site-00", BootId = BootA, Seq = 2,
            EventKind = "brownout", Severity = "warning",
        });

        var a = p.Aggregates();
        Assert.Equal(2, a.Total);
        Assert.Equal(1, a.Online);
        Assert.Equal(1, a.Offline);
        Assert.Equal(1, a.Alerting);
        Assert.Equal(2, a.Sites);
    }

    /// <summary>
    /// The log consumer applies from one reader, but requests and sockets read concurrently.
    /// Records are swapped whole, so a reader never observes a half-applied device.
    /// </summary>
    [Fact]
    public async Task ConcurrentApplyAndReadIsSafe()
    {
        var p = new FleetProjection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= 5000 && !cts.IsCancellationRequested; i++)
                p.ApplyTelemetry(Telemetry(BootA, i, temp: i));
        });

        var reader = Task.Run(() =>
        {
            while (!writer.IsCompleted)
            {
                foreach (var d in p.Snapshot()) Assert.NotNull(d.DeviceId);
                _ = p.Aggregates();
                _ = p.DrainDirty();
            }
        });

        await Task.WhenAll(writer, reader);
        Assert.Equal(5000, p.Get(Device)!.Seq);
    }
}
