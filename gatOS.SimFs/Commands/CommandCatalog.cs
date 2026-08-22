namespace gatOS.SimFs.Commands;

/// <summary>Whether an action normally addresses a vessel/module or a game-global feature.</summary>
public enum CommandTargetKind
{
    /// <summary>The command's <see cref="SimCommand.VesselId"/> identifies a live vessel.</summary>
    Vessel,

    /// <summary>The command addresses a global store, renderer, camera, or schedule player.</summary>
    Global,
}

/// <summary>A discoverable description of one stable <see cref="SimCommand.Action"/> key.</summary>
/// <param name="Action">The stable action key.</param>
/// <param name="Target">The normal target addressing model.</param>
/// <param name="Summary">Short transport-neutral intent for structured API descriptions.</param>
public sealed record CommandDescriptor(string Action, CommandTargetKind Target, string Summary)
{
    /// <summary>The game-thread phase derived from <see cref="Action"/>.</summary>
    public CommandPhase Phase => SimCommand.PhaseFor(Action);

    /// <summary>Whether the action is behind the debug namespace gate.</summary>
    public bool IsDebug => Action.StartsWith("debug.", StringComparison.Ordinal);

    /// <summary>The preferred logical MCP tool family; <c>gatos.command</c> remains the complete backstop.</summary>
    public string LogicalTool => Action switch
    {
        SimActions.VesselIgnite => "gatos.ignite_engines",
        SimActions.VesselShutdown => "gatos.shutdown_engines",
        SimActions.VesselStage => "gatos.activate_stage",
        SimActions.CameraFocus or SimActions.DebugControlVessel => "gatos.vessel_control",
        _ when Action.StartsWith("vessel.", StringComparison.Ordinal) => "gatos.vessel_control",
        _ when Action.StartsWith("engine.", StringComparison.Ordinal) || Action.StartsWith("rcs.", StringComparison.Ordinal)
            || Action.StartsWith("light.", StringComparison.Ordinal) || Action.StartsWith("animation.", StringComparison.Ordinal)
            || Action.StartsWith("docking.", StringComparison.Ordinal) || Action.StartsWith("decoupler.", StringComparison.Ordinal)
            => "gatos.module_control",
        _ when Action.StartsWith("camera.", StringComparison.Ordinal) => "gatos.camera_control",
        _ when Action.StartsWith("audio.", StringComparison.Ordinal) => "gatos.audio_control",
        _ when Action.StartsWith("paint.sticker_", StringComparison.Ordinal) => "gatos.paint_sticker",
        _ when Action.StartsWith("paint.", StringComparison.Ordinal) => "gatos.paint_control",
        _ when Action.StartsWith("schedule.", StringComparison.Ordinal) => "gatos.schedule_control",
        _ when Action.StartsWith("debug.engineplume", StringComparison.Ordinal)
            || Action.StartsWith("debug.plumetrail", StringComparison.Ordinal)
            || Action.StartsWith("debug.clouds", StringComparison.Ordinal)
            || Action.StartsWith("debug.terrain", StringComparison.Ordinal) => "gatos.render_fx_control",
        _ when Action.StartsWith("debug.", StringComparison.Ordinal) => "gatos.debug_control",
        _ => "gatos.command",
    };

    /// <summary>The configuration or authority gate an agent must preflight.</summary>
    public string Gate => Action switch
    {
        _ when Action.StartsWith("audio.", StringComparison.Ordinal) => "audio_enabled",
        _ when Action.StartsWith("schedule.", StringComparison.Ordinal) => "schedule_enabled",
        _ when Action.StartsWith("camera.", StringComparison.Ordinal) && Action != SimActions.CameraFocus => "camera_enabled",
        _ when Action.StartsWith("paint.sticker_", StringComparison.Ordinal)
            => "control_enabled + paint stickers",
        _ when Action.StartsWith("paint.texture_", StringComparison.Ordinal)
            => "control_enabled + paint textures store",
        _ when Action.StartsWith("paint.", StringComparison.Ordinal) => "control_enabled + paint runtime master",
        _ when IsDebug => "debug_namespace",
        _ => "control_enabled",
    };

