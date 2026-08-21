using Fleet.Client.Core;

namespace Fleet.Client.Core.Tests;

/// <summary>
/// The store is shared by every .NET client, so a bug here is a bug in all of them at once.
/// These tests pin the snapshot/delta semantics that the other language clients must also
/// reproduce.
/// </summary>
public class FleetStoreTests
{
    private static DeviceState Device(string id, double temp = 20, bool online = true, long seq = 1) => new()
    {
        DeviceId = id,
        Site = "site-00",
        BootId = "aaaaaaaaaaaaaaaa",
        Online = online,
        Seq = seq,
        Metrics = new Metrics { TempC = temp },
    };

    [Fact]
    public void SnapshotReplacesEverything()
    {
        var store = new FleetStore();
        store.ApplySnapshot(new[] { Device("dev-1"), Device("dev-2") }, new FleetAggregates { Total = 2 }, 250, 1);

        Assert.Equal(2, store.Snapshot().Length);
        Assert.Equal(250, store.CadenceMs);
        Assert.Equal(2, store.Aggregates.Total);

        // A reconnect delivers a fresh snapshot. Anything could have changed while the
        // socket was down, so the old contents must not survive it.
        store.ApplySnapshot(new[] { Device("dev-3") }, new FleetAggregates { Total = 1 }, 250, 1);

        Assert.Single(store.Snapshot());
        Assert.Null(store.Get("dev-1"));
        Assert.NotNull(store.Get("dev-3"));
    }

    [Fact]
    public void DeltaUpdatesOnlyTheDevicesItCarries()
    {
        var store = new FleetStore();
        store.ApplySnapshot(new[] { Device("dev-1", temp: 10), Device("dev-2", temp: 20) }, null, 250, 1);

        store.ApplyDelta(new[] { Device("dev-1", temp: 99, seq: 2) }, null, 2);

        Assert.Equal(99, store.Get("dev-1")!.Metrics!.TempC);
        Assert.Equal(20, store.Get("dev-2")!.Metrics!.TempC); // untouched
        Assert.Equal(2, store.Snapshot().Length);
    }

    [Fact]
    public void DeltaAddsDevicesNotSeenBefore()
    {
        var store = new FleetStore();
        store.ApplySnapshot(new[] { Device("dev-1") }, null, 250, 1);

        // A device that comes online after the snapshot arrives only in a delta.
        store.ApplyDelta(new[] { Device("dev-new") }, null, 2);

        Assert.Equal(2, store.Snapshot().Length);
        Assert.NotNull(store.Get("dev-new"));
    }

    /// <summary>
    /// The highlight marks devices from the most recent frame only. If it accumulated, every
    /// row would end up flagged within seconds and the cue would stop meaning anything.
    /// </summary>
    [Fact]
    public void ChangeHighlightLastsExactlyOneFrame()
    {
        var store = new FleetStore();
        store.ApplySnapshot(new[] { Device("dev-1"), Device("dev-2") }, null, 250, 1);

        store.ApplyDelta(new[] { Device("dev-1", seq: 2) }, null, 2);
        Assert.True(store.Get("dev-1")!.JustChanged);
        Assert.False(store.Get("dev-2")!.JustChanged);

        store.ApplyDelta(new[] { Device("dev-2", seq: 2) }, null, 3);
        Assert.False(store.Get("dev-1")!.JustChanged); // cleared
        Assert.True(store.Get("dev-2")!.JustChanged);
    }

    [Fact]
    public void VersionChangesOnEveryAppliedFrame()
    {
        var store = new FleetStore();
        var v0 = store.Version;

        store.ApplySnapshot(new[] { Device("dev-1") }, null, 250, 1);
        var v1 = store.Version;
        Assert.NotEqual(v0, v1);

        store.ApplyDelta(new[] { Device("dev-1", seq: 2) }, null, 2);
        Assert.NotEqual(v1, store.Version);
    }

    /// <summary>
    /// One notification per frame, not one per device. Raising per device would hand the UI
    /// back the fan-out the delta protocol exists to remove.
    /// </summary>
    [Fact]
    public void ChangedFiresOncePerFrameRegardlessOfDeviceCount()
    {
        var store = new FleetStore();
        var notifications = 0;
        store.Changed += () => notifications++;

        store.ApplySnapshot(Enumerable.Range(0, 500).Select(i => Device($"dev-{i}")).ToArray(), null, 250, 1);
        Assert.Equal(1, notifications);

        store.ApplyDelta(Enumerable.Range(0, 300).Select(i => Device($"dev-{i}", seq: 2)).ToArray(), null, 2);
        Assert.Equal(2, notifications);
    }

    [Fact]
    public void FrameCounterTracksTheServersFrameNumber()
    {
        var store = new FleetStore();
        store.ApplySnapshot(new[] { Device("dev-1") }, null, 250, 1);
        store.ApplyDelta(new[] { Device("dev-1", seq: 2) }, null, 2);
        store.ApplyDelta(new[] { Device("dev-1", seq: 3) }, null, 3);

        // Divergence between these two is how a dropped frame becomes visible instead of
        // silently leaving the client behind.
        Assert.Equal(3, store.FramesApplied);
        Assert.Equal(3, store.LastFrame);
    }

    [Fact]
    public void AggregatesSurviveAFrameThatOmitsThem()
    {
        var store = new FleetStore();
        store.ApplySnapshot(new[] { Device("dev-1") }, new FleetAggregates { Total = 1, Online = 1 }, 250, 1);

        store.ApplyDelta(new[] { Device("dev-1", seq: 2) }, null, 2);

        Assert.Equal(1, store.Aggregates.Total);
        Assert.Equal(1, store.Aggregates.Online);
    }

    [Fact]
    public void SnapshotReturnsAnIndependentCopy()
    {
        var store = new FleetStore();
        store.ApplySnapshot(new[] { Device("dev-1") }, null, 250, 1);

        var first = store.Snapshot();
        store.ApplyDelta(new[] { Device("dev-2") }, null, 2);

        // The array handed out earlier must not grow underneath a render pass already
        // iterating it.
        Assert.Single(first);
        Assert.Equal(2, store.Snapshot().Length);
    }
}
