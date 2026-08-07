using Brutal.Numerics;
using gatOS.Logging;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using KSA;
using KsaCamera = KSA.Camera;

namespace gatOS.GameMod.Game.Ksa.Camera;

/// <summary>
///     The camera director (plans/CAMERA_CONTROLS_PLAN.md §5): takes ownership of the main viewport's
///     camera, writes a composed pose onto it once per rendered frame, publishes what it is doing, and
///     hands the camera back to the game exactly as it found it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Zero Harmony patches — and that is the design, not an economy.</b> The pose is written at
///         the <i>end</i> of frame <i>N</i> (from the <c>[StarMapAfterOnFrame]</c> postfix on
///         <c>Program.OnFrame</c>), so frame <i>N+1</i>'s <c>OnFrameViewports</c> runs
///         <c>Camera.OnFrame</c> and rebuilds the view/projection matrices and frustum <b>from gatOS's
///         transform</b> — and then celestial LOD, the light system, the cursor ray,
///         <c>UpdateShaderData</c> and the render all consume that one consistent camera. gatOS never
///         touches a matrix. The cost is exactly one frame of pipeline latency, which is constant and
///         therefore invisible.
///     </para>
///     <para>
///         <b>Ownership is a mode park, not a fight.</b> <c>Viewport.Mode = CameraMode.Fixed</c> plus
///         <c>Camera.Unfollow</c> makes <c>FixedController.OnFrame</c> a literal no-op — its entire body
///         is wrapped in <c>if (Following != null)</c> — so the game's camera solver produces nothing
///         and gatOS is the only writer. Its <c>OnKey</c>/<c>OnCursorEnter</c>/<c>OnGamepad*</c> all
///         return false, so player input cannot perturb it either.
///     </para>
///     <para>
///         <b>Two things this deliberately does to avoid on-screen text.</b> The mode park assigns
///         <c>Viewport.Mode</c> <i>directly</i> rather than calling <c>SetCameraMode</c>, because
///         <c>FixedController.OnSwitchOn</c> fires a <c>TimedAlert("Fixed Camera")</c> that would appear
///         in the footage; and every <c>SetFollow</c>/<c>Unfollow</c> passes <c>alert: false</c> and
///         <c>changeControl: false</c> — the latter because the defaults would null
///         <c>Program.ControlledVehicle</c> and drop the player's vessel mid-flight.
///     </para>
///     <para>
///         <b>The one exception to direct mode assignment is Map.</b> <c>MapController.OnSwitchOn</c>
///         sets <c>Camera.NoRotation = true</c> (which changes what <c>PositionCce</c>/<c>LocalPosition</c>
///         even mean) and clears <c>Program.IsControlledVehicleActive</c>; only
///         <c>MapController.OnSwitchOff</c> undoes both. Leaving Map by direct assignment would strand
///         the player with an uncontrollable vessel, so that one transition goes through
///         <c>SetCameraMode</c> and accepts the three-second alert.
///     </para>
///     <para>
///         <b>⚠ Owning the main camera changes which way a kittenaut walks.</b>
///         <c>KittenEva.PrepareWorker</c> feeds <c>Program.GetMainCamera().GetForwardEcl()</c> /
///         <c>GetRightEcl()</c> / <c>GetUpEcl()</c> into EVA locomotion, so while gatOS holds the camera,
///         "forward" for a kittenaut on EVA is wherever the <i>shot</i> is facing. This is documented
///         rather than worked around: silently releasing EVA control would be a more surprising side
///         effect than the one it fixes.
///     </para>
///     <para>
///         <b>Threading:</b> game thread only (threading rule 1). Transport threads only enqueue
///         <c>SimCommand</c>s and read the volatile <c>CameraStatus</c> this class publishes.
///     </para>
/// </remarks>
internal sealed class CameraDirector(CameraStore store, double releaseBlendSeconds)
{
    private const double RadToDeg = 180.0 / Math.PI;

    /// <summary>Where the director is in its ownership lifecycle.</summary>
    private enum Phase
    {
        /// <summary>The game owns its camera; the driver is a single branch.</summary>
        Idle,

        /// <summary>gatOS is the sole writer of the main viewport's camera.</summary>
        Owned,

        /// <summary>Easing back toward the captured pose before the hard restore.</summary>
        Releasing,
    }

    private readonly PoseSmoother _smoother = new();

    private Phase _phase = Phase.Idle;
    private bool _publishedIdle = true; // the store starts at CameraStatus.Idle

    // ---- restore capture (see Take/Restore) --------------------------------------------------------
    private KsaCamera? _restoreCamera;
    private CameraMode _restoreMode;
    private IFollowable? _restoreFollowing;
    private bool _restoreTidal;
    private bool _restoreNoRotation;
    private double3 _restoreLocalPosition;
    private doubleQuat _restoreLocalRotation;
    private double _restoreFovDeg = 60;
    private bool _restoreOrtho;
    private double3 _restorePositionEcl;

