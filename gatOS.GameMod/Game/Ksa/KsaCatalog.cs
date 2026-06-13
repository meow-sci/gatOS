using gatOS.GameMod.Game.Ksa.Actuators;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa;

/// <summary>
///     The command executor (KSA_GAME_INTEGRATION_PLAN §3.2): routes a game-free
///     <see cref="SimCommand"/> to the matching actuator, resolving the target vehicle against
///     live game state. This is the only place actuator code is reached, so it owns the
///     cross-cutting concerns: authority gating (G-D1), and per-action health latches that turn a
///     thrown KSA call into a degraded sensor (EOPNOTSUPP) instead of a crash. Always invoked on
///     the game thread by <see cref="CommandQueue.Drain"/>; never throws (faults are returned).
/// </summary>
internal sealed class KsaCatalog(KsaHealth health, bool allVessels) : ICommandExecutor
{
    /// <inheritdoc />
    public CommandResult Execute(SimCommand command)
    {
        var accessor = $"actuator.{command.Action}";
        try
        {
            if (health.IsDegraded(accessor))
                return new CommandResult(CommandOutcome.Unsupported, $"'{command.Action}' is latched degraded");

            var vehicle = ResolveVehicle(command.VesselId);
            if (vehicle is null)
                return new CommandResult(CommandOutcome.NotFound, $"vessel '{command.VesselId}' is gone");

            // Authority gate (G-D1): with all_vessels=false only the controlled vehicle is commandable.
            if (!allVessels && Program.ControlledVehicle?.Id != vehicle.Id)
                return new CommandResult(CommandOutcome.Denied, "control is restricted to the active vessel");

            var result = Dispatch(vehicle, command);
            if (result.IsSuccess)
                health.Clear(accessor);
            return result;
        }
        catch (Exception ex)
        {
            health.Fault(accessor, SafeUt(), ex.Message);
            return new CommandResult(CommandOutcome.Fault, ex.Message);
        }
    }

    private static CommandResult Dispatch(Vehicle vehicle, SimCommand c) => c.Action switch
    {
        "vessel.ignite" => EngineActuator.Ignite(vehicle),
        "vessel.shutdown" => EngineActuator.Shutdown(vehicle),
        "engine.active" => EngineActuator.SetActive(vehicle, c.Ordinal, c.Value > 0.5),
        "vessel.lights" => LightActuator.SetMaster(vehicle, c.Value > 0.5),
        "animation.goal" => AnimationActuator.SetGoal(vehicle, c.Ordinal, c.Value),
        _ => new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'"),
    };

    [KsaAnchor("Universe.CurrentSystem.All.UnsafeAsList(); Vehicle.Id", SourceFile = "KSA/Universe.cs",
        Verified = "2026-06-12", Risk = ChurnRisk.Low,
        Notes = "Same enumeration the telemetry sampler uses to find vessels by id.")]
    private static Vehicle? ResolveVehicle(string id)
    {
        if (Universe.CurrentSystem is not { } system)
            return null;
        foreach (var astronomical in system.All.UnsafeAsList())
            if (astronomical is Vehicle vehicle && vehicle.Id == id)
                return vehicle;
        return null;
    }

    private static double SafeUt()
    {
        try
        {
            return Universe.GetElapsedSimTime().Seconds();
        }
        catch
        {
            return 0;
        }
    }
}
