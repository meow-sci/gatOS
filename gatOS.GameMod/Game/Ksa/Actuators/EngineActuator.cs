using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Engine controls (KSA_GAME_INTEGRATION_PLAN §5.1/§5.2). Vessel-level ignite/shutdown go
///     through <see cref="Vehicle.SetEnum"/> (the proven unscience <c>unladen-swallow</c> path);
///     per-engine activation goes through <see cref="EngineController.SetIsActive"/> (which queues
///     an InputEvents activation). Game-thread only; may throw — <c>KsaCatalog</c> wraps every call.
/// </summary>
internal static class EngineActuator
{
    [KsaAnchor("Vehicle.SetEnum(VehicleEngine.MainIgnite)", SourceFile = "KSA/Vehicle.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium,
        Notes = "Sets _manualControlInputs.EngineOn = true (ignites the active stage's engines).")]
    internal static CommandResult Ignite(Vehicle vehicle)
    {
        vehicle.SetEnum(VehicleEngine.MainIgnite);
        return CommandResult.Ok;
    }

    [KsaAnchor("Vehicle.SetEnum(VehicleEngine.MainShutdown)", SourceFile = "KSA/Vehicle.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Medium)]
    internal static CommandResult Shutdown(Vehicle vehicle)
    {
        vehicle.SetEnum(VehicleEngine.MainShutdown);
        return CommandResult.Ok;
    }

    /// <summary>The <c>ctl/engine</c> toggle: ignite when <paramref name="on"/>, else shut down.</summary>
    internal static CommandResult SetEngineOn(Vehicle vehicle, bool on)
        => on ? Ignite(vehicle) : Shutdown(vehicle);

    [KsaAnchor("EngineController.SetIsActive(Vehicle, bool)", SourceFile = "KSA/EngineController.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "Ordinal is the vessel-level engine index from VesselReader.SampleEngines.")]
    internal static CommandResult SetActive(Vehicle vehicle, int ordinal, bool active)
    {
        var engines = vehicle.Parts.Modules.Get<EngineController>();
        if (ordinal < 0 || ordinal >= engines.Length)
            return new CommandResult(CommandOutcome.NotFound, $"engine {ordinal} does not exist");
        engines[ordinal].SetIsActive(vehicle, active);
        return CommandResult.Ok;
    }

    [KsaAnchor("EngineController.MinimumThrottle (float, settable); PartTree.MarkDerivedDirty(DerivedData.RocketControls)",
        SourceFile = "KSA/EngineController.cs / KSA/PartTree.cs:480,993-1017 / KSA/Vehicle.cs:1264,2411",
        Verified = "2026-09-25", GameVersion = "2026.9.22.5482", Risk = ChurnRisk.Medium,
        Notes = "Deep-throttle floor 0..1. 5348 (rev 5317 era): EngineController.MinimumThrottle itself "
            + "is unchanged and this write still lands, but FlightComputer."
            + "ComputeActiveEnginePerformance flipped its fold over the active engines — seed 1f -> 0f "
            + "and MathF.Min -> MathF.Max — so the effective ActiveEnginePerformance.MinThrottle clamp "
            + "on a multi-engine stack is now set by the MOST restrictive engine instead of the least, "
            + "and the empty-set default flipped 1.0 -> 0.0. "
            + "5482 (rev 5464, lazy PartTree derived data): the manual-throttle clamp in Vehicle.PrepareWorker "
            + "reads Vehicle.GetMinThrottle() = PartTree.EngineThrottleMin, a cached MIN over every engine's "
            + "MinimumThrottle recomputed only by RecomputeRocketControls. The write alone left that cache stale "
            + "(pre-existing: 5438 also only recomputed it on structural change), so the write now marks "
            + "DerivedData.RocketControls dirty; KSA flushes it in PrepareFrame before the next solve, and the "
            + "EngineThrottleMin getter also ensures it lazily.")]
    internal static CommandResult SetMinThrottle(Vehicle vehicle, int ordinal, double fraction)
    {
        var engines = vehicle.Parts.Modules.Get<EngineController>();
        if (ordinal < 0 || ordinal >= engines.Length)
            return new CommandResult(CommandOutcome.NotFound, $"engine {ordinal} does not exist");
        engines[ordinal].MinimumThrottle = (float)Math.Clamp(fraction, 0, 1);
        // Refresh the tree-wide floor (PartTree.EngineThrottleMin) the manual-throttle clamp reads.
        vehicle.Parts.MarkDerivedDirty(DerivedData.RocketControls);
        return CommandResult.Ok;
    }
}