    // ---- per-frame carry ---------------------------------------------------------------------------
    private double3 _lastPositionEcl;
    private doubleQuat _lastRotation = doubleQuat.Identity;
    private double3 _lastResolvedPositionEcl;
    private double _appliedFovDeg = double.NaN;
    private string _degradeReason = "";

    // ---- release blend -----------------------------------------------------------------------------
    private double _blendElapsed;
    private double _blendDuration;
    private double3 _blendStartPosition;
    private doubleQuat _blendStartRotation = doubleQuat.Identity;
    private double _blendStartFovDeg = 60;

    /// <summary>The game-free store this director drives (tracks, compositor, status, events).</summary>
    internal CameraStore Store => store;

    /// <summary>
    ///     True when the per-frame driver can be skipped entirely: gatOS does not own the camera and the
    ///     idle status has already been published. This is the default state, and it is what makes the
    ///     whole feature cost nothing until a guest writes <c>/sim/camera/enabled</c>.
    /// </summary>
    internal bool IsIdle => _phase == Phase.Idle && _publishedIdle;

    /// <summary>Whether gatOS currently owns the camera (a release blend still counts as owned).</summary>
    internal bool IsOwned => _phase != Phase.Idle;

    // ================================================================================================
    //  Ownership
    // ================================================================================================

    /// <summary>
    ///     <c>camera/enabled 1</c>: takes the main viewport's camera. Idempotent, and it cancels an
    ///     in-flight release blend (a director who changed their mind mid-handback keeps their shot).
    /// </summary>
    /// <remarks>
    ///     The order matters and each step has a reason:
    ///     <list type="number">
    ///         <item>capture the restore state from the <b>active</b> camera — which is the map camera in
    ///             Map mode, so the capture must not assume the base camera;</item>
    ///         <item>park the mode (see the type remarks for why Map is special);</item>
    ///         <item><c>Unfollow(changeControl: false)</c> so <c>FixedController.OnFrame</c> goes inert;</item>
    ///         <item>seed the compositor's <b>baseline</b> from the captured values, so an owned-but-unwritten
    ///             camera sits exactly where the game left it — taking the camera is visually a no-op until
    ///             something is actually written.</item>
    ///     </list>
    /// </remarks>
    [KsaAnchor("Program.MainViewport; Viewport.{Mode,GetCamera,SetCameraMode,BaseCamera}; "
            + "Camera.{PositionEcl,LocalPosition,LocalRotation,NoRotation,Following,TidalLocking,"
            + "GetFieldOfView,Orthographic,Unfollow}",
        SourceFile = "KSA/Program.cs / KSA/Viewport.cs / KSA/Camera.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "Program.MainViewport (:437), NOT Program.GetCamera()/GetCameraMode(), which read the "
            + "FRAME viewport — and never a viewport by index (Program.ViewportCount is 4, including "
            + "the offscreen thumbnail). Viewport.Mode is a public field so the park can bypass "
            + "SetCameraMode's TimedAlert. Unfollow(changeControl:false) preserves PositionEcl; the "
            + "default true would null Program.ControlledVehicle.")]
    internal CommandResult Take()
    {
        if (_phase == Phase.Releasing)
        {
            _phase = Phase.Owned;
            _smoother.Reset();
            return CommandResult.Ok;
        }

        if (_phase == Phase.Owned)
            return CommandResult.Ok;

        var viewport = Program.MainViewport;
        var active = viewport.GetCamera();

        _restoreCamera = active;
        _restoreMode = viewport.Mode;
        _restoreFollowing = active.Following;
        _restoreTidal = active.TidalLocking;
        _restoreNoRotation = active.NoRotation;
        _restoreLocalPosition = active.LocalPosition;
        _restoreLocalRotation = active.LocalRotation;
        _restoreFovDeg = active.GetFieldOfView() * RadToDeg; // getter is RADIANS, setter is DEGREES
        _restoreOrtho = active.Orthographic;
        _restorePositionEcl = active.PositionEcl;

        ParkMode(viewport);

        var owned = viewport.BaseCamera;
        owned.Unfollow(changeControl: false);
        owned.NoRotation = false; // insurance: a stuck map flag would re-interpret every write below
        owned.PositionEcl = _restorePositionEcl;
        owned.LocalRotation = _restoreLocalRotation;

        store.State.SetBaseline(new CameraPose
        {
            Position = new Vec3(_restorePositionEcl.X, _restorePositionEcl.Y, _restorePositionEcl.Z),
            PositionIsGeo = false,
            Frame = FrameKind.Ecl,
            Anchor = TargetRef.None,
            Rotation = ToQuat(_restoreLocalRotation),
            AimTarget = TargetRef.None,
            AimOffset = Vec3.Zero,
            AimFrame = FrameKind.BodyFixed,
            AimUp = AimUpKind.World,
            Roll = 0,
            Fov = _restoreFovDeg,
            Ortho = _restoreOrtho,
            OrthoHeight = CameraPose.Default.OrthoHeight,
            Smoothing = 0,
            OrbitRadius = 0,
            OrbitAzimuth = 0,
            OrbitElevation = 0,
            TimeScale = 1,
        });

        _lastPositionEcl = _restorePositionEcl;
        _lastResolvedPositionEcl = _restorePositionEcl;
        _lastRotation = _restoreLocalRotation;
        _appliedFovDeg = double.NaN;
        _degradeReason = "";
        _smoother.Reset();
        _phase = Phase.Owned;
        _publishedIdle = false;
        return CommandResult.Ok;
    }

