using Brutal.Numerics;
using gatOS.GameMod.Game.Ksa.Actuators;
using gatOS.GameMod.Game.Ksa.Camera;
using gatOS.GameMod.Game.Ksa.Fx;
using gatOS.GameMod.Game.Ksa.Iva;
using gatOS.GameMod.Game.Ksa.Paint;
using gatOS.GameMod.Game.Ksa.Render;
using gatOS.GameMod.Game.Ksa.ThugLife;
using gatOS.GameMod.Game.Ksa.Welds;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Fx;
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
    IvaPhysicsManager iva, AudioActuator? audio = null, ScheduleStore? schedules = null,
    CameraDirector? camera = null, FaceFxManager? faceFx = null, PaintManager? paint = null)
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
            if (command.Action == SimActions.DebugWarp)
                return Finish(accessor, DebugActuator.SetWarp(command.Value));

            // Global render cheat: force interior (IVA) meshes visible.
            if (command.Action == SimActions.DebugAlwaysRenderIva)
                return Finish(accessor, IvaActuator.SetAlwaysRender(command.Value > 0.5));

            // Remove every weld (the per-source create/remove resolve vehicles below).
            if (command.Action == SimActions.DebugWeldClear)
                return Finish(accessor, welds.Clear());

            // Thug-life sunglasses cheat: registry-keyed (entry id in Ordinal; anchor vessel in Token for
            // add), so all of it is handled vessel-agnostically here, before the per-vessel resolution.
            if (command.Action.StartsWith("debug.thug_life", StringComparison.Ordinal))
                return Finish(accessor, ThugLife(command));

            // Face-anchored particle effects: the target vessel rides Token (resolved inside), so the
            // family routes vessel-agnostically here like thug_life.
            if (command.Action.StartsWith("debug.fx_", StringComparison.Ordinal))
                return Finish(accessor, FaceFx(command));

            // IVA free-floating cabin objects: registry-keyed like thug_life (object id in Ordinal,
            // vessel id in Token for adopt), so the whole grammar is handled vessel-agnostically here.
            // debug.iva_physics is the master switch that starts and ends the feature.
            if (command.Action.StartsWith("debug.iva_", StringComparison.Ordinal))
                return Finish(accessor, Iva(command));

            // FX editors (plans/FX_EDITORS_PLAN.md): the addressed entity is a render template, the one
            // trail renderer, or a celestial body — never a vehicle — so all four families route here,
            // vessel-agnostically, before the per-vessel resolution (the thug_life precedent).
            if (command.Action.StartsWith("debug.engineplume", StringComparison.Ordinal))
                return Finish(accessor, EnginePlume(command));
            if (command.Action.StartsWith("debug.plumetrail", StringComparison.Ordinal))
                return Finish(accessor, PlumeTrail(command));
            if (command.Action.StartsWith("debug.clouds", StringComparison.Ordinal))
                return Finish(accessor, Clouds(command));
            if (command.Action.StartsWith("debug.terrain", StringComparison.Ordinal))
                return Finish(accessor, Terrain(command));

            // Userland audio playback (GATOS_CUSTOM_AUDIO_PLAN): vessel-agnostic — the target is a
            // clip/channel, never a vehicle, so it bypasses vehicle resolution and the authority gate.
            // Unsupported (EOPNOTSUPP) when [audio] enabled=false left the actuator unwired.
            if (command.Action.StartsWith("audio.", StringComparison.Ordinal))
                return Finish(accessor, audio is { } audioActuator
                    ? audioActuator.Execute(command)
                    : new CommandResult(CommandOutcome.Unsupported, "audio is disabled in gatos.toml"));

            // Host-side timed schedules (plans/CAMERA_CONTROLS_PLAN.md §3): the target is a live
            // player in the /sim/ctl/schedules registry, never a vehicle, so — like audio — this
            // bypasses vehicle resolution and the authority gate. Nothing about pausing or scrubbing a
            // host-side player touches KSA, so the whole family routes straight to the game-free store
            // rather than being re-implemented here (that would be a second definition of the grammar).
            // Unsupported (EOPNOTSUPP) when [schedule] schedule_enabled=false left the store unwired.
            if (command.Action.StartsWith("schedule.", StringComparison.Ordinal))
                return Finish(accessor, schedules is { } scheduleStore
                    ? scheduleStore.Execute(command)
                    : new CommandResult(CommandOutcome.Unsupported, "scheduling is disabled in gatos.toml"));

            // The whole camera family (plans/CAMERA_CONTROLS_PLAN.md §4): the addressed entity is the
            // main viewport's camera or an astronomical named by id — never a vehicle to mutate — so,
            // exactly like audio.* and schedule.*, it routes here before the vehicle resolution and
            // before the authority gate.
            if (command.Action.StartsWith("camera.", StringComparison.Ordinal))
                return Finish(accessor, Camera(command));

            // Paint's own manager resolves vessel/part/EVA identities and deliberately permits
            // visual edits by-id, like scale/always_render. Its runtime masters are the opt-in gate.
            if (command.Action.StartsWith("paint.", StringComparison.Ordinal))
                return Finish(accessor, paint is { } paintManager
                    ? paintManager.Execute(command)
                    : new CommandResult(CommandOutcome.Unsupported, "paint is unavailable"));

            // control_vessel targets the vehicle named by the token (the one to take control of),
            // not the (sender) VesselId.
            var targetId = command.Action == SimActions.DebugControlVessel ? command.Token ?? command.VesselId : command.VesselId;
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
        new(StringComparer.Ordinal) { SimActions.VesselScale, SimActions.VesselAlwaysRender };

    private CommandResult Finish(string accessor, CommandResult result)
    {
        if (result.IsSuccess)
            health.Clear(accessor);
        return result;
    }

    private CommandResult Dispatch(Vehicle vehicle, SimCommand c) => c.Action switch
    {
        // Engines / vessel-level (G1)
        SimActions.VesselIgnite => EngineActuator.Ignite(vehicle),
        SimActions.VesselShutdown => EngineActuator.Shutdown(vehicle),
        SimActions.VesselEngine => EngineActuator.SetEngineOn(vehicle, c.Value > 0.5),
        SimActions.EngineActive => EngineActuator.SetActive(vehicle, c.Ordinal, c.Value > 0.5),
        SimActions.EngineMinThrottle => EngineActuator.SetMinThrottle(vehicle, c.Ordinal, c.Value),
        SimActions.VesselLights => LightActuator.SetMaster(vehicle, c.Value > 0.5),
        SimActions.AnimationGoal => AnimationActuator.SetGoal(vehicle, c.Ordinal, c.Value),

        // Vessel control surface (G4)
        SimActions.VesselThrottle => ThrottleActuator.Set(vehicle, c.Value),
        SimActions.VesselStage => StagingActuator.Stage(vehicle),
        SimActions.VesselRcs => RcsActuator.SetMaster(vehicle, c.Value > 0.5),
        // Manual RCS translation (body-axis signs; latches until rewritten).
        SimActions.VesselTranslate => TranslateActuator.SetTranslation(vehicle, c.Values ?? []),
        // Manual RCS rotation (body-axis torque signs; latches until rewritten; full authority
        // only in manual attitude mode — auto strips the rotation bits).
        SimActions.VesselRotate => RotateActuator.SetRotation(vehicle, c.Values ?? []),
        SimActions.VesselAttitudeMode => FlightComputerActuator.SetAttitudeMode(vehicle, c.Token ?? ""),
        // The FC's RCS master switch (in-game R). Disabled zeroes the manual thruster flags, so
        // ctl/translate + ctl/rotate go dead until it is re-enabled.
        SimActions.VesselRcsMode => FlightComputerActuator.SetRcsMode(vehicle, c.Token ?? ""),
        SimActions.VesselAttitudeFrame => FlightComputerActuator.SetAttitudeFrame(vehicle, c.Token ?? ""),
        SimActions.VesselAttitudeTarget => FlightComputerActuator.SetAttitudeTarget(vehicle, c.Values ?? []),
        SimActions.VesselBurn => FlightComputerActuator.SetBurn(vehicle, c.Values ?? []),

        // First-class per-vessel nodes (any-vessel — see AnyVesselActions above).
        SimActions.VesselScale => ScaleActuator.Set(vehicle, c.Value),
        SimActions.VesselAlwaysRender => VesselForceRender.Set(vehicle, c.Value > 0.5),

        // Per-module (G4)
        SimActions.RcsActive => RcsActuator.SetActive(vehicle, c.Ordinal, c.Value > 0.5),
        SimActions.LightOn => LightActuator.SetOn(vehicle, c.Ordinal, c.Value > 0.5),
        SimActions.LightBrightness => LightActuator.SetBrightness(vehicle, c.Ordinal, c.Value),
        SimActions.LightColor => LightActuator.SetColor(vehicle, c.Ordinal, c.Values ?? []),
        SimActions.LightOuterAngle => LightActuator.SetOuterAngle(vehicle, c.Ordinal, c.Value),
        SimActions.LightInnerAngle => LightActuator.SetInnerAngle(vehicle, c.Ordinal, c.Value),
        SimActions.DecouplerFire => DecouplerActuator.Fire(vehicle, c.Ordinal),
        SimActions.DockingUndock => DockingActuator.Undock(vehicle, c.Ordinal),

        // Cheat namespace (G4 / G-D2)
        SimActions.DebugControlVessel => DebugActuator.ControlVessel(vehicle),
        SimActions.DebugTeleport => DebugActuator.Teleport(vehicle, c.Values ?? []),
        // One-shot impulsive kick (frame keyword rides in Token, unit keyword in Aux).
        SimActions.DebugImpulse => DebugActuator.Impulse(vehicle, c.Values ?? [], c.Token, c.Aux),
        SimActions.DebugRefillFuel => DebugActuator.RefillFuel(vehicle),
        SimActions.DebugRefillBattery => DebugActuator.RefillBattery(vehicle),
        SimActions.DebugDockingPushoff => DockingActuator.SetPushoffImpulse(vehicle, c.Ordinal, c.Value),

        // Welds cheat (vehicle = the source; the target rides in Token; part_iid + offsets in Values).
        SimActions.DebugWeldCreate => WeldCreate(vehicle, c),
        SimActions.DebugWeldHere => WeldHere(vehicle, c),
        SimActions.DebugWeldRemove => welds.Remove(vehicle.Id),
        SimActions.DebugWeldEnable => welds.SetEnabled(vehicle.Id, c.Value > 0.5),

        _ => new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'"),
    };

    /// <summary>
    ///     Routes the whole <c>camera.*</c> family. <c>camera.focus</c> is handled here directly because
    ///     it predates the director and must keep working with the camera surface disabled — it names any
    ///     <see cref="Astronomical"/> (vehicle or celestial) by id and only moves the view. Everything
    ///     else needs the director, so it answers EOPNOTSUPP when <c>[camera] camera_enabled</c> left it
    ///     unwired.
    /// </summary>
    /// <remarks>
    ///     One sub-dispatcher rather than two branches in <see cref="Execute"/>: focus and the C1/C2
    ///     actions both act on <c>Program.MainViewport</c>'s cameras, and two independently-ordered
    ///     entry points into the same object is how they would eventually come to disagree about, say,
    ///     whether a follow change happens before or after an ownership take.
    /// </remarks>
    private CommandResult Camera(SimCommand c)
    {
        // The id rides in Token (debug/focus) or VesselId (the per-vessel and per-body triggers).
        if (c.Action == SimActions.CameraFocus)
        {
            var focusId = c.Token ?? c.VesselId;
            return ResolveAstronomical(focusId) is { } followable
                ? CameraActuator.Focus(followable)
                : new CommandResult(CommandOutcome.NotFound, $"'{focusId}' is gone");
        }

        return camera is { } director
            ? CameraActuator.Execute(c, director)
            : new CommandResult(CommandOutcome.Unsupported, "camera is disabled in gatos.toml");
    }

    /// <summary>
    ///     Routes the thug-life cheat actions to the <see cref="ThugLifeManager"/>. All registry-keyed
    ///     (entry id in <see cref="SimCommand.Ordinal"/>); <c>add</c> resolves the anchor vehicle from the
    ///     <see cref="SimCommand.Token"/> and the part by its instance_id inside the manager.
    /// </summary>
    private CommandResult ThugLife(SimCommand c)
    {
        switch (c.Action)
        {
            case SimActions.DebugThugLifeClear:
                return thugLife.Clear();
            case SimActions.DebugThugLifeRemove:
                return thugLife.Remove(c.Ordinal);
            case SimActions.DebugThugLifeVisible:
                return thugLife.SetVisible(c.Ordinal, c.Value > 0.5);
            case SimActions.DebugThugLifeCameras:
                return thugLife.SetCameras(c.Ordinal, (int)c.Value);
            case SimActions.DebugThugLifeAdd:
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
            case SimActions.DebugThugLifePosition:
            case SimActions.DebugThugLifeRotation:
            {
                var v = c.Values ?? [];
                if (v.Count != 3)
                    return new CommandResult(CommandOutcome.Invalid, $"'{c.Action}' expects 'x y z'");
                var vec = new double3(v[0], v[1], v[2]);
                return c.Action == SimActions.DebugThugLifePosition
                    ? thugLife.SetPosition(c.Ordinal, vec)
                    : thugLife.SetRotation(c.Ordinal, vec);
            }
            case SimActions.DebugThugLifeSize:
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

    /// <summary>
    ///     Routes the face-FX actions to the <see cref="FaceFxManager"/>. The target vessel arrives in
    ///     <see cref="SimCommand.Token"/> and the profile in <see cref="SimCommand.Aux"/>; the game-free
    ///     parser already validated both shapes, so failures here are live-state ones (despawned vessel,
    ///     exhausted pool).
    /// </summary>
    private CommandResult FaceFx(SimCommand c)
    {
        if (faceFx is not { } fx)
            return new CommandResult(CommandOutcome.Unsupported, "face FX is not wired");
        switch (c.Action)
        {
            case FaceFxRules.ClearAction:
                return fx.Clear();
            case FaceFxRules.SpawnAction:
            {
                if (ResolveVehicle(c.Token ?? "") is not { } vehicle)
                    return new CommandResult(CommandOutcome.NotFound, $"vessel '{c.Token}' is gone");
                var v = c.Values ?? [];
                if (v.Count != FaceFxRules.SpawnSlots)
                    return new CommandResult(CommandOutcome.Invalid,
                        "fx spawn expects [scale, hasOffset, x, y, z]");
                double3? offset = v[FaceFxRules.SpawnHasOffset] > 0.5
                    ? new double3(v[FaceFxRules.SpawnOffX], v[FaceFxRules.SpawnOffY],
                        v[FaceFxRules.SpawnOffZ])
                    : null;
                return fx.Spawn(vehicle, c.Aux ?? "", v[FaceFxRules.SpawnScale], offset);
            }
            default:
                return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        }
    }

    /// <summary>
    ///     Routes the IVA cabin-physics actions to the <see cref="IvaPhysicsManager"/>. The master
    ///     switch and <c>clear</c> take no target; <c>adopt</c>/<c>adopt_all</c> resolve the vessel from
    ///     <see cref="SimCommand.Token"/>; the per-object actions key on
    ///     <see cref="SimCommand.Ordinal"/>.
    /// </summary>
    private CommandResult Iva(SimCommand c)
    {
        switch (c.Action)
        {
            case SimActions.DebugIvaPhysics:
                return iva.SetEnabled(c.Value > 0.5);
            case SimActions.DebugIvaRunOutsideIva:
                return iva.SetRunOutsideIva(c.Value > 0.5);
            case SimActions.DebugIvaClear:
                return iva.Clear();
            case SimActions.DebugIvaRelease:
                return iva.Release(c.Ordinal);
            case SimActions.DebugIvaNudge:
            {
                var v = c.Values ?? [];
                if (v.Count != 3)
                    return new CommandResult(CommandOutcome.Invalid, "iva nudge expects 'vx vy vz'");
                return iva.Nudge(c.Ordinal, new double3(v[0], v[1], v[2]));
            }

            case SimActions.DebugIvaAdopt:
            {
                if (ResolveVehicle(c.Token ?? "") is not { } vehicle)
                    return new CommandResult(CommandOutcome.NotFound, $"vessel '{c.Token}' is gone");
                var v = c.Values ?? [];
                // [subpart_iid] (rest velocity) or [subpart_iid, vx, vy, vz].
                if (v.Count is not (1 or 4))
                    return new CommandResult(CommandOutcome.Invalid,
                        "iva adopt expects '<vessel> <subpart_iid>' or '<vessel> <subpart_iid> vx vy vz'");
                var velocity = v.Count == 4 ? new double3(v[1], v[2], v[3]) : double3.Zero;
                return iva.Adopt(vehicle, (uint)v[0], velocity);
            }

            case SimActions.DebugIvaAdoptAll:
            {
                if (ResolveVehicle(c.Token ?? "") is not { } vehicle)
                    return new CommandResult(CommandOutcome.NotFound, $"vessel '{c.Token}' is gone");
                return iva.AdoptAll(vehicle, (int)c.Value, c.Aux);
            }

            default:
                return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        }
    }

    /// <summary>
    ///     <c>/sim/debug/engineplume</c>: the entity is a shared volumetric-exhaust template named by
    ///     <see cref="SimCommand.Token"/>, the concrete field path rides in <see cref="SimCommand.Aux"/>.
    /// </summary>
    private CommandResult EnginePlume(SimCommand c)
    {
        if (c.Action == FxCatalog.EnginePlumeReset)
            return PlumeActuator.Reset(c.Token ?? "");
        if (c.Action != FxCatalog.EnginePlumeSet)
            return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        if (FxField(c, out var match, out var field, out var values) is { } rejected)
            return rejected;
        return PlumeActuator.Set(c.Token ?? "", match.Spec, field, values);
    }

    /// <summary><c>/sim/debug/plumetrail</c>: one global renderer, so no entity token.</summary>
    private CommandResult PlumeTrail(SimCommand c)
    {
        if (c.Action == FxCatalog.PlumeTrailReset)
            return TrailActuator.Reset(health);
        if (c.Action == FxCatalog.PlumeTrailClear)
            return TrailActuator.Clear(health);
        if (c.Action != FxCatalog.PlumeTrailSet)
            return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        if (FxField(c, out var match, out var field, out var values) is { } rejected)
            return rejected;
        return TrailActuator.Set(health, match.Spec, field, values);
    }

    /// <summary><c>/sim/debug/clouds</c>: the entity is the body named by the token.</summary>
    private CommandResult Clouds(SimCommand c)
    {
        if (c.Action == FxCatalog.CloudsReset)
            return CloudActuator.Reset(health, c.Token ?? "");
        if (c.Action != FxCatalog.CloudsSet)
            return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        if (FxField(c, out var match, out var field, out var values) is { } rejected)
            return rejected;
        return CloudActuator.Set(health, c.Token ?? "", match, field, values);
    }

    /// <summary>
    ///     <c>/sim/debug/terrain</c>: the entity is the body named by the token, or the family-global
    ///     pseudo-entity (empty token) that carries <c>wireframe</c>.
    /// </summary>
    private CommandResult Terrain(SimCommand c)
    {
        if (c.Action == FxCatalog.TerrainReset)
            return TerrainActuator.Reset(health, c.Token ?? "");
        if (c.Action != FxCatalog.TerrainSet)
            return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        if (FxField(c, out var match, out var field, out var values) is { } rejected)
            return rejected;
        return TerrainActuator.Set(health, c.Token ?? "", match, field, values);
    }

    /// <summary>
    ///     Re-validates an FX <c>_set</c> payload game-side: the action picks the family table, the
    ///     concrete field path (<see cref="SimCommand.Aux"/>) must match a catalog row, and the payload
    ///     must satisfy that row's arity/range/finiteness. The 9p control files already enforce all of
    ///     this at parse time, but <c>POST /v1/command</c> and <c>gatos/command</c> bypass that parse.
    ///     Returns non-null only when the command is rejected.
    /// </summary>
    private static CommandResult? FxField(SimCommand c, out FxFieldMatch match, out string field,
        out IReadOnlyList<double> values)
    {
        match = EmptyMatch;
        field = c.Aux ?? "";
        values = [];
        if (FxCatalog.FieldsFor(c.Action) is not { } table)
            return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        if (FxCatalog.Match(table, field) is not { } resolved)
            return new CommandResult(CommandOutcome.Invalid, $"unknown fx field '{field}'");

        match = resolved;
        // A scalar may arrive in Values or — from a hand-written HTTP/MQTT command — in Value alone.
        values = c.Values ?? (resolved.Spec.Arity == 1 ? [c.Value] : []);
        return FxCatalog.IsValid(resolved.Spec, values)
            ? null
            : new CommandResult(CommandOutcome.Invalid,
                $"'{field}' expects {resolved.Spec.Arity} finite value(s) "
                + $"in [{resolved.Spec.Min}, {resolved.Spec.Max}]");
    }

    /// <summary>The placeholder assigned to a rejected <see cref="FxField"/> out-parameter.</summary>
    private static readonly FxFieldMatch EmptyMatch =
        new(new FxFieldSpec("", FxKind.Number, 0, 0, "", ""), []);

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
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Low,
        Notes = "Same enumeration the telemetry sampler uses to find vessels by id."
            + "5402: unchanged (UnsafeAsList :210, CelestialSystem.Get :288). Fragment/debris vehicles ('<id>_N') spawned by the new part-failure system are ordinary Vehicles in All, so they resolve here like any other id.")]
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
            return Universe.GetElapsedSeconds();
        }
        catch
        {
            return 0;
        }
    }
}
