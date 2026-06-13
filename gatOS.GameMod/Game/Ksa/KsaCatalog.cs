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

            var isDebug = command.Action.StartsWith("debug.", StringComparison.Ordinal);

            // Vessel-agnostic debug actions (no target vehicle to resolve).
            if (command.Action == "debug.warp")
                return Finish(accessor, DebugActuator.SetWarp(command.Value));

            // switch_vessel targets the vessel named by the token, not the (sender) VesselId.
            var targetId = command.Action == "debug.switch_vessel" ? command.Token ?? command.VesselId : command.VesselId;
            var vehicle = ResolveVehicle(targetId);
            if (vehicle is null)
                return new CommandResult(CommandOutcome.NotFound, $"vessel '{targetId}' is gone");

            // Authority gate (G-D1): with all_vessels=false only the controlled vehicle is commandable.
            // The cheat namespace is exempt — it is its own opt-in (G-D2).
            if (!isDebug && !allVessels && Program.ControlledVehicle?.Id != vehicle.Id)
                return new CommandResult(CommandOutcome.Denied, "control is restricted to the active vessel");

            return Finish(accessor, Dispatch(vehicle, command));
        }
        catch (Exception ex)
        {
            health.Fault(accessor, SafeUt(), ex.Message);
            return new CommandResult(CommandOutcome.Fault, ex.Message);
        }
    }

    private CommandResult Finish(string accessor, CommandResult result)
    {
        if (result.IsSuccess)
            health.Clear(accessor);
        return result;
    }

    private static CommandResult Dispatch(Vehicle vehicle, SimCommand c) => c.Action switch
    {
        // Engines / vessel-level (G1)
        "vessel.ignite" => EngineActuator.Ignite(vehicle),
        "vessel.shutdown" => EngineActuator.Shutdown(vehicle),
        "engine.active" => EngineActuator.SetActive(vehicle, c.Ordinal, c.Value > 0.5),
        "engine.min_throttle" => EngineActuator.SetMinThrottle(vehicle, c.Ordinal, c.Value),
        "vessel.lights" => LightActuator.SetMaster(vehicle, c.Value > 0.5),
        "animation.goal" => AnimationActuator.SetGoal(vehicle, c.Ordinal, c.Value),

        // Vessel control surface (G4)
        "vessel.throttle" => ThrottleActuator.Set(vehicle, c.Value),
        "vessel.stage" => StagingActuator.Stage(vehicle),
        "vessel.rcs" => RcsActuator.SetMaster(vehicle, c.Value > 0.5),
        "vessel.attitude_mode" => FlightComputerActuator.SetAttitudeMode(vehicle, c.Token ?? ""),
        "vessel.attitude_frame" => FlightComputerActuator.SetAttitudeFrame(vehicle, c.Token ?? ""),
        "vessel.attitude_target" => FlightComputerActuator.SetAttitudeTarget(vehicle, c.Values ?? []),
        "vessel.burn" => FlightComputerActuator.SetBurn(vehicle, c.Values ?? []),

        // Per-module (G4)
        "rcs.active" => RcsActuator.SetActive(vehicle, c.Ordinal, c.Value > 0.5),
        "light.on" => LightActuator.SetOn(vehicle, c.Ordinal, c.Value > 0.5),
        "light.brightness" => LightActuator.SetBrightness(vehicle, c.Ordinal, c.Value),
        "light.color" => LightActuator.SetColor(vehicle, c.Ordinal, c.Values ?? []),
        "decoupler.fire" => DecouplerActuator.Fire(vehicle, c.Ordinal),

        // Cheat namespace (G4 / G-D2)
        "debug.switch_vessel" => DebugActuator.SwitchVessel(vehicle),
        "debug.teleport" => DebugActuator.Teleport(vehicle, c.Values ?? []),
        "debug.refill_fuel" => DebugActuator.RefillFuel(vehicle),
        "debug.refill_battery" => DebugActuator.RefillBattery(vehicle),

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