    /// <summary>
    ///     <c>camera/enabled 0</c>: hands the camera back over <c>camera_release_blend_s</c> so the
    ///     hand-off is a move rather than a cut. A configured blend of zero (or an unresolvable restore
    ///     pose) collapses to the immediate restore. Idempotent.
    /// </summary>
    internal CommandResult Release()
    {
        if (_phase == Phase.Idle)
            return CommandResult.Ok;
        if (_phase == Phase.Releasing)
            return CommandResult.Ok;
        if (!(releaseBlendSeconds > 0))
            return Restore();

        _blendDuration = releaseBlendSeconds;
        _blendElapsed = 0;
        _blendStartPosition = _lastPositionEcl;
        _blendStartRotation = _lastRotation;
        _blendStartFovDeg = double.IsNaN(_appliedFovDeg) ? _restoreFovDeg : _appliedFovDeg;
        _phase = Phase.Releasing;
        return CommandResult.Ok;
    }

    /// <summary>
    ///     <c>camera/release</c> and the unload teardown: the hard cut back to game control, with no
    ///     blend. This is the "give it back <i>now</i>" verb — the panic button and the teardown path —
    ///     which is why it does not ease; <c>camera/enabled 0</c> is the one that does.
    /// </summary>
    /// <remarks>
    ///     <b>Order matters.</b> <c>SetFollow</c> <i>teleports</i> the camera to
    ///     <c>target + 2.5 × MeanRadius × forward</c>, so the follow target is restored <b>first</b> and
    ///     the captured transform written over it afterwards. <c>NoRotation</c> goes before the
    ///     transform because it changes how <c>LocalPosition</c> is interpreted, and the viewport mode
    ///     goes last so no controller's <c>OnSwitchOn</c> can re-run against a half-restored camera.
    ///     Safe to call at any time and idempotent; a follow target that despawned while gatOS held the
    ///     camera degrades to no-follow rather than throwing.
    /// </remarks>
    [KsaAnchor("Camera.{SetFollow,Unfollow,LocalPosition,LocalRotation,NoRotation,SetFieldOfView,"
            + "SetOrthographic}; Viewport.{Mode,SetCameraMode}",
        SourceFile = "KSA/Camera.cs / KSA/Viewport.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "SetFollow(target, tidal, changeControl:false, alert:false) — the defaults would take "
            + "the player's vessel and print 'Following X' on screen. Restoring INTO Map goes through "
            + "SetCameraMode so MapController.OnSwitchOn re-establishes NoRotation and the map's own "
            + "control state; every other mode is a direct field assignment (no alert). The orthographic "
            + "HALF-HEIGHT is not restored: Camera has no public getter for it (see ApplyProjection).")]
    internal CommandResult Restore()
    {
        if (_phase == Phase.Idle)
            return CommandResult.Ok;

        _phase = Phase.Idle;
        try
        {
            var viewport = Program.MainViewport;
            var camera = _restoreCamera ?? viewport.BaseCamera;

            if (CameraTargets.IsLive(_restoreFollowing) && _restoreFollowing is { } following)
                camera.SetFollow(following, _restoreTidal, changeControl: false, alert: false);
            else
                camera.Unfollow(changeControl: false);

            camera.NoRotation = _restoreNoRotation;
            camera.LocalPosition = _restoreLocalPosition;
            camera.LocalRotation = _restoreLocalRotation;
            camera.SetFieldOfView((float)_restoreFovDeg);
            camera.SetOrthographic(_restoreOrtho);

            if (_restoreMode == CameraMode.Map)
                viewport.SetCameraMode(CameraMode.Map);
            else
                viewport.Mode = _restoreMode;
        }
        finally
        {
            _restoreCamera = null;
            _restoreFollowing = null;
            _appliedFovDeg = double.NaN;
            _degradeReason = "";
            store.State.ClearAll();
            store.PublishStatus(CameraStatus.Idle);
            _publishedIdle = true;
            _smoother.Reset();
        }

        return CommandResult.Ok;
    }

