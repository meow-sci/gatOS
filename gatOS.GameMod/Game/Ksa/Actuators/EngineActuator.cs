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

    [KsaAnchor("EngineController.MinimumThrottle (float, settable)", SourceFile = "KSA/EngineController.cs",
        Verified = "2026-08-23", GameVersion = "2026.8.22.5348", Risk = ChurnRisk.Medium,
        Notes = "Deep-throttle floor 0..1. 5348 (rev 5317 era): EngineController.MinimumThrottle itself "
            + "is unchanged and this write still lands, but FlightComputer."
            + "ComputeActiveEnginePerformance flipped its fold over the active engines — seed 1f -> 0f "
            + "and MathF.Min -> MathF.Max — so the effective ActiveEnginePerformance.MinThrottle clamp "
            + "on a multi-engine stack is now set by the MOST restrictive engine instead of the least, "
            + "and the empty-set default flipped 1.0 -> 0.0.")]
    internal static CommandResult SetMinThrottle(Vehicle vehicle, int ordinal, double fraction)
    {
        var engines = vehicle.Parts.Modules.Get<EngineController>();
        if (ordinal < 0 || ordinal >= engines.Length)
            return new CommandResult(CommandOutcome.NotFound, $"engine {ordinal} does not exist");
        engines[ordinal].MinimumThrottle = (float)Math.Clamp(fraction, 0, 1);
        return CommandResult.Ok;
    }
}
