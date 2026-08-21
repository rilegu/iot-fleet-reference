using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fleet.Client.Core;

namespace Fleet.Client.Xaml;

public enum DeviceSort { Device, Site, Status, Temperature, Voltage, Signal, Sequence, Gaps }

/// <summary>
/// The fleet, as XAML binds to it.
///
/// This is the piece WinUI and WPF share unchanged. Neither reference assembly appears
/// anywhere in this project — the only framework-specific need, marshalling to the UI
/// thread, arrives as <see cref="IUiDispatcher"/>. Two hosts running these ViewModels
/// verbatim is what makes the reuse claim demonstrable rather than asserted.
/// </summary>
public sealed partial class FleetViewModel : ObservableObject, IAsyncDisposable
{
    private readonly FleetStore _store;
    private readonly FleetConnection _connection;
    private readonly IUiDispatcher _ui;

    /// <summary>
    /// Long-lived ViewModels, keyed by device id.
    ///
    /// Devices are updated in place rather than replaced. Replacing them would raise a
    /// collection change per device per frame — around a thousand of them, four times a
    /// second — and every bound row would be torn down and rebuilt. Updating in place means
    /// a frame costs one property notification per field that actually moved.
    /// </summary>
    private readonly Dictionary<string, DeviceViewModel> _byId = new(StringComparer.Ordinal);

    private readonly List<DeviceViewModel> _lastChanged = new();

    public FleetViewModel(FleetStore store, FleetConnection connection, IUiDispatcher ui)
    {
        _store = store;
        _connection = connection;
        _ui = ui;

        Devices = new ObservableCollection<DeviceViewModel>();
        Detail = new DeviceDetailViewModel(connection, ui);

        _store.Changed += OnStoreChanged;
        _connection.StateChanged += OnConnectionChanged;
    }

    /// <summary>The visible fleet, filtered and sorted. Bound directly by both hosts.</summary>
    public ObservableCollection<DeviceViewModel> Devices { get; }

    /// <summary>
    /// The detail panel. Shared with the hosts rather than rebuilt per selection, so the
    /// panel's own bindings stay attached and only its contents change.
    /// </summary>
    public DeviceDetailViewModel Detail { get; }

    /// <summary>Whether the detail panel should be visible. Bound to its container.</summary>
    public bool HasSelection => Selected is not null;

    // ---- connection ---------------------------------------------------------------------

    [ObservableProperty] private bool _connected;
    [ObservableProperty] private string? _connectionError;
    [ObservableProperty] private int _cadenceMs;
    [ObservableProperty] private long _frame;

    public string ConnectionSummary => Connected
        ? $"live · {CadenceMs} ms cadence · frame {Frame}"
        : ConnectionError is null ? "connecting" : $"reconnecting — {ConnectionError}";

    // ---- aggregates ---------------------------------------------------------------------

    [ObservableProperty] private int _total;
    [ObservableProperty] private int _online;
    [ObservableProperty] private int _offline;
    [ObservableProperty] private int _alerting;
    [ObservableProperty] private long _gaps;
    [ObservableProperty] private long _staleDropped;
    [ObservableProperty] private int _shown;

    // ---- filters ------------------------------------------------------------------------
    //
    // Each of these re-runs the projection when set. The generated OnXChanged hooks are why
    // the setters stay one line: the toolkit's source generator writes the notification and
    // calls the partial method, so the filter logic lives in one place.

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlineOnly;
    [ObservableProperty] private bool _alertingOnly;
    [ObservableProperty] private DeviceSort _sort = DeviceSort.Device;
    [ObservableProperty] private bool _descending;
    [ObservableProperty] private DeviceViewModel? _selected;

    partial void OnSelectedChanged(DeviceViewModel? oldValue, DeviceViewModel? newValue)
    {
        OnPropertyChanged(nameof(HasSelection));

        // Carried on the row so a virtualising list can redraw the highlight when a recycled
        // row scrolls back into view.
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;

        if (newValue is null) Detail.Clear();
        else _ = Detail.LoadAsync(newValue);
    }

