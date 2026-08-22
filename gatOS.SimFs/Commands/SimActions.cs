namespace gatOS.SimFs.Commands;

#pragma warning disable CS1591 // Constants are documented by their grouped action-family headings.

/// <summary>
///     Stable, transport-neutral action keys understood by the game command executor.  The file
///     tree, HTTP, MQTT, serial, and MCP-shaped transports all name the same actions; adding an
///     action here makes it discoverable to structured transports without coupling them to KSA.
/// </summary>
public static class SimActions
{
    // Vessel and module control.
    public const string VesselIgnite = "vessel.ignite";
    public const string VesselShutdown = "vessel.shutdown";
    public const string VesselEngine = "vessel.engine";
    public const string VesselStage = "vessel.stage";
    public const string VesselThrottle = "vessel.throttle";
    public const string VesselLights = "vessel.lights";
    public const string VesselRcs = "vessel.rcs";
    public const string VesselTranslate = "vessel.translate";
    public const string VesselRotate = "vessel.rotate";
    public const string VesselAttitudeMode = "vessel.attitude_mode";
    public const string VesselAttitudeFrame = "vessel.attitude_frame";
    public const string VesselAttitudeTarget = "vessel.attitude_target";
    public const string VesselBurn = "vessel.burn";
    public const string VesselRcsMode = "vessel.rcs_mode";
    public const string VesselScale = "vessel.scale";
    public const string VesselAlwaysRender = "vessel.always_render";
    public const string EngineActive = "engine.active";
    public const string EngineMinThrottle = "engine.min_throttle";
    public const string RcsActive = "rcs.active";
    public const string LightOn = "light.on";
    public const string LightBrightness = "light.brightness";
    public const string LightColor = "light.color";
    public const string LightOuterAngle = "light.outer_angle";
    public const string LightInnerAngle = "light.inner_angle";
    public const string AnimationGoal = "animation.goal";
    public const string DockingUndock = "docking.undock";
    public const string DecouplerFire = "decoupler.fire";

    // Camera and host-side schedule controls.
    public const string CameraFocus = "camera.focus";
    public const string SchedulePause = "schedule.pause";
    public const string ScheduleScrub = "schedule.scrub";
    public const string ScheduleRate = "schedule.rate";
    public const string ScheduleLoop = "schedule.loop";
    public const string ScheduleStop = "schedule.stop";
    public const string ScheduleRemove = "schedule.remove";
    public const string ScheduleClear = "schedule.clear";

    // Debug/game manipulation controls which do not need an MCP-specific action vocabulary.
    public const string DebugWarp = "debug.warp";
    public const string DebugControlVessel = "debug.control_vessel";
    public const string DebugAlwaysRenderIva = "debug.always_render_iva";
    public const string DebugTeleport = "debug.teleport";
    public const string DebugImpulse = "debug.impulse";
    public const string DebugRefillFuel = "debug.refill_fuel";
    public const string DebugRefillBattery = "debug.refill_battery";
    public const string DebugWeldCreate = "debug.weld_create";
    public const string DebugWeldHere = "debug.weld_here";
    public const string DebugWeldRemove = "debug.weld_remove";
    public const string DebugWeldClear = "debug.weld_clear";
    public const string DebugWeldEnable = "debug.weld_enable";
    public const string DebugDockingPushoff = "debug.docking_pushoff";
    public const string DebugThugLifeAdd = "debug.thug_life_add";
    public const string DebugThugLifeClear = "debug.thug_life_clear";
    public const string DebugThugLifeRemove = "debug.thug_life_remove";
    public const string DebugThugLifeVisible = "debug.thug_life_visible";
    public const string DebugThugLifePosition = "debug.thug_life_position";
    public const string DebugThugLifeRotation = "debug.thug_life_rotation";
    public const string DebugThugLifeSize = "debug.thug_life_size";
    public const string DebugThugLifeCameras = "debug.thug_life_cameras";
    public const string DebugIvaPhysics = "debug.iva_physics";
    public const string DebugIvaRunOutsideIva = "debug.iva_run_outside_iva";
    public const string DebugIvaClear = "debug.iva_clear";
    public const string DebugIvaRelease = "debug.iva_release";
    public const string DebugIvaNudge = "debug.iva_nudge";
    public const string DebugIvaAdopt = "debug.iva_adopt";
    public const string DebugIvaAdoptAll = "debug.iva_adopt_all";

