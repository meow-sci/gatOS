using gatOS.GameMod.Game.Ksa.Camera;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     The write half of <c>/sim/camera</c>: <c>camera.focus</c> (the original one-action surface) plus
///     the twenty-seven <c>camera.*</c> actions of tasks C1/C2 (the ownership, live-camera and pose
///     families), C3 (<c>play</c>/<c>set</c>/<c>stop</c>) and C5 (<c>map_scope</c>) — validated
///     game-side and applied through the <see cref="CameraDirector"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why every argument is re-validated here.</b> A 9p write parses through
///         <c>CameraCommands</c>/<c>CameraRules</c> and can never reach the sink with a bad value — but
///         <c>POST /v1/command</c> and MQTT <c>gatos/command</c> author a <c>SimCommand</c> directly and
///         bypass that parse entirely (AGENTS.md §3). So the same <see cref="CameraRules"/> run again on
///         this side of the queue, which is also what keeps the errnos identical on every transport.
///     </para>
///     <para>
///         <b>Errno vocabulary:</b> <c>Invalid</c> (EINVAL) for a malformed or out-of-range argument,
///         <c>NotFound</c> (ENOENT) for a vessel/body/part that is not in the live system,
///         <c>Unsupported</c> (EOPNOTSUPP) for a frame that cannot be resolved or a capability that is
///         off, and <c>Fault</c> (EIO) for a KSA call that threw — which <c>KsaCatalog</c> produces from
///         its own catch, latching the action degraded.
///     </para>
///     <para>Game thread only (threading rule 1): this runs inside the Frame-phase command drain.</para>
/// </remarks>
internal static class CameraActuator
{
    /// <summary>
    ///     Camera focus (<c>ctl/focus</c> on a vessel, <c>bodies/&lt;id&gt;/focus</c> on a celestial):
    ///     point the main view at any <see cref="Astronomical"/> — vehicle or body — the way the game's
    ///     own <c>follow</c> terminal action does. Uses the deterministic <c>Program.MainViewport</c>
    ///     (not the mouse-hovered viewport, which is meaningless when the player is typing in the SSH
    ///     terminal) and <c>changeControl: false</c> so it only moves the camera — it never switches or
    ///     clears the controlled vessel (that is <c>debug.control_vessel</c>). A pure view op, so it is
    ///     exempt from the authority gate. Game-thread only.
    /// </summary>
    /// <remarks>
    ///     <b>Both of the viewport's cameras are set</b> (plan §11.1, task C1.4). A viewport carries a
    ///     <c>BaseCamera</c> and a <c>MapCamera</c>, and <c>GetCamera()</c> returns whichever the current
    ///     mode uses; the game's own follow action sets the target on <b>both</b>
    ///     (<c>KSA/InputEvents.cs:759-760</c>). Setting only the active one — which this actuator
    ///     originally did, through <c>Program.GetMainCamera()</c> — left the map view pointing at the
    ///     previous target until the player happened to re-focus from inside map mode.
    /// </remarks>
    [KsaAnchor("Program.MainViewport.{BaseCamera,MapCamera}; Camera.SetFollow(IFollowable, "
            + "tidalLocking:true, changeControl:false, alert:false)",
        SourceFile = "KSA/Program.cs / KSA/IGameViewport.cs / KSA/Camera.cs / KSA/InputEvents.cs",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "Mirrors the game's `follow` terminal action; Astronomical (Vehicle and celestials) is "
            + "IFollowable. changeControl:false leaves Program.ControlledVehicle untouched; alert:false "
            + "keeps the 'Following X' TimedAlert off screen, which matters now that the same camera "
            + "surface is used to shoot footage."
            + "5402: Program.MainViewport is an IGameViewport (BaseCamera/MapCamera get-only properties); InputEvents' follow action is unchanged.")]
    internal static CommandResult Focus(Astronomical target)
    {
        var viewport = Program.MainViewport;
        viewport.MapCamera.SetFollow(target, tidalLocking: true, changeControl: false, alert: false);
        viewport.BaseCamera.SetFollow(target, tidalLocking: true, changeControl: false, alert: false);
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Routes one <c>camera.*</c> command (other than <c>camera.focus</c>) to the director, having
    ///     re-validated its payload. Invoked by <c>KsaCatalog</c> on the game thread.
    /// </summary>
    /// <param name="c">The command, exactly as any transport authored it.</param>
    /// <param name="director">The live director (never null — the catalog answers EOPNOTSUPP without one).</param>
    internal static CommandResult Execute(SimCommand c, CameraDirector director)
    {
        var state = director.Store.State;
        var limits = director.Store.Limits;
        var values = c.Values ?? [];

        switch (c.Action)
        {
            // ---- ownership -------------------------------------------------------------------------
            case CameraCommands.EnabledAction:
                if (!TryFlag(c.Value, out var take))
                    return Invalid("camera.enabled expects 0 or 1");
                return take ? director.Take() : director.Release();

            case CameraCommands.ReleaseAction:
                // The hard cut. camera/enabled 0 is the eased hand-back; this one is the "give it back
                // now" verb, so it does not blend.
                return director.Restore();

            // ---- the live game camera ----------------------------------------------------------------
            case CameraCommands.ModeAction:
                if (!CameraRules.TryParseMode(c.Token, out var mode))
                    return Invalid($"camera.mode expects one of {Tokens(CameraRules.ModeTokens)}");
                return director.SetMode(mode);

            case CameraCommands.FollowAction:
                if (!TargetRef.TryParse(c.Token, out var follow))
                    return Invalid("camera.follow expects vessel:<id> | body:<id> | none");
                if (follow.Kind == TargetKind.Part)
                    return new CommandResult(CommandOutcome.Unsupported,
                        "the game camera can only follow a whole vessel or body; aim at a part instead "
                        + "(pose/aim_target part:<vessel>/<iid>)");
                return director.SetFollow(follow);

            case CameraCommands.TidalAction:
                if (!TryFlag(c.Value, out var tidal))
                    return Invalid("camera.tidal expects 0 or 1");
                return director.SetTidal(tidal);

            case CameraCommands.MapScopeAction:
                // Range-validated inside the director, which is also where the game's own clamping and
                // its read-back publish live — one place, so the two cannot drift apart.
                return director.SetMapScope(c.Value);

            // ---- placement ---------------------------------------------------------------------------
            case CameraCommands.PositionAction:
            {
                if (!CameraRules.IsFiniteVector(values, CameraCommands.PositionSlots))
                    return Invalid("camera.position expects three finite components");
                // The frame tail is optional: an empty token means "keep whatever pose/frame says", so
                // a client animating a curve names its frame once and then writes bare triples.
                if (!string.IsNullOrEmpty(c.Token))
                {
                    if (!CameraRules.TryParseFrame(c.Token, out var positionFrame))
                        return Invalid($"camera.position frame expects one of {Tokens(CameraRules.FrameTokens)}");
                    state.SetOverride(CameraChannel.Frame, positionFrame);
                }

                state.SetOverride(CameraChannel.Position, new Vec3(values[0], values[1], values[2]));
                return CommandResult.Ok;
            }

            case CameraCommands.FrameAction:
                if (!CameraRules.TryParseFrame(c.Token, out var frame))
                    return Invalid($"camera.frame expects one of {Tokens(CameraRules.FrameTokens)}");
                state.SetOverride(CameraChannel.Frame, frame);
                return CommandResult.Ok;

            case CameraCommands.AnchorAction:
            {
                if (!TargetRef.TryParse(c.Token, out var anchor))
                    return Invalid("camera.anchor expects vessel:<id> | body:<id> | "
                                   + "part:<vessel>/<iid> | none");
                if (Missing(anchor) is { } gone)
                    return gone;
                state.SetOverride(CameraChannel.Anchor, anchor);
                return CommandResult.Ok;
            }

            case CameraCommands.GeoAction:
            {
                if (values.Count != CameraCommands.GeoSlots
                    || !CameraRules.IsValidLatitude(values[CameraCommands.GeoLat])
                    || !CameraRules.IsValidLongitude(values[CameraCommands.GeoLon])
                    || !CameraRules.IsValidAltitude(values[CameraCommands.GeoAlt]))
                    return Invalid("camera.geo expects lat[-90,90] lon[-180,360] alt(>=0)");

                // The body tail is optional; without it the placement uses the current pose/anchor,
                // which must therefore already be a body — a lat/lon about a vessel means nothing.
                var body = TargetRef.None;
                if (!string.IsNullOrEmpty(c.Token))
                {
                    if (!TargetRef.TryParse(c.Token, out body) || body.Kind != TargetKind.Body)
                        return Invalid("camera.geo's optional fourth field must be body:<id>");
                    if (Missing(body) is { } goneBody)
                        return goneBody;
                }
                else if (state.Compose(null, CameraChannelMask.None).Anchor.Kind != TargetKind.Body)
                {
                    return new CommandResult(CommandOutcome.Unsupported,
                        "camera.geo needs a body: name one in the line (\"lat lon alt body:<id>\") or "
                        + "set pose/anchor to a body first");
                }

                state.SetGeoOverride(values[CameraCommands.GeoLat], values[CameraCommands.GeoLon],
                    values[CameraCommands.GeoAlt], body);
                return CommandResult.Ok;
            }

            case CameraCommands.OrbitRadiusAction:
                if (!CameraRules.IsValidOrbitRadius(c.Value))
                    return Invalid("camera.orbit_radius expects a finite value >= 0 (0 hands placement "
                                   + "back to pose/position)");
                state.SetOverride(CameraChannel.OrbitRadius, c.Value);
                return CommandResult.Ok;

            case CameraCommands.OrbitAzimuthAction:
                if (!CameraRules.IsValidOrbitAzimuth(c.Value))
                    return Invalid("camera.orbit_azimuth expects a finite value in degrees");
                state.SetOverride(CameraChannel.OrbitAzimuth, c.Value);
                return CommandResult.Ok;

            case CameraCommands.OrbitElevationAction:
                if (!CameraRules.IsValidOrbitElevation(c.Value))
                    return Invalid("camera.orbit_elevation expects degrees in [-90, 90]");
                state.SetOverride(CameraChannel.OrbitElevation, c.Value);
                return CommandResult.Ok;

            // ---- orientation -------------------------------------------------------------------------
            case CameraCommands.RotationAction:
                if (!CameraRules.IsUnitQuaternionish(values))
                    return Invalid("camera.rotation expects four finite components with a norm in "
                                   + "[0.5, 2] (a zero quaternion names no rotation)");
                state.SetOverride(CameraChannel.Rotation,
                    new Quat(values[0], values[1], values[2], values[3]).Normalized());
                return CommandResult.Ok;

            case CameraCommands.AimAction:
                return Aim(c, state, values);

            case CameraCommands.AimTargetAction:
            {
                if (!TargetRef.TryParse(c.Token, out var aimTarget))
                    return Invalid("camera.aim_target expects vessel:<id> | body:<id> | "
                                   + "part:<vessel>/<iid> | none");
                if (Missing(aimTarget) is { } goneAim)
                    return goneAim;
                state.SetOverride(CameraChannel.AimTarget, aimTarget);
                return CommandResult.Ok;
            }

            case CameraCommands.AimOffsetAction:
                if (!CameraRules.IsFiniteVector(values, 3))
                    return Invalid("camera.aim_offset expects three finite components");
                state.SetOverride(CameraChannel.AimOffset, new Vec3(values[0], values[1], values[2]));
                return CommandResult.Ok;

            case CameraCommands.AimFrameAction:
                if (!CameraRules.TryParseFrame(c.Token, out var aimFrame))
                    return Invalid($"camera.aim_frame expects one of {Tokens(CameraRules.FrameTokens)}");
                state.SetOverride(CameraChannel.AimFrame, aimFrame);
                return CommandResult.Ok;

            case CameraCommands.AimUpAction:
                if (!CameraRules.TryParseAimUp(c.Token, out var aimUp))
                    return Invalid($"camera.aim_up expects one of {Tokens(CameraRules.AimUpTokens)}");
                state.SetOverride(CameraChannel.AimUp, aimUp);
                return CommandResult.Ok;

            case CameraCommands.RollAction:
                if (!CameraRules.IsValidRoll(c.Value))
                    return Invalid("camera.roll expects a finite value in degrees");
                state.SetOverride(CameraChannel.Roll, c.Value);
                return CommandResult.Ok;

            // ---- projection --------------------------------------------------------------------------
            case CameraCommands.FovAction:
                if (!CameraRules.IsValidFov(c.Value, limits.FovMin, limits.FovMax))
                    return Invalid($"camera.fov expects degrees in [{limits.FovMin}, {limits.FovMax}]");
                state.SetOverride(CameraChannel.Fov, c.Value);
                return CommandResult.Ok;

            case CameraCommands.OrthoAction:
                if (!TryFlag(c.Value, out var ortho))
                    return Invalid("camera.ortho expects 0 or 1");
                state.SetOverride(CameraChannel.Ortho, ortho);
                return CommandResult.Ok;

            case CameraCommands.OrthoHeightAction:
                if (!CameraRules.IsValidOrthoHeight(c.Value))
                    return Invalid("camera.ortho_height expects a finite value > 0 (metres)");
                state.SetOverride(CameraChannel.OrthoHeight, c.Value);
                return CommandResult.Ok;

            case CameraCommands.SmoothingAction:
                if (!CameraRules.IsValidSmoothing(c.Value))
                    return Invalid($"camera.smoothing expects seconds in "
                                   + $"[0, {CameraRules.MaxSmoothingSeconds}] (0 is raw)");
                state.SetOverride(CameraChannel.Smoothing, c.Value);
                return CommandResult.Ok;

            case CameraCommands.PoseResetAction:
                director.ResetPose();
                return CommandResult.Ok;

            // ---- track playback (task C3) ---------------------------------------------------------------
            // Straight to the game-free executor, exactly as KsaCatalog routes schedule.* to
            // ScheduleStore.Execute: resolving a track, parsing it, driving a PlaybackClock and
            // registering the player touch no KSA type at all, and re-implementing any of it here would
            // be a second definition of the grammar. A camera track IS a /sim/ctl/schedules entry, so
            // with [schedule] scheduling off there is no registry to put one in.
            case CameraCommands.PlayAction:
            case CameraCommands.SetAction:
            case CameraCommands.StopAction:
                return director.Playback is { } track
                    ? track.Execute(c)
                    : new CommandResult(CommandOutcome.Unsupported,
                        "camera track playback needs [schedule] schedule_enabled = true (a track is a "
                        + "player in /sim/ctl/schedules); every camera channel is still reachable from "
                        + "/sim/camera/pose and schedulable through /sim/ctl/timed_batch");

            default:
                return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{c.Action}'");
        }
    }

    /// <summary>
    ///     The composite <c>pose/aim</c> convenience: four channels at once (target, offset, frame, up)
    ///     plus roll when the line carried it. Roll is the exception on purpose — it is animatable on
    ///     its own, so an aim write that never mentioned it must leave it alone rather than silently
    ///     level the camera.
    /// </summary>
    private static CommandResult Aim(SimCommand c, CameraState state, IReadOnlyList<double> values)
    {
        if (!TargetRef.TryParse(c.Token, out var target))
            return Invalid("camera.aim expects a target: vessel:<id> | body:<id> | "
                           + "part:<vessel>/<iid> | none");
        if (values.Count != CameraCommands.AimSlots || !CameraRules.IsFiniteVector(values))
            return Invalid($"camera.aim expects the {CameraCommands.AimSlots}-slot values array");

        var frameOrdinal = (int)values[CameraCommands.AimFrameOrdinal];
        var upOrdinal = (int)values[CameraCommands.AimUpOrdinal];
        if (CameraRules.NameOf((FrameKind)frameOrdinal) is null)
            return Invalid($"camera.aim frame ordinal {frameOrdinal} is out of range");
        if (CameraRules.NameOf((AimUpKind)upOrdinal) is null)
            return Invalid($"camera.aim up ordinal {upOrdinal} is out of range");
        var rollPresent = values[CameraCommands.AimRollPresent] > 0.5;
        if (rollPresent && !CameraRules.IsValidRoll(values[CameraCommands.AimRoll]))
            return Invalid("camera.aim roll expects a finite value in degrees");
        if (Missing(target) is { } gone)
            return gone;

        state.SetOverride(CameraChannel.AimTarget, target);
        state.SetOverride(CameraChannel.AimOffset, new Vec3(
            values[CameraCommands.AimOffX], values[CameraCommands.AimOffY],
            values[CameraCommands.AimOffZ]));
        state.SetOverride(CameraChannel.AimFrame, (FrameKind)frameOrdinal);
        state.SetOverride(CameraChannel.AimUp, (AimUpKind)upOrdinal);
        if (rollPresent)
            state.SetOverride(CameraChannel.Roll, values[CameraCommands.AimRoll]);
        return CommandResult.Ok;
    }

    /// <summary>
    ///     ENOENT for a reference that names nothing in the live system, or null when it is fine.
    ///     Existence is checked at <i>write</i> time so a typo fails the guest's <c>write(2)</c>
    ///     immediately instead of quietly producing a shot that never moves.
    /// </summary>
    private static CommandResult? Missing(in TargetRef reference)
        => !reference.HasTarget || CameraTargets.TryResolve(reference, out _)
            ? null
            : new CommandResult(CommandOutcome.NotFound, $"'{reference}' is gone");

    /// <summary>Accepts a strict 0/1 flag; anything else is EINVAL (a control file is not a rounder).</summary>
    private static bool TryFlag(double value, out bool flag)
    {
        flag = value > 0.5;
        return value is 0.0 or 1.0;
    }

    private static CommandResult Invalid(string message)
        => new(CommandOutcome.Invalid, message);

    private static string Tokens(string[] table) => string.Join('|', table);
}
