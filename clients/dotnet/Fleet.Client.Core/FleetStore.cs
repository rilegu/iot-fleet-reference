using System.Collections.Concurrent;

namespace Fleet.Client.Core;

/// <summary>
/// Client-side fleet state, rebuilt from the snapshot/delta stream.
///
/// This is the piece every .NET client shares. It is deliberately free of any UI framework
/// type: WinUI will wrap it in MVVM ViewModels, while Blazor subscribes to it directly,
/// because those are the idiomatic patterns in each and forcing one onto the other produces
/// a client that fights its own framework.
///
/// It exposes state two ways on purpose:
///
///   - <see cref="Changed"/>, an event, for hosts that want to be told when to re-render.
///   - <see cref="Snapshot"/>, returning an immutable array, for hosts that pull.
///
/// A binding layer wants the first; a render loop wants the second. Supporting both is a
/// small amount of extra surface for a large amount of flexibility, and it is what keeps
/// this assembly honestly UI-agnostic — it has to serve two consumption styles, so a leaked
/// view concern shows up immediately.
/// </summary>
public sealed class FleetStore
{
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);

    private FleetAggregates _aggregates = new();
    private volatile int _version;

    /// <summary>
    /// Raised after a frame has been applied.
    ///
    /// Raised once per frame, never once per device: the server already coalesces changes
    /// into a frame, and re-raising per device would hand the UI back the very fan-out the
    /// delta protocol exists to remove.
    /// </summary>
    public event Action? Changed;

    /// <summary>Increments on every applied frame. A cheap way for a view to tell whether anything moved.</summary>
    public int Version => _version;

    public FleetAggregates Aggregates => _aggregates;

    /// <summary>Cadence the server told us it is sending at, useful for display and for measurement.</summary>
    public int CadenceMs { get; private set; }

    public long LastFrame { get; private set; }

    /// <summary>
    /// Frames applied since connecting. Compared against <see cref="LastFrame"/> this reveals
    /// dropped frames, which would otherwise be invisible.
    /// </summary>
    public long FramesApplied { get; private set; }

    public DeviceState? Get(string deviceId) =>
        _devices.TryGetValue(deviceId, out var d) ? d : null;

    /// <summary>
    /// Returns the current fleet as an immutable array.
    ///
    /// Materialising a copy sounds wasteful at a thousand devices, but it happens at most
    /// once per frame — four times a second by default — and it removes any possibility of
    /// the collection mutating underneath an enumerator during a render pass.
    /// </summary>
    public DeviceState[] Snapshot() => _devices.Values.ToArray();

    /// <summary>
    /// Replaces the entire fleet. Sent once when a connection is established, and again
    /// after a reconnect, since anything could have changed while the socket was down.
    /// </summary>
    public void ApplySnapshot(IReadOnlyList<DeviceState> devices, FleetAggregates? aggregates, int cadenceMs, long frame)
    {
        _devices.Clear();
        foreach (var d in devices)
            _devices[d.DeviceId] = d;

        if (aggregates is not null) _aggregates = aggregates;
        CadenceMs = cadenceMs;
        LastFrame = frame;
        FramesApplied++;
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies a delta frame: only devices that changed since the previous frame.
    ///
    /// No ordering rule is applied here. The server's projection already enforces
    /// (boot_id, seq) ordering and sends whole current records, so a client applying frames
    /// in the order they arrive on a single ordered stream cannot go backwards. Re-deriving
    /// that logic here would duplicate it in every client language, and any divergence would
    /// be a bug that only manifests in one of them.
    /// </summary>
    public void ApplyDelta(IReadOnlyList<DeviceState> changed, FleetAggregates? aggregates, long frame)
    {
        // Clear the previous frame's highlight before marking this one, so a flash lasts
        // exactly one frame rather than accumulating.
        foreach (var id in _lastChanged)
        {
            if (_devices.TryGetValue(id, out var prev) && prev.JustChanged)
                _devices[id] = prev with { JustChanged = false };
        }
        _lastChanged.Clear();

        foreach (var d in changed)
        {
            _devices[d.DeviceId] = d with { JustChanged = true };
            _lastChanged.Add(d.DeviceId);
        }

        if (aggregates is not null) _aggregates = aggregates;
        LastFrame = frame;
        FramesApplied++;
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }

    private readonly List<string> _lastChanged = new();

    public void Clear()
    {
        _devices.Clear();
        _lastChanged.Clear();
        _aggregates = new FleetAggregates();
        Interlocked.Increment(ref _version);
        Changed?.Invoke();
    }
}