    // Opt-in vehicle shader and EVA material paint controls.
    public const string PaintPartsEnabled = "paint.parts_enabled";
    public const string PaintBlend = "paint.blend";
    public const string PaintPartsClear = "paint.parts_clear";
    public const string PaintGlobalEnabled = "paint.global_enabled";
    public const string PaintGlobalColor = "paint.global_color";
    public const string PaintGlobalClear = "paint.global_clear";
    public const string PaintTemplateEnabled = "paint.template_enabled";
    public const string PaintTemplateColor = "paint.template_color";
    public const string PaintTemplateClear = "paint.template_clear";
    public const string PaintVesselEnabled = "paint.vessel_enabled";
    public const string PaintVesselColor = "paint.vessel_color";
    public const string PaintVesselClear = "paint.vessel_clear";
    public const string PaintPartEnabled = "paint.part_enabled";
    public const string PaintPartColor = "paint.part_color";
    public const string PaintPartClear = "paint.part_clear";
    public const string PaintKittensEnabled = "paint.kittens_enabled";
    public const string PaintKittensClear = "paint.kittens_clear";
    public const string PaintKittenSharedEnabled = "paint.kitten_shared_enabled";
    public const string PaintKittenSharedColor = "paint.kitten_shared_color";
    public const string PaintKittenSharedClear = "paint.kitten_shared_clear";
    public const string PaintKittenSharedMaterialEnabled = "paint.kitten_shared_material_enabled";
    public const string PaintKittenSharedMaterialColor = "paint.kitten_shared_material_color";
    public const string PaintKittenSharedMaterialClear = "paint.kitten_shared_material_clear";
    public const string PaintKittenEnabled = "paint.kitten_enabled";
    public const string PaintKittenColor = "paint.kitten_color";
    public const string PaintKittenClear = "paint.kitten_clear";
    public const string PaintKittenMaterialEnabled = "paint.kitten_material_enabled";
    public const string PaintKittenMaterialColor = "paint.kitten_material_color";
    public const string PaintKittenMaterialClear = "paint.kitten_material_clear";

    // Custom clutter textures (GATOS_CUSTOM_CLUTTER_TEXTURES_PLAN): user PNG overrides of stock
    // ground-clutter texture assets. token=<stock texture id>, aux=<uploaded file name>.
    public const string PaintTextureBind = "paint.texture_bind";
    public const string PaintTextureUnbind = "paint.texture_unbind";
    public const string PaintTextureClear = "paint.texture_clear";

    // Stickers (STICKERS_PLAN): user PNG decals projected onto vehicles, terrain and clutter.
    // Registry-keyed (ordinal = sticker id), vessel-agnostic; token=<image>, aux=<anchor descriptor>.
    public const string PaintStickerPlace = "paint.sticker_place";
    public const string PaintStickerSpray = "paint.sticker_spray";
    public const string PaintStickerRemove = "paint.sticker_remove";
    public const string PaintStickerClear = "paint.sticker_clear";
    public const string PaintStickerVisible = "paint.sticker_visible";
    public const string PaintStickerSize = "paint.sticker_size";
    public const string PaintStickerDepth = "paint.sticker_depth";
    public const string PaintStickerRotation = "paint.sticker_rotation";
    public const string PaintStickerAlpha = "paint.sticker_alpha";
    public const string PaintStickerBrightness = "paint.sticker_brightness";
    public const string PaintStickerImage = "paint.sticker_image";

    /// <summary>
    ///     Global flag: draw every sticker as a magenta checker of its projection box instead of its
    ///     image, so the decal box, the reverse-Z depth reconstruction and the ego matrices can be
    ///     verified visually before any art is involved (STICKERS_PLAN S3).
    /// </summary>
    public const string PaintStickerDebug = "paint.sticker_debug";

    // Feature families with their own game-free parsers/catalogues.
    public const string AudioPlay = "audio.play";
    public const string AudioSet = "audio.set";
    public const string AudioStop = "audio.stop";
    public const string CameraEnabled = "camera.enabled";
    public const string CameraRelease = "camera.release";
    public const string CameraMode = "camera.mode";
    public const string CameraFollow = "camera.follow";
    public const string CameraTidal = "camera.tidal";
    public const string CameraMapScope = "camera.map_scope";
    public const string CameraPosition = "camera.position";
    public const string CameraFrame = "camera.frame";
    public const string CameraAnchor = "camera.anchor";
    public const string CameraGeo = "camera.geo";
    public const string CameraOrbitRadius = "camera.orbit_radius";
    public const string CameraOrbitAzimuth = "camera.orbit_azimuth";
    public const string CameraOrbitElevation = "camera.orbit_elevation";
    public const string CameraRotation = "camera.rotation";
    public const string CameraAim = "camera.aim";
    public const string CameraAimTarget = "camera.aim_target";
    public const string CameraAimOffset = "camera.aim_offset";
    public const string CameraAimFrame = "camera.aim_frame";
    public const string CameraAimUp = "camera.aim_up";
    public const string CameraRoll = "camera.roll";
    public const string CameraFov = "camera.fov";
    public const string CameraOrtho = "camera.ortho";
    public const string CameraOrthoHeight = "camera.ortho_height";
    public const string CameraSmoothing = "camera.smoothing";
    public const string CameraPoseReset = "camera.pose_reset";
    public const string CameraPlay = "camera.play";
    public const string CameraSet = "camera.set";
    public const string CameraStop = "camera.stop";
    public const string DebugFxSpawn = "debug.fx_spawn";
    public const string DebugFxClear = "debug.fx_clear";
    public const string DebugEnginePlumeSet = "debug.engineplume_set";
    public const string DebugEnginePlumeReset = "debug.engineplume_reset";
    public const string DebugPlumeTrailSet = "debug.plumetrail_set";
    public const string DebugPlumeTrailReset = "debug.plumetrail_reset";
    public const string DebugPlumeTrailClear = "debug.plumetrail_clear";
    public const string DebugCloudsSet = "debug.clouds_set";
    public const string DebugCloudsReset = "debug.clouds_reset";
    public const string DebugTerrainSet = "debug.terrain_set";
    public const string DebugTerrainReset = "debug.terrain_reset";
}

#pragma warning restore CS1591