    /// <summary>Unload teardown: gives the camera back and drops every uploaded track.</summary>
    internal void Shutdown()
    {
        Restore();
        store.Clear();
    }

    // ================================================================================================
    //  Per-frame driver
    // ================================================================================================

    /// <summary>
    ///     The per-frame drive, called from <c>Mod.OnAfterFrame</c> — the one hook that fires on
    ///     <b>every</b> rendered frame. (The two StarMap GUI hooks live inside
    ///     <c>Program.OnFrame</c>'s <c>if (DrawUI)</c> block, and hiding the UI with F2 is the first
    ///     thing a director does, so a camera driven from those would freeze the moment the shot got
    ///     clean.)
    /// </summary>
    /// <param name="dt">The frame's player-clock delta, in seconds.</param>
    internal void Update(double dt)
    {
        if (_phase == Phase.Idle)
        {
            if (_publishedIdle)
                return;
            store.PublishStatus(CameraStatus.Idle);
            _publishedIdle = true;
            return;
        }

        var viewport = Program.MainViewport;
        var camera = viewport.BaseCamera;

        // Re-assert the park. A camera hotkey (or a script) can move the viewport out of Fixed or give
        // the camera a follow target behind our back, which would wake FixedController.OnFrame and make
        // two writers fight over one transform every frame. While gatOS owns the camera it is the sole
        // writer, full stop — release it to get the player's camera controls back.
        if (viewport.Mode != CameraMode.Fixed)
            ParkMode(viewport);
        if (camera.Following is not null)
            camera.Unfollow(changeControl: false);

        // ---- THE C3 SEAM -------------------------------------------------------------------------
        // The track evaluator will pass its evaluated sample and the channel mask the active shot
        // declares in here; the compositor's Track ?? Override ?? Baseline precedence then does the
        // rest, with no other change to this driver. Until C3 lands nothing claims anything, so every
        // channel resolves to a live override or to the baseline captured at ownership take.
        const CameraChannelMask trackClaims = CameraChannelMask.None;
        var pose = store.State.Compose(trackSample: null, trackClaims);
        CameraTargets.TryResolve(pose.Anchor, out var anchor);

        if (_phase == Phase.Releasing)
        {
            StepRelease(camera, dt);
            if (_phase == Phase.Idle)
                return; // Restore() already published the idle status
        }
        else
        {
            Apply(camera, pose, anchor, trackClaims, dt);
        }

        store.PublishStatus(CameraReader.Sample(viewport, camera, owned: true, pose, anchor,
            _lastResolvedPositionEcl));
        _publishedIdle = false;
    }

    /// <summary>
    ///     Writes one composed pose onto the camera: resolve the placement, resolve the orientation,
    ///     run both through the critically-damped smoother, then set the projection.
    /// </summary>
    /// <remarks>
    ///     An unresolvable placement or aim <b>holds the last good value</b> rather than moving the
    ///     camera somewhere arbitrary or letting a NaN reach the view matrix — a vessel that despawns
    ///     mid-shot leaves the camera exactly where it was, which is the least surprising failure and
    ///     the only one that can be recovered from by writing a new target.
    /// </remarks>
    [KsaAnchor("Camera.{PositionEcl,LocalRotation,LookAtRotation,SetFieldOfView,SetOrthographic,"
            + "SetOrthoHalfHeight}",
        SourceFile = "KSA/Camera.cs", Verified = "2026-08-06", GameVersion = "2026.8.5.5168",
        Risk = ChurnRisk.Medium,
        Notes = "LocalRotation (a Transform3D public field) is what Camera.OnFrame builds the view "
            + "matrix from — Ego2View is LocalRotation.Inverse(), NOT WorldRotation — so the pose is "
            + "written there. LookAtRotation returns NaN for a zero forward vector, hence the guard. "
            + "SetFieldOfView takes DEGREES and does NOT clamp (which is what makes fisheye/telephoto "
            + "reachable); it rebuilds and inverts the projection matrix on every call, so it is only "
            + "called when the value actually changed.")]
    private void Apply(KsaCamera camera, in CameraPose pose, in CameraTarget anchor,
        CameraChannelMask trackClaims, double dt)
    {
        // ---- placement -----------------------------------------------------------------------------
        if (CameraFrames.TryResolvePosition(pose, anchor, out var targetPositionEcl, out var error))
        {
            _lastResolvedPositionEcl = targetPositionEcl;
            _degradeReason = "";
        }
        else
        {
            LogDegradeOnce(error);
            targetPositionEcl = _lastResolvedPositionEcl;
        }

        var smoothed = _smoother.Step(ToVec(_lastPositionEcl), ToVec(targetPositionEcl),
            pose.Smoothing, dt);
        _lastPositionEcl = ToKsa(smoothed);
        camera.PositionEcl = _lastPositionEcl;

        // ---- orientation ---------------------------------------------------------------------------
        var targetRotation = ResolveRotation(pose, anchor, _lastPositionEcl);
        var smoothedRotation = _smoother.Step(ToQuat(_lastRotation), ToQuat(targetRotation),
            pose.Smoothing, dt);
        _lastRotation = ToKsaQuat(smoothedRotation);
        camera.LocalRotation = _lastRotation;

        ApplyProjection(camera, pose, trackClaims);
    }

