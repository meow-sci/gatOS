using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Telemetry;

/// <summary>
///     Pure snapshot diffing (OS_PLAN.md T9.2): compares the previous published snapshot
///     against the about-to-be-published sample and produces the <c>/sim/events</c> entries.
///     Event types are fixed in T8.3: <c>situation-change</c>, <c>vessel-appeared</c>,
///     <c>vessel-removed</c>, <c>active-changed</c>, <c>warp-changed</c>, <c>soi-changed</c>.
/// </summary>
/// <remarks>
///     <para>Lives in game-free <c>gatOS.SimFs</c> rather than the plan's
///     <c>gatOS.GameMod/Telemetry/</c> (as-built): GameMod has no test project by design, and
///     this is pure snapshot-domain logic — only the accessor half of the sampler is
///     game-coupled.</para>
///     <para><b>Zero-allocation steady state (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP3).</b>
///     This runs on the game thread every sample tick, and the overwhelmingly common case is "no
///     events". The event list is allocated lazily on the first event, and vessels/modules are
///     matched by <b>position</b> when the rosters align: every reader builds its module lists with
///     <c>Index == list position</c> (a sampler invariant), so an index-keyed dictionary and a
///     positional walk match identically — the dictionaries the old differ allocated per vessel per
///     tick bought nothing. The dictionary path survives only for the rare roster-changed tick
///     (vessel appeared/removed/reordered).</para>
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

        List<SimEvent>? events = null;

        if (previous.WarpFactor != warpFactor)
            Add(ref events, new SimEvent(utSeconds, "warp-changed", null,
                $"{Formats.Scalar(previous.WarpFactor)}→{Formats.Scalar(warpFactor)}"));

        if (previous.ActiveVesselId != activeVesselId)
            Add(ref events, new SimEvent(utSeconds, "active-changed", activeVesselId,
                $"{previous.ActiveVesselId ?? "none"}→{activeVesselId ?? "none"}"));

        var before = previous.Vessels;
        if (SameRoster(before, vessels))
        {
            // The common case: same vessels in the same order — pairwise diff, no allocation.
            for (var i = 0; i < vessels.Count; i++)
                DiffVessel(ref events, utSeconds, before[i], vessels[i]);
        }
        else
        {
            DiffChangedRoster(ref events, utSeconds, before, vessels);
        }

        return events ?? (IReadOnlyList<SimEvent>)[];
    }

    private static void Add(ref List<SimEvent>? events, SimEvent evt) => (events ??= []).Add(evt);

    private static bool SameRoster(IReadOnlyList<VesselSnapshot> before, IReadOnlyList<VesselSnapshot> now)
    {
        if (before.Count != now.Count)
            return false;
        for (var i = 0; i < now.Count; i++)
            if (!string.Equals(before[i].Id, now[i].Id, StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>
    ///     The roster-changed (rare) path: match vessels by id through a scratch dictionary and
    ///     report appearances/removals — exactly the pre-GP3 behavior.
    /// </summary>
    private static void DiffChangedRoster(ref List<SimEvent>? events, double utSeconds,
        IReadOnlyList<VesselSnapshot> before, IReadOnlyList<VesselSnapshot> now)
    {
        var was = new Dictionary<string, VesselSnapshot>(before.Count);
        for (var i = 0; i < before.Count; i++)
            was.TryAdd(before[i].Id, before[i]);

        for (var i = 0; i < now.Count; i++)
        {
            var vessel = now[i];
            if (!was.Remove(vessel.Id, out var prev))
            {
                Add(ref events, new SimEvent(utSeconds, "vessel-appeared", vessel.Id, vessel.Name));
                continue;
            }

            DiffVessel(ref events, utSeconds, prev, vessel);
        }

        // Whatever was not matched above is gone.
        foreach (var (id, prev) in was)
            Add(ref events, new SimEvent(utSeconds, "vessel-removed", id, prev.Name));
    }

    private static void DiffVessel(ref List<SimEvent>? events, double ut, VesselSnapshot was, VesselSnapshot now)
    {
        if (was.Situation != now.Situation)
            Add(ref events, new SimEvent(ut, "situation-change", now.Id,
                $"{was.Situation}→{now.Situation}"));
        if (was.ParentBodyName != now.ParentBodyName)
            Add(ref events, new SimEvent(ut, "soi-changed", now.Id,
                $"{was.ParentBodyName ?? "none"}→{now.ParentBodyName ?? "none"}"));

        DiffModules(ref events, ut, now.Id, was, now);
    }

    /// <summary>
    ///     Per-module edge detection (KSA_GAME_INTEGRATION_PLAN §4.7): engine activation/flameout,
    ///     docking, decoupling, animation completion and battery depletion/charge. Modules are
    ///     matched by their stable index, which every reader assigns as the list position — so a
    ///     positional walk over the shared prefix is the index match, allocation-free; an index that
    ///     appears or disappears (structural edit) is ignored rather than reported as a change, and
    ///     the <c>Index</c> guard keeps that contract even if a list were ever built out of order.
    /// </summary>
    private static void DiffModules(
        ref List<SimEvent>? events, double ut, string vesselId, VesselSnapshot was, VesselSnapshot now)
    {
        // Engines: active flip + flameout (propellant lost while active).
        var wasEngines = was.Engines;
        var nowEngines = now.Engines;
        for (int i = 0, n = Math.Min(wasEngines.Count, nowEngines.Count); i < n; i++)
        {
            var prev = wasEngines[i];
            var e = nowEngines[i];
            if (prev.Index != e.Index)
                continue;
            if (prev.Active != e.Active)
                Add(ref events, new SimEvent(ut, "engine-state", vesselId,
                    $"engine {e.Index} {(e.Active ? "on" : "off")}"));
            if (e.Active && prev.PropellantAvailable && !e.PropellantAvailable)
                Add(ref events, new SimEvent(ut, "flameout", vesselId, $"engine {e.Index}"));
        }

        // Docking: docked/undocked.
        var wasDock = was.Docking;
        var nowDock = now.Docking;
        for (int i = 0, n = Math.Min(wasDock.Count, nowDock.Count); i < n; i++)
        {
            var prev = wasDock[i];
            var d = nowDock[i];
            if (prev.Index != d.Index || prev.Docked == d.Docked)
                continue;
            Add(ref events, new SimEvent(ut, d.Docked ? "docked" : "undocked", vesselId,
                d.DockedToPart ?? $"port {d.Index}"));
        }

        // Decouplers: fired (rising edge only — firing is irreversible).
        var wasDec = was.Decouplers;
        var nowDec = now.Decouplers;
        for (int i = 0, n = Math.Min(wasDec.Count, nowDec.Count); i < n; i++)
        {
            var prev = wasDec[i];
            var d = nowDec[i];
            if (prev.Index == d.Index && !prev.Fired && d.Fired)
                Add(ref events, new SimEvent(ut, "decoupled", vesselId, $"decoupler {d.Index}"));
        }

        // Animations: settled to a terminal state (Deploying/Retracting → Deployed/Retracted).
        var wasAnim = was.Animations;
        var nowAnim = now.Animations;
        for (int i = 0, n = Math.Min(wasAnim.Count, nowAnim.Count); i < n; i++)
        {
            var prev = wasAnim[i];
            var a = nowAnim[i];
            if (prev.Index != a.Index || prev.DeploymentState == a.DeploymentState)
                continue;
            if (a.DeploymentState is "Deployed" or "Retracted"
                && prev.DeploymentState is "Deploying" or "Retracting")
                Add(ref events, new SimEvent(ut, "animation-complete", vesselId,
                    $"animation {a.Index} {a.DeploymentState}"));
        }

        // Battery: crossing empty / full.
        if (was.BatteryChargeFraction is { } prevCharge && now.BatteryChargeFraction is { } charge)
        {
            if (prevCharge > 0.01 && charge <= 0.001)
                Add(ref events, new SimEvent(ut, "battery-depleted", vesselId, "0"));
            else if (prevCharge < 0.999 && charge >= 0.999)
                Add(ref events, new SimEvent(ut, "battery-charged", vesselId, "1"));
        }
    }
}
