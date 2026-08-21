using System.Collections.Concurrent;

namespace FleetApi.Fleet;

/// <summary>
/// Current fleet state, built by applying the event log.
///
/// The projection is a cache, never the source of truth: TimescaleDB is. That is what makes
/// it safe to rebuild by replaying the log, and why an over-replay is harmless.
///
/// Every apply is idempotent and ordered by (boot_id, seq), so redelivery cannot corrupt
/// state and out-of-order delivery cannot move a device backwards. See ADR-0008.
/// </summary>
public sealed class FleetProjection
{
    private readonly ConcurrentDictionary<string, DeviceState> _devices = new(StringComparer.Ordinal);

    private long _applied;
    private long _staleDropped;
    private long _gaps;

    /// <summary>
    /// Devices changed since the last drain, coalesced last-write-wins. The dashboard is
    /// told what changed, not every message that caused it.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.Ordinal);

    public int Count => _devices.Count;
    public long Applied => Interlocked.Read(ref _applied);
    public long StaleDropped => Interlocked.Read(ref _staleDropped);

    public IReadOnlyCollection<DeviceState> Snapshot() => _devices.Values.ToArray();

    public DeviceState? Get(string deviceId) =>
        _devices.TryGetValue(deviceId, out var d) ? d : null;

    /// <summary>
    /// Takes the set of devices that changed since the last call and clears it.
    /// Coalescing happens here: a device that changed fifty times appears once.
    /// </summary>
    public IReadOnlyList<string> DrainDirty()
    {
        if (_dirty.IsEmpty) return Array.Empty<string>();
        var keys = _dirty.Keys.ToArray();
        foreach (var k in keys) _dirty.TryRemove(k, out _);
        return keys;
    }

    public void ApplyTelemetry(TelemetryMessage msg, bool retained = false)
    {
        Interlocked.Increment(ref _applied);

        _devices.AddOrUpdate(msg.DeviceId,
            key => new DeviceState
            {
                DeviceId = msg.DeviceId,
                Site = msg.Site,
                BootId = msg.BootId,
                Seq = msg.Seq,
                Metrics = msg.Metrics,
                LastSeen = DateTimeOffset.UtcNow,
                Provisional = retained,
            },
            (key, current) =>
            {
                if (!ShouldApply(current, msg.BootId, msg.Seq, out var gapped))
                {
                    Interlocked.Increment(ref _staleDropped);
                    FleetTelemetry.StaleDropped.Add(1, new KeyValuePair<string, object?>("kind", "telemetry"));
                    return current;
                }

                // The first live message after a retained baseline is not a gap: the
                // baseline came from the broker's replay, not the device's previous send.
                if (current.Provisional) gapped = false;
                if (gapped)
                {
                    Interlocked.Increment(ref _gaps);
                    FleetTelemetry.Gaps.Add(1, new KeyValuePair<string, object?>("site", msg.Site));
                }

                return current with
                {
                    Site = msg.Site,
                    BootId = msg.BootId,
                    // Presence belongs to status messages. Telemetry proves liveness but
                    // must not resurrect a device the broker reported offline.
                    Seq = msg.Seq,
                    Gaps = current.Gaps + (gapped ? 1 : 0),
                    Metrics = msg.Metrics,
                    LastSeen = DateTimeOffset.UtcNow,
                    Provisional = false,
                };
            });

        _dirty[msg.DeviceId] = 1;
    }

