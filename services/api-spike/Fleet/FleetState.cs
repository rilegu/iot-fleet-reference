using System.Collections.Concurrent;

namespace ApiSpike.Fleet;

public sealed record DeviceView
{
    public required string DeviceId { get; init; }
    public required string Site { get; init; }
    public string BootId { get; init; } = "";
    public bool Online { get; init; }
    public string? OfflineReason { get; init; }
    public string? FwVersion { get; init; }
    public ulong LastSeq { get; init; }

    /// <summary>Count of detected sequence gaps. A non-zero value means messages were lost.</summary>
    public long Gaps { get; init; }

    public Metrics? Metrics { get; init; }

    /// <summary>Server-side arrival time. Device timestamps are never used for ordering or latency.</summary>
    public DateTimeOffset LastSeenUtc { get; init; }

    /// <summary>
    /// True while everything known about this device came from a retained replay rather
    /// than a live message, so its sequence is a historical baseline and the next live
    /// message must not be scored against it.
    /// </summary>
    public bool Provisional { get; init; }
}

public sealed class FleetStats
{
    public int Total { get; init; }
    public int Online { get; init; }
    public int Offline { get; init; }
    public long TelemetryReceived { get; init; }
    public long StatusReceived { get; init; }
    public long Rejected { get; init; }
    public long Gaps { get; init; }
    public long StaleDropped { get; init; }
}

/// <summary>
/// Exploratory in-memory fleet projection. Deliberately naive: no checkpoint, no replay, no
/// persistence. A recoverable projection replaces it once there is a durable event log to
/// recover from.
/// </summary>
public sealed class FleetState
{
    private readonly ConcurrentDictionary<string, DeviceView> _devices = new();

    private long _telemetryReceived;
    private long _statusReceived;
    private long _rejected;
    private long _gaps;
    private long _staleDropped;

    public IReadOnlyCollection<DeviceView> Snapshot() => _devices.Values.ToArray();

    public void CountRejected() => Interlocked.Increment(ref _rejected);

    public void ApplyTelemetry(TelemetryMessage msg)
    {
        Interlocked.Increment(ref _telemetryReceived);

        _devices.AddOrUpdate(
            msg.DeviceId,
            key => new DeviceView
            {
                DeviceId = msg.DeviceId,
                Site = msg.Site,
                BootId = msg.BootId,
                Online = true,
                LastSeq = msg.Seq,
                Metrics = msg.Metrics,
                LastSeenUtc = DateTimeOffset.UtcNow,
            },
            (key, current) =>
            {
                if (!ShouldApply(current, msg.BootId, msg.Seq, out var gapped))
                {
                    Interlocked.Increment(ref _staleDropped);
                    return current;
                }

                // The first message after a retained baseline is not a gap: the baseline
                // came from the broker's replay, not from the device's previous send.
                if (current.Provisional)
                {
                    gapped = false;
                }
                if (gapped) Interlocked.Increment(ref _gaps);

                return current with
                {
                    Site = msg.Site,
                    BootId = msg.BootId,
                    // Telemetry proves liveness, but presence is owned by status messages;
                    // a device is only marked online here if it was never seen otherwise.
                    Online = current.Online,
                    Provisional = false,
                    LastSeq = msg.Seq,
                    Gaps = current.Gaps + (gapped ? 1 : 0),
                    Metrics = msg.Metrics,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            });
    }

    /// <param name="retained">
    /// True when the broker replayed this on subscribe rather than the device publishing it
    /// now. A retained message is historical by definition: it establishes a baseline for a
    /// device we have not seen, but it must never count as a gap or as a stale drop, and it
    /// must never move a known device's sequence backwards.
    /// </param>
    public void ApplyStatus(StatusMessage msg, bool retained = false)
    {
        Interlocked.Increment(ref _statusReceived);

        _devices.AddOrUpdate(
            msg.DeviceId,
            key => new DeviceView
            {
                DeviceId = msg.DeviceId,
                Site = msg.Site,
                BootId = msg.BootId,
                Online = msg.Online,
                OfflineReason = msg.Online ? null : msg.Reason,
                FwVersion = msg.FwVersion,
                LastSeq = msg.Seq,
                LastSeenUtc = DateTimeOffset.UtcNow,
                Provisional = retained,
            },
            (key, current) =>
            {
                // A will carries seq 0 because it is composed at connect time, before the
                // device knows its final sequence number. Applying the normal ordering rule
                // would discard every offline transition and the dashboard would never
                // notice a device dying. See contracts/README.md.
                var isWill = msg.Seq == 0;

                // A replayed retained message is older than anything we already hold. Take
                // only the metadata we may be missing and leave the sequence alone.
                if (retained)
                {
                    return current with
                    {
                        FwVersion = current.FwVersion ?? msg.FwVersion,
                        Site = string.IsNullOrEmpty(current.Site) ? msg.Site : current.Site,
                    };
                }

                if (!isWill && !ShouldApply(current, msg.BootId, msg.Seq, out _))
                {
                    Interlocked.Increment(ref _staleDropped);
                    return current;
                }

                return current with
                {
                    Site = msg.Site,
                    BootId = isWill ? current.BootId : msg.BootId,
                    Online = msg.Online,
                    OfflineReason = msg.Online ? null : msg.Reason,
                    FwVersion = msg.FwVersion ?? current.FwVersion,
                    LastSeq = isWill ? current.LastSeq : msg.Seq,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };
            });
    }

    /// <summary>
    /// Ordering rule from ADR-0008: apply only if this message is newer than what we hold.
    /// A changed boot id means the device restarted and its sequence reset, so the new
    /// sequence is accepted even though it is numerically lower.
    /// </summary>
    private static bool ShouldApply(DeviceView current, string bootId, ulong seq, out bool gapped)
    {
        gapped = false;

        if (!string.Equals(current.BootId, bootId, StringComparison.Ordinal))
            return true; // device rebooted; sequence restarts

        if (seq <= current.LastSeq)
            return false; // duplicate or reordered

        gapped = seq > current.LastSeq + 1;
        return true;
    }

    public FleetStats Stats()
    {
        var devices = _devices.Values.ToArray();
        var online = devices.Count(d => d.Online);
        return new FleetStats
        {
            Total = devices.Length,
            Online = online,
            Offline = devices.Length - online,
            TelemetryReceived = Interlocked.Read(ref _telemetryReceived),
            StatusReceived = Interlocked.Read(ref _statusReceived),
            Rejected = Interlocked.Read(ref _rejected),
            Gaps = Interlocked.Read(ref _gaps),
            StaleDropped = Interlocked.Read(ref _staleDropped),
        };
    }
}
