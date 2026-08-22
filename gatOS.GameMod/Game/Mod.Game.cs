using System.Diagnostics;
using Brutal.ImGuiApi;
using gatOS.GameMod.Game;
using gatOS.GameMod.Game.Ksa;
using gatOS.GameMod.Game.Ksa.Actuators;
using gatOS.GameMod.Game.Ksa.Camera;
using gatOS.GameMod.Game.Ksa.Fx;
using gatOS.GameMod.Game.Ksa.Iva;
using gatOS.GameMod.Game.Ksa.Paint;
using gatOS.GameMod.Game.Ksa.Render;
using gatOS.GameMod.Game.Ksa.ThugLife;
using gatOS.GameMod.Game.Ksa.Welds;
using gatOS.Logging;
using gatOS.SimFs;
using gatOS.SimFs.Camera;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Display;
using gatOS.SimFs.Snapshots;
using gatOS.Vm;
using HarmonyLib;
using KSA;
using ModMenu;
using float2 = Brutal.Numerics.float2;
using float4 = Brutal.Numerics.float4;

namespace gatOS.GameMod;

/// <summary>
///     The game-coupled half of <see cref="Mod"/> (OS_PLAN.md T6.4): the ModMenu entry, the ImGui
///     status window, the reset-disk confirm modal, and the game-backed logging swap. This file
///     compiles only when the KSA reference assemblies are present (compile gate in
///     gatOS.GameMod.csproj); without them the partial-method calls in Mod.cs drop out.
/// </summary>
/// <remarks>
///     Threading rule 5: everything here runs on the render thread and reads only cached/volatile
///     state — <see cref="Mod.CurrentVmStatus"/> is a volatile snapshot, and the one filesystem
///     lookup (newest QEMU log) is cached and refreshed only when the VM status object changes.
/// </remarks>
public sealed partial class Mod
{
    private const string StatusWindowId = "gatOS Status##gatos_status";
    private const string ResetDiskPopupId = "Reset gatOS Disk##gatos_reset_disk_modal";

    /// <summary>
    ///     Game-typed statics live in a nested class, not on <see cref="Mod"/> itself: field
    ///     types resolve at <i>type load</i>, and the Mod type must stay loadable without the
    ///     game assemblies (nested types — like method bodies — resolve lazily on first use).
    /// </summary>
    private static class Palette
    {
        internal static readonly float4 Ok = new(0.5f, 1f, 0.5f, 1f);
        internal static readonly float4 Warn = new(1f, 0.8f, 0.3f, 1f);
        internal static readonly float4 Error = new(1f, 0.4f, 0.4f, 1f);
        internal static readonly float4 Idle = new(0.7f, 0.7f, 0.7f, 1f);
    }

    // Render-thread-only UI state (fields live here so a no-KSA build has no unread fields).
    private bool _statusWindowVisible;
    private bool _resetDiskModalRequested;
    private VmStatus? _qemuLogStatusMark;
    private string? _newestQemuLog;

    // Game-thread-only sampler state (T9.1). The sampler type is ours (loads without game
    // assemblies); only its method bodies touch KSA types, which is exactly what the
    // NoInlining + catch-at-call-site discipline below is for.
    private TelemetrySampler? _telemetry;
    private bool _samplerDead;

    // Game-thread-only control state (KSA_GAME_INTEGRATION_PLAN G1). The health latches are shared
    // between the sampler (publishes degraded accessors to /sim/status) and the executor (latches
    // a faulting actuator). A drain failure disables the drain for the session (one error log).
    private KsaHealth? _health;
    private KsaCatalog? _catalog;
    private bool _commandsDead;

    // Game-thread-only screen-stream capture (STREAM_PLAN.md). FrameCapture owns the reusable per-slot
    // Vulkan scratch images + host staging buffers; the Harmony render hook (DisplayRenderPatch) drives
    // it, recording the capture into the engine's frame command buffer. Disposed at unload.
    private FrameCapture? _frameCapture;

    // The Harmony patch injecting the screen-stream capture into Program.RenderGame (STREAM_PLAN.md).
    // Installed in OnFullyLoaded, removed at unload; null when the render hook could not be installed.
    private Harmony? _displayHarmony;

    // The welds cheat registry + per-frame driver (Game/Ksa/Welds). Created lazily on the game thread,
    // shared by the sampler (projects it into /sim/debug/welds) and the executor (mutates it). The
    // driver runs in OnAfterUi (DriveWelds) and self-gates to nothing when empty — no Harmony patch.
    private WeldManager? _weldManager;
    private bool _weldsDead;

    // The IVA cabin-physics registry + per-frame driver (Game/Ksa/Iva). Created lazily on the game
    // thread with the other control objects and shared by the sampler (projects it into
    // /sim/debug/iva) and the executor (mutates it). Its master switch is off by default, so until a
    // guest writes /sim/debug/iva/enabled the object is an empty registry: no physics simulation, no
    // interior mesh, no Bepu type ever loaded. DriveIvaPhysics (OnAfterUi) self-gates on that.
    private IvaPhysicsManager? _ivaPhysics;
    private bool _ivaDead;

    // The thug-life cheat registry + GPU renderer (Game/Ksa/ThugLife). Created lazily on the game thread,
    // shared by the sampler (projects it into /sim/debug/thug_life) and the executor (mutates it). The
    // render postfix + GPU resources are installed lazily on the first entry and torn down on the last;
    // UpdateThugLife (OnBeforeUi) validates entries each frame and self-gates to nothing when empty.
    private ThugLifeManager? _thugLife;

    // Face-anchored particle FX (Game/Ksa/Fx/FaceFxManager): one-shot bursts from the game's own
    // particle pool, empty and free until a guest writes /sim/debug/fx/spawn.
    private FaceFxManager? _faceFx;
    private bool _thugLifeDead;

    // The audio playback actuator (GATOS_CUSTOM_AUDIO_PLAN): owns the FMOD Sound cache + channel
    // table over the game-free _audioStore. Created lazily on the game thread with the other control
    // objects (null when [audio] enabled=false), ticked per frame by DriveAudio, torn down at unload.
    private AudioActuator? _audioActuator;
    private bool _audioDead;

    // The camera director (Game/Ksa/Camera): owns the main viewport's camera while a guest has taken
    // it, writes the composed pose in Viewport.OnFrame before KSA builds matrices, and hands it back.
    // Created lazily on the game thread with the other control objects (null when
    // [camera] camera_enabled=false), driven by CameraViewportPatch, released at unload.
    private CameraDirector? _cameraDirector;
    private bool _cameraDead;
    private Harmony? _cameraHarmony;

