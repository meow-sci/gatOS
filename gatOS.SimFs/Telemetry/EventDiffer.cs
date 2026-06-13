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

            DiffModules(events, utSeconds, vessel.Id, was, vessel);
        }

        // Whatever was not matched above is gone.
        foreach (var (id, was) in before)
            events.Add(new SimEvent(utSeconds, "vessel-removed", id, was.Name));

        return events;
    }

    /// <summary>
    ///     Per-module edge detection (KSA_GAME_INTEGRATION_PLAN §4.7): engine activation/flameout,
    ///     docking, decoupling, animation completion and battery depletion/charge. Modules are
    ///     matched by their stable index; an index that appears or disappears (structural edit) is
    ///     ignored rather than reported as a change.
    /// </summary>
    private static void DiffModules(
        List<SimEvent> events, double ut, string vesselId, VesselSnapshot was, VesselSnapshot now)
    {
        // Engines: active flip + flameout (propellant lost while active).
        var wasEngines = new Dictionary<int, EngineSnapshot>();
        foreach (var e in was.Engines)
            wasEngines.TryAdd(e.Index, e);
        foreach (var e in now.Engines)
        {
            if (!wasEngines.TryGetValue(e.Index, out var prev))
                continue;
            if (prev.Active != e.Active)
                events.Add(new SimEvent(ut, "engine-state", vesselId,
                    $"engine {e.Index} {(e.Active ? "on" : "off")}"));
            if (e.Active && prev.PropellantAvailable && !e.PropellantAvailable)
                events.Add(new SimEvent(ut, "flameout", vesselId, $"engine {e.Index}"));
        }

        // Docking: docked/undocked.
        var wasDock = new Dictionary<int, DockingSnapshot>();
        foreach (var d in was.Docking)
            wasDock.TryAdd(d.Index, d);
        foreach (var d in now.Docking)
        {
            if (!wasDock.TryGetValue(d.Index, out var prev) || prev.Docked == d.Docked)
                continue;
            events.Add(new SimEvent(ut, d.Docked ? "docked" : "undocked", vesselId,
                d.DockedToPart ?? $"port {d.Index}"));
        }

        // Decouplers: fired (rising edge only — firing is irreversible).
        var wasDec = new Dictionary<int, DecouplerSnapshot>();
        foreach (var d in was.Decouplers)
            wasDec.TryAdd(d.Index, d);
        foreach (var d in now.Decouplers)
            if (wasDec.TryGetValue(d.Index, out var prev) && !prev.Fired && d.Fired)
                events.Add(new SimEvent(ut, "decoupled", vesselId, $"decoupler {d.Index}"));

        // Animations: settled to a terminal state (Deploying/Retracting → Deployed/Retracted).
        var wasAnim = new Dictionary<int, AnimationSnapshot>();
        foreach (var a in was.Animations)
            wasAnim.TryAdd(a.Index, a);
        foreach (var a in now.Animations)
        {
            if (!wasAnim.TryGetValue(a.Index, out var prev) || prev.DeploymentState == a.DeploymentState)
                continue;
            if (a.DeploymentState is "Deployed" or "Retracted"
                && prev.DeploymentState is "Deploying" or "Retracting")
                events.Add(new SimEvent(ut, "animation-complete", vesselId,
                    $"animation {a.Index} {a.DeploymentState}"));
        }

        // Battery: crossing empty / full.
        if (was.BatteryChargeFraction is { } prevCharge && now.BatteryChargeFraction is { } charge)
        {
            if (prevCharge > 0.01 && charge <= 0.001)
                events.Add(new SimEvent(ut, "battery-depleted", vesselId, "0"));
            else if (prevCharge < 0.999 && charge >= 0.999)
                events.Add(new SimEvent(ut, "battery-charged", vesselId, "1"));
        }
    }
}
