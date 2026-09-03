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
///     camera, drives it through the engine's own follow-and-controller machinery, publishes what it is
///     doing, and hands the camera back to the game exactly as it found it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Methodology (the KSArmory pattern — 2026-08-11 re-implementation).</b> Ownership is not
///         "unfollow and write absolute transforms" any more; that pattern left the camera's per-frame
///         bookkeeping (<c>NearbyCelestial</c>, <c>CurrentAltitudeKm</c> — the inputs to
///         <c>ClampCamera</c>'s surface teleport) stale and its poses one phase out of register with
///         the scene. Instead:
///     </para>
///     <list type="number">
///         <item><b>The camera follows a mod-supplied <c>IFollowable</c></b> (<see cref="CameraFollowable"/>)
///             that re-resolves the live anchor in the engine's own epoch, so the engine's follow
///             machinery keeps every derived quantity coherent.</item>
///         <item><b>A <c>FixedController</c> subclass</b> (<see cref="CameraPoseController"/>) is
///             installed into <c>GameViewport.FixedController</c> through <see cref="ViewportSeam"/>; its
///             <c>OnFrame</c> runs inside the engine's viewport pass and asks this director for the pose
///             at the exact instant the answer is used (<see cref="TryPose"/>).</item>
///         <item><b>Every placement is an offset from the followed anchor</b>, measured against the very
///             sample the engine adds it to. Two absolute positions taken in different frame phases are
///             never differenced — that discrepancy is ~600 m per 20 ms frame at orbital speed, and it
///             is the whole reason the previous implementation could not hold a subject.</item>
///     </list>
///     <para>
///         <b>Division of labour per frame.</b> The viewport <i>prefix</i> (via <c>Mod.DriveCamera</c> →
///         <see cref="Update"/>) drains state that must precede the pass: track evaluation, channel
///         composition, pointing the followable at this frame's anchor, ownership-loss detection, status
///         publishing. The <i>in-phase</i> half (<see cref="TryPose"/>, called back by the controller
///         inside <c>GameViewport.OnFrame</c>) resolves the placement, aim and projection against the
///         engine's fresh anchor sample and returns the final offset + rotation for the controller to
///         write. The Harmony viewport prefix/postfix remain for scheduling lockstep and applied-status
///         publishing; they no longer write the camera.
///     </para>
///     <para>
///         <b>Ownership loss is a stand-down, not a fight.</b> A camera mode change gatOS did not make,
///         a follow target swapped behind its back (vessel destruction → wreckage follow, another mod),
///         or a faulted pose solve all mean someone else has the camera now. The director stops driving,
///         publishes idle, and leaves the camera exactly where the reclaimer put it — re-asserting a
///         park every frame is two writers fighting over one transform, which is the failure mode this
///         design retires. <c>camera/enabled</c> reads <c>0</c> afterwards; take it again to resume.
///     </para>
///     <para>
///         <b>Silent-footage guarantees are kept:</b> the pose controller suppresses the stock
///         "Fixed Camera" alert only while gatOS owns the park, and every <c>SetFollow</c>/<c>Unfollow</c>
///         passes <c>alert: false</c> and <c>changeControl: false</c> (the defaults would print on
///         screen and null <c>Program.ControlledVehicle</c>).
///     </para>
///     <para>
///         <b>⚠ Owning the main camera changes which way a kittenaut walks.</b>
///         <c>KittenEva.PrepareWorker</c> feeds the main camera's forward/right/up into EVA locomotion,
///         so while gatOS holds the camera, "forward" for a kittenaut on EVA is wherever the shot is
///         facing. Documented rather than worked around.
///     </para>
///     <para>
///         <b>Threading:</b> game thread only (threading rule 1). Transport threads only enqueue
///         <c>SimCommand</c>s and read the volatile <c>CameraStatus</c> this class publishes.
///     </para>
/// </remarks>
/// <param name="store">The game-free camera store: tracks, the compositor, the status, the events.</param>
/// <param name="releaseBlendSeconds"><c>[camera] camera_release_blend_s</c> — the eased hand-back.</param>
/// <param name="playback">
///     The track player (task C3), or null when <c>[schedule] schedule_enabled=false</c> left the
///     registry it lives in unwired. Every L1/L2 channel still works in that configuration; only
///     <c>camera/play</c> is unavailable.
/// </param>
/// <param name="debugNamespace"><c>[control] debug_namespace</c> — the first gate on the time channel.</param>
/// <param name="allowTimeChannel"><c>[camera] camera_allow_time_channel</c> — the second gate.</param>
internal sealed class CameraDirector(
    CameraStore store,
    double releaseBlendSeconds,
    CameraPlaybackController? playback = null,
    bool debugNamespace = false,
    bool allowTimeChannel = false) : ICameraPoseSource
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

    private readonly AnchoredPositionSmoother _positionSmoother = new();
    private readonly PoseSmoother _rotationSmoother = new();
    private readonly CameraFollowable _followable = new();

    private Phase _phase = Phase.Idle;
    private bool _publishedIdle = true; // the store starts at CameraStatus.Idle

    // ---- the in-phase seam -------------------------------------------------------------------------
    private CameraPoseController? _controller;
    private FixedController? _stockFixedController;

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
    private double3 _restoreFixedOffset;
    private double3 _restoreFixedRotation;

    // ---- per-frame carry ---------------------------------------------------------------------------
    private double3 _lastPositionEcl;
    private doubleQuat _lastRotation = doubleQuat.Identity;
    private double3 _lastResolvedPositionEcl;
    private double _appliedFovDeg = double.NaN;
    private string _degradeReason = "";
    private CameraPose _statusPose = CameraPose.Default;
    private CameraTarget _statusAnchor;
    private CameraChannelMask _frameClaims;
    private bool _frameValid;

    // ---- the interpolated time channel (task C4) ---------------------------------------------------
    // The simulation speed is captured LAZILY, the first time a shot actually drives the channel, and
    // restored only if it was captured: a director that never touches time must not stomp the warp
    // setting the player left running.
    private bool _timeCaptured;
    private double _restoreSimSpeed = 1;
    private double _appliedTimeScale = double.NaN;
    private bool _timeWarned;

    // ---- release blend -----------------------------------------------------------------------------
    private double _blendElapsed;
    private double _blendDuration;
    private double3 _blendStartPosition;
    private doubleQuat _blendStartRotation = doubleQuat.Identity;
    private double _blendStartFovDeg = 60;
    private bool _blendComplete;

    /// <summary>The game-free store this director drives (tracks, compositor, status, events).</summary>
    internal CameraStore Store => store;

    /// <summary>
    ///     The game-free <c>camera.play</c>/<c>set</c>/<c>stop</c> executor and track sampler, or null
    ///     when scheduling is disabled. The actuator routes those three verbs straight to it rather than
    ///     re-implementing them — nothing about playing a track touches KSA.
    /// </summary>
    internal CameraPlaybackController? Playback => playback;

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
    ///             Map mode, so the capture must not assume the base camera — including the stock fixed
    ///             controller's own input fields, which a restore into Fixed mode replays;</item>
    ///         <item>install the pose controller into <c>GameViewport.FixedController</c> (stock instance
    ///             saved for shutdown) and seed its <c>CameraRotation</c> from the capture <i>before</i>
    ///             the mode can reach Fixed — a frame drawn by the stock controller with a zero rotation
    ///             is a process crash (<c>double3.Normalized</c> throws on zero);</item>
    ///         <item>follow the mod anchor (<see cref="CameraFollowable"/>, held at the captured point),
    ///             then write the captured transform back over <c>SetFollow</c>'s teleport;</item>
    ///         <item>park the mode (see <see cref="ParkMode"/> for why Map is special);</item>
    ///         <item>seed the compositor's <b>baseline</b> from the captured values, so an
    ///             owned-but-unwritten camera sits exactly where the game left it.</item>
    ///     </list>
    /// </remarks>
    [KsaAnchor("Program.MainViewport (IGameViewport); IGameViewport.{Mode,GetCamera,SetCameraMode,BaseCamera,FixedController}; GameViewport.FixedController setter via ViewportSeam; ViewportBase.Mode setter via ViewportSeam; "
            + "Camera.{PositionEcl,LocalPosition,LocalRotation,NoRotation,Following,TidalLocking,"
            + "GetFieldOfView,Orthographic,SetFollow}",
        SourceFile = "KSA/Program.cs / KSA/GameViewport.cs / KSA/ViewportBase.cs / KSA/Camera.cs / KSA/FixedController.cs",
        Verified = "2026-09-02", GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "Program.MainViewport (NOT Program.GetCamera()/GetCameraMode(), which read the FRAME "
            + "viewport — and never a viewport by index). SetFollow teleports to target + 2.5 × "
            + "MeanRadius × forward (2.5 m for the 1 m followable), undone by re-writing the captured "
            + "transform; changeControl:false because the default would null Program.ControlledVehicle. "
            + "The camera is NOT unfollowed any more: following the mod anchor is what keeps the "
            + "engine's per-camera bookkeeping (NearbyCelestial, CurrentAltitudeKm → ClampCamera) live."
            + "5402: the controller install and the silent Fixed park both go through ViewportSeam (reflection on the protected setters); a seam miss makes camera/enabled answer EOPNOTSUPP instead of crashing. Camera.ClampCamera changed: it is now camera-local and terrain-aware (Program.FindNearbyCelestial(this) + TryGetSurfaceClampPositionEcl(0.5) clamps when the camera is within 0.5 m of MeanRadius + terrain height along its own direction) instead of gating on the FRAME viewport's CurrentAltitudeKm — so an owned camera placed below terrain is lifted to 0.5 m above it regardless of which viewport is being framed. Following the mod anchor is still what keeps NearbyCelestial bookkeeping live.")]
    internal CommandResult Take()
    {
        if (_phase == Phase.Releasing)
        {
            _phase = Phase.Owned;
            _blendComplete = false;
            ResetSmoothing();
            return CommandResult.Ok;
        }

        if (_phase == Phase.Owned)
            return CommandResult.Ok;

        var viewport = Program.MainViewport;
        var active = viewport.GetCamera();

        var controller = EnsureController(viewport);
        if (controller is null)
            return new CommandResult(CommandOutcome.Unsupported,
                "the fixed-camera controller seam could not be installed");

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
        _restoreFixedOffset = controller.CameraOffset;
        _restoreFixedRotation = controller.CameraRotation;

        // Crash guard before any path can run the stock OnFrame: a zero CameraRotation is the
        // documented DivideByZero crash. Seeded from the capture so a fallback holds the player's view.
        var capturedForward = (-double3.UnitZ).Transform(_restoreLocalRotation);
        if (capturedForward.IsFinite() && !capturedForward.IsNearlyZero())
            controller.CameraRotation = capturedForward;
        controller.CameraOffset = double3.Zero;
        controller.ClearFault();
        controller.Pose = this;

        _followable.Hold(_restorePositionEcl);

        var owned = viewport.BaseCamera;
        owned.NoRotation = false; // insurance: a stuck map flag would re-interpret every write below
        owned.SetFollow(_followable, tidalLocking: false, changeControl: false, alert: false);
        owned.PositionEcl = _restorePositionEcl; // undo SetFollow's 2.5 m teleport
        owned.LocalRotation = _restoreLocalRotation;

        ParkMode(viewport);

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
        _timeWarned = false; // one warning per ownership session, not one per process
        _frameValid = false; // the controller holds the captured pose until the first prefix runs
        _blendComplete = false;
        ResetSmoothing();
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
        _blendComplete = false;
        _phase = Phase.Releasing;
        return CommandResult.Ok;
    }

    /// <summary>
    ///     <c>camera/release</c> and the unload teardown: the hard cut back to game control, with no
    ///     blend. This is the "give it back <i>now</i>" verb — the panic button and the teardown path —
    ///     which is why it does not ease; <c>camera/enabled 0</c> is the one that does.
    /// </summary>
    /// <remarks>
    ///     <b>Order matters.</b> <c>SetFollow</c> <i>teleports</i> the camera, so the follow target is
    ///     restored <b>first</b> and the captured transform written over it afterwards.
    ///     <c>NoRotation</c> goes before the transform because it changes how <c>LocalPosition</c> is
    ///     interpreted, and the viewport mode goes last so no controller's <c>OnSwitchOn</c> can re-run
    ///     against a half-restored camera. The stock fixed-controller input fields are put back too, so
    ///     a player who was <i>in</i> the fixed camera gets their exact view again. Safe to call at any
    ///     time and idempotent; a follow target that despawned degrades to no-follow rather than
    ///     throwing. The pose controller stays installed (inert with <c>Pose</c> null) until
    ///     <see cref="Shutdown"/> puts the stock instance back.
    /// </remarks>
    [KsaAnchor("Camera.{SetFollow,Unfollow,LocalPosition,LocalRotation,NoRotation,SetFieldOfView,"
            + "SetOrthographic}; IGameViewport.{Mode,SetCameraMode}; ViewportBase.Mode setter via ViewportSeam; Universe.SetSimulationSpeed",
        SourceFile = "KSA/Camera.cs / KSA/GameViewport.cs / KSA/ViewportBase.cs / KSA/Universe.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "SetFollow(target, tidal, changeControl:false, alert:false) — the defaults would take "
            + "the player's vessel and print 'Following X' on screen. Restoring INTO Map goes through "
            + "SetCameraMode so MapController.OnSwitchOn re-establishes NoRotation and the map's own "
            + "control state; every other mode is a direct field assignment (no alert). The orthographic "
            + "HALF-HEIGHT is not restored: Camera has no public getter for it (see ApplyProjection). "
            + "The simulation speed is restored ONLY when the C4 time channel actually captured it."
            + "5402: the mode restore writes ViewportBase.Mode through ViewportSeam (falls back to SetCameraMode, which also clears held player input). Universe.SetSimulationSpeed/GetSimulationSpeed/IsAutoWarpActive are unchanged.")]
    internal CommandResult Restore()
    {
        if (_phase == Phase.Idle)
            return CommandResult.Ok;

        _phase = Phase.Idle;
        try
        {
            // Only if the time channel was actually driven — see ApplyTimeScale.
            if (_timeCaptured)
                Universe.SetSimulationSpeed(_restoreSimSpeed, alert: false);

            var viewport = Program.MainViewport;
            var camera = _restoreCamera ?? viewport.BaseCamera;

            if (_controller is { } controller)
            {
                controller.Pose = null;
                controller.CameraOffset = _restoreFixedOffset;
                if (_restoreFixedRotation.IsFinite() && !_restoreFixedRotation.IsNearlyZero())
                    controller.CameraRotation = _restoreFixedRotation;
            }

            if (CameraTargets.IsLive(_restoreFollowing) && _restoreFollowing is { } following)
                camera.SetFollow(following, _restoreTidal, changeControl: false, alert: false);
            else
                camera.Unfollow(changeControl: false);

            camera.NoRotation = _restoreNoRotation;
            camera.LocalPosition = _restoreLocalPosition;
            camera.LocalRotation = _restoreLocalRotation;
            camera.SetFieldOfView((float)_restoreFovDeg);
            camera.SetOrthographic(_restoreOrtho);

            if (_restoreMode == CameraMode.Map || !ViewportSeam.TrySetMode(viewport, _restoreMode))
                viewport.SetCameraMode(_restoreMode);

            // The take ends with the camera: a track that kept running would drive nothing (the driver
            // goes idle here) yet still sit in /sim/ctl/schedules as a live player, and its shot events
            // would stop firing because the director is what samples it. This is literally the
            // camera/stop verb, so it emits the documented `camera.finished reason=stopped`. It comes
            // last on purpose — giving the player their camera back is the part that must not be
            // skipped if anything above it throws.
            playback?.Execute(new SimCommand("", CameraCommands.StopAction, SimCommand.NoOrdinal, 1));
        }
        finally
        {
            ClearOwnershipState();
        }

        return CommandResult.Ok;
    }

    /// <summary>
    ///     Ownership loss detected: someone else — the player's view menu, a vessel destruction's
    ///     wreckage follow, another mod — has the camera now. Stop driving and leave the camera exactly
    ///     where the reclaimer put it; restoring the captured pose over their change would be a second
    ///     fight, not a hand-back. The captured simulation speed is still restored (that change is
    ///     invisible to the reclaimer and unambiguously gatOS's).
    /// </summary>
    private void StandDown(string reason)
    {
        _phase = Phase.Idle;
        try
        {
            if (_timeCaptured)
                Universe.SetSimulationSpeed(_restoreSimSpeed, alert: false);
            if (_controller is { } controller)
                controller.Pose = null;
            playback?.Execute(new SimCommand("", CameraCommands.StopAction, SimCommand.NoOrdinal, 1));
            ModLog.Log.Info($"gatOS camera: standing down — {reason}.");
        }
        finally
        {
            ClearOwnershipState();
        }
    }

    /// <summary>The shared tail of <see cref="Restore"/> and <see cref="StandDown"/>.</summary>
    private void ClearOwnershipState()
    {
        _restoreCamera = null;
        _restoreFollowing = null;
        _appliedFovDeg = double.NaN;
        _timeCaptured = false;
        _appliedTimeScale = double.NaN;
        _degradeReason = "";
        _frameValid = false;
        _blendComplete = false;
        store.State.ClearAll();
        store.PublishStatus(CameraStatus.Idle);
        _publishedIdle = true;
        ResetSmoothing();
    }

    /// <summary>
    ///     Unload teardown: gives the camera back (restoring the simulation speed if a shot moved it),
    ///     puts the stock fixed controller back, stops and unregisters the track player, and drops every
    ///     uploaded track and parsed-track cache. Unconditional and idempotent.
    /// </summary>
    internal void Shutdown()
    {
        Restore();
        try
        {
            var viewport = Program.MainViewport;
            if (_stockFixedController is { } stock && ReferenceEquals(viewport.FixedController, _controller))
                ViewportSeam.TrySetFixedController(viewport, stock);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS camera: stock controller restore failed — {ex.Message}");
        }

        _controller = null;
        _stockFixedController = null;
        store.Clear();
        playback?.Clear();
    }

    /// <summary>
    ///     Installs the pose controller into <c>GameViewport.FixedController</c>, saving the stock instance
    ///     the first time. Idempotent; a failure degrades to "cannot own the camera" rather than
    ///     throwing into the command drain. The stock controller's live input fields (including a
    ///     docking-camera connector) are carried over so a player mid-fixed-view loses nothing.
    /// </summary>
    private CameraPoseController? EnsureController(IGameViewport viewport)
    {
        if (_controller is { } installed && ReferenceEquals(viewport.FixedController, installed))
            return installed;

        try
        {
            var current = viewport.FixedController;
            var controller = new CameraPoseController(viewport.BaseCamera)
            {
                CameraOffset = current.CameraOffset,
                CameraRotation = current.CameraRotation,
                DockingConnector = current.DockingConnector,
            };
            if (!ViewportSeam.TrySetFixedController(viewport, controller))
                return null;
            _stockFixedController ??= current;
            _controller = controller;
            return controller;
        }
        catch (Exception ex)
        {
            ModLog.Log.Error($"gatOS camera: cannot install the fixed-controller seam — {ex.Message}");
            return null;
        }
    }

    // ================================================================================================
    //  Per-frame driver — the out-of-phase half
    // ================================================================================================

    /// <summary>
    ///     The per-frame prefix drive, called after KSA has advanced the simulation and immediately
    ///     before the main viewport runs its controller (the pose controller) and rebuilds matrices.
    ///     Prepares this frame's composed pose for <see cref="TryPose"/> and detects ownership loss —
    ///     it writes nothing to the camera itself.
    /// </summary>
    /// <param name="dt">
    ///     The frame's player-clock delta, in seconds. Unused here: every time-dependent step
    ///     (smoothing, the release blend) runs in <see cref="TryPose"/> on the engine's own
    ///     <c>inDeltaTime</c>, so the pose never integrates a different clock than the pass that
    ///     renders it. Kept in the signature because the prefix hands it to every per-frame driver.
    /// </param>
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

        // Ownership-loss ladder (KSArmory's stand-down doctrine): a state gatOS did not create means
        // someone else has the camera. Never fight a second writer frame-by-frame.
        if (_controller is not { } controller || controller.Faulted)
        {
            StandDown("the in-phase pose solve faulted");
            return;
        }

        if (!ReferenceEquals(viewport.FixedController, controller))
        {
            StandDown("another mod replaced the fixed-camera controller");
            return;
        }

        if (viewport.Mode != CameraMode.Fixed)
        {
            StandDown("the camera mode changed outside gatOS (the view was reclaimed)");
            return;
        }

        if (!ReferenceEquals(camera.Following, _followable))
        {
            StandDown("the camera's follow target changed outside gatOS");
            return;
        }

        // The release blend completed in-phase last frame while holding the exact restore pose; do the
        // real restore now, from the prefix, where mode and follow changes are safe.
        if (_phase == Phase.Releasing && _blendComplete)
        {
            Restore();
            return;
        }

        // ---- the track evaluator (task C3) ---------------------------------------------------------
        // TryEvaluateNow, not TryEvaluate(t, …): it samples at the player's OWN PlaybackClock, which is
        // the same instance /sim/ctl/schedules/<id>/{t,rate,pause,scrub} drives and the same one a
        // shared-clock group shares. Deriving a second notion of "now" here — from dt, or from the
        // frame counter — is exactly the drift that makes a dolly move slide against its own cue track.
        // The compositor's Track ?? Override ?? Baseline precedence then does the rest: a shot claims
        // only what it declares, so a timed_batch can still pull focus mid-shot.
        CameraPose? trackSample = null;
        var trackClaims = CameraChannelMask.None;
        if (playback is { } player && player.TryEvaluateNow(out var sampled, out var claims))
        {
            trackSample = sampled;
            trackClaims = claims;
        }

        var pose = store.State.Compose(trackSample, trackClaims);
        CameraTargets.TryResolve(pose.Anchor, out var anchor);
        _statusPose = pose;
        _statusAnchor = anchor;
        _frameClaims = trackClaims;
        _frameValid = true;

        // Point the followable at this frame's anchor. An anchored pose follows the live object (the
        // engine re-resolves it in its own pass); an un-anchored one holds the last resolved absolute
        // point, so the offset the in-phase solve produces is near zero either way.
        if (pose.Anchor.HasTarget)
            _followable.Follow(pose.Anchor);
        else
            _followable.Hold(_lastResolvedPositionEcl);

        // The requested status; the viewport postfix re-publishes with this frame's applied transform.
        store.PublishStatus(CameraReader.Sample(viewport, camera, owned: true, pose, anchor,
            _lastResolvedPositionEcl, playback?.Current));
        _publishedIdle = false;
    }

    /// <summary>
    ///     Re-publishes status after the original <c>GameViewport.OnFrame</c> has run, so the applied fields
    ///     include the in-phase solve and KSA's own camera clamp — true render-time read-back rather
    ///     than requested input.
    /// </summary>
    internal void PublishAppliedStatus()
    {
        if (_phase == Phase.Idle)
            return;
        var viewport = Program.MainViewport;
        store.PublishStatus(CameraReader.Sample(viewport, viewport.BaseCamera, owned: true,
            _statusPose, _statusAnchor, _lastResolvedPositionEcl, playback?.Current));
        _publishedIdle = false;
    }

    // ================================================================================================
    //  The in-phase half — called by CameraPoseController inside the engine's viewport pass
    // ================================================================================================

    /// <summary>
    ///     Resolves this frame's pose against the engine's fresh anchor sample: placement (smoothed on
    ///     the anchor-relative component only), aim, projection and the time channel. Returns the final
    ///     camera placement as an offset from <paramref name="followedEcl"/> so the controller's write —
    ///     <c>followedEcl + offset</c> — carries no cross-phase term at all.
    /// </summary>
    /// <remarks>
    ///     An unresolvable placement or aim <b>holds the last good value</b> rather than moving the
    ///     camera somewhere arbitrary or letting a NaN reach the view matrix — a vessel that despawns
    ///     mid-shot leaves the camera exactly where it was, which is the least surprising failure and
    ///     the only one that can be recovered from by writing a new target.
    /// </remarks>
    [KsaAnchor("Camera.{LookAtRotation,SetFieldOfView,SetOrthographic,SetOrthoHalfHeight}; "
            + "FixedController.OnFrame ordering (position, then rotation); Camera.OnFrame calls "
            + "ClampCamera itself immediately after the controller",
        SourceFile = "KSA/Camera.cs / KSA/FixedController.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "SetFieldOfView takes DEGREES and does NOT clamp (which is what makes "
            + "fisheye/telephoto reachable); it rebuilds and inverts the projection matrix on every "
            + "call, so it is only called when the value actually changed. The explicit ClampCamera "
            + "dance of the previous implementation is gone: the engine clamps in Camera.OnFrame right "
            + "after this, from bookkeeping the live follow keeps current."
            + "5402: Camera.OnFrame still calls ClampCamera first, but ClampCamera is now TryGetSurfaceClampPositionEcl(0.5) — camera-local (Program.FindNearbyCelestial(this)) and terrain-aware (MeanRadius + GetTerrainHeightFromDirCce) — so a pose solved below the terrain is lifted 0.5 m above it by the engine after this controller runs; the read-back postfix sees the clamped transform.")]
    public bool TryPose(KsaCamera camera, double3 followedEcl, double dt,
        out double3 offsetFromFollowed, out doubleQuat rotationEcl)
    {
        offsetFromFollowed = default;
        rotationEcl = _lastRotation;

        if (!_frameValid || _phase == Phase.Idle)
            return false;

        if (_phase == Phase.Releasing)
            return StepRelease(camera, followedEcl, dt, out offsetFromFollowed, out rotationEcl);

        var pose = _statusPose;
        var anchor = _statusAnchor;

        // ---- placement, resolved fresh in the engine's epoch ---------------------------------------
        if (CameraFrames.TryResolvePlacement(pose, anchor, out var placement, out var error))
        {
            _lastResolvedPositionEcl = placement.PositionEcl;
            _degradeReason = "";
        }
        else
        {
            LogDegradeOnce(error);
            placement = new ResolvedPlacement(double3.Zero, _lastResolvedPositionEcl, Relative: false);
        }

        var smoothed = _positionSmoother.Step(
            ToVec(_lastPositionEcl),
            ToVec(placement.OriginEcl),
            ToVec(placement.ComponentEcl),
            pose.Anchor,
            placement.Relative,
            pose.Smoothing,
            dt);
        _lastPositionEcl = ToKsa(smoothed);

        // ---- orientation ---------------------------------------------------------------------------
        var targetRotation = ResolveRotation(pose, anchor, _lastPositionEcl);
        if (pose.AimTarget.HasTarget)
        {
            // Aim is a constraint, not a loose rotation suggestion: smoothing it makes a moving
            // subject drift out of frame. Smooth the camera's offset, then solve look-at exactly from
            // that applied point every frame — the same sample the engine renders from.
            _rotationSmoother.Reset();
            _lastRotation = targetRotation;
        }
        else
        {
            var smoothedRotation = _rotationSmoother.Step(ToQuat(_lastRotation), ToQuat(targetRotation),
                pose.Smoothing, dt);
            _lastRotation = ToKsaQuat(smoothedRotation);
        }

        ApplyProjection(camera, pose, _frameClaims);
        ApplyTimeScale(pose, _frameClaims);

        offsetFromFollowed = _lastPositionEcl - followedEcl;
        rotationEcl = _lastRotation;
        return true;
    }

    private void ResetSmoothing()
    {
        _positionSmoother.Reset();
        _rotationSmoother.Reset();
    }

    /// <summary>
    ///     Task C4 — the interpolated <c>time</c> channel: drives the simulation speed from the composed
    ///     pose, so a shot can ease into slow motion (<c>0.15</c>), hold on a paused world (<c>0</c>) or
    ///     ramp into warp (<c>&gt; 1</c>) as one continuous curve rather than as discrete
    ///     <c>debug/time/warp</c> steps. The discrete case still exists and is still schedulable through
    ///     <c>ctl/timed_batch</c>; this only adds the interpolated one, and it adds <b>no</b> new
    ///     <c>/sim</c> leaf.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Double-gated, and a closed gate is a warning rather than a failure.</b> The channel
    ///         needs both <c>[control] debug_namespace</c> (it is <c>debug.warp</c>'s power under a new
    ///         name) and <c>[camera] camera_allow_time_channel</c>. When either is off the channel is
    ///         ignored and the rest of the shot runs at 1× — failing the whole take because of a config
    ///         flag would be strictly worse than running it at normal speed, and the author would have no
    ///         way to see why from inside the shot. The warning is one-shot per ownership session so a
    ///         60 Hz driver cannot fill the log.
    ///     </para>
    ///     <para>
    ///         <b>Capture is lazy and restore is conditional.</b> <c>Universe.GetSimulationSpeed()</c> is
    ///         read the first time the channel is actually driven, not at ownership take, and
    ///         <see cref="Restore"/> puts it back only if that capture happened — a director who never
    ///         touches time must leave the player's own warp setting exactly as it found it.
    ///     </para>
    ///     <para>
    ///         <b>⚠ Auto-warp interaction, stated rather than guarded.</b> Neither public
    ///         <c>Universe.SetSimulationSpeed</c> overload checks <c>Universe.IsAutoWarpActive</c> — only
    ///         the private <c>simspeed</c> terminal command does, and it refuses outright. So a track
    ///         driving the time channel while an auto-warp is running will <i>fight</i> it: auto-warp
    ///         re-computes and re-sets the speed every step of its own update, and whichever wrote last
    ///         in the frame wins. No guard is added here because both behaviours are defensible and the
    ///         game itself does not pick. Stop the auto-warp before rolling the shot.
    ///     </para>
    /// </remarks>
    [KsaAnchor("Universe.SetSimulationSpeed(double, alert:false); Universe.GetSimulationSpeed(); "
            + "Universe.IsAutoWarpActive",
        SourceFile = "KSA/Universe.cs", Verified = "2026-08-11", GameVersion = "2026.8.19.5261",
        Risk = ChurnRisk.Medium,
        Notes = "SetSimulationSpeed writes _simulationSpeed and draws a TimedAlert unless alert:false — "
            + "which matters here, the alert would be in the footage. It does NOT check "
            + "IsAutoWarpActive; only the private terminal command does. GetSimulationSpeed is the "
            + "plain getter. The same primitive debug.warp binds.")]
    private void ApplyTimeScale(in CameraPose pose, CameraChannelMask trackClaims)
    {
        // Nothing claims the channel unless a shot declares it: there is no pose/time leaf, by design
        // (plan §7 C4.1 — debug/time/warp already covers the discrete case). The override arm is kept
        // so a future leaf lights up here with no change.
        if (!store.State.HasOverride(CameraChannel.TimeScale) && !trackClaims.Has(CameraChannel.TimeScale))
            return;

        if (!debugNamespace || !allowTimeChannel)
        {
            if (_timeWarned)
                return;
            _timeWarned = true;
            ModLog.Log.Warn("gatOS camera: this track drives the 'time' channel, which is ignored — "
                            + (debugNamespace
                                ? "set [camera] camera_allow_time_channel = true in gatos.toml to enable it."
                                : "it needs [control] debug_namespace = true in gatos.toml.")
                            + " The rest of the shot plays at normal speed.");
            return;
        }

        if (!double.IsFinite(pose.TimeScale) || pose.TimeScale < 0)
            return;

        if (!_timeCaptured)
        {
            _restoreSimSpeed = Universe.GetSimulationSpeed();
            _timeCaptured = true;
        }

        // Self-gating: SetSimulationSpeed is cheap, but a curve that has settled on a value should not
        // keep re-writing it (and the alert-suppressed path still walks the change test inside).
        if (pose.TimeScale == _appliedTimeScale)
            return;
        Universe.SetSimulationSpeed(pose.TimeScale, alert: false);
        _appliedTimeScale = pose.TimeScale;
    }

    /// <summary>
    ///     Resolves the camera's ECL orientation: an aim target if one is set (target + offset in the
    ///     aim frame, with the chosen "up" and then roll), otherwise the explicit <c>pose/rotation</c>
    ///     quaternion. Falls back to the previous frame's rotation on anything degenerate.
    /// </summary>
    /// <remarks>
    ///     Roll is applied <b>only</b> on the aim path, exactly as the surface documents it ("degrees,
    ///     applied after aim"): an explicit <c>pose/rotation</c> already names a complete orientation,
    ///     and quietly rolling it further would make the two channels fight over one value. Called from
    ///     the in-phase solve, so the aim point and the eye come from the same engine epoch — the
    ///     structural guarantee that the camera actually looks at its subject.
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
    ///     getter for it, so there is nothing to capture at ownership take and therefore nothing to put
    ///     back at release. Writing the composed default every frame would silently clobber a value we
    ///     could never restore; writing it only on request means gatOS changes it exactly when a guest
    ///     asked it to — and that one change is, honestly, not restorable.
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
    ///     One frame of the eased hand-back, solved in-phase: interpolate from the pose gatOS was
    ///     holding toward the captured restore pose, expressed — like everything else — as an offset
    ///     from the followed anchor's fresh sample. The completing frame holds the restore pose exactly
    ///     and flags the blend done; the next prefix performs the real restore, where mode and follow
    ///     changes are safe (swapping controllers from inside the controller's own pass is not).
    /// </summary>
    /// <remarks>
    ///     The restore point is recomputed <i>every</i> frame of the blend rather than baked at its
    ///     start, so blending back onto a moving follow target lands on the target and not on where it
    ///     used to be. If that target despawned mid-blend, the captured absolute ecliptic point is used
    ///     instead — the blend still completes, it just ends somewhere fixed.
    /// </remarks>
    private bool StepRelease(KsaCamera camera, double3 followedEcl, double dt,
        out double3 offsetFromFollowed, out doubleQuat rotationEcl)
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
        if (double.IsFinite(fov) && fov != _appliedFovDeg)
        {
            camera.SetFieldOfView((float)fov);
            _appliedFovDeg = fov;
        }

        offsetFromFollowed = _lastPositionEcl - followedEcl;
        rotationEcl = _lastRotation;

        if (progress >= 1)
            _blendComplete = true;
        return true;
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
        SourceFile = "KSA/Camera.cs", Verified = "2026-08-11", GameVersion = "2026.8.19.5261",
        Risk = ChurnRisk.Medium,
        Notes = "Reproduced rather than called because the camera being blended follows the gatOS "
            + "anchor at this point, so its own PositionCce would not use the captured target. Called "
            + "from the in-phase solve, so the target's position is the engine's own fresh sample.")]
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
    ///     Parks the viewport in <c>Fixed</c>. A direct <c>Mode</c> write (through
    ///     <see cref="ViewportSeam"/>) except when leaving Map, which must run
    ///     <c>MapController.OnSwitchOff</c> (it undoes <c>NoRotation</c> and re-establishes the player's
    ///     vessel control), or when the seam is unavailable. Either way no "Fixed Camera" text is drawn:
    ///     <see cref="CameraPoseController.OnSwitchOn"/> suppresses the stock alert while gatOS owns
    ///     the park.
    /// </summary>
    private static void ParkMode(IGameViewport viewport)
    {
        if (viewport.Mode == CameraMode.Map || !ViewportSeam.TrySetMode(viewport, CameraMode.Fixed))
            viewport.SetCameraMode(CameraMode.Fixed);
    }

    // ================================================================================================
    //  Live game-camera controls (they act whether or not gatOS owns the camera)
    // ================================================================================================

    /// <summary>
    ///     <c>camera/mode</c>: switches the main viewport's camera mode through the game's own
    ///     <c>SetCameraMode</c>, so each controller's switch-on/off bookkeeping runs (the alert is the
    ///     player's own doing here — they asked for a mode change). Refused while gatOS owns the camera,
    ///     because ownership <i>is</i> a mode park: honouring it would instead be detected as a reclaim
    ///     by the next frame's stand-down ladder, which is not what a guest writing <c>camera/mode</c>
    ///     meant.
    /// </summary>
    [KsaAnchor("Program.MainViewport; IGameViewport.SetCameraMode(CameraMode)",
        SourceFile = "KSA/Program.cs / KSA/GameViewport.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "SetCameraMode also calls Program.ControlledVehicle?.ClearHeldPlayerInput(), which "
            + "drops any latched ctl/translate + ctl/rotate flags (SPEC §3.4.19)."
            + "5402: GameViewport.SetCameraMode is byte-identical (OnSwitchOff/OnSwitchOn pair + ClearHeldPlayerInput).")]
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
    ///     gatOS owns the camera: the camera is following the gatOS anchor, and swapping that follow is
    ///     what <c>pose/anchor</c> is for.
    /// </summary>
    /// <remarks>
    ///     A viewport carries two camera instances — <c>BaseCamera</c> and <c>MapCamera</c> — and
    ///     <c>GetCamera()</c> returns whichever the current mode uses. The game's own follow action sets
    ///     the target on <b>both</b> (<c>KSA/InputEvents.cs</c>); setting only one leaves the map
    ///     view pointing at the previous target, which is exactly what gatOS's original
    ///     <c>camera.focus</c> did.
    /// </remarks>
    [KsaAnchor("Program.MainViewport.{BaseCamera,MapCamera}; Camera.{SetFollow,Unfollow}",
        SourceFile = "KSA/IGameViewport.cs / KSA/Camera.cs / KSA/InputEvents.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "SetFollow teleports to target + 2.5×MeanRadius×forward — kept, because that is what "
            + "the game's own follow does and what a viewer expects from 'go look at this'."
            + "5402: BaseCamera/MapCamera are get-only properties on IGameViewport; the game's follow action still sets both.")]
    internal CommandResult SetFollow(in TargetRef reference)
    {
        if (_phase != Phase.Idle)
            return new CommandResult(CommandOutcome.Unsupported,
                "gatOS owns the camera and it follows the gatOS anchor; use pose/anchor + "
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
        Verified = "2026-08-11", GameVersion = "2026.8.19.5261", Risk = ChurnRisk.Medium,
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

    /// <summary>
    ///     <c>camera/map/scope</c> (task C5.2): sets the map view's scope — the radius, in metres, the
    ///     map camera orbits its focus at, i.e. the map's zoom. Accepted whether or not gatOS owns the
    ///     camera, because it configures the <i>game's</i> map controller rather than the composed pose.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three things the caller should expect, all of them the game's own behaviour.</b>
    ///         (1) <c>MapController.OnFrame</c> clamps <c>Scope</c> up to the followed object's
    ///         <c>MeanRadius</c> on every map frame, so a smaller value reads back clamped.
    ///         (2) <c>OnSwitchOn</c> calls <c>SetDefaults()</c> — which recomputes <c>Scope</c> from the
    ///         focus's radius and sphere of influence — whenever the follow target changed since the map
    ///         was last left, so a scope written before a focus change does not survive it.
    ///         (3) It has no visible effect outside <c>map</c> mode; it is a stored field until then.
    ///     </para>
    ///     <para>
    ///         The write is published straight back into the status, because the director only samples
    ///         the live viewport while it <i>owns</i> the camera — and the map is precisely the mode in
    ///         which it does not. Without this the read-back would report the idle <c>0</c> for a value
    ///         the guest had just written.
    ///     </para>
    /// </remarks>
    [KsaAnchor("Program.MainViewport.MapController; MapController.Scope",
        SourceFile = "KSA/IGameViewport.cs / KSA/MapController.cs", Verified = "2026-09-02",
        GameVersion = "2026.9.7.5402", Risk = ChurnRisk.Medium,
        Notes = "Scope is a plain public double field with no setter hook. It is clamped up to "
            + "Camera.Following.MeanRadius near the end of every MapController.OnFrame and recomputed "
            + "wholesale by SetDefaults() from OnSwitchOn after a focus change. The viewport's "
            + "MapController instance is per-viewport, so bind the main one explicitly."
            + "5402: MapController gained CanChangeControl => ViewportRegistry.IsMainCamera(Camera) — the control-vehicle juggling on map enter/exit now happens only for the main viewport's cameras (which are the only ones gatOS drives), and Program.GridFlag is no longer toggled by the controller. Scope and its clamp are unchanged.")]
    internal CommandResult SetMapScope(double scopeMetres)
    {
        if (!double.IsFinite(scopeMetres) || scopeMetres < 0)
            return new CommandResult(CommandOutcome.Invalid,
                "camera.map_scope expects a finite radius in metres >= 0");

        var controller = Program.MainViewport.MapController;
        controller.Scope = scopeMetres;
        store.PublishStatus(store.Status with { MapScope = controller.Scope });
        return CommandResult.Ok;
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
    ///     baseline captured at ownership take (an active track keeps driving — a reset is about
    ///     <i>your</i> writes, not about stopping playback). The smoother is reset with them:
    ///     a reset is a hard cut, and carrying the previous setpoint's momentum into it would sail the
    ///     camera past the baseline before settling back onto it.
    /// </summary>
    internal void ResetPose()
    {
        store.State.ClearOverrides();
        ResetSmoothing();
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
