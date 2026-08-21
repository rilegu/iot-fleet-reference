using Fleet.Client.Core;
using FleetDashboard;

namespace Fleet.Dashboard.Tests;

/// <summary>
/// The Blazor dashboard's view state.
///
/// This is the third implementation of filter and sort semantics in the repository — the XAML
/// ViewModels hold one, the Electron client holds another — because view state belongs to the
/// view and the three frameworks do not share one. Three copies of a rule is three chances to
/// disagree, so the cases here are deliberately the same cases the other two pin.
/// </summary>
public class FleetViewTests
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
        FwVersion = "1.4.2",
        Metrics = new Metrics { TempC = temp },
        LastEventSeverity = severity,
    };

    private static (FleetView view, FleetStore store) Build()
    {
        var store = new FleetStore();
        var connection = new FleetConnection(store, new FleetClientOptions { BaseUrl = "http://localhost:1" });
        return (new FleetView(store, connection), store);
    }

    [Fact]
    public void SortsByDeviceIdByDefault()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[] { State("dev-c"), State("dev-a"), State("dev-b") }, null, 250, 1);

        Assert.Equal(new[] { "dev-a", "dev-b", "dev-c" }, view.Devices.Select(d => d.DeviceId));
    }

    [Fact]
    public void SearchesDeviceIdSiteAndFirmware()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[]
        {
            State("dev-1", site: "site-01"),
            State("dev-2", site: "site-02"),
        }, null, 250, 1);

        view.Search = "site-02";
        Assert.Equal(new[] { "dev-2" }, view.Devices.Select(d => d.DeviceId));

        view.Search = "DEV-1";
        Assert.Equal(new[] { "dev-1" }, view.Devices.Select(d => d.DeviceId));
    }

    [Fact]
    public void FiltersByOnlineAndAlerting()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[]
        {
            State("dev-1"),
            State("dev-2", online: false),
            State("dev-3", severity: "critical"),
        }, null, 250, 1);

        view.OnlineOnly = true;
        Assert.Equal(new[] { "dev-1", "dev-3" }, view.Devices.Select(d => d.DeviceId));

        view.OnlineOnly = false;
        view.AlertingOnly = true;
        Assert.Equal(new[] { "dev-3" }, view.Devices.Select(d => d.DeviceId));
    }

    [Fact]
    public void SortByReversesOnTheActiveColumnAndResetsOnAnother()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1", temp: 10), State("dev-2", temp: 30) }, null, 250, 1);

        view.SortBy(SortColumn.Temp);
        Assert.Equal(new[] { "dev-1", "dev-2" }, view.Devices.Select(d => d.DeviceId));

        view.SortBy(SortColumn.Temp);
        Assert.True(view.Descending);
        Assert.Equal(new[] { "dev-2", "dev-1" }, view.Devices.Select(d => d.DeviceId));

        view.SortBy(SortColumn.Site);
        Assert.False(view.Descending);
    }

    // ---- sort determinism ------------------------------------------------------------
    //
    // The store hands out ConcurrentDictionary.Values, whose order the type explicitly does
    // not define: it changes on internal resize, and again on the clear-and-refill every
    // snapshot performs. Leaning on OrderBy's stability over that input let tied rows reorder
    // after a reconnect.

    [Fact]
    public void TiedSortKeysResolveByDeviceId()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[] { State("dev-c"), State("dev-b"), State("dev-a") }, null, 250, 1);

        view.SortBy(SortColumn.Seq);

        Assert.Equal(new[] { "dev-a", "dev-b", "dev-c" }, view.Devices.Select(d => d.DeviceId));
    }

    /// <summary>
    /// Descending reverses the fallback too, matching the XAML and Electron clients.
    /// </summary>
    [Fact]
    public void TiedSortKeysReverseWithTheSortDirection()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[] { State("dev-a"), State("dev-b"), State("dev-c") }, null, 250, 1);

        view.SortBy(SortColumn.Seq);
        view.SortBy(SortColumn.Seq);

        Assert.Equal(new[] { "dev-c", "dev-b", "dev-a" }, view.Devices.Select(d => d.DeviceId));
    }

    /// <summary>
    /// A reconnect replaces the whole fleet, which is where the underlying order moved.
    /// </summary>
    [Fact]
    public void TiedOrderSurvivesASnapshotReplacingTheFleet()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[] { State("dev-a"), State("dev-b"), State("dev-c") }, null, 250, 1);
        view.SortBy(SortColumn.Status);
        var before = view.Devices.Select(d => d.DeviceId).ToArray();

        store.ApplySnapshot(new[] { State("dev-c"), State("dev-b"), State("dev-a") }, null, 250, 2);

        Assert.Equal(before, view.Devices.Select(d => d.DeviceId));
    }

    /// <summary>The projection is cached; a filter change has to invalidate it.</summary>
    [Fact]
    public void FilterChangesInvalidateTheCachedProjection()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1"), State("dev-2", online: false) }, null, 250, 1);

        Assert.Equal(2, view.Devices.Length);
        view.OnlineOnly = true;
        Assert.Single(view.Devices);
    }

    [Fact]
    public void SitesAreListedOnceAndInOrder()
    {
        var (view, store) = Build();
        store.ApplySnapshot(new[]
        {
            State("dev-1", site: "site-02"),
            State("dev-2", site: "site-01"),
            State("dev-3", site: "site-02"),
        }, null, 250, 1);

        Assert.Equal(new[] { "site-01", "site-02" }, view.Sites);
    }
}
