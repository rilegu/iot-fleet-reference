using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Fleet.Client.Core;

namespace Fleet.Client.Xaml;

/// <summary>A sparkline vertex, normalised to a 0-100 box on both axes.</summary>
/// <remarks>
/// Normalised rather than in pixels because the two XAML hosts draw with different types
/// and at different sizes. Scaling to the actual control is a few lines in each host; the
/// arithmetic that turns a temperature series into a shape belongs here, where it is
/// written and tested once.
/// </remarks>
public readonly record struct SparkPoint(double X, double Y);

/// <summary>
/// The detail panel for one device.
///
/// Live fields come from the <see cref="DeviceViewModel"/> the grid already holds — no
/// request is needed for those, and they keep updating from the socket while the panel is
/// open. History and events do require a request, because the realtime channel carries
/// current state only: pushing every device's history to every client would undo the saving
/// the delta protocol exists to make.
///
/// Those requests fire once per device, not once per frame.
/// </summary>
public sealed partial class DeviceDetailViewModel : ObservableObject
{
    private readonly FleetConnection _connection;
    private readonly IUiDispatcher _ui;
    private CancellationTokenSource? _inFlight;

    public DeviceDetailViewModel(FleetConnection connection, IUiDispatcher ui)
    {
        _connection = connection;
        _ui = ui;
    }

    [ObservableProperty] private DeviceViewModel? _device;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private double _minTemperature;
    [ObservableProperty] private double _maxTemperature;

    /// <summary>True once there are enough points for a line to mean anything.</summary>
    [ObservableProperty] private bool _hasHistory;

    public ObservableCollection<SparkPoint> Spark { get; } = new();
    public ObservableCollection<DeviceEvent> Events { get; } = new();

    public string TemperatureRange => HasHistory
        ? $"{MinTemperature:0.0}° to {MaxTemperature:0.0}°"
        : "";

    /// <summary>
    /// Loads history and events for a device.
    ///
    /// Any load already running is cancelled first. Clicking through rows quickly would
    /// otherwise let a slow earlier response land after a faster later one and leave the
    /// panel showing another device's history.
    /// </summary>
    public async Task LoadAsync(DeviceViewModel device)
    {
        _inFlight?.Cancel();
        _inFlight?.Dispose();
        _inFlight = new CancellationTokenSource();
        var ct = _inFlight.Token;

        Device = device;
        IsLoading = true;
        Spark.Clear();
        Events.Clear();
        HasHistory = false;

        try
        {
            // Independent reads, so issued together rather than in sequence.
            var historyTask = _connection.DeviceHistoryAsync(device.DeviceId, 60, ct);
            var eventsTask = _connection.EventsAsync(device.DeviceId, 20, ct);
            await Task.WhenAll(historyTask, eventsTask);

            if (ct.IsCancellationRequested) return;

            var points = BuildSpark(historyTask.Result, out var min, out var max);
            var events = eventsTask.Result;

            // Collections are bound, so they must only be touched on the UI thread.
            _ui.Post(() =>
            {
                if (ct.IsCancellationRequested) return;

                MinTemperature = min;
                MaxTemperature = max;

                Spark.Clear();
                foreach (var p in points) Spark.Add(p);

                Events.Clear();
                foreach (var e in events) Events.Add(e);

                HasHistory = points.Count >= 2;
                OnPropertyChanged(nameof(TemperatureRange));
                IsLoading = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection.
        }
        catch
        {
            // A failed history read must not take the panel down: live fields still work,
            // and the chart simply shows nothing.
            _ui.Post(() => IsLoading = false);
        }
    }

    public void Clear()
    {
        _inFlight?.Cancel();
        Device = null;
        Spark.Clear();
        Events.Clear();
        HasHistory = false;
        IsLoading = false;
    }

    /// <summary>
    /// Turns a bucket series into normalised vertices.
    /// </summary>
    internal static IReadOnlyList<SparkPoint> BuildSpark(
        IReadOnlyList<TelemetryPoint> history, out double min, out double max)
    {
        var values = history.Where(p => p.TempCAvg.HasValue)
                            .Select(p => p.TempCAvg!.Value)
                            .ToArray();

        min = values.Length > 0 ? values.Min() : 0;
        max = values.Length > 0 ? values.Max() : 0;

        if (values.Length < 2) return Array.Empty<SparkPoint>();

        // A flat series has zero range, which would divide by zero and make every vertex
        // NaN — a line that draws as nothing. Clamping the divisor keeps it a straight line
        // at the floor of the box, which is what a reading pinned to its own minimum is.
        var range = Math.Max(max - min, 0.001);
        var stepX = 100.0 / (values.Length - 1);

        var points = new List<SparkPoint>(values.Length);
        for (var i = 0; i < values.Length; i++)
        {
            // Y is inverted: both XAML hosts grow Y downward, so hot belongs at the top.
            points.Add(new SparkPoint(i * stepX, 100 - ((values[i] - min) / range * 100)));
        }
        return points;
    }
}