    [RelayCommand]
    private void CloseDetail() => Selected = null;

    partial void OnSearchChanged(string value) => Reproject();
    partial void OnOnlineOnlyChanged(bool value) => Reproject();
    partial void OnAlertingOnlyChanged(bool value) => Reproject();
    partial void OnSortChanged(DeviceSort value) => Reproject();
    partial void OnDescendingChanged(bool value) => Reproject();

    /// <summary>Clicking the active column reverses it; clicking another switches to it ascending.</summary>
    [RelayCommand]
    private void SortBy(DeviceSort column)
    {
        if (Sort == column) Descending = !Descending;
        else { Sort = column; Descending = false; }
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Search = "";
        OnlineOnly = false;
        AlertingOnly = false;
    }

    /// <summary>
    /// Opens the connection. A command rather than a constructor call so a host can bind it
    /// to a retry button, and so construction stays free of I/O.
    /// </summary>
    [RelayCommand]
    private Task ConnectAsync()
    {
        _connection.Start();
        return Task.CompletedTask;
    }

    // ---- frame handling -----------------------------------------------------------------

    private void OnConnectionChanged() => _ui.Post(() =>
    {
        Connected = _connection.Connected;
        ConnectionError = _connection.LastError;
        OnPropertyChanged(nameof(ConnectionSummary));
    });

    /// <summary>
    /// Applies one frame.
    ///
    /// Posted to the UI thread as a single unit. Doing it per device would put a thousand
    /// work items on the dispatcher queue four times a second, and the queue would never
    /// drain — the classic way a XAML application becomes unresponsive while its data source
    /// is perfectly healthy.
    /// </summary>
    private void OnStoreChanged() => _ui.Post(ApplyFrame);

    private void ApplyFrame()
    {
        // Clear the previous frame's highlight before marking this one, so a flash lasts
        // exactly one cadence.
        foreach (var vm in _lastChanged) vm.JustChanged = false;
        _lastChanged.Clear();

        var snapshot = _store.Snapshot();
        var seen = new HashSet<string>(snapshot.Length, StringComparer.Ordinal);
        var added = false;

        foreach (var state in snapshot)
        {
            seen.Add(state.DeviceId);

            if (_byId.TryGetValue(state.DeviceId, out var vm))
            {
                vm.Apply(state);
            }
            else
            {
                vm = new DeviceViewModel(state);
                _byId[state.DeviceId] = vm;
                added = true;
            }

            if (state.JustChanged)
            {
                vm.JustChanged = true;
                _lastChanged.Add(vm);
            }
        }

        // Devices only disappear when the projection drops them, which is rare. Checking
        // for it every frame is cheap; failing to check would leak a ViewModel per device
        // that ever existed.
        var removed = _byId.Keys.Where(id => !seen.Contains(id)).ToArray();
        foreach (var id in removed) _byId.Remove(id);

        var aggregates = _store.Aggregates;
        Total = aggregates.Total;
        Online = aggregates.Online;
        Offline = aggregates.Offline;
        Alerting = aggregates.Alerting;
        Gaps = aggregates.Gaps;
        StaleDropped = aggregates.StaleDropped;
        CadenceMs = _store.CadenceMs;
        Frame = _store.LastFrame;
        OnPropertyChanged(nameof(ConnectionSummary));

        // The visible collection only needs rebuilding when membership or ordering could
        // have changed. Field updates alone are picked up by each row's own bindings, so
        // rebuilding on every frame would be pure waste — and visible waste, since it
        // resets scroll position and selection.
        if (added || removed.Length > 0 || SortDependsOnValues())
            Reproject();
    }

