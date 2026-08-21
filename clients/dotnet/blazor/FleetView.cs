using Fleet.Client.Core;

namespace FleetDashboard;

public enum SortColumn { Device, Site, Status, Temp, Voltage, Rssi, Seq, Gaps }

/// <summary>
/// The dashboard's view of the fleet: filtering, sorting, selection.
///
/// Deliberately NOT a ViewModel in the MVVM sense — no INotifyPropertyChanged, no
/// observable collections, no commands. Blazor renders by being told to re-render, so
/// property-change notification would be machinery the framework never consults. The XAML
/// clients wrap the same <see cref="FleetStore"/> in real ViewModels because XAML's binding
/// engine does consult it; that difference is the point of implementing the same dashboard
/// several times rather than a limitation of either.
///
/// Scoped per circuit, so each browser session has its own filter state and its own
/// connection, exactly as a desktop client would.
/// </summary>
public sealed class FleetView : IAsyncDisposable
{
    private readonly FleetStore _store;
    private readonly FleetConnection _connection;

    public FleetView(FleetStore store, FleetConnection connection)
    {
        _store = store;
        _connection = connection;
    }

    public FleetStore Store => _store;
    public FleetConnection Connection => _connection;

    // Filter state. Changing any of these invalidates the cached projection below.
    private string _search = "";
    private string? _site;
    private bool _onlineOnly;
    private bool _alertingOnly;
    private SortColumn _sort = SortColumn.Device;
    private bool _descending;

    public string Search
    {
        get => _search;
        set { _search = value ?? ""; Invalidate(); }
    }

    public string? Site
    {
        get => _site;
        set { _site = value; Invalidate(); }
    }

    public bool OnlineOnly
    {
        get => _onlineOnly;
        set { _onlineOnly = value; Invalidate(); }
    }

    public bool AlertingOnly
    {
        get => _alertingOnly;
        set { _alertingOnly = value; Invalidate(); }
    }

    public SortColumn Sort => _sort;
    public bool Descending => _descending;

    /// <summary>Clicking the active column reverses it; clicking another switches to it ascending.</summary>
    public void SortBy(SortColumn column)
    {
        if (_sort == column) _descending = !_descending;
        else { _sort = column; _descending = false; }
        Invalidate();
    }

    public string? SelectedDeviceId { get; private set; }

    public void Select(string? deviceId) => SelectedDeviceId = deviceId;

    public DeviceState? SelectedDevice =>
        SelectedDeviceId is null ? null : _store.Get(SelectedDeviceId);

    // -----------------------------------------------------------------------------------
    // The filtered and sorted list, cached.
    //
    // Filtering and sorting a thousand devices is a few hundred microseconds — negligible
    // once, wasteful if repeated. Blazor may call a render fragment more than once per
    // state change, so without caching this work would run several times per frame for no
    // benefit. The cache is keyed on the store's version plus a filter generation counter,
    // so it recomputes exactly when the data or the filters actually moved.
    // -----------------------------------------------------------------------------------

    private DeviceState[] _cached = Array.Empty<DeviceState>();
    private int _cachedStoreVersion = -1;
    private int _cachedFilterGeneration = -1;
    private int _filterGeneration;

    private void Invalidate() => _filterGeneration++;

    public DeviceState[] Devices
    {
        get
        {
            if (_cachedStoreVersion == _store.Version && _cachedFilterGeneration == _filterGeneration)
                return _cached;

            IEnumerable<DeviceState> q = _store.Snapshot();

            if (!string.IsNullOrWhiteSpace(_search))
            {
                var term = _search.Trim();
                q = q.Where(d =>
                    d.DeviceId.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    d.Site.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    (d.FwVersion?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (!string.IsNullOrEmpty(_site)) q = q.Where(d => d.Site == _site);
            if (_onlineOnly) q = q.Where(d => d.Online);
            if (_alertingOnly) q = q.Where(d => d.LastEventSeverity is "warning" or "critical");

            // Ordinal comparison throughout: device ids and sites are machine identifiers,
            // and culture-aware comparison would be both slower and occasionally surprising.
            q = _sort switch
            {
                SortColumn.Site => Order(q, d => d.Site, StringComparer.Ordinal),
                SortColumn.Status => Order(q, d => d.Online ? 1 : 0),
                SortColumn.Temp => Order(q, d => d.Metrics?.TempC ?? double.MinValue),
                SortColumn.Voltage => Order(q, d => d.Metrics?.VoltageV ?? double.MinValue),
                SortColumn.Rssi => Order(q, d => d.Metrics?.RssiDbm ?? int.MinValue),
                SortColumn.Seq => Order(q, d => d.Seq),
                SortColumn.Gaps => Order(q, d => d.Gaps),
                _ => Order(q, d => d.DeviceId, StringComparer.Ordinal),
            };

            _cached = q.ToArray();
            _cachedStoreVersion = _store.Version;
            _cachedFilterGeneration = _filterGeneration;
            return _cached;
        }
    }

    private IOrderedEnumerable<DeviceState> Order<TKey>(IEnumerable<DeviceState> q, Func<DeviceState, TKey> key) =>
        _descending ? q.OrderByDescending(key) : q.OrderBy(key);

    private IOrderedEnumerable<DeviceState> Order<TKey>(IEnumerable<DeviceState> q, Func<DeviceState, TKey> key, IComparer<TKey> comparer) =>
        _descending ? q.OrderByDescending(key, comparer) : q.OrderBy(key, comparer);

    public IReadOnlyList<string> Sites =>
        _store.Snapshot().Select(d => d.Site).Distinct(StringComparer.Ordinal)
              .OrderBy(s => s, StringComparer.Ordinal).ToArray();

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