    /// <summary>Whether catch-up must preserve every occurrence rather than coalescing state.</summary>
    public bool IsTrigger => Action.EndsWith(".ignite", StringComparison.Ordinal)
        || Action.EndsWith(".stage", StringComparison.Ordinal)
        || Action.EndsWith(".fire", StringComparison.Ordinal)
        || Action.EndsWith(".undock", StringComparison.Ordinal)
        || Action.EndsWith(".spawn", StringComparison.Ordinal)
        || Action.EndsWith(".play", StringComparison.Ordinal)
        || Action.EndsWith(".release", StringComparison.Ordinal)
        || Action.EndsWith(".stop", StringComparison.Ordinal)
        || Action.EndsWith(".remove", StringComparison.Ordinal)
        || Action.EndsWith(".clear", StringComparison.Ordinal)
        || Action.EndsWith(".reset", StringComparison.Ordinal);

    /// <summary>Whether submitting the same setpoint repeatedly has the same intended effect.</summary>
    public bool IsIdempotent => !IsTrigger;

    /// <summary>Transport-neutral argument slots accepted by the canonical command envelope.</summary>
    public string ArgumentShape => Target == CommandTargetKind.Vessel
        ? "vessel_id; optional ordinal/value/values/token/aux as documented for the action"
        : "global target; optional ordinal/value/values/token/aux as documented for the action";

    /// <summary>Primary unit vocabulary; exact per-field units remain in SPEC_9P_FILESYSTEM.md.</summary>
    public string Units => Action switch
    {
        SimActions.VesselThrottle or SimActions.EngineMinThrottle or SimActions.AnimationGoal => "fraction [0,1]",
        SimActions.DebugWarp or SimActions.ScheduleRate => "multiplier",
        SimActions.ScheduleScrub => "milliseconds",
        SimActions.DebugImpulse or SimActions.DebugDockingPushoff => "N*s (or documented direct delta-v mode)",
        SimActions.DebugTeleport or SimActions.VesselBurn or SimActions.VesselTranslate or SimActions.VesselRotate
            or SimActions.VesselAttitudeTarget => "documented vector/quaternion slots",
        _ when Action.EndsWith("_color", StringComparison.Ordinal) => "normalized sRGB triple [0,1]",
        _ when Action.Contains("angle", StringComparison.Ordinal) || Action is SimActions.CameraRoll or SimActions.CameraFov => "degrees",
        _ => "action-specific; see SPEC_9P_FILESYSTEM.md",
    };

    /// <summary>Concise safety metadata used by discovery and documentation generation.</summary>
    public string Safety => IsTrigger ? "one-shot or lifecycle trigger; review live state before retry" :
        IsDebug ? "debug/cheat mutation; gated and session-scoped where documented" :
        "state setpoint; game authority and feature gates remain authoritative";

    /// <summary>Stable schedule catch-up key construction for state controls.</summary>
    public string? CoalescingKey => IsTrigger ? null : "action + vessel_id + ordinal";
}

/// <summary>The result of validating a transport-neutral command before submission.</summary>
public sealed record CommandValidationResult(CommandDescriptor? Descriptor, string? Error)
{
    /// <summary>Whether validation succeeded.</summary>
    public bool IsValid => Error is null;

    /// <summary>A successful validation result.</summary>
    public static CommandValidationResult Valid(CommandDescriptor descriptor) => new(descriptor, null);

    /// <summary>A successful structural validation that does not identify one particular action.</summary>
    public static CommandValidationResult Valid() => new(null, null);

    /// <summary>An invalid validation result.</summary>
    public static CommandValidationResult Invalid(string error) => new(null, error);
}