    // Paint manager is inert until a transport writes one of its two runtime masters. It owns all
    // dynamic renderer hooks and material clones and therefore has one unconditional unload path.
    private PaintManager? _paintManager;

    // Game-thread-only driver state for the timed-command scheduler (CAMERA_CONTROLS_PLAN §3.2).
    // The due list is reused across ticks (Clear()ed, never reallocated) so a steady-state tick
    // allocates nothing; the stopwatch IS the `wall` clock base (KSA's dtPlayer is the `render` one
    // and cannot substitute for it), and the two _scheduleUt fields carry the `ut` base's previous
    // reading so the tick can hand the registry a sim-time delta.
    private readonly List<DueCommand> _scheduleDue = [];
    private readonly Stopwatch _scheduleWallClock = new();
    private readonly PerfStat _scheduleStats = new();
    private double _scheduleLastUt;
    private bool _scheduleUtSeeded;
    private bool _schedulesDead;

    // The Harmony patch draining solver-phase commands (G4). Installed in OnFullyLoaded, removed at
    // Unload; null when the solver hook could not be installed (solver commands then never drain).
    private Harmony? _solverHarmony;

    // The Harmony patch drawing the fallback top-level "gatOS" menu when the ModMenu mod is absent
    // (see InstallMenuFallback). Cached ModMenu-presence probe, dropped on unload.
    private Harmony? _menuHarmony;
    private static bool? _modMenuPresent;

    // Sample-rate presets offered as one-click menu items (the status window has a free slider).
    private static readonly int[] RatePresets = [1, 2, 5, 10, 20, 30, 60];

    /// <summary>
    ///     The "gatOS" entry drawn two ways with identical content (same mechanism as purrTTY's):
    ///     via this <c>[ModMenuEntry]</c> attribute when the ModMenu companion mod is present, and
    ///     via the <see cref="MenuFallbackPostfix"/> Harmony postfix on KSA's
    ///     <c>Program.DrawProgramMenusHook()</c> otherwise. Both call the shared
    ///     <see cref="DrawMenuContentSafe"/>.
    /// </summary>
    [ModMenuEntry("gatOS")]
    public static void DrawMenu() => DrawMenuContentSafe();