    /// <summary>
    ///     Resolves the camera's ECL orientation: an aim target if one is set (target + offset in the
    ///     aim frame, with the chosen "up" and then roll), otherwise the explicit <c>pose/rotation</c>
    ///     quaternion. Falls back to the previous frame's rotation on anything degenerate.
    /// </summary>
    /// <remarks>
    ///     Roll is applied <b>only</b> on the aim path, exactly as the surface documents it ("degrees,
    ///     applied after aim"): an explicit <c>pose/rotation</c> already names a complete orientation,
    ///     and quietly rolling it further would make the two channels fight over one value.
    /// </remarks>
    private doubleQuat ResolveRotation(in CameraPose pose, in CameraTarget anchor, double3 positionEcl)
    {
        if (!pose.AimTarget.HasTarget)
        {
            var explicitRotation = doubleQuat.NormalizeOrZero(ToKsaQuat(pose.Rotation));
            return explicitRotation == default ? _lastRotation : explicitRotation;
        }

        if (!CameraTargets.TryResolve(pose.AimTarget, out var aim))
        {
            LogDegradeOnce($"aim target '{pose.AimTarget}' is gone");
            return _lastRotation;
        }

        var aimPointEcl = CameraTargets.PositionEcl(aim);
        if (pose.AimOffset != Vec3.Zero)
        {
            // The offset resolves in the aim target's own live frame every frame — which is what makes
            // "+0.9 m on a kittenaut's Y axis" stay its head as it walks and turns.
            if (CameraFrames.TryFrame2Ecl(pose.AimFrame, aim, pose.Latitude, pose.Longitude,
                    out var aimFrame2Ecl, out var aimError))
                aimPointEcl += ToKsa(pose.AimOffset).Transform(aimFrame2Ecl);
            else
                LogDegradeOnce($"aim offset frame: {aimError}");
        }

        var forward = aimPointEcl - positionEcl;
        if (forward.IsNearlyZero() || !forward.IsFinite())
            return _lastRotation; // the camera is sitting on its subject: no direction to look in

        var up = ResolveUp(pose, anchor, aim, forward);
        var rotation = KsaCamera.LookAtRotation(forward, up);
        if (!IsFinite(rotation))
            return _lastRotation;

        if (pose.Roll is not 0.0 && double.IsFinite(pose.Roll))
        {
            // Roll about the camera's own view axis (view-space Z), applied before the view→ECL map, so
            // a positive value rolls the camera clockwise and the horizon tilts counter-clockwise.
            var roll = doubleQuat.CreateFromAxisAngle(double3.UnitZ, -pose.Roll * (Math.PI / 180.0));
            rotation = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(roll, rotation));
            if (rotation == default)
                return _lastRotation;
        }

