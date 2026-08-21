using Fleet.Client.Core;
using Fleet.Client.Xaml;

namespace Fleet.Client.Xaml.Tests;

/// <summary>
/// The detail panel's arithmetic and selection wiring.
///
/// Both XAML hosts draw the sparkline from the vertices produced here, so a mistake in this
/// projection is a mistake in two clients at once. That is exactly why the arithmetic lives
/// in the shared layer instead of in either code-behind.
/// </summary>
public class DeviceDetailViewModelTests
{
    private static TelemetryPoint Point(double? temp) => new() { Samples = 1, TempCAvg = temp };

    private static (FleetViewModel vm, FleetStore store) Build()
    {
        var store = new FleetStore();
        var connection = new FleetConnection(store, new FleetClientOptions { BaseUrl = "http://localhost:1" });
        return (new FleetViewModel(store, connection, new ImmediateDispatcher()), store);
    }

    private static DeviceState State(string id) => new()
    {
        DeviceId = id,
        Site = "site-00",
        BootId = "aaaaaaaaaaaaaaaa",
        Online = true,
        Seq = 1,
        Metrics = new Metrics { TempC = 20 },
    };

    [Fact]
    public void SparkNormalisesToTheUnitBoxWithHotAtTheTop()
    {
        var points = DeviceDetailViewModel.BuildSpark(
            new[] { Point(10), Point(15), Point(20) }, out var min, out var max);

        Assert.Equal(10, min);
        Assert.Equal(20, max);
        Assert.Equal(3, points.Count);

        // X spans the full width.
        Assert.Equal(0, points[0].X, 3);
        Assert.Equal(50, points[1].X, 3);
        Assert.Equal(100, points[2].X, 3);

        // Y is inverted: the coldest reading sits at the bottom of the box, not the top.
        Assert.Equal(100, points[0].Y, 3);
        Assert.Equal(50, points[1].Y, 3);
        Assert.Equal(0, points[2].Y, 3);
    }

    /// <summary>
    /// A device sitting at a constant temperature has zero range. Without the clamp this
    /// divides by zero, and the line becomes NaN vertices that draw as nothing at all.
    /// </summary>
    [Fact]
    public void FlatSeriesDrawsAFlatLineRatherThanDividingByZero()
    {
        var points = DeviceDetailViewModel.BuildSpark(
            new[] { Point(21), Point(21), Point(21) }, out var min, out var max);

        Assert.Equal(21, min);
        Assert.Equal(21, max);
        Assert.All(points, p => Assert.False(double.IsNaN(p.Y)));
        Assert.All(points, p => Assert.Equal(100, p.Y, 3));
    }

    [Fact]
    public void BucketsWithoutATemperatureAreSkipped()
    {
        var points = DeviceDetailViewModel.BuildSpark(
            new[] { Point(10), Point(null), Point(20) }, out var min, out var max);

        // The empty bucket contributes neither a vertex nor a bound.
        Assert.Equal(2, points.Count);
        Assert.Equal(10, min);
        Assert.Equal(20, max);
    }

    /// <summary>A single point is not a line, and drawing one would be misleading.</summary>
    [Fact]
    public void FewerThanTwoReadingsProduceNoLine()
    {
        Assert.Empty(DeviceDetailViewModel.BuildSpark(new[] { Point(10) }, out _, out _));
        Assert.Empty(DeviceDetailViewModel.BuildSpark(Array.Empty<TelemetryPoint>(), out _, out _));
    }

    [Fact]
    public void SelectingADeviceOpensTheDetailOnIt()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1"), State("dev-2") }, null, 250, 1);

        Assert.False(vm.HasSelection);

        vm.Selected = vm.Devices[0];

        Assert.True(vm.HasSelection);
        Assert.Same(vm.Devices[0], vm.Detail.Device);
        Assert.True(vm.Devices[0].IsSelected);
        Assert.False(vm.Devices[1].IsSelected);
    }

    /// <summary>
    /// The flag is per-row rather than global because WinUI's virtualising grid recycles
    /// rows, so exactly one must carry it at a time.
    /// </summary>
    [Fact]
    public void MovingTheSelectionClearsTheOldRow()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1"), State("dev-2") }, null, 250, 1);

        vm.Selected = vm.Devices[0];
        vm.Selected = vm.Devices[1];

        Assert.False(vm.Devices[0].IsSelected);
        Assert.True(vm.Devices[1].IsSelected);
    }

    [Fact]
    public void ClosingTheDetailClearsSelectionAndContents()
    {
        var (vm, store) = Build();
        store.ApplySnapshot(new[] { State("dev-1") }, null, 250, 1);

        vm.Selected = vm.Devices[0];
        vm.CloseDetailCommand.Execute(null);

        Assert.False(vm.HasSelection);
        Assert.Null(vm.Detail.Device);
        Assert.Empty(vm.Detail.Spark);
        Assert.Empty(vm.Detail.Events);
        Assert.False(vm.Devices[0].IsSelected);
    }
}