    /// <summary>
    /// True when the active sort is over a value that changes with every frame.
    ///
    /// Sorting by device id or site is stable, so the order cannot change between frames and
    /// the collection can be left alone. Sorting by temperature means the order genuinely
    /// moves, and the list has to be rebuilt to reflect it.
    /// </summary>
    private bool SortDependsOnValues() => Sort
        is DeviceSort.Temperature or DeviceSort.Voltage or DeviceSort.Signal
        or DeviceSort.Sequence or DeviceSort.Gaps or DeviceSort.Status;

    private void Reproject()
    {
        IEnumerable<DeviceViewModel> query = _byId.Values;

        if (!string.IsNullOrWhiteSpace(Search))
        {
            var term = Search.Trim();
            query = query.Where(d =>
                d.DeviceId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                d.Site.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (d.Firmware?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (OnlineOnly) query = query.Where(d => d.Online);
        if (AlertingOnly) query = query.Where(d => d.IsAlerting);

        // Ordinal throughout: device ids and sites are machine identifiers, and culture-aware
        // comparison would be slower and occasionally surprising.
        query = Sort switch
        {
            DeviceSort.Site => Order(query, d => d.Site, StringComparer.Ordinal),
            DeviceSort.Status => Order(query, d => d.Online ? 1 : 0),
            DeviceSort.Temperature => Order(query, d => d.Temperature),
            DeviceSort.Voltage => Order(query, d => d.Voltage),
            DeviceSort.Signal => Order(query, d => d.Signal),
            DeviceSort.Sequence => Order(query, d => d.Seq),
            DeviceSort.Gaps => Order(query, d => d.Gaps),
            _ => Order(query, d => d.DeviceId, StringComparer.Ordinal),
        };

        var projected = query.ToArray();

        // Rebuild only if the result actually differs. An ObservableCollection cleared and
        // refilled raises a reset, which collapses grouping, drops selection and jumps the
        // scroll position — every frame, if done unconditionally.
        if (!SameSequence(Devices, projected))
        {
            Devices.Clear();
            foreach (var vm in projected) Devices.Add(vm);
        }

        Shown = projected.Length;
    }

    private static bool SameSequence(IList<DeviceViewModel> current, DeviceViewModel[] next)
    {
        if (current.Count != next.Length) return false;
        for (var i = 0; i < next.Length; i++)
            if (!ReferenceEquals(current[i], next[i]))
                return false;
        return true;
    }

    // Every sort falls back to the device id, which makes the comparison total.
    //
    // OrderBy is a stable sort, but stability only preserves the order of the input, and the
    // input here is Dictionary.Values — insertion order until the first removal, after which
    // freed slots are reused by later insertions. So a device leaving the fleet could permute
    // every row that ties on the sort key. Invisible when sorting by device id, obvious when
    // sorting by status, where hundreds of rows share a value.
    //
    // The tiebreaker follows the sort direction rather than staying ascending, so that the
    // Electron client, which reverses its whole comparison, agrees row for row.

    private IOrderedEnumerable<DeviceViewModel> Order<TKey>(
        IEnumerable<DeviceViewModel> q, Func<DeviceViewModel, TKey> key) =>
        Descending
            ? q.OrderByDescending(key).ThenByDescending(d => d.DeviceId, StringComparer.Ordinal)
            : q.OrderBy(key).ThenBy(d => d.DeviceId, StringComparer.Ordinal);

    private IOrderedEnumerable<DeviceViewModel> Order<TKey>(
        IEnumerable<DeviceViewModel> q, Func<DeviceViewModel, TKey> key, IComparer<TKey> comparer) =>
        Descending
            ? q.OrderByDescending(key, comparer).ThenByDescending(d => d.DeviceId, StringComparer.Ordinal)
            : q.OrderBy(key, comparer).ThenBy(d => d.DeviceId, StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        _store.Changed -= OnStoreChanged;
        _connection.StateChanged -= OnConnectionChanged;
        await _connection.DisposeAsync();
    }
}
