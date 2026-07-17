using System.Runtime.CompilerServices;
using gatOS.SimFs.Snapshots;
using gatOS.SimFs.Telemetry;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Readers;

/// <summary>
///     Reads a vehicle's top-level parts (<c>Vehicle.Parts.Parts</c>) into <see cref="PartSnapshot"/>s
///     — the anchor picker for the welds cheat (<c>/sim/vessels/by-id/&lt;id&gt;/parts</c>). Each part's
///     subparts (<c>Part.SubParts</c> — full <see cref="Part"/> instances with their own
///     <c>InstanceId</c>) are surfaced under it as <see cref="SubpartSnapshot"/>s, so a weld can anchor
///     to either level.
/// </summary>
/// <remarks>
///     <para>The list is cached per vehicle and rebuilt only when the part <b>count changes</b> — the
///     cheap "vehicle was edited" signal (KSA exposes no part-tree version/dirty flag) — or when
///     <see cref="RebuildIntervalSeconds"/> sim-seconds elapse, the backstop for a count-preserving
///     edit. So the hot path is one <c>Parts.Count</c> read per vehicle per sample tick.</para>
///     <para>The cache is keyed by <see cref="Vehicle"/> through a <see cref="ConditionalWeakTable{TKey,TValue}"/>,
///     so an entry is collected with its vehicle (no leak, no manual pruning). Game-thread only (the
///     sampler is the sole caller), so the table sees no concurrent access.</para>
/// </remarks>
internal static class PartsReader
{
    private const double RebuildIntervalSeconds = 10.0;

    private static readonly ConditionalWeakTable<Vehicle, CacheEntry> Cache = new();

    [KsaAnchor("Vehicle.Parts.Parts (ReadOnlySpan<Part>), .Count; Part.{InstanceId,Id,DisplayName,"
            + "Template.Id,PartParent,SubParts,PositionVehicleAsmb}",
        SourceFile = "KSA/Part.cs / KSA/PartTree.cs", Verified = "2026-07-16", GameVersion = "2026.7.6.4939",
        Risk = ChurnRisk.Low,
        Notes = "Part + subpart enumeration for the welds anchor picker (subparts are Part instances "
            + "with their own InstanceId; PositionVehicleAsmb is subpart-aware). Cached; rebuilt on "
            + "Parts.Count change (no public part-tree version exists) or every 10 s.")]
    public static IReadOnlyList<PartSnapshot> Sample(Vehicle vehicle, double utSeconds)
    {
        var entry = Cache.GetOrCreateValue(vehicle);
        var liveCount = vehicle.Parts.Count;
        if (entry.Count != liveCount || utSeconds - entry.BuiltUt >= RebuildIntervalSeconds)
        {
            entry.List = Build(vehicle);
            entry.Count = liveCount;
            entry.BuiltUt = utSeconds;
        }

        return entry.List;
    }

    private static List<PartSnapshot> Build(Vehicle vehicle)
    {
        var parts = vehicle.Parts.Parts; // top-level; subparts nested per part
        var list = new List<PartSnapshot>(parts.Length);
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var pos = part.PositionVehicleAsmb;
            list.Add(new PartSnapshot(
                i, part.InstanceId, part.Id, part.DisplayName, part.Template.Id,
                part.PartParent is null, part.SubParts.Length,
                new double3Snap(Sanitize.Finite(pos.X), Sanitize.Finite(pos.Y), Sanitize.Finite(pos.Z)))
            {
                Subparts = BuildSubparts(part),
            });
        }

        return list;
    }

    private static IReadOnlyList<SubpartSnapshot> BuildSubparts(Part part)
    {
        var subParts = part.SubParts;
        if (subParts.Length == 0)
            return [];
        var list = new List<SubpartSnapshot>(subParts.Length);
        for (var j = 0; j < subParts.Length; j++)
        {
            var sub = subParts[j];
            var pos = sub.PositionVehicleAsmb; // subpart-aware: composes through PartParent
            list.Add(new SubpartSnapshot(
                j, sub.InstanceId, sub.Id, sub.DisplayName, sub.Template.Id,
                new double3Snap(Sanitize.Finite(pos.X), Sanitize.Finite(pos.Y), Sanitize.Finite(pos.Z))));
        }

        return list;
    }

    private sealed class CacheEntry
    {
        public IReadOnlyList<PartSnapshot> List = [];
        public int Count = -1;
        public double BuiltUt = double.NegativeInfinity;
    }
}
