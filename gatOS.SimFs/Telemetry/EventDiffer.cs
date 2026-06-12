using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Telemetry;

/// <summary>
///     Pure snapshot diffing (OS_PLAN.md T9.2): compares the previous published snapshot
///     against the about-to-be-published sample and produces the <c>/sim/events</c> entries.
///     Event types are fixed in T8.3: <c>situation-change</c>, <c>vessel-appeared</c>,
///     <c>vessel-removed</c>, <c>active-changed</c>, <c>warp-changed</c>, <c>soi-changed</c>.
/// </summary>
/// <remarks>
///     Lives in game-free <c>gatOS.SimFs</c> rather than the plan's
///     <c>gatOS.GameMod/Telemetry/</c> (as-built): GameMod has no test project by design, and
///     this is pure snapshot-domain logic — only the accessor half of the sampler is
///     game-coupled.
/// </remarks>
public static class EventDiffer
{
    /// <summary>
    ///     Diffs <paramref name="previous"/> against the current sample fields. A null
    ///     <paramref name="previous"/> (the first sample of a session) is the baseline and
    ///     produces no events — players care about changes, not the starting roster.
    /// </summary>
    public static IReadOnlyList<SimEvent> Diff(
        SimSnapshot? previous,
        double utSeconds,
        double warpFactor,
        string? activeVesselId,
        IReadOnlyList<VesselSnapshot> vessels)
    {
        if (previous is null)
            return [];

        var events = new List<SimEvent>();

        if (previous.WarpFactor != warpFactor)
            events.Add(new SimEvent(utSeconds, "warp-changed", null,
                $"{Formats.Scalar(previous.WarpFactor)}→{Formats.Scalar(warpFactor)}"));

        if (previous.ActiveVesselId != activeVesselId)
            events.Add(new SimEvent(utSeconds, "active-changed", activeVesselId,
                $"{previous.ActiveVesselId ?? "none"}→{activeVesselId ?? "none"}"));

        var before = new Dictionary<string, VesselSnapshot>();
        foreach (var vessel in previous.Vessels)
            before.TryAdd(vessel.Id, vessel);

        foreach (var vessel in vessels)
        {
            if (!before.Remove(vessel.Id, out var was))
            {
                events.Add(new SimEvent(utSeconds, "vessel-appeared", vessel.Id, vessel.Name));
                continue;
            }

            if (was.Situation != vessel.Situation)
                events.Add(new SimEvent(utSeconds, "situation-change", vessel.Id,
                    $"{was.Situation}→{vessel.Situation}"));
            if (was.ParentBodyName != vessel.ParentBodyName)
                events.Add(new SimEvent(utSeconds, "soi-changed", vessel.Id,
                    $"{was.ParentBodyName ?? "none"}→{vessel.ParentBodyName ?? "none"}"));
        }

        // Whatever was not matched above is gone.
        foreach (var (id, was) in before)
            events.Add(new SimEvent(utSeconds, "vessel-removed", id, was.Name));

        return events;
    }
}