        return rotation;
    }

    /// <summary>
    ///     The "up" reference for the aim look-at, per <c>pose/aim_up</c>. Every mode degrades to world
    ///     up rather than failing, because an unusable up vector would make <c>LookAtRotation</c> emit
    ///     NaN — and a shot with the wrong roll is recoverable while a NaN view matrix is not.
    /// </summary>
    private double3 ResolveUp(in CameraPose pose, in CameraTarget anchor, in CameraTarget aim,
        double3 forward)
    {
        var up = pose.AimUp switch
        {
            // The subject's own up axis: the shot rolls with the vessel or with the planet's pole.
            AimUpKind.Target => CameraTargets.UpEcl(aim),

            // "Along the track": the anchor's velocity when there is one, else the subject's — the
            // anchor is what the frame vocabulary calls the thing the shot is built around.
            AimUpKind.Velocity => anchor.Found
                ? CameraTargets.VelocityEcl(anchor)
                : CameraTargets.VelocityEcl(aim),

            // No up constraint: carry the camera's current up forward (parallel transport), so tracking
            // a subject never snaps the horizon back to level and roll is whatever pose/roll says.
            AimUpKind.Free => double3.UnitY.Transform(_lastRotation),

            _ => double3.UnitZ, // world up — the ecliptic pole, the stable default
        };

        // A degenerate or collinear up makes the look-at basis singular; fall back rather than emit NaN
        // into the view matrix. World up first, then the ecliptic X axis for the one case world up
        // cannot cover — looking straight along the pole.
        var direction = forward.Normalized();
        if (!Usable(direction, up))
            up = double3.UnitZ;
        if (!Usable(direction, up))
            up = double3.UnitX;
        return up;
    }

    /// <summary>Whether an up vector yields a non-singular look-at basis for this view direction.</summary>
    private static bool Usable(double3 direction, double3 up)
        => up.IsFinite() && !up.IsNearlyZero()
           && !double3.Cross(direction, up.Normalized()).IsNearlyZero();

    /// <summary>
    ///     Writes the projection channels. Field of view and the orthographic toggle are always applied
    ///     (the captured baseline makes that a no-op until something is written), but the orthographic
    ///     <b>half-height is applied only when its channel is explicitly claimed</b>.
    /// </summary>
    /// <remarks>
    ///     That asymmetry is forced: <c>Camera</c> exposes <c>SetOrthoHalfHeight</c> but <i>no</i>
    ///     getter for it in 5168, so there is nothing to capture at ownership take and therefore nothing
    ///     to put back at release. Writing the composed default every frame would silently clobber a
    ///     value we could never restore; writing it only on request means gatOS changes it exactly when
    ///     a guest asked it to — and that one change is, honestly, not restorable.
    /// </remarks>
    private void ApplyProjection(KsaCamera camera, in CameraPose pose, CameraChannelMask trackClaims)
    {
        if (double.IsFinite(pose.Fov) && pose.Fov != _appliedFovDeg)
        {
            camera.SetFieldOfView((float)pose.Fov);
            _appliedFovDeg = pose.Fov;
        }

        camera.SetOrthographic(pose.Ortho); // self-gating: no-ops unless the flag actually changed

        // "Explicitly claimed" means a live leaf override or a channel the active shot declares, so a
        // C3 track that animates ortho_height without anyone having written the leaf still drives it.
        var claimed = store.State.HasOverride(CameraChannel.OrthoHeight)
                      || trackClaims.Has(CameraChannel.OrthoHeight);
        if (claimed && double.IsFinite(pose.OrthoHeight))
            camera.SetOrthoHalfHeight((float)pose.OrthoHeight);
    }

    /// <summary>
    ///     One frame of the eased hand-back: interpolate from the pose gatOS was holding toward the
    ///     captured restore pose, then perform the real restore on the completing frame.
    /// </summary>
    /// <remarks>
    ///     The restore point is recomputed <i>every</i> frame of the blend rather than baked at its
    ///     start, so blending back onto a moving follow target lands on the target and not on where it
    ///     used to be. If that target despawned mid-blend, the captured absolute ecliptic point is used
    ///     instead — the blend still completes, it just ends somewhere fixed.
    /// </remarks>
    private void StepRelease(KsaCamera camera, double dt)
    {
        _blendElapsed += double.IsFinite(dt) && dt > 0 ? dt : 0;
        var progress = _blendDuration > 0 ? Math.Clamp(_blendElapsed / _blendDuration, 0, 1) : 1;
        var eased = Easing.Apply(progress, EaseSpec.Named(EaseKind.InOut));

        var targetPosition = RestorePositionEcl();
        var position = Vec3.Lerp(ToVec(_blendStartPosition), ToVec(targetPosition), eased);
        var rotation = Splines.Slerp(ToQuat(_blendStartRotation), ToQuat(_restoreLocalRotation), eased);
        var fov = _blendStartFovDeg + (_restoreFovDeg - _blendStartFovDeg) * eased;

        _lastPositionEcl = ToKsa(position);
        _lastRotation = ToKsaQuat(rotation);
        _lastResolvedPositionEcl = _lastPositionEcl;
        camera.PositionEcl = _lastPositionEcl;
        camera.LocalRotation = _lastRotation;
        if (double.IsFinite(fov) && fov != _appliedFovDeg)
        {
            camera.SetFieldOfView((float)fov);
            _appliedFovDeg = fov;
        }

        if (progress >= 1)
            Restore();
    }

    /// <summary>
    ///     Where the captured pose lives <i>right now</i> in absolute ecliptic metres: the follow
    ///     target's live position plus the captured body-fixed offset, reproducing
    ///     <c>Camera.PositionCce</c>'s own composition (which transforms through
    ///     <c>GetBodyFixed2Ecl()</c> unless <c>NoRotation</c> is set). Falls back to the absolute point
    ///     captured at ownership take when there is no live follow target.
    /// </summary>
    [KsaAnchor("Camera.PositionCce composition (LocalPosition ⇄ IFollowable.GetBodyFixed2Ecl unless "
            + "NoRotation); IPosition.GetPositionEcl()",
        SourceFile = "KSA/Camera.cs", Verified = "2026-08-06", GameVersion = "2026.8.5.5168",
        Risk = ChurnRisk.Medium,
        Notes = "Reproduced rather than called because the camera we are blending is unfollowed at this "
            + "point, so its own PositionCce would not use the captured target.")]
    private double3 RestorePositionEcl()
    {
        if (!CameraTargets.IsLive(_restoreFollowing) || _restoreFollowing is not { } following)
            return _restorePositionEcl;
        var offset = _restoreNoRotation
            ? _restoreLocalPosition
            : _restoreLocalPosition.Transform(following.GetBodyFixed2Ecl());
        var resolved = following.GetPositionEcl() + offset;
        return resolved.IsFinite() ? resolved : _restorePositionEcl;
    }

    /// <summary>
    ///     Parks the viewport in <c>Fixed</c>. Direct field assignment (no <c>TimedAlert</c>) except
    ///     when leaving Map, which must run <c>MapController.OnSwitchOff</c> — see the type remarks.
    /// </summary>
    private static void ParkMode(Viewport viewport)
    {
        if (viewport.Mode == CameraMode.Map)
            viewport.SetCameraMode(CameraMode.Fixed);
        else
            viewport.Mode = CameraMode.Fixed;
    }

    // ================================================================================================
    //  Live game-camera controls (they act whether or not gatOS owns the camera)
    // ================================================================================================

    /// <summary>
    ///     <c>camera/mode</c>: switches the main viewport's camera mode through the game's own
    ///     <c>SetCameraMode</c>, so each controller's switch-on/off bookkeeping runs (the alert is the
    ///     player's own doing here — they asked for a mode change). Refused while gatOS owns the camera,
    ///     because ownership <i>is</i> a mode park: honouring it would immediately be undone by the
    ///     per-frame re-assert.
    /// </summary>
    [KsaAnchor("Program.MainViewport; Viewport.SetCameraMode(CameraMode)",
        SourceFile = "KSA/Program.cs / KSA/Viewport.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "SetCameraMode also calls Program.ControlledVehicle?.ClearHeldPlayerInput(), which "
            + "drops any latched ctl/translate + ctl/rotate flags (SPEC §3.4.19).")]
    internal CommandResult SetMode(CameraModeKind mode)
    {
        if (_phase != Phase.Idle)
            return new CommandResult(CommandOutcome.Unsupported,
                "gatOS owns the camera (it is parked in 'fixed'); write 0 to camera/enabled first");
        Program.MainViewport.SetCameraMode(CameraReader.ModeOf(mode));
        return CommandResult.Ok;
    }

    /// <summary>
    ///     <c>camera/follow</c>: points the game camera at a vessel or body — <b>on both of the
    ///     viewport's cameras</b>, which is the fix for the latent desync described below. Refused while
    ///     gatOS owns the camera: a follow target would wake <c>FixedController.OnFrame</c> and give the
    ///     transform a second writer.
    /// </summary>
    /// <remarks>
    ///     A viewport carries two camera instances — <c>BaseCamera</c> and <c>MapCamera</c> — and
    ///     <c>GetCamera()</c> returns whichever the current mode uses. The game's own follow action sets
    ///     the target on <b>both</b> (<c>KSA/InputEvents.cs:759-760</c>); setting only one leaves the map
    ///     view pointing at the previous target, which is exactly what gatOS's original
    ///     <c>camera.focus</c> did.
    /// </remarks>
    [KsaAnchor("Program.MainViewport.{BaseCamera,MapCamera}; Camera.{SetFollow,Unfollow}",
        SourceFile = "KSA/Viewport.cs / KSA/Camera.cs / KSA/InputEvents.cs", Verified = "2026-08-06",
        GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "SetFollow teleports to target + 2.5×MeanRadius×forward — kept, because that is what "
            + "the game's own follow does and what a viewer expects from 'go look at this'.")]
    internal CommandResult SetFollow(in TargetRef reference)
    {
        if (_phase != Phase.Idle)
            return new CommandResult(CommandOutcome.Unsupported,
                "gatOS owns the camera and has parked the game's follow; use pose/anchor + "
                + "pose/aim_target, or write 0 to camera/enabled first");

        var viewport = Program.MainViewport;
        if (!reference.HasTarget)
        {
            viewport.BaseCamera.Unfollow(changeControl: false);
            viewport.MapCamera.Unfollow(changeControl: false);
            return CommandResult.Ok;
        }

        if (!CameraTargets.TryResolve(reference, out var target) || target.Followable is not { } followable)
            return new CommandResult(CommandOutcome.NotFound, $"'{reference}' is gone");

        // Changing the target keeps whatever tidal-locking flag is already in force — camera/tidal is
        // the leaf that changes it, and silently re-defaulting it here would make one control quietly
        // reset another.
        var tidal = viewport.BaseCamera.TidalLocking;
        viewport.MapCamera.SetFollow(followable, tidal, changeControl: false, alert: false);
        viewport.BaseCamera.SetFollow(followable, tidal, changeControl: false, alert: false);
        return CommandResult.Ok;
    }

    /// <summary>
    ///     <c>camera/tidal</c>: retunes the tidal-locking flag of the <i>existing</i> follow.
    ///     <c>TidalLocking</c> has no setter, so this re-issues <c>SetFollow</c> with the same target —
    ///     and restores each camera's position afterwards, because a flag change must not fling the view
    ///     the way <c>SetFollow</c>'s teleport otherwise would.
    /// </summary>
    [KsaAnchor("Camera.{Following,TidalLocking,SetFollow,PositionEcl}", SourceFile = "KSA/Camera.cs",
        Verified = "2026-08-06", GameVersion = "2026.8.5.5168", Risk = ChurnRisk.Medium,
        Notes = "TidalLocking is get-only (`=> _tidalLocking`); SetFollow is the only writer. Its "
            + "unconditional teleport is undone here by re-asserting the captured PositionEcl.")]
    internal CommandResult SetTidal(bool tidal)
    {
        if (_phase != Phase.Idle)
            return new CommandResult(CommandOutcome.Unsupported,
                "gatOS owns the camera and has parked the game's follow; write 0 to camera/enabled first");

        var viewport = Program.MainViewport;
        var changed = false;
        changed |= RetuneTidal(viewport.BaseCamera, tidal);
        changed |= RetuneTidal(viewport.MapCamera, tidal);
        return changed
            ? CommandResult.Ok
            : new CommandResult(CommandOutcome.NotFound, "the camera is not following anything");
    }

    private static bool RetuneTidal(KsaCamera camera, bool tidal)
    {
        if (camera.Following is not { } following)
            return false;
        var positionEcl = camera.PositionEcl;
        camera.SetFollow(following, tidal, changeControl: false, alert: false);
        camera.PositionEcl = positionEcl; // undo SetFollow's teleport: this is a flag change, not a move
        return true;
    }

    // ================================================================================================
    //  Housekeeping
    // ================================================================================================

    /// <summary>
    ///     <c>camera/pose/reset</c>: drops every live override so each channel falls back to the
    ///     baseline captured at ownership take (an active track, once C3 lands, keeps driving — a reset
    ///     is about <i>your</i> writes, not about stopping playback). The smoother is reset with them:
    ///     a reset is a hard cut, and carrying the previous setpoint's momentum into it would sail the
    ///     camera past the baseline before settling back onto it.
    /// </summary>
    internal void ResetPose()
    {
        store.State.ClearOverrides();
        _smoother.Reset();
    }

    /// <summary>
    ///     Drops a captured follow target whose vessel has despawned, so the release path unfollows
    ///     instead of re-attaching the game camera to a dead object. Rides the telemetry sampler's
    ///     vehicle enumeration (the <c>VesselForceRender.Prune</c> pattern) and self-gates to a single
    ///     branch while the director is idle. Game thread only.
    /// </summary>
    internal void Prune(IReadOnlyList<VesselSnapshot> live)
    {
        if (_phase == Phase.Idle || _restoreFollowing is not Vehicle vehicle)
            return;

        foreach (var vessel in live)
            if (vessel.Id == vehicle.Id)
                return;

        _restoreFollowing = null;
        ModLog.Log.Info($"gatOS camera: the captured follow target '{vehicle.Id}' despawned; "
                        + "the camera will be released unfollowed.");
    }

    /// <summary>Takes the pending <c>camera.*</c> events for the telemetry sampler to fold in.</summary>
    internal IReadOnlyList<SimEvent> DrainEvents() => store.DrainEvents();

    /// <summary>
    ///     Logs a per-frame degradation once per distinct reason. A shot whose anchor despawns would
    ///     otherwise write the same line 60 times a second, which is worse than silence.
    /// </summary>
    private void LogDegradeOnce(string reason)
    {
        if (reason.Length == 0 || reason == _degradeReason)
            return;
        _degradeReason = reason;
        ModLog.Log.Debug($"gatOS camera: holding the last pose — {reason}");
    }

    // ---- numeric seams between the game types and the game-free camera maths ------------------------

    private static Vec3 ToVec(double3 v) => new(v.X, v.Y, v.Z);

    private static double3 ToKsa(Vec3 v) => new(v.X, v.Y, v.Z);

    private static Quat ToQuat(doubleQuat q) => new(q.X, q.Y, q.Z, q.W);

    private static doubleQuat ToKsaQuat(Quat q) => new(q.X, q.Y, q.Z, q.W);

    private static bool IsFinite(doubleQuat q)
        => double.IsFinite(q.X) && double.IsFinite(q.Y) && double.IsFinite(q.Z) && double.IsFinite(q.W);
}
