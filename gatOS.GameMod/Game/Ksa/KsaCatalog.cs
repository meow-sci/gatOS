using Brutal.Numerics;
using gatOS.GameMod.Game.Ksa.Actuators;
using gatOS.GameMod.Game.Ksa.Render;
using gatOS.GameMod.Game.Ksa.ThugLife;
using gatOS.GameMod.Game.Ksa.Welds;
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
internal sealed class KsaCatalog(KsaHealth health, bool allVessels, WeldManager welds, ThugLifeManager thugLife,
    AudioActuator? audio = null)
    : ICommandExecutor
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

            // Global render cheat: force interior (IVA) meshes visible.
            if (command.Action == "debug.always_render_iva")
                return Finish(accessor, IvaActuator.SetAlwaysRender(command.Value > 0.5));

            // Remove every weld (the per-source create/remove resolve vehicles below).
            if (command.Action == "debug.weld_clear")
                return Finish(accessor, welds.Clear());

            // Thug-life sunglasses cheat: registry-keyed (entry id in Ordinal; anchor vessel in Token for
            // add), so all of it is handled vessel-agnostically here, before the per-vessel resolution.
            if (command.Action.StartsWith("debug.thug_life", StringComparison.Ordinal))
                return Finish(accessor, ThugLife(command));

            // Userland audio playback (GATOS_CUSTOM_AUDIO_PLAN): vessel-agnostic — the target is a
            // clip/channel, never a vehicle, so it bypasses vehicle resolution and the authority gate.
            // Unsupported (EOPNOTSUPP) when [audio] enabled=false left the actuator unwired.
            if (command.Action.StartsWith("audio.", StringComparison.Ordinal))
                return Finish(accessor, audio is { } audioActuator
                    ? audioActuator.Execute(command)
                    : new CommandResult(CommandOutcome.Unsupported, "audio is disabled in gatos.toml"));

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
            // The cheat namespace is exempt — it is its own opt-in (G-D2) — and so is the
            // AnyVesselActions set: deliberate per-vessel operations that work on any addressed
            // vessel (the controls moved out of the /sim/debug namespace).
            var anyVessel = isDebug || AnyVesselActions.Contains(command.Action);
            if (!anyVessel && !allVessels && Program.ControlledVehicle?.Id != vehicle.Id)
                return new CommandResult(CommandOutcome.Denied, "control is restricted to the active vessel");

            return Finish(accessor, Dispatch(vehicle, command));
        }
        catch (Exception ex)
        {
            health.Fault(accessor, SafeUt(), ex.Message);
            return new CommandResult(CommandOutcome.Fault, ex.Message);
        }
    }

    /// <summary>
    ///     The first-class per-vessel controls exempt from the active-vessel authority gate: each is a
    ///     deliberate by-id operation on an arbitrary vessel, placed under the regular vessel area
    ///     rather than <c>/sim/debug</c>. This is a GameMod authority policy — the SimFs layer never
    ///     sees it.
    /// </summary>
    private static readonly HashSet<string> AnyVesselActions =
        new(StringComparer.Ordinal) { "vessel.scale", "vessel.always_render" };

    private CommandResult Finish(string accessor, CommandResult result)
    {
        if (result.IsSuccess)
            health.Clear(accessor);
        return result;
    }

    private CommandResult Dispatch(Vehicle vehicle, SimCommand c) => c.Action switch
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
        // Manual RCS translation (body-axis signs; latches until rewritten).
        "vessel.translate" => TranslateActuator.SetTranslation(vehicle, c.Values ?? []),
        "vessel.attitude_mode" => FlightComputerActuator.SetAttitudeMode(vehicle, c.Token ?? ""),
        "vessel.attitude_frame" => FlightComputerActuator.SetAttitudeFrame(vehicle, c.Token ?? ""),
        "vessel.attitude_target" => FlightComputerActuator.SetAttitudeTarget(vehicle, c.Values ?? []),
        "vessel.burn" => FlightComputerActuator.SetBurn(vehicle, c.Values ?? []),

        // First-class per-vessel nodes (any-vessel — see AnyVesselActions above).
        "vessel.scale" => ScaleActuator.Set(vehicle, c.Value),
        "vessel.always_render" => VesselForceRender.Set(vehicle, c.Value > 0.5),

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
        // One-shot impulsive kick (frame keyword rides in Token, unit keyword in Aux).
        "debug.impulse" => DebugActuator.Impulse(vehicle, c.Values ?? [], c.Token, c.Aux),
        "debug.refill_fuel" => DebugActuator.RefillFuel(vehicle),
        "debug.refill_battery" => DebugActuator.RefillBattery(vehicle),
        "debug.docking_pushoff" => DockingActuator.SetPushoffImpulse(vehicle, c.Ordinal, c.Value),

        // Welds cheat (vehicle = the source; the target rides in Token; part_iid + offsets in Values).
        "debug.weld_create" => WeldCreate(vehicle, c),
        "debug.weld_here" => WeldHere(vehicle, c),
        "debug.weld_remove" => welds.Remove(vehicle.Id),
        "debug.weld_enable" => welds.SetEnabled(vehicle.Id, c.Value > 0.5),

        _ => new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'"),
    };

    /// <summary>
    ///     Routes the thug-life cheat actions to the <see cref="ThugLifeManager"/>. All registry-keyed
    ///     (entry id in <see cref="SimCommand.Ordinal"/>); <c>add</c> resolves the anchor vehicle from the
    ///     <see cref="SimCommand.Token"/> and the part by its instance_id inside the manager.
    /// </summary>
    private CommandResult ThugLife(SimCommand c)
    {
        switch (c.Action)
        {
            case "debug.thug_life_clear":
                return thugLife.Clear();
            case "debug.thug_life_remove":
                return thugLife.Remove(c.Ordinal);
            case "debug.thug_life_visible":
                return thugLife.SetVisible(c.Ordinal, c.Value > 0.5);
            case "debug.thug_life_add":
            {
                if (ResolveVehicle(c.Token ?? "") is not { } vehicle)
                    return new CommandResult(CommandOutcome.NotFound, $"vessel '{c.Token}' is gone");
                var v = c.Values ?? [];
                // Accept the short form [part_iid] (transform defaulted, like the 9p `add` 2-token form)
                // or the full [iid, x, y, z, pitch, yaw, roll, width, height].
                if (v.Count is not (1 or 9))
                    return new CommandResult(CommandOutcome.Invalid,
                        "thug_life add expects 'iid' or 'iid x y z pitch yaw roll width height'");
                var pos = v.Count == 9 ? new double3(v[1], v[2], v[3]) : default;
                var rot = v.Count == 9 ? new double3(v[4], v[5], v[6]) : default;
                var width = v.Count == 9 ? v[7] : 0.975;
                var height = v.Count == 9 ? v[8] : 0.1875;
                return thugLife.Add(vehicle, (uint)v[0], pos, rot, width, height);
            }
            case "debug.thug_life_position":
            case "debug.thug_life_rotation":
            {
                var v = c.Values ?? [];
                if (v.Count != 3)
                    return new CommandResult(CommandOutcome.Invalid, $"'{c.Action}' expects 'x y z'");
                var vec = new double3(v[0], v[1], v[2]);
                return c.Action == "debug.thug_life_position"
                    ? thugLife.SetPosition(c.Ordinal, vec)
                    : thugLife.SetRotation(c.Ordinal, vec);
            }
            case "debug.thug_life_size":
            {
                var v = c.Values ?? [];
                if (v.Count != 2)
                    return new CommandResult(CommandOutcome.Invalid, "thug_life size expects 'width height'");
                return thugLife.SetSize(c.Ordinal, v[0], v[1]);
            }
            default:
                return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        }
    }

    private CommandResult WeldCreate(Vehicle source, SimCommand c)
    {
        if (ResolveVehicle(c.Token ?? "") is not { } target)
            return new CommandResult(CommandOutcome.NotFound, $"target '{c.Token}' is gone");
        var v = c.Values ?? [];
        if (v.Count != 8) // part_iid x y z pitch yaw roll lock
            return new CommandResult(CommandOutcome.Invalid, "weld expects 'part x y z pitch yaw roll lock'");
        return welds.Create(source, target, (uint)v[0],
            new double3(v[1], v[2], v[3]), new double3(v[4], v[5], v[6]), v[7] > 0.5);
    }

    private CommandResult WeldHere(Vehicle source, SimCommand c)
    {
        if (ResolveVehicle(c.Token ?? "") is not { } target)
            return new CommandResult(CommandOutcome.NotFound, $"target '{c.Token}' is gone");
        var v = c.Values ?? [];
        if (v.Count != 2) // part_iid lock
            return new CommandResult(CommandOutcome.Invalid, "weld_here expects 'part lock'");
        return welds.CreateAtCurrentPose(source, target, (uint)v[0], v[1] > 0.5);
    }

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
