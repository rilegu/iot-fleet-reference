using System.ComponentModel;
using Fleet.Client.Core;
using Fleet.Client.Xaml;

namespace Fleet.Client.Xaml.Tests;

/// <summary>
/// The ViewModels are tested without a UI thread at all, which is the practical payoff of
/// keeping the dispatcher behind an interface: if these needed a real WPF or WinUI host to
/// run, the layer would not be genuinely framework-neutral and the reuse claim would be
/// thinner than it looks.
/// </summary>
public class FleetViewModelTests
{
    private static DeviceState State(string id, double temp = 20, bool online = true,
                                     long seq = 1, string site = "site-00",
                                     string? severity = null) => new()
    {
        DeviceId = id,
        Site = site,
        BootId = "aaaaaaaaaaaaaaaa",
        Online = online,
        Seq = seq,
        Metrics = new Metrics { TempC = temp },
        LastEventSeverity = severity,
        LastEvent = severity is null ? null : "brownout",
    };

    private static (FleetViewModel vm, FleetStore store) Build()
    {
        var store = new FleetStore();
        var connection = new FleetConnection(store, new FleetClientOptions { BaseUrl = "http://localhost:1" });
        return (new FleetViewModel(store, connection, new ImmediateDispatcher()), store);
    }

    [Fact]
    public void SnapshotPopulatesTheBoundCollection()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1"), State("dev-2") },
            new FleetAggregates { Total = 2, Online = 2 }, 250, 1);

        Assert.Equal(2, vm.Devices.Count);
        Assert.Equal(2, vm.Total);
        Assert.Equal(2, vm.Shown);
    }

    /// <summary>
    /// The central performance property. Replacing ViewModels each frame would raise a
    /// collection change per device — a thousand of them, four times a second — and every
    /// bound row would be rebuilt. Updating in place costs one notification per field that
    /// actually moved.
    /// </summary>
    [Fact]
    public void DeltaUpdatesInPlaceWithoutReplacingViewModels()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1", temp: 10) }, null, 250, 1);

        var original = vm.Devices[0];

        store.ApplyDelta(new[] { State("dev-1", temp: 99, seq: 2) }, null, 2);

        Assert.Same(original, vm.Devices[0]);
        Assert.Equal(99, vm.Devices[0].Temperature);
    }

    [Fact]
    public void ChangedFieldsRaisePropertyChanged()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1", temp: 10) }, null, 250, 1);

        var device = vm.Devices[0];
        var raised = new List<string>();
        ((INotifyPropertyChanged)device).PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        store.ApplyDelta(new[] { State("dev-1", temp: 42, seq: 2) }, null, 2);

        Assert.Contains(nameof(DeviceViewModel.Temperature), raised);
        Assert.Contains(nameof(DeviceViewModel.Seq), raised);
        // Site did not change, so the generated setter must not have raised for it.
        Assert.DoesNotContain(nameof(DeviceViewModel.Site), raised);
    }

    [Fact]
    public void NewDevicesAppearAndDepartedOnesAreDropped()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1") }, null, 250, 1);
        Assert.Single(vm.Devices);

        store.ApplyDelta(new[] { State("dev-2") }, null, 2);
        Assert.Equal(2, vm.Devices.Count);

        // A fresh snapshot is the whole fleet, so anything absent is genuinely gone.
        store.ApplySnapshot(new[] { State("dev-2") }, null, 250, 1);
        Assert.Single(vm.Devices);
        Assert.Equal("dev-2", vm.Devices[0].DeviceId);
    }

    [Fact]
    public void FiltersNarrowTheCollection()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[]
        {
            State("dev-1", online: true),
            State("dev-2", online: false),
            State("dev-3", online: true, severity: "critical"),
        }, null, 250, 1);

        vm.OnlineOnly = true;
        Assert.Equal(2, vm.Devices.Count);

        vm.AlertingOnly = true;
        Assert.Single(vm.Devices);
        Assert.Equal("dev-3", vm.Devices[0].DeviceId);

        vm.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, vm.Devices.Count);
    }

    [Fact]
    public void SearchMatchesIdentityAndFirmware()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[]
        {
            State("dev-1", site: "site-00"),
            State("dev-2", site: "site-99"),
        }, null, 250, 1);

        vm.Search = "site-99";
        Assert.Single(vm.Devices);
        Assert.Equal("dev-2", vm.Devices[0].DeviceId);

        vm.Search = "";
        Assert.Equal(2, vm.Devices.Count);
    }

    [Fact]
    public void SortingReordersAndToggles()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[]
        {
            State("dev-1", temp: 30),
            State("dev-2", temp: 10),
            State("dev-3", temp: 20),
        }, null, 250, 1);

        vm.SortByCommand.Execute(DeviceSort.Temperature);
        Assert.Equal(new[] { "dev-2", "dev-3", "dev-1" }, vm.Devices.Select(d => d.DeviceId));

        // Clicking the active column reverses it rather than re-selecting it.
        vm.SortByCommand.Execute(DeviceSort.Temperature);
        Assert.True(vm.Descending);
        Assert.Equal(new[] { "dev-1", "dev-3", "dev-2" }, vm.Devices.Select(d => d.DeviceId));
    }

    /// <summary>
    /// Rebuilding the bound collection resets scroll position and selection, so it must
    /// happen only when the order could actually have changed. Sorting by device id is
    /// stable across frames; sorting by temperature is not.
    /// </summary>
    [Fact]
    public void StableSortDoesNotRebuildTheCollectionEveryFrame()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1"), State("dev-2") }, null, 250, 1);

        var resets = 0;
        vm.Devices.CollectionChanged += (_, _) => resets++;

        // Sorted by device id, which no frame can reorder.
        store.ApplyDelta(new[] { State("dev-1", temp: 55, seq: 2) }, null, 2);
        store.ApplyDelta(new[] { State("dev-2", temp: 66, seq: 2) }, null, 3);

        Assert.Equal(0, resets);
        Assert.Equal(55, vm.Devices[0].Temperature);
    }

    [Fact]
    public void ChangeHighlightLastsExactlyOneFrame()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1"), State("dev-2") }, null, 250, 1);

        store.ApplyDelta(new[] { State("dev-1", seq: 2) }, null, 2);
        Assert.True(vm.Devices.Single(d => d.DeviceId == "dev-1").JustChanged);
        Assert.False(vm.Devices.Single(d => d.DeviceId == "dev-2").JustChanged);

        store.ApplyDelta(new[] { State("dev-2", seq: 2) }, null, 3);
        Assert.False(vm.Devices.Single(d => d.DeviceId == "dev-1").JustChanged);
        Assert.True(vm.Devices.Single(d => d.DeviceId == "dev-2").JustChanged);
    }

    [Fact]
    public void AlertingIsDerivedFromSeverityAndNotifies()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1") }, null, 250, 1);

        var device = vm.Devices[0];
        Assert.False(device.IsAlerting);

        var raised = new List<string>();
        ((INotifyPropertyChanged)device).PropertyChanged += (_, e) => raised.Add(e.PropertyName!);

        store.ApplyDelta(new[] { State("dev-1", seq: 2, severity: "critical") }, null, 2);

        Assert.True(device.IsAlerting);
        Assert.Contains(nameof(DeviceViewModel.IsAlerting), raised);
    }

    // ---- sort determinism ------------------------------------------------------------
    //
    // Sorting used to lean on OrderBy being a stable sort. It is, but stability only
    // preserves the order of the input, and the input is Dictionary.Values, whose order is
    // not defined and does change. On a column where many devices share a value that let
    // rows swap places for no reason the operator could see.

    /// <summary>
    /// Every device here has the same sequence number, so the sort key decides nothing and
    /// the fallback decides everything. They are fed in reverse, which is the order the
    /// dictionary then iterates.
    /// </summary>
    [Fact]
    public void TiedSortKeysResolveByDeviceId()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-c"), State("dev-b"), State("dev-a") }, null, 250, 1);

        vm.Sort = DeviceSort.Sequence;

        Assert.Equal(new[] { "dev-a", "dev-b", "dev-c" }, vm.Devices.Select(d => d.DeviceId));
    }

    /// <summary>
    /// Descending reverses the fallback too, so the Electron client — which reverses its
    /// whole comparison rather than just the primary key — agrees row for row.
    /// </summary>
    [Fact]
    public void TiedSortKeysReverseWithTheSortDirection()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-a"), State("dev-b"), State("dev-c") }, null, 250, 1);

        vm.Sort = DeviceSort.Sequence;
        vm.Descending = true;

        Assert.Equal(new[] { "dev-c", "dev-b", "dev-a" }, vm.Devices.Select(d => d.DeviceId));
    }

    /// <summary>
    /// The condition that actually triggered this in a running fleet.
    ///
    /// A device leaving frees its slot in the dictionary, and the next device to arrive
    /// reuses it — so iteration order stops matching insertion order, and every row tied on
    /// the sort key can move.
    /// </summary>
    [Fact]
    public void TiedOrderSurvivesADeviceLeavingAndAnotherJoining()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-a"), State("dev-b"), State("dev-c") }, null, 250, 1);

        store.ApplySnapshot(new[] { State("dev-a"), State("dev-c") }, null, 250, 2);
        store.ApplyDelta(new[] { State("dev-d") }, null, 3);

        vm.Sort = DeviceSort.Status;

        Assert.Equal(new[] { "dev-a", "dev-c", "dev-d" }, vm.Devices.Select(d => d.DeviceId));
    }
}