/// <summary>
///     The game-free action catalogue used by structured transports. It intentionally validates
///     only identity and wire-safe command structure; individual control files and the KSA executor
///     remain the source of truth for action-specific range and live-game validation.
/// </summary>
public static class CommandCatalog
{
    private static readonly CommandDescriptor[] Entries =
    [
        Vessel(SimActions.VesselIgnite, "Ignite every ignitable engine on a vessel."),
        Vessel(SimActions.VesselShutdown, "Shut down every shutdown-capable engine on a vessel."),
        Vessel(SimActions.VesselEngine, "Enable or disable vessel engines."),
        Vessel(SimActions.VesselStage, "Activate the next staging event."),
        Vessel(SimActions.VesselThrottle, "Set the vessel throttle."),
        Vessel(SimActions.VesselLights, "Enable or disable vessel lights."),
        Vessel(SimActions.VesselRcs, "Enable or disable vessel RCS."),
        Vessel(SimActions.VesselTranslate, "Set manual RCS translation axes."),
        Vessel(SimActions.VesselRotate, "Set manual RCS rotation axes."),
        Vessel(SimActions.VesselAttitudeMode, "Set the flight-computer attitude mode."),
        Vessel(SimActions.VesselAttitudeFrame, "Set the flight-computer attitude frame."),
        Vessel(SimActions.VesselAttitudeTarget, "Set a body-to-CCI attitude target quaternion."),
        Vessel(SimActions.VesselBurn, "Set a flight-computer burn target."),
        Vessel(SimActions.VesselRcsMode, "Set the flight-computer RCS mode."),
        Vessel(SimActions.VesselScale, "Set a vessel render scale."),
        Vessel(SimActions.VesselAlwaysRender, "Force a vessel to render at any distance."),
        Vessel(SimActions.EngineActive, "Enable or disable one engine."),
        Vessel(SimActions.EngineMinThrottle, "Set one engine's minimum throttle."),
        Vessel(SimActions.RcsActive, "Enable or disable one RCS controller."),
        Vessel(SimActions.LightOn, "Enable or disable one light."),
        Vessel(SimActions.LightBrightness, "Set one light's brightness."),
        Vessel(SimActions.LightColor, "Set one light's colour."),
        Vessel(SimActions.LightOuterAngle, "Set one light's outer cone angle."),
        Vessel(SimActions.LightInnerAngle, "Set one light's inner cone angle."),
        Vessel(SimActions.AnimationGoal, "Set one animation's goal."),
        Vessel(SimActions.DockingUndock, "Undock one docking port."),
        Vessel(SimActions.DecouplerFire, "Fire one decoupler."),
        Global(SimActions.CameraFocus, "Focus the game camera on a target."),
        Global(SimActions.SchedulePause, "Pause or resume a live schedule."),
        Global(SimActions.ScheduleScrub, "Scrub a live schedule in milliseconds."),
        Global(SimActions.ScheduleRate, "Set a live schedule playback rate."),
        Global(SimActions.ScheduleLoop, "Enable or disable a live schedule loop."),
        Global(SimActions.ScheduleStop, "Stop a live schedule."),
        Global(SimActions.ScheduleRemove, "Remove a live schedule."),
        Global(SimActions.ScheduleClear, "Remove every live schedule."),
        Global(SimActions.DebugWarp, "Set game time warp."),
        Global(SimActions.DebugControlVessel, "Change the controlled vessel."),
        Global(SimActions.DebugAlwaysRenderIva, "Force IVA meshes to render."),
        Vessel(SimActions.DebugTeleport, "Teleport a vessel using a CCI state vector."),
        Vessel(SimActions.DebugImpulse, "Apply an impulse to a vessel."),
        Vessel(SimActions.DebugRefillFuel, "Refill a vessel's fuel."),
        Vessel(SimActions.DebugRefillBattery, "Refill a vessel's battery."),
        Vessel(SimActions.DebugWeldCreate, "Create a vessel weld."),
        Vessel(SimActions.DebugWeldHere, "Create a weld from current relative pose."),
        Vessel(SimActions.DebugWeldRemove, "Remove a vessel weld."),
        Global(SimActions.DebugWeldClear, "Remove every vessel weld."),
        Vessel(SimActions.DebugWeldEnable, "Enable or disable a vessel weld."),
        Vessel(SimActions.DebugDockingPushoff, "Set a docking port push-off impulse."),
        Global(SimActions.DebugThugLifeAdd, "Add a thug-life render quad."),
        Global(SimActions.DebugThugLifeClear, "Remove every thug-life render quad."),
        Global(SimActions.DebugThugLifeRemove, "Remove one thug-life render quad."),
        Global(SimActions.DebugThugLifeVisible, "Show or hide one thug-life render quad."),
        Global(SimActions.DebugThugLifePosition, "Set a thug-life render quad position."),
        Global(SimActions.DebugThugLifeRotation, "Set a thug-life render quad rotation."),
        Global(SimActions.DebugThugLifeSize, "Set a thug-life render quad size."),
        Global(SimActions.DebugThugLifeCameras, "Set thug-life render camera visibility."),
        Global(SimActions.DebugIvaPhysics, "Enable or disable IVA cabin physics."),
        Global(SimActions.DebugIvaRunOutsideIva, "Set IVA physics outside-IVA behavior."),
        Global(SimActions.DebugIvaClear, "Release every IVA physics object."),
        Global(SimActions.DebugIvaRelease, "Release one IVA physics object."),
        Global(SimActions.DebugIvaNudge, "Apply a velocity nudge to one IVA physics object."),
        Global(SimActions.DebugIvaAdopt, "Adopt an IVA subpart into cabin physics."),
        Global(SimActions.DebugIvaAdoptAll, "Adopt matching IVA subparts into cabin physics."),
        Global(SimActions.AudioPlay, "Play an uploaded audio clip."),
        Global(SimActions.AudioSet, "Update a live audio channel."),
        Global(SimActions.AudioStop, "Stop one or all audio channels."),
        Global(SimActions.CameraEnabled, "Take or release camera ownership."),
        Global(SimActions.CameraRelease, "Release camera ownership immediately."),
        Global(SimActions.CameraMode, "Set camera mode."),
        Global(SimActions.CameraFollow, "Set camera follow behavior."),
        Global(SimActions.CameraTidal, "Set camera tidal behavior."),
        Global(SimActions.CameraMapScope, "Set map camera scope."),
        Global(SimActions.CameraPosition, "Set camera position."),
        Global(SimActions.CameraFrame, "Set camera placement frame."),
        Global(SimActions.CameraAnchor, "Set camera anchor."),
        Global(SimActions.CameraGeo, "Set camera geodetic position."),
        Global(SimActions.CameraOrbitRadius, "Set camera orbit radius."),
        Global(SimActions.CameraOrbitAzimuth, "Set camera orbit azimuth."),
        Global(SimActions.CameraOrbitElevation, "Set camera orbit elevation."),
        Global(SimActions.CameraRotation, "Set camera rotation."),
        Global(SimActions.CameraAim, "Set a camera aim constraint."),
        Global(SimActions.CameraAimTarget, "Set the camera aim target."),
        Global(SimActions.CameraAimOffset, "Set the camera aim offset."),
        Global(SimActions.CameraAimFrame, "Set the camera aim offset frame."),
        Global(SimActions.CameraAimUp, "Set the camera aim up reference."),
        Global(SimActions.CameraRoll, "Set camera roll."),
        Global(SimActions.CameraFov, "Set camera field of view."),
        Global(SimActions.CameraOrtho, "Enable or disable orthographic camera projection."),
        Global(SimActions.CameraOrthoHeight, "Set orthographic camera height."),
        Global(SimActions.CameraSmoothing, "Set camera smoothing."),
        Global(SimActions.CameraPoseReset, "Reset camera pose overrides."),
        Global(SimActions.CameraPlay, "Play a camera track."),
        Global(SimActions.CameraSet, "Update a camera track player."),
        Global(SimActions.CameraStop, "Stop a camera track player."),
        Global(SimActions.DebugFxSpawn, "Spawn a face-anchored particle effect."),
        Global(SimActions.DebugFxClear, "Clear face-anchored particle effects."),
        Global(SimActions.DebugEnginePlumeSet, "Set an engine plume field."),
        Global(SimActions.DebugEnginePlumeReset, "Reset an engine plume field."),
        Global(SimActions.DebugPlumeTrailSet, "Set a plume trail field."),
        Global(SimActions.DebugPlumeTrailReset, "Reset a plume trail field."),
        Global(SimActions.DebugPlumeTrailClear, "Clear existing plume trails."),
        Global(SimActions.DebugCloudsSet, "Set a cloud-rendering field."),
        Global(SimActions.DebugCloudsReset, "Reset a cloud-rendering field."),
        Global(SimActions.DebugTerrainSet, "Set a terrain-rendering field."),
        Global(SimActions.DebugTerrainReset, "Reset a terrain-rendering field."),
        Global(SimActions.PaintPartsEnabled, "Opt in or out of the in-memory vehicle shader transformation."),
        Global(SimActions.PaintBlend, "Select multiply, tint, or replace vehicle paint blending."),
        Global(SimActions.PaintPartsClear, "Clear every retained vehicle paint rule."),
        Global(SimActions.PaintGlobalEnabled, "Enable or disable the global vehicle paint rule."),
        Global(SimActions.PaintGlobalColor, "Set the global vehicle paint colour."),
        Global(SimActions.PaintGlobalClear, "Reset the global vehicle paint rule."),
        Global(SimActions.PaintTemplateEnabled, "Enable or disable one part-template paint rule."),
        Global(SimActions.PaintTemplateColor, "Set one part-template paint colour."),
        Global(SimActions.PaintTemplateClear, "Remove one part-template paint rule."),
        Vessel(SimActions.PaintVesselEnabled, "Enable or disable a live whole-vessel paint rule."),
        Vessel(SimActions.PaintVesselColor, "Set a live whole-vessel paint colour."),
        Vessel(SimActions.PaintVesselClear, "Remove a live whole-vessel paint rule."),
        Vessel(SimActions.PaintPartEnabled, "Enable or disable one stable part-instance paint rule."),
        Vessel(SimActions.PaintPartColor, "Set one stable part-instance paint colour."),
        Vessel(SimActions.PaintPartClear, "Remove one stable part-instance paint rule."),
        Global(SimActions.PaintKittensEnabled, "Opt in or out of gatOS-owned EVA material clones."),
        Global(SimActions.PaintKittensClear, "Clear every retained EVA paint rule."),
        Global(SimActions.PaintKittenSharedEnabled, "Enable or disable the shared EVA paint rule."),
        Global(SimActions.PaintKittenSharedColor, "Set the shared EVA paint colour."),
        Global(SimActions.PaintKittenSharedClear, "Reset the shared EVA paint rule."),
        Global(SimActions.PaintKittenSharedMaterialEnabled, "Enable or disable a shared EVA material rule."),
        Global(SimActions.PaintKittenSharedMaterialColor, "Set a shared EVA material colour."),
        Global(SimActions.PaintKittenSharedMaterialClear, "Remove a shared EVA material rule."),
        Vessel(SimActions.PaintKittenEnabled, "Enable or disable one EVA's default paint rule."),
        Vessel(SimActions.PaintKittenColor, "Set one EVA's default paint colour."),
        Vessel(SimActions.PaintKittenClear, "Remove every rule for one EVA."),
        Vessel(SimActions.PaintKittenMaterialEnabled, "Enable or disable one EVA material rule."),
        Vessel(SimActions.PaintKittenMaterialColor, "Set one EVA material colour."),
        Vessel(SimActions.PaintKittenMaterialClear, "Remove one EVA material rule."),
        Global(SimActions.PaintTextureBind, "Draw a stock ground-clutter texture with an uploaded image."),
        Global(SimActions.PaintTextureUnbind, "Restore one stock ground-clutter texture."),
        Global(SimActions.PaintTextureClear, "Restore every stock ground-clutter texture (global teardown)."),
        Global(SimActions.PaintStickerPlace, "Place a sticker decal at explicit anchor coordinates."),
        Global(SimActions.PaintStickerSpray, "Spray a sticker decal where the camera or cursor points."),
        Global(SimActions.PaintStickerRemove, "Remove one sticker decal."),
        Global(SimActions.PaintStickerClear, "Remove every sticker decal."),
        Global(SimActions.PaintStickerVisible, "Show or hide one sticker decal."),
        Global(SimActions.PaintStickerSize, "Set one sticker decal's width and height."),
        Global(SimActions.PaintStickerDepth, "Set one sticker decal's projection depth."),
        Global(SimActions.PaintStickerRotation, "Set one sticker decal's roll or heading."),
        Global(SimActions.PaintStickerAlpha, "Set one sticker decal's opacity."),
        Global(SimActions.PaintStickerBrightness, "Set one sticker decal's brightness."),
        Global(SimActions.PaintStickerImage, "Point one sticker decal at another uploaded image."),
        Global(SimActions.PaintStickerDebug, "Draw sticker decals as projection-box checkers instead of images."),
    ];

