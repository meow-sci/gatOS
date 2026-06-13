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
}