    public void ApplyStatus(StatusMessage msg, bool retained = false)
    {
        Interlocked.Increment(ref _applied);

        _devices.AddOrUpdate(msg.DeviceId,
            key => new DeviceState
            {
                DeviceId = msg.DeviceId,
                Site = msg.Site,
                BootId = msg.BootId,
                Online = msg.Online,
                OfflineReason = msg.Online ? null : msg.Reason,
                FwVersion = msg.FwVersion,
                Model = msg.Model,
                Seq = msg.Seq,
                LastSeen = DateTimeOffset.UtcNow,
                Provisional = retained,
            },
            (key, current) =>
            {
                // A retained replay is older than anything already held. Take only the
                // metadata that may be missing and leave the sequence alone.
                if (retained)
                {
                    return current with
                    {
                        FwVersion = current.FwVersion ?? msg.FwVersion,
                        Model = current.Model ?? msg.Model,
                    };
                }

                // A will carries seq 0, because it is composed at connect time before the
                // device knows its final sequence number. Applying the ordinary ordering
                // rule would discard every offline transition and a dying device would
                // never be noticed.
                var isWill = msg.Seq == 0;

                if (!isWill && !ShouldApply(current, msg.BootId, msg.Seq, out _))
                {
                    Interlocked.Increment(ref _staleDropped);
                    FleetTelemetry.StaleDropped.Add(1, new KeyValuePair<string, object?>("kind", "status"));
                    return current;
                }

                return current with
                {
                    Site = msg.Site,
                    BootId = isWill ? current.BootId : msg.BootId,
                    Online = msg.Online,
                    OfflineReason = msg.Online ? null : msg.Reason,
                    FwVersion = msg.FwVersion ?? current.FwVersion,
                    Model = msg.Model ?? current.Model,
                    Seq = isWill ? current.Seq : msg.Seq,
                    LastSeen = DateTimeOffset.UtcNow,
                    Provisional = false,
                };
            });

        _dirty[msg.DeviceId] = 1;
    }

    public void ApplyEvent(EventMessage msg, bool retained = false)
    {
        Interlocked.Increment(ref _applied);

        _devices.AddOrUpdate(msg.DeviceId,
            key => new DeviceState
            {
                DeviceId = msg.DeviceId,
                Site = msg.Site,
                BootId = msg.BootId,
                Seq = msg.Seq,
                LastEvent = msg.EventKind,
                LastEventSeverity = msg.Severity,
                LastSeen = DateTimeOffset.UtcNow,
                Provisional = retained,
            },
            (key, current) =>
            {
                if (!ShouldApply(current, msg.BootId, msg.Seq, out _))
                {
                    Interlocked.Increment(ref _staleDropped);
                    FleetTelemetry.StaleDropped.Add(1, new KeyValuePair<string, object?>("kind", "event"));
                    return current;
                }
                return current with
                {
                    Site = msg.Site,
                    BootId = msg.BootId,
                    Seq = msg.Seq,
                    LastEvent = msg.EventKind,
                    LastEventSeverity = msg.Severity,
                    LastSeen = DateTimeOffset.UtcNow,
                    Provisional = false,
                };
            });

        _dirty[msg.DeviceId] = 1;
    }

    /// <summary>
    /// The ordering rule. Apply only if this message is newer than what is held.
    ///
    /// A changed boot id means the device restarted and its sequence reset, so the new
    /// sequence is accepted even though it is numerically lower. Without that, a rebooted
    /// device sending seq 1 would be ignored forever.
    /// </summary>
    private static bool ShouldApply(DeviceState current, string bootId, long seq, out bool gapped)
    {
        gapped = false;

        if (!string.Equals(current.BootId, bootId, StringComparison.Ordinal))
            return true;

        if (seq <= current.Seq)
            return false;

        gapped = seq > current.Seq + 1;
        return true;
    }

    public FleetAggregates Aggregates()
    {
        var devices = _devices.Values.ToArray();
        var online = 0;
        var alerting = 0;
        long gaps = 0;
        var sites = new HashSet<string>(StringComparer.Ordinal);

        foreach (var d in devices)
        {
            if (d.Online) online++;
            if (d.LastEventSeverity is "warning" or "critical") alerting++;
            gaps += d.Gaps;
            sites.Add(d.Site);
        }

        return new FleetAggregates
        {
            Total = devices.Length,
            Online = online,
            Offline = devices.Length - online,
            Alerting = alerting,
            Gaps = gaps,
            Applied = Applied,
            StaleDropped = StaleDropped,
            Sites = sites.Count,
        };
    }
}
