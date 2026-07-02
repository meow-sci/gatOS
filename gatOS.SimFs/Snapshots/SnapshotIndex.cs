using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace gatOS.SimFs.Snapshots;

/// <summary>
///     Per-snapshot memoized projections (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP1 —
///     design key K1). A <see cref="SimSnapshot"/> is immutable and shared by reference across
///     every consumer, so any pure projection of it — the vessel-by-id index, a vessel's
///     formatted NDJSON stream line — can be computed <b>once per snapshot</b> and shared by all
///     readers and transports. The memo lives in a <see cref="ConditionalWeakTable{TKey,TValue}"/>
///     keyed by the snapshot instance: zero change to the record shape (no JSON/equality risk),
///     and the cache dies with the snapshot.
/// </summary>
/// <remarks>
///     Thread-safety: initialization races are benign — two threads may compute the same
///     projection concurrently once; the last volatile swap wins and both results are correct
///     (pure functions of an immutable input). Steady state is a table hit + a field read.
/// </remarks>
public static class SnapshotIndex
{
    private static readonly ConditionalWeakTable<SimSnapshot, Entry> Table = new();

    private sealed class Entry
    {
        internal volatile Dictionary<string, VesselSnapshot>? VesselsById;
        internal volatile ConcurrentDictionary<string, byte[]>? StreamLines;
    }

    /// <summary>
    ///     The vessel with this id in <paramref name="snapshot"/>, or null — an O(1) dictionary hit
    ///     replacing the per-read O(N) list scans (and their closure allocations) every transport
    ///     used to do.
    /// </summary>
    public static VesselSnapshot? VesselById(SimSnapshot snapshot, string vesselId)
    {
        var entry = Table.GetOrCreateValue(snapshot);
        var byId = entry.VesselsById;
        if (byId is null)
        {
            byId = new Dictionary<string, VesselSnapshot>(snapshot.Vessels.Count, StringComparer.Ordinal);
            for (var i = 0; i < snapshot.Vessels.Count; i++)
                byId.TryAdd(snapshot.Vessels[i].Id, snapshot.Vessels[i]);
            entry.VesselsById = byId;
        }

        return byId.GetValueOrDefault(vesselId);
    }

    /// <summary>
    ///     The vessel's NDJSON stream line for this snapshot (<see cref="Formats.StreamLine"/>),
    ///     formatted <b>once per (snapshot, vessel)</b> and shared: every open stream fid on the
    ///     same vessel appends the same bytes, and the unopened-stat <c>Size</c> reuses them
    ///     instead of serializing a line just to measure it.
    /// </summary>
    public static byte[] StreamLine(SimSnapshot snapshot, VesselSnapshot vessel)
    {
        var entry = Table.GetOrCreateValue(snapshot);
        var lines = entry.StreamLines;
        if (lines is null)
        {
            lines = new ConcurrentDictionary<string, byte[]>(StringComparer.Ordinal);
            entry.StreamLines = lines; // racing initializers may drop a colleague's dict — benign
        }

        return lines.TryGetValue(vessel.Id, out var cached)
            ? cached
            : lines.GetOrAdd(vessel.Id, Formats.StreamLine(snapshot, vessel));
    }
}