    /// <summary>
    ///     The shared menu-item body reused by both the ModMenu entry and the fallback injected
    ///     menu. Resolves the live mod instance (kept alive even when init failed so the player can
    ///     see why) and swallows per-frame draw errors so a UI bug never breaks the game's frame.
    /// </summary>
    private static void DrawMenuContentSafe()
    {
        if (_instance is not { } mod)
        {
            ImGui.TextDisabled("gatOS did not initialize (see the game log)");
            return;
        }

        try
        {
            mod.DrawMenuContent();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS menu draw error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Draws the fallback top-level "gatOS" menu via the <c>DrawProgramMenusHook()</c> extension
    ///     point KSA calls at the end of its menu bar — used only when the ModMenu companion mod is
    ///     absent (with ModMenu present, the <c>[ModMenuEntry]</c> path draws the same content under
    ///     the Mods menu instead). Installed by <see cref="InstallMenuFallback"/>.
    /// </summary>
    private static void MenuFallbackPostfix()
    {
        try
        {
            if (IsModMenuPresent())
                return;

            if (ImGui.BeginMenu("gatOS"))
            {
                // Keep the (possibly auto-hidden) menu bar shown while our menu is open.
                Program.MainViewport.MenuBarInUse = true;
                DrawMenuContentSafe();
                ImGui.EndMenu();
            }
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS fallback menu draw error: {ex.Message}");
        }
    }

    private static bool IsModMenuPresent() => _modMenuPresent ??= ModLibrary.Find("ModMenu") is not null;

    // NoInlining on the partial impls: their bodies reference game assemblies, and a missing
    // assembly must fail at the call site (where Mod.cs catches it), not at the JIT of the
    // calling method — inlining would hoist the type resolution into the caller.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void InstallGameLogging() => ModLog.SetLogger(new BrutalModLogger());

    /// <summary>
    ///     T9.1: ticks the telemetry sampler on the game thread. A sampler failure disables
    ///     sampling for the session (one error log, no per-frame spam) — `/sim` then serves
    ///     the last published snapshot; everything else keeps working.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void SampleTelemetry(double dt)
    {
        if (_samplerDead || _simStore is not { } store)
            return;

        try
        {
            EnsureControlObjects();
            _telemetry ??= new TelemetrySampler(store, _telemetrySettings, _health!, _sampleStats,
                _sampleAllocStats, _weldManager!, _thugLife!, _ivaPhysics!, _ivaStats, _audioStore,
                _scheduleStore, _cameraDirector, Config.DebugNamespace, _faceFx);
            // Sample only while something can actually read /sim: the VM is up, or a host-side
            // transport client is connected (9p / HTTP / MQTT). Otherwise the sampler idles for free.
            var state = CurrentVmStatus.State;
            var active = state is VmState.Starting or VmState.Running
                         || (_simServer?.ActiveSessions ?? 0) > 0
                         || (_httpServer?.ActiveSessions ?? 0) > 0
                         || (_mqttBroker?.ConnectedClients ?? 0) > 0
                         || (_mcpServer?.ActiveRequests ?? 0) > 0;
            _telemetry.Tick(dt, active);
        }
        catch (Exception ex)
        {
            _samplerDead = true;
            ModLog.Log.Error($"gatOS telemetry sampling disabled after an error: {ex.Message}");
        }
    }

    /// <summary>
    ///     G1: drains queued control commands on the game thread, executing each through the KSA
    ///     actuator catalog (threading rule 1). A drain failure disables control for the session
    ///     (one error log); telemetry and the VM keep working.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DrainCommands()
    {
        if (_commandsDead || _commandQueue is not { } queue)
            return;

        try
        {
            EnsureControlObjects();
            using (_drainStats.Measure()) // alloc-free; usually an empty-queue no-op
                queue.Drain(CommandPhase.Frame, _catalog!, Config.MaxCommandsPerFrame);
        }
        catch (Exception ex)
        {
            _commandsDead = true;
            ModLog.Log.Error($"gatOS control disabled after a drain error: {ex.Message}");
        }
    }

    private void EnsureControlObjects()
    {
        _health ??= new KsaHealth();
        _weldManager ??= new WeldManager();
        _thugLife ??= new ThugLifeManager();
        if (_ivaPhysics is null)
        {
            // Constructing the registry touches no physics type and starts nothing: the master switch
            // is off until a guest writes /sim/debug/iva/enabled (or the config seed turns it on).
            _ivaPhysics = new IvaPhysicsManager();
            _ivaPhysics.Configure(IvaTuningFromConfig());
            _ivaPhysics.SeedRunOutsideIva(Config.IvaRunOutsideIva);
            if (Config.IvaPhysicsEnabled)
                _ivaPhysics.SetEnabled(true);
        }

        if (_audioActuator is null && _audioStore is { } audioStore)
            _audioActuator = new AudioActuator(audioStore, Config.AudioMaxChannels);
        // Constructing the director touches no camera and takes nothing: it stays idle (and DriveCamera
        // stays one branch) until a guest writes /sim/camera/enabled.
        if (_cameraDirector is null && _cameraStore is { } cameraStore)
        {
            // The track player is an entry in the /sim/ctl/schedules registry (kind = camera-track), so
            // it exists only when scheduling does; the controller also installs itself as the store's
            // commit-time track validator, which is what turns a malformed upload into an EINVAL on the
            // write rather than a mystery at play time.
            var cameraPlayback = _scheduleStore is { } scheduleStore
                ? new CameraPlaybackController(cameraStore, scheduleStore)
                : null;
            _cameraDirector = new CameraDirector(cameraStore, Config.CameraReleaseBlendS, cameraPlayback,
                Config.DebugNamespace, Config.CameraAllowTimeChannel);
        }
        // Constructing the face-FX registry touches no particle type: it holds no emitter until a
        // guest writes /sim/debug/fx/spawn, and every spawn is a self-retiring burst.
        _faceFx ??= new FaceFxManager();
        if (_paintManager is null && _paintStore is { } paintStore)
        {
            // The clutter-texture bridge is inert until something is bound: it never touches KSA
            // while the store's revision stays at its initial value.
            var textures = _textureStore is { } textureStore
                ? new ClutterTextureBridge(textureStore, _health, Config.PaintTextureMaxDimension)
                : null;
            _paintManager = new PaintManager(paintStore, textures);
            if (Config.PaintPartsEnabled)
                _paintManager.Execute(new SimCommand("", SimActions.PaintPartsEnabled, SimCommand.NoOrdinal, 1));
            if (Config.PaintKittensEnabled)
                _paintManager.Execute(new SimCommand("", SimActions.PaintKittensEnabled, SimCommand.NoOrdinal, 1));
        }
        _catalog ??= new KsaCatalog(_health, Config.ControlAllVessels, _weldManager, _thugLife,
            _ivaPhysics, _audioActuator, _scheduleStore, _cameraDirector, _faceFx, _paintManager);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DrivePaint()
    {
        try { _paintManager?.Tick(); }
        catch (Exception ex) { ModLog.Log.Error($"gatOS paint tick failed: {ex.Message}"); }
    }

    /// <summary>Snapshots the <c>[iva]</c> config section into the cabin simulation's tuning record.</summary>
    private CabinTuning IvaTuningFromConfig()
        => CabinTuning.Default with
        {
            SubstepHz = Config.IvaSubstepHz,
            MaxSubstepsPerFrame = Config.IvaMaxSubstepsPerFrame,
            Friction = Config.IvaFriction,
            Restitution = Config.IvaRestitution,
            MaxSpeed = Config.IvaMaxSpeed,
            DensityKgM3 = Config.IvaDensityKgM3,
            MaxObjectsPerVessel = Config.IvaMaxObjects,
            MaxObjectSize = Config.IvaMaxObjectSize,
            ImpactSpeed = Config.IvaImpactSpeed,
            DoubleSidedInterior = Config.IvaDoubleSidedInterior,
        };

    /// <summary>
    ///     Plays out the host-side timed schedules on the game thread, immediately <i>before</i> the
    ///     command drain (CAMERA_CONTROLS_PLAN §3.2): activate what transport threads committed,
    ///     advance every live clock, and <see cref="CommandQueue.Post"/> whatever came due — so a
    ///     scheduled command executes on the same frame it fell due, through the ordinary executor,
    ///     health latches and per-frame command budget. Self-gates to one branch while nothing is
    ///     live; a failure disables scheduling for the session (one error log).
    /// </summary>
    /// <remarks>
    ///     <para><b>The three clock bases are sourced here, and only here.</b> <c>render</c> is
    ///     <paramref name="dt"/> — KSA's <c>dtPlayer</c>, which the engine clamps to
    ///     <c>1/MinTargetFrameRate</c>, so it lags true elapsed time after a hitch and never catches
    ///     up (correct for cinematics, and exactly why the other two exist). <c>wall</c> is our own
    ///     <see cref="Stopwatch"/>, which is stopped while the registry is empty so an idle gap is
    ///     never banked into the first tick of the next schedule. <c>ut</c> is the sim-time delta;
    ///     it is unavoidably discontinuous (a scene load rewinds it, warp leaps it forward), so a
    ///     backwards step is clamped to zero rather than rewinding a player's timeline.</para>
    /// </remarks>
    /// <param name="dt">The frame's player-clock delta, in seconds (the <c>render</c> clock base).</param>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void TickSchedules(double dt)
    {
        if (_schedulesDead || _scheduleStore is not { } schedules || _commandQueue is not { } queue)
            return;

        try
        {
            // Free (a static field read behind AggressiveInlining), and needed before Activate so a
            // schedule's `started` event carries the sim time it actually started at.
            var ut = SafeUt();
            schedules.Activate(ut);
            if (schedules.Count == 0)
            {
                // Nothing live. Park the wall clock (stopped, at zero) so the next schedule's first
                // tick advances by one frame rather than by however long the registry sat empty, and
                // forget the UT reading for the same reason.
                _scheduleWallClock.Reset();
                _scheduleUtSeeded = false;
                return;
            }

            using (_scheduleStats.Measure()) // alloc-free; the status window's schedule readout
            {
                double wallMs;
                if (_scheduleWallClock.IsRunning)
                {
                    wallMs = _scheduleWallClock.Elapsed.TotalMilliseconds;
                    _scheduleWallClock.Restart();
                }
                else
                {
                    wallMs = 0; // first tick of a run: there is no previous instant to measure from
                    _scheduleWallClock.Start();
                }

                var utMs = _scheduleUtSeeded ? Math.Max(0, (ut - _scheduleLastUt) * 1000.0) : 0;
                _scheduleLastUt = ut;
                _scheduleUtSeeded = true;

                schedules.AdvanceAll(dt * 1000.0, wallMs, utMs);

                _scheduleDue.Clear(); // reused every tick — never reallocated (GP3 discipline)
                schedules.Tick(_scheduleDue, ut);
                for (var i = 0; i < _scheduleDue.Count; i++)
                {
                    var due = _scheduleDue[i];
                    // Fire-and-forget: the schedule outlives any write that could have waited on it,
                    // so outcomes come back through the observer into schedules/<id>/last_error.
                    queue.Post(due.Command, due.Observer, due.Token);
                }
            }
        }
        catch (Exception ex)
        {
            _schedulesDead = true;
            ModLog.Log.Error($"gatOS schedules disabled after a tick error: {ex.Message}");
        }
    }

    /// <summary>
    ///     The current sim time, or 0 if the universe is not up yet. <c>GetElapsedSeconds</c> is a
    ///     static field read, so this is safe to call every frame — but it throws before the first
    ///     sim step, and a scheduler tick must not die on that.
    /// </summary>
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

    /// <summary>
    ///     Ticks the audio actuator on the game thread, right after the command drain (threading
    ///     rule 1 — the same thread that pumps FMOD): prunes finished channels, enforces
    ///     <c>end=</c>, releases evicted sounds and publishes the <c>/sim/audio/status</c>
    ///     snapshot. Self-gates to nothing while no channel or cached sound exists; a failure
    ///     disables audio for the session (one error log).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DriveAudio()
    {
        if (_audioDead || _audioActuator is not { } audio || audio.IsEmpty)
            return;
        try
        {
            audio.Tick();
        }
        catch (Exception ex)
        {
            _audioDead = true;
            ModLog.Log.Error($"gatOS audio disabled after a tick error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Drives active welds on the game thread from <c>OnAfterUi</c> — after the vehicle-solver
    ///     workers have finished during the render (the manager drains them before mutating state).
    ///     Self-gates to nothing when no welds exist, so the cheat costs nothing when unused and needs
    ///     no Harmony patch. A failure disables welds for the session (one error log).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DriveWelds(double dt)
    {
        if (_weldsDead || _weldManager is not { } welds || welds.IsEmpty)
            return;
        try
        {
            welds.Update(dt);
        }
        catch (Exception ex)
        {
            _weldsDead = true;
            ModLog.Log.Error($"gatOS welds disabled after an update error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Drives the IVA cabin physics on the game thread from <c>OnAfterUi</c> — the same hook (and
    ///     for the same reason) as the welds driver: the vehicle-solver workers have finished, so the
    ///     accelerometer/rates/CoM readings the forcing field needs are the settled values for this
    ///     step. Self-gates to a single branch while the master switch is off or nothing is adopted, so
    ///     the feature costs nothing until a guest explicitly starts it. A failure disables it for the
    ///     session (one error log) after releasing every object.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DriveIvaPhysics(double dt)
    {
        if (_ivaDead || _ivaPhysics is not { } iva || iva.IsIdle)
            return;
        try
        {
            using (_ivaStats.Measure()) // alloc-free; the /sim/debug/iva/stats + status-window readout
                iva.Update(dt);
        }
        catch (Exception ex)
        {
            _ivaDead = true;
            try
            {
                iva.Teardown(); // put every prop back before going dark
            }
            catch (Exception teardownEx)
            {
                ModLog.Log.Debug($"gatOS IVA physics teardown error: {teardownEx.Message}");
            }

            ModLog.Log.Error($"gatOS IVA physics disabled after a driver error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Drives the camera director on the game thread from the main viewport prefix, after KSA has
    ///     advanced simulation state and immediately before it rebuilds view/projection matrices. Self-gates
    ///     to a single branch while gatOS does not own the camera — the default — so the feature costs
    ///     nothing until a guest writes <c>/sim/camera/enabled</c>. A failure disables the camera for
    ///     the session (one error log) after handing the camera back to the game.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DriveCamera(double dt)
    {
        if (_cameraDead || _cameraDirector is not { } camera || camera.IsIdle)
            return;
        try
        {
            camera.Update(dt);
        }
        catch (Exception ex)
        {
            _cameraDead = true;
            try
            {
                camera.Restore(); // never leave the player holding a camera gatOS stopped driving
            }
            catch (Exception restoreEx)
            {
                ModLog.Log.Debug($"gatOS camera restore error: {restoreEx.Message}");
            }

            ModLog.Log.Error($"gatOS camera disabled after a driver error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Entry from <see cref="CameraViewportPatch"/>. Runs the old BeforeGui trio early enough for
    ///     camera commands and shared-clock timed cues to affect this render, then lets the director
    ///     prepare this frame's pose — the write itself happens in-phase, inside KSA's own viewport
    ///     pass, via the installed <c>CameraPoseController</c>.
    /// </summary>
    internal static void PrepareMainViewportFrame(double dt)
    {
        if (_instance is not { IsInitialized: true } mod)
            return;
        mod.SampleTelemetry(dt);
        mod.TickSchedules(dt);
        mod.DrainCommands();
        mod._mainViewportPreparedThisFrame = true;
        mod.DriveCamera(dt);
    }

    /// <summary>Entry from the viewport postfix: publish KSA's final clamped camera transform.</summary>
    internal static void PublishMainViewportCameraStatus()
    {
        if (_instance is { IsInitialized: true, _cameraDead: false, _cameraDirector: { } camera })
            camera.PublishAppliedStatus();
    }

    partial void InstallCameraHook()
    {
        if (_cameraStore is null)
            return;
        try
        {
            _cameraHarmony = new Harmony("gatos.camera");
            if (!CameraViewportPatch.Install(_cameraHarmony))
            {
                _cameraHarmony = null;
                _cameraDead = true;
                return;
            }
            ModLog.Log.Info("gatOS same-frame main-viewport camera hook installed.");
        }
        catch (Exception ex)
        {
            _cameraHarmony = null;
            _cameraDead = true;
            ModLog.Log.Error($"gatOS camera hook failed to install (camera disabled): {ex.Message}");
        }
    }

    partial void RemoveCameraHook()
    {
        try
        {
            _cameraHarmony?.UnpatchAll("gatos.camera");
            _cameraHarmony = null;
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS camera hook unpatch error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Validates thug-life entries on the game thread before the scene renders this frame: drops
    ///     entries whose vehicle is gone and re-resolves each anchor part by InstanceId (robust to
    ///     staging). Self-gates to nothing when empty; a failure disables the feature for the session.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void UpdateThugLife()
    {
        if (_thugLifeDead || _thugLife is not { } thugLife || thugLife.IsEmpty)
            return;
        try
        {
            thugLife.Update();
        }
        catch (Exception ex)
        {
            _thugLifeDead = true;
            ModLog.Log.Error($"gatOS thug-life disabled after an update error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Tears down the runtime cheats at unload: clears every weld, removes the thug-life quads (which
    ///     also unpatches the render hook + frees its GPU resources), releases every IVA floating object
    ///     at its rest pose and disposes its cabin simulation, restores IVA rendering (which unpatches
    ///     the IVA Harmony hooks), and drops every vessel force-render mark (which unpatches the
    ///     <c>gatos.always_render</c> prefixes). Game-coupled, so it is elided without the KSA assemblies.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void TeardownGameCheats()
    {
        try
        {
            _paintManager?.Dispose();
            _paintManager = null;
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS paint teardown error: {ex.Message}");
        }

        try
        {
            // FX editors first: every touched plume template / trail setting / cloud layer / terrain
            // body goes back to the value it had before gatOS wrote it (the reads are still live here).
            FxPristine.RestoreAll();
            FxEditorReader.Reset();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS fx-editor teardown error: {ex.Message}");
        }

        try
        {
            _audioActuator?.Shutdown(); // stops every channel, releases every FMOD Sound, clears the store
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS audio teardown error: {ex.Message}");
        }

        try
        {
            // Stops and drops every live player, plus anything committed but not yet activated: a
            // schedule must never survive into a world its entries were never written against.
            _scheduleStore?.Clear();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS schedule teardown error: {ex.Message}");
        }

        try
        {
            // Hands the main viewport's camera back exactly as it was found (mode, follow, tidal lock,
            // transform, FOV, projection — plus the simulation speed, if and only if a shot's time
            // channel moved it), stops the track player, and drops every uploaded track. Unconditional
            // and idempotent: leaving a player's camera parked in 'fixed' and unfollowed after an
            // unload would look exactly like a broken game.
            _cameraDirector?.Shutdown();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS camera teardown error: {ex.Message}");
        }

        try
        {
            _weldManager?.Clear();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS weld teardown error: {ex.Message}");
        }

        try
        {
            _thugLife?.Clear();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS thug-life teardown error: {ex.Message}");
        }

        try
        {
            // Cuts every gatOS particle burst short (ForceSpawningComplete — the graceful stop) so no
            // pool slot outlives the mod.
            _faceFx?.Shutdown();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS face-FX teardown error: {ex.Message}");
        }

        try
        {
            // Restores every floating prop's exact rest pose and disposes every cabin simulation
            // (and its buffer pool), leaving the master switch off.
            _ivaPhysics?.Teardown();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS IVA physics teardown error: {ex.Message}");
        }

        try
        {
            IvaForceRender.SetEnabled(false);
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS IVA teardown error: {ex.Message}");
        }

        try
        {
            VesselForceRender.Teardown();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS force-render teardown error: {ex.Message}");
        }
    }

    /// <summary>
    ///     STREAM_PLAN.md §4.1: installs the screen-stream render hook — a Harmony transpiler on
    ///     <c>Program.RenderGame</c> that records the capture into the engine's own frame command
    ///     buffer (<see cref="DisplayRenderPatch"/>). Default-off and throttled at the hook, so the
    ///     always-installed patch costs only a branch per frame while the stream is off. A failed
    ///     install leaves the feature dark; telemetry, control and the VM keep working.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void InstallDisplayHook()
    {
        if (_displaySurface is not { } surface)
            return;

        try
        {
            _frameCapture = new FrameCapture();
            DisplayRenderPatch.Bind(_frameCapture, surface);
            _displayHarmony = new Harmony("gatos.display");
            if (DisplayRenderPatch.Install(_displayHarmony))
                ModLog.Log.Info("gatOS screen-stream render hook installed.");
            else
                DisplayRenderPatch.Unbind(); // injection site not found; keep the hook dark
        }
        catch (Exception ex)
        {
            DisplayRenderPatch.Unbind();
            ModLog.Log.Error($"gatOS screen-stream hook failed to install (display disabled): {ex.Message}");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DisposeDisplayCapture()
    {
        // Detach the hook first (stops new recordings), then remove the patch.
        try
        {
            DisplayRenderPatch.Unbind();
            _displayHarmony?.UnpatchAll("gatos.display");
            _displayHarmony = null;
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS display hook unpatch error: {ex.Message}");
        }

        // Drain the submit queue before freeing the capture's buffers: a copy from the last frame(s)
        // may still be in flight. Queue.WaitIdle takes the queue's own lock, so it is safe even if the
        // render thread is mid-submit; a full wait is fine at unload (the game is going away).
        try
        {
            if (Program.GetRenderer()?.GraphicsAndCompute is { } queue)
                queue.WaitIdle();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS display queue drain at unload skipped: {ex.Message}");
        }

        _frameCapture?.Dispose();
        _frameCapture = null;
    }

    /// <summary>
    ///     G4: drains solver-phase commands (refills) inside the vehicle-solver step so the
    ///     mutation is visible to the physics solvers that same tick. Called from the Harmony
    ///     prefix on <see cref="Universe.ExecuteNextVehicleSolvers"/> — still the game thread
    ///     (threading rule 1).
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    internal void DrainSolverCommands()
    {
        if (_commandsDead || _commandQueue is not { } queue)
            return;
        EnsureControlObjects();
        using (_drainStats.Measure()) // shares the stat with the Frame-phase drain (both game-thread)
            queue.Drain(CommandPhase.Solver, _catalog!, Config.MaxCommandsPerFrame);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void InstallSolverHook()
    {
        try
        {
            _solverHarmony = new Harmony("gatos.solver");
            var original = AccessTools.Method(typeof(Universe), nameof(Universe.ExecuteNextVehicleSolvers));
            if (original is null)
            {
                ModLog.Log.Warn("Solver hook: Universe.ExecuteNextVehicleSolvers not found; "
                                + "solver-phase commands (refills) are disabled.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(Mod), nameof(SolverDrainPrefix)) { priority = Priority.First };
            _solverHarmony.Patch(original, prefix: prefix);
            ModLog.Log.Info("gatOS solver-phase command hook installed.");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error($"gatOS solver hook failed to install (solver commands disabled): {ex.Message}");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void RemoveSolverHook()
    {
        try
        {
            _solverHarmony?.UnpatchAll("gatos.solver");
            _solverHarmony = null;
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS solver hook unpatch error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Installs the fallback game-menu Harmony postfix (see <see cref="MenuFallbackPostfix"/>),
    ///     so the gatOS menu is reachable even without the ModMenu mod. Installed early (right after
    ///     the instance is published) so the menu surfaces a failed init just like the ModMenu path.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void InstallMenuFallback()
    {
        try
        {
            _menuHarmony = new Harmony("gatos.menu");
            var original = AccessTools.Method(typeof(Program), nameof(Program.DrawProgramMenusHook));
            if (original is null)
            {
                ModLog.Log.Warn("Menu fallback: Program.DrawProgramMenusHook not found; "
                                + "the gatOS menu is only available via the ModMenu mod.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(Mod), nameof(MenuFallbackPostfix));
            _menuHarmony.Patch(original, postfix: postfix);
            ModLog.Log.Info("gatOS fallback menu hook installed.");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error($"gatOS menu fallback failed to install (menu only via ModMenu): {ex.Message}");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void RemoveMenuFallback()
    {
        try
        {
            _menuHarmony?.UnpatchAll("gatos.menu");
            _menuHarmony = null;
            _modMenuPresent = null; // statics survive a StarMap reload; re-probe next install
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS menu fallback unpatch error: {ex.Message}");
        }
    }

    private static void SolverDrainPrefix()
    {
        try
        {
            _instance?.DrainSolverCommands();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"gatOS solver-phase drain error: {ex.Message}");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    partial void DrawGameUi()
    {
        try
        {
            RenderResetDiskModal();
            if (_statusWindowVisible)
                DrawStatusWindow();
        }
        catch (Exception ex)
        {
            // A diagnostics-UI bug must never break the game's frame.
            ModLog.Log.Debug($"gatOS UI draw error: {ex.Message}");
        }
    }

    private void DrawMenuContent()
    {
        var state = CurrentVmStatus.State;

        if (ImGui.MenuItem("Status", default, _statusWindowVisible))
            _statusWindowVisible = !_statusWindowVisible;

        ImGui.Separator();
        var canStart = IsInitialized && state is VmState.Stopped or VmState.Faulted;
        if (ImGui.MenuItem("Start VM", default, false, canStart))
            StartVm();
        var canStop = state is VmState.Starting or VmState.Running;
        if (ImGui.MenuItem("Shut Down VM", default, false, canStop))
            StopVm();

        ImGui.Separator();
        if (ImGui.BeginMenu("Telemetry", IsInitialized))
        {
            DrawTelemetryMenu();
            ImGui.EndMenu();
        }

        ImGui.Separator();
        if (ImGui.MenuItem("Restart SimFs", default, false, IsInitialized))
            RestartSimFs();
        if (ImGui.MenuItem("Open Data Folder"))
            OpenDataFolder();
        if (ImGui.MenuItem("Reset Disk...", default, false, IsInitialized))
            _resetDiskModalRequested = true;
    }

    /// <summary>
    ///     The Telemetry submenu: the per-stream gates as checkmark items (the data the sampler
    ///     actually computes — turning one off skips its KSA reads <i>and</i> shrinks every
    ///     transport's output) plus the master sample-rate presets. Each change applies live to
    ///     <see cref="Mod.Telemetry"/> and is persisted to <c>gatos.toml</c> as the next startup
    ///     default (off the render thread).
    /// </summary>
    private void DrawTelemetryMenu()
    {
        var t = _telemetrySettings;

        if (ImGui.MenuItem("Sampling enabled", default, t.Enabled))
        {
            t.Enabled = !t.Enabled;
            _config.TelemetryEnabled = t.Enabled;
            PersistConfig();
        }

        if (ImGui.MenuItem("Vessel detail", default, t.VesselDetail, t.Enabled))
        {
            t.VesselDetail = !t.VesselDetail;
            _config.TelemetryVesselDetail = t.VesselDetail;
            PersistConfig();
        }

        if (ImGui.MenuItem("Vessel parts", default, t.VesselParts, t.Enabled))
        {
            t.VesselParts = !t.VesselParts;
            _config.TelemetryVesselParts = t.VesselParts;
            PersistConfig();
        }

        if (ImGui.MenuItem("Celestial bodies", default, t.Bodies, t.Enabled))
        {
            t.Bodies = !t.Bodies;
            _config.TelemetryBodies = t.Bodies;
            PersistConfig();
        }

        if (ImGui.MenuItem("Events", default, t.Events, t.Enabled))
        {
            t.Events = !t.Events;
            _config.TelemetryEvents = t.Events;
            PersistConfig();
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Sample rate: {t.SampleRateHz} Hz");
        foreach (var hz in RatePresets)
            if (ImGui.MenuItem($"{hz} Hz", default, t.SampleRateHz == hz, t.Enabled))
                SetSampleRate(hz);
    }

    private void SetSampleRate(int hz)
    {
        _telemetrySettings.SampleRateHz = hz;
        _config.SampleRateHz = _telemetrySettings.SampleRateHz; // read back the clamped value
        PersistConfig();
        Note($"Telemetry rate set to {_telemetrySettings.SampleRateHz} Hz.");
    }

    private void DrawStatusWindow()
    {
        var status = CurrentVmStatus;
        var open = true;
        // Default to 650 wide (height auto-fits content the first time) and leave the window
        // user-resizable — no AlwaysAutoResize, so a long QEMU log line wraps instead of stretching.
        ImGui.SetNextWindowSize(new float2(650f, 0f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin(StatusWindowId, ref open))
            DrawStatusContent(status);
        ImGui.End();
        if (!open)
            _statusWindowVisible = false;
    }

    private void DrawStatusContent(VmStatus status)
    {
        if (ImGui.BeginTable("##gatos_status_rows", 2, ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("##label", ImGuiTableColumnFlags.WidthFixed, 130f);
            ImGui.TableSetupColumn("##value", ImGuiTableColumnFlags.WidthStretch, 3f);

            Row("VM state");
            ImGui.TextColored(StateColor(status.State), status.State.ToString());
            Row("Accelerator");
            ImGui.Text(status.EffectiveAccel ?? "—");
            Row("SSH port");
            ImGui.Text(status.SshPort?.ToString() ?? "—");
            Row("Sim port");
            ImGui.Text(status.SimPort?.ToString() ?? "—");
            Row("SimFs");
            ImGui.Text(SimFsStatusText());
            Row("HTTP");
            ImGui.Text(HttpStatusText());
            Row("MQTT");
            ImGui.Text(MqttStatusText());
            Row("MCP");
            ImGui.Text(McpStatusText());
            Row("Mounts");
            ImGui.Text(MountsStatusText());
            Row("Uptime");
            ImGui.Text(FormatUptime(status));
            Row("Guest");
            ImGui.Text(Assets?.Manifest is { } m ? $"v{m.GuestVersion} (Alpine {m.AlpineVersion})" : "—");
            Row("Config");
            ImGui.Text($"{Config.MemoryMb} MB RAM, {Config.Cpus} vCPU, {Config.DiskSizeGb} GB disk"
                       + (Config.RestrictNetwork ? ", network restricted" : ""));
            Row("Newest QEMU log");
            ImGui.TextWrapped(NewestQemuLog(status) ?? "—");
            ImGui.EndTable();
        }

        if (OperatingSystem.IsWindows() && status.EffectiveAccel == "tcg")
        {
            ImGui.Spacing();
            ImGui.TextColored(Palette.Warn, "Running under TCG software emulation.");
            ImGui.TextWrapped("For full speed, enable the Windows Hypervisor Platform feature and "
                              + "reboot (admin prompt: DISM /Online /Enable-Feature "
                              + "/FeatureName:HypervisorPlatform).");
        }

        if (status.FaultReason is { } fault)
        {
            ImGui.Spacing();
            ImGui.TextColored(Palette.Error, "Last fault:");
            ImGui.TextWrapped(fault);
        }

        ImGui.Spacing();
        if (Assets is { Ok: true })
        {
            ImGui.TextColored(Palette.Ok, "Mod assets: OK");
        }
        else
        {
            ImGui.TextColored(Palette.Error, "Mod assets: problems found");
            ImGui.TextWrapped(Assets?.Error ?? "Asset validation did not run.");
        }

        DrawTelemetryControls();

        if (LastActionNote is { } note)
        {
            ImGui.Spacing();
            ImGui.TextDisabled(note);
        }
    }

    /// <summary>
    ///     The telemetry tuning block in the status window: a free sample-rate slider (persisted when
    ///     the drag ends) and the per-stream gates as checkboxes. The same live settings + persistence
    ///     the Telemetry menu uses — this just gives the rate a full 1..120 Hz slider.
    /// </summary>
    private void DrawTelemetryControls()
    {
        // Only when wired up: editing (and persisting) settings on a failed init would write a
        // default config over a file we deliberately left untouched (e.g. a parse error).
        if (!IsInitialized)
            return;

        ImGui.Spacing();
        ImGui.SeparatorText("Telemetry");
        var t = _telemetrySettings;

        var rate = t.SampleRateHz;
        ImGui.SetNextItemWidth(200f);
        if (ImGui.SliderInt("Sample rate (Hz)", ref rate, TelemetrySettings.MinRateHz, TelemetrySettings.MaxRateHz))
            t.SampleRateHz = rate; // apply live every drag frame; persist once on release
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            _config.SampleRateHz = t.SampleRateHz;
            PersistConfig();
        }

        var enabled = t.Enabled;
        if (ImGui.Checkbox("Sampling enabled", ref enabled))
        {
            t.Enabled = enabled;
            _config.TelemetryEnabled = enabled;
            PersistConfig();
        }

        var detail = t.VesselDetail;
        if (ImGui.Checkbox("Vessel detail (navball / environment / per-module)", ref detail))
        {
            t.VesselDetail = detail;
            _config.TelemetryVesselDetail = detail;
            PersistConfig();
        }

        var bodies = t.Bodies;
        if (ImGui.Checkbox("Celestial bodies + system", ref bodies))
        {
            t.Bodies = bodies;
            _config.TelemetryBodies = bodies;
            PersistConfig();
        }

        var events = t.Events;
        if (ImGui.Checkbox("Events", ref events))
        {
            t.Events = events;
            _config.TelemetryEvents = events;
            PersistConfig();
        }

        DrawPerfStats();
    }

    /// <summary>
    ///     The performance readout: how long one telemetry sample takes on the game thread (the cost
    ///     the rate + stream toggles drive, and the only gatOS work that can hitch a frame), plus the
    ///     MQTT world-publish cost when a client is connected (the heaviest CPU, but on a background
    ///     pump — it never blocks the game). Both are recorded allocation-free; this only reads them.
    /// </summary>
    private void DrawPerfStats()
    {
        ImGui.Spacing();
        var s = _sampleStats;
        if (s.Count == 0)
        {
            ImGui.TextDisabled("Sample timing: no samples yet (start the VM or connect a client)");
        }
        else
        {
            ImGui.Text($"Sample: avg {s.AvgMicros / 1000:F3} ms, max {s.MaxMicros / 1000:F3} ms, "
                       + $"last {s.LastMicros / 1000:F3} ms");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Game-thread cost of one telemetry sample (all vessels + the enabled "
                                 + "streams). Lower the rate or turn off vessel detail / bodies to reduce it.");
            var a = _sampleAllocStats;
            ImGui.Text($"Sample alloc: avg {a.Avg / 1024:F1} KiB, max {a.Max / 1024.0:F1} KiB, "
                       + $"last {a.Last / 1024.0:F1} KiB");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Bytes allocated on the game thread by one telemetry sample — the GP3 "
                                 + "regression tripwire (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md). "
                                 + "Scales with vessels/parts; spikes on vehicle edits are expected.");
        }

        var d = _drainStats;
        if (d.Count > 0)
        {
            ImGui.Text($"Command drain: avg {d.AvgMicros / 1000:F3} ms, max {d.MaxMicros / 1000:F3} ms");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Game-thread cost of draining queued control commands each frame (and "
                                 + "in the solver phase). Near-zero when idle; the max catches a burst of "
                                 + "writes actuating KSA.");
        }

        // Like the IVA row: only once something has actually been scheduled — an always-present
        // "0 live" line would be noise for the many sessions that never commit a schedule.
        if (_scheduleStore is { } schedules && _scheduleStats.Count > 0)
        {
            var sch = _scheduleStats;
            ImGui.Text($"Schedules: {schedules.Count} live — tick avg {sch.AvgMicros / 1000:F3} ms, "
                       + $"max {sch.MaxMicros / 1000:F3} ms");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Live players in /sim/ctl/schedules and the game-thread cost of one "
                                 + "scheduler tick (advance the clocks, post the due commands). Zero "
                                 + "while nothing is scheduled.");
        }

        if (_mqttBroker is { } mqtt && mqtt.PublishStats.Count > 0)
        {
            var p = mqtt.PublishStats;
            ImGui.Text($"MQTT publish: avg {p.AvgMicros / 1000:F3} ms, max {p.MaxMicros / 1000:F3} ms "
                       + "(background)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cost of serializing + injecting the world topics per snapshot, on a "
                                 + "background pump (never blocks the game; only while an MQTT client is connected).");
        }

        // Only once the feature has actually run — off by default, and its whole point is to cost
        // nothing until someone starts it, so a permanent row would be misleading noise.
        var ivaStats = _ivaStats;
        if (ivaStats.Count > 0)
        {
            ImGui.Text($"IVA physics: avg {ivaStats.AvgMicros / 1000:F3} ms, "
                       + $"max {ivaStats.MaxMicros / 1000:F3} ms");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Game-thread cost of one IVA cabin-physics pass (interior collision + "
                                 + "the floating objects). Zero while /sim/debug/iva/enabled is 0 — "
                                 + "settled objects sleep and cost nothing either.");
        }

        if (_displaySurface is { } disp)
        {
            var ds = disp.Settings;
            var state = !ds.Enabled ? "off"
                : disp.ReaderCount > 0 ? $"on, {disp.ReaderCount} reader(s)"
                : "on, idle (no reader)";
            ImGui.Text($"Display: {state} — {ds.Width}x{ds.Height}@{ds.Fps} {ds.Encoding.Token()}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The /sim/display screen stream. Control it from any SSH client: "
                                 + "echo 1 > /sim/display/enabled, then cat /sim/display/stream in a kitty terminal.");
            var cap = disp.CaptureStat;
            if (cap.Count > 0)
                ImGui.Text($"  capture avg {cap.AvgMicros / 1000:F3} ms, max {cap.MaxMicros / 1000:F3} ms; "
                           + $"encode avg {disp.EncodeStat.AvgMicros / 1000:F3} ms; "
                           + $"skipped {disp.EncodeSkips} demand + {disp.StaticSkips} static");
        }

        if (_simServer is { } simNineP && simNineP.Stats.TreadCount > 0)
        {
            var np = simNineP.Stats;
            ImGui.Text($"9p reads: {np.TreadCount} treads, {np.TreadBytes / (1024.0 * 1024.0):F1} MiB; "
                       + $"send avg {np.SendAvgMicros / 1000:F3} ms");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Rread replies served by the /sim 9p server (all files; the display "
                                 + "stream dominates while it runs) and the mean reply socket-write time. "
                                 + "The transport half of the display pipeline — see PERF_IMPROVEMENT_PLAN.md P4/P8.");
        }

        if (ImGui.SmallButton("Reset perf##gatos_reset_perf"))
        {
            _sampleStats.Reset();
            _ivaStats.Reset();
            _sampleAllocStats.Reset();
            _drainStats.Reset();
            _scheduleStats.Reset();
            _mqttBroker?.PublishStats.Reset();
            _displaySurface?.CaptureStat.Reset();
            _displaySurface?.EncodeStat.Reset();
            _simServer?.Stats.Reset();
        }
    }

    private void RenderResetDiskModal()
    {
        if (_resetDiskModalRequested)
        {
            _resetDiskModalRequested = false;
            ImGui.OpenPopup(ResetDiskPopupId);
        }

        var open = true;
        ImGui.SetNextWindowSize(new float2(520f, 0f), ImGuiCond.Appearing);
        if (!ImGui.BeginPopupModal(ResetDiskPopupId, ref open, ImGuiWindowFlags.AlwaysAutoResize))
            return;

        ImGui.TextWrapped("Reset the gatOS disk? This stops the VM and permanently deletes "
                          + "everything installed or saved inside the guest (packages, files, "
                          + "shell history). The base system is kept — the next session boots a "
                          + "factory-fresh gatOS.");
        ImGui.Spacing();

        var availW = ImGui.GetContentRegionAvail().X;
        const float gap = 8f;
        var buttonWidth = (availW - gap) / 2f;

        if (ImGui.Button(" Reset Disk ##gatos_confirm_reset", new float2(buttonWidth, 0f)))
        {
            ResetDisk();
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine(0, gap);
        if (ImGui.Button(" Cancel ##gatos_cancel_reset", new float2(buttonWidth, 0f)) || !open)
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    /// <summary>
    ///     The newest <c>qemu-*.log</c> under the logs dir — a directory listing, so cached and
    ///     refreshed only when the <see cref="VmStatus"/> snapshot object changes (state
    ///     transitions swap the record reference) or on the first draw.
    /// </summary>
    private string? NewestQemuLog(VmStatus status)
    {
        if (ReferenceEquals(_qemuLogStatusMark, status))
            return _newestQemuLog;

        _qemuLogStatusMark = status;
        try
        {
            _newestQemuLog = Directory.EnumerateFiles(GatOsPaths.LogsDir, "qemu-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (Exception ex)
        {
            _newestQemuLog = null;
            ModLog.Log.Debug($"Could not list the QEMU logs: {ex.Message}");
        }

        return _newestQemuLog;
    }

    private static void Row(string label)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.Text(label);
        ImGui.TableNextColumn();
    }

    private static float4 StateColor(VmState state) => state switch
    {
        VmState.Running => Palette.Ok,
        VmState.Faulted => Palette.Error,
        VmState.Starting or VmState.Stopping => Palette.Warn,
        _ => Palette.Idle,
    };

    private static string FormatUptime(VmStatus status)
    {
        if (status.State != VmState.Running || status.StartedUtc is not { } started)
            return "—";
        var up = DateTime.UtcNow - started;
        return $"{(int)up.TotalHours:00}:{up.Minutes:00}:{up.Seconds:00}";
    }
}
