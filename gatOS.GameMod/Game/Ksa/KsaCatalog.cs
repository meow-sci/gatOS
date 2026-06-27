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

            // camera.focus targets ANY astronomical (vessel or celestial) named by id and only moves
            // the view — no vessel mutation, so it bypasses the vehicle-only resolution and the
            // authority gate. The id rides in Token (debug/focus) or VesselId (the per-node triggers).
            if (command.Action == "camera.focus")
            {
                var focusId = command.Token ?? command.VesselId;
                var followable = ResolveAstronomical(focusId);
                return followable is null
                    ? new CommandResult(CommandOutcome.NotFound, $"'{focusId}' is gone")
                    : Finish(accessor, CameraActuator.Focus(followable));
            }

            // control_vessel targets the vehicle named by the token (the one to take control of),
            // not the (sender) VesselId.
            var targetId = command.Action == "debug.control_vessel" ? command.Token ?? command.VesselId : command.VesselId;
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
        "vessel.engine" => EngineActuator.SetEngineOn(vehicle, c.Value > 0.5),
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
        "light.outer_angle" => LightActuator.SetOuterAngle(vehicle, c.Ordinal, c.Value),
        "light.inner_angle" => LightActuator.SetInnerAngle(vehicle, c.Ordinal, c.Value),
        "decoupler.fire" => DecouplerActuator.Fire(vehicle, c.Ordinal),
        "docking.undock" => DockingActuator.Undock(vehicle, c.Ordinal),

        // Cheat namespace (G4 / G-D2)
        "debug.control_vessel" => DebugActuator.ControlVessel(vehicle),
        "debug.teleport" => DebugActuator.Teleport(vehicle, c.Values ?? []),
        "debug.refill_fuel" => DebugActuator.RefillFuel(vehicle),
        "debug.refill_battery" => DebugActuator.RefillBattery(vehicle),
        "debug.docking_pushoff" => DockingActuator.SetPushoffImpulse(vehicle, c.Ordinal, c.Value),

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

    [KsaAnchor("Universe.CurrentSystem.Get(id) → Astronomical (vehicle or celestial)",
        SourceFile = "KSA/Universe.cs", Verified = "2026-06-16", Risk = ChurnRisk.Low,
        Notes = "Same id lookup the game's follow/control terminal actions use; returns null when absent.")]
    private static Astronomical? ResolveAstronomical(string id)
        => Universe.CurrentSystem?.Get(id);

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
