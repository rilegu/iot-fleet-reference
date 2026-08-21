using CommunityToolkit.Mvvm.ComponentModel;
using Fleet.Client.Core;

namespace Fleet.Client.Xaml;

/// <summary>
/// One device, as XAML binds to it.
///
/// This exists rather than binding directly to <see cref="DeviceState"/> for one reason:
/// <c>DeviceState</c> is an immutable record replaced wholesale on every frame, and a
/// binding pointed at a replaced object updates nothing. A long-lived ViewModel whose
/// properties change in place is what the binding engine is built to observe.
///
/// That difference is the whole reason the XAML clients need a ViewModel layer and the
/// Blazor client does not: Blazor re-renders when told to and can read a fresh record each
/// time, while XAML watches objects and needs the object to stay put.
/// </summary>
public sealed partial class DeviceViewModel : ObservableObject
{
    public DeviceViewModel(DeviceState state)
    {
        DeviceId = state.DeviceId;
        Apply(state);
    }

    /// <summary>Identity, fixed for the lifetime of this ViewModel. Not observable because it never changes.</summary>
    public string DeviceId { get; }

    [ObservableProperty] private string _site = "";
    [ObservableProperty] private string _bootId = "";
    [ObservableProperty] private bool _online;
    [ObservableProperty] private string? _offlineReason;
    [ObservableProperty] private string? _firmware;
    [ObservableProperty] private string? _model;
    [ObservableProperty] private long _seq;
    [ObservableProperty] private long _gaps;
    [ObservableProperty] private double _temperature;
    [ObservableProperty] private double _humidity;
    [ObservableProperty] private double _voltage;
    [ObservableProperty] private int _signal;
    [ObservableProperty] private long _uptimeSeconds;
    [ObservableProperty] private string? _lastEvent;
    [ObservableProperty] private string? _lastEventSeverity;
    [ObservableProperty] private DateTimeOffset _lastSeen;

    /// <summary>
    /// Presentation-only. Both hosts bind a row highlight to it, and it is cleared on the
    /// next frame so a flash lasts exactly one cadence rather than accumulating.
    /// </summary>
    [ObservableProperty] private bool _justChanged;

    /// <summary>
    /// Presentation-only, and maintained by <see cref="FleetViewModel"/> rather than set
    /// from a view.
    ///
    /// WPF gets selection from the ListView it binds and does not need this. WinUI draws the
    /// grid with an ItemsRepeater, which has no selection concept at all, so the flag has to
    /// live on the row itself for the highlight to survive virtualisation — a scrolled-away
    /// row is recycled, and only per-item state comes back with it.
    /// </summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>True when the last event was serious enough to warrant attention.</summary>
    public bool IsAlerting => LastEventSeverity is "warning" or "critical";

    /// <summary>
    /// Copies a frame's values in.
    ///
    /// Each assignment raises PropertyChanged only if the value actually differs — the
    /// generated setters compare first. That matters at a thousand devices: a frame where
    /// only the temperature moved should cost one notification, not sixteen.
    /// </summary>
    public void Apply(DeviceState state)
    {
        Site = state.Site;
        BootId = state.BootId;
        Online = state.Online;
        OfflineReason = state.OfflineReason;
        Firmware = state.FwVersion;
        Model = state.Model;
        Seq = state.Seq;
        Gaps = state.Gaps;
        LastSeen = state.LastSeen;

        var previousSeverity = LastEventSeverity;
        LastEvent = state.LastEvent;
        LastEventSeverity = state.LastEventSeverity;
        if (previousSeverity != state.LastEventSeverity)
            OnPropertyChanged(nameof(IsAlerting));

        if (state.Metrics is { } m)
        {
            Temperature = m.TempC;
            Humidity = m.HumidityPct;
            Voltage = m.VoltageV;
            Signal = m.RssiDbm;
            UptimeSeconds = m.UptimeS;
        }
    }
}