    private static readonly IReadOnlyDictionary<string, CommandDescriptor> ByAction =
        Entries.ToDictionary(entry => entry.Action, StringComparer.Ordinal);

    /// <summary>Every known action descriptor in stable catalogue order.</summary>
    public static IReadOnlyList<CommandDescriptor> All => Entries;

    /// <summary>Finds a descriptor by its stable action key.</summary>
    public static bool TryGet(string? action, out CommandDescriptor descriptor)
    {
        if (action is not null && ByAction.TryGetValue(action, out descriptor!))
            return true;
        descriptor = null!;
        return false;
    }

    /// <summary>Validates a command's action identity and transport-safe structural shape.</summary>
    public static CommandValidationResult Validate(SimCommand? command)
    {
        if (command is null)
            return CommandValidationResult.Invalid("command is required");
        if (!TryGet(command.Action, out var descriptor))
            return CommandValidationResult.Invalid($"unknown action '{command.Action}'");
        if (command.Ordinal < SimCommand.NoOrdinal)
            return CommandValidationResult.Invalid("ordinal must be -1 or greater");
        if (!double.IsFinite(command.Value))
            return CommandValidationResult.Invalid("value must be finite");
        if (command.Values is not null && command.Values.Any(value => !double.IsFinite(value)))
            return CommandValidationResult.Invalid("values must contain only finite numbers");
        return CommandValidationResult.Valid(descriptor);
    }

    private static CommandDescriptor Vessel(string action, string summary)
        => new(action, CommandTargetKind.Vessel, summary);

    private static CommandDescriptor Global(string action, string summary)
        => new(action, CommandTargetKind.Global, summary);
}
