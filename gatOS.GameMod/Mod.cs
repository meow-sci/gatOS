using System.Diagnostics;
using gatOS.Bus;
using gatOS.GameMod.Configuration;
using gatOS.Logging;
using gatOS.Http;
using gatOS.Mqtt;
using gatOS.NineP.Server;
using gatOS.NineP.Vfs;
using gatOS.SimFs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Display;
using gatOS.SimFs.Snapshots;
using gatOS.Ssh;
using gatOS.Vm;
using purrTTY.Core.Terminal;
using StarMap.API;

namespace gatOS.GameMod;

/// <summary>
///     The KSA mod entry point (OS_PLAN.md T6.1): owns the shared VM lifecycle objects, registers
///     the <c>"gatos"</c> custom shell with purrTTY's <see cref="CustomShellRegistry"/>, and hosts
///     the diagnostics UI. The game-coupled half (ModMenu entry, ImGui status window, game-backed
///     logging) lives in the <c>Game/</c> partial files, which compile only when the KSA reference
///     assemblies are present — CI without the private game DLLs still builds this project and the
///     partial-method calls below simply drop out (see gatOS.GameMod.csproj).
/// </summary>
/// <remarks>
///     Lifecycle (D2): nothing boots at game launch — init only validates assets, loads config and
///     registers the shell; the VM starts lazily on the first session (or the menu's Start VM) and
///     is stopped at mod unload. Every hook body is guarded: a gatOS failure must never break game
///     init, frames, or shutdown. Menu/draw code reads only volatile state (threading rule 5); all
///     VM operations run on background tasks.
/// </remarks>
[StarMapMod]
public sealed partial class Mod
{
    private const string ShellId = "gatos";
    private const string Profile = "default";
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnloadWaitBudget = TimeSpan.FromSeconds(15);

    // The static ModMenu entry reaches the live mod through this; set at the top of
    // OnFullyLoaded (even a failed init keeps the menu alive so the player can see why),
    // cleared in Unload.
    private static Mod? _instance;

    private VmConnectionBroker? _broker;
    private DiskManager? _disks;
    private GatOsConfig _config = new();
    private AssetStatus? _assets;
    private int _resetInFlight;
    private bool _uiDead;

    // The /sim stack (T9.3): store + tree are immutable after init; the server reference is
    // volatile because Restart SimFs swaps it from a background task while the render thread
    // reads it (threading rule 5) and the sampler/VM boot consult it on their own threads.
    private SnapshotStore? _simStore;
    private VfsDirectory? _simRoot;
    private volatile NinePServer? _simServer;

    // Runtime-mutable telemetry cadence + per-stream gates. Seeded from config at init; the sampler
    // (game thread) reads it every tick and the gatOS menu / status window mutate it live. Its fields
    // are volatile, so this needs no lock (threading rule 5).
    private TelemetrySettings _telemetrySettings = new();

    // The screen-stream hub (STREAM_PLAN.md): owns the runtime-mutable DisplaySettings + the encode
    // worker + the /sim/display/stream feed. Game-free (from gatOS.SimFs); the render-thread capture
    // (Game/Ksa/FrameCapture) feeds it. Null until init; default-off so it idles until a client enables it.
    private DisplaySurface? _displaySurface;

    // Timing of one telemetry sample (game thread, written by the sampler; read by the status
    // window). Allocation-free; owned here so the status window can read it before the sampler
    // is lazily constructed and survive its disposal.
    private readonly PerfStat _sampleStats = new();

    // Timing of one command-queue drain (game thread — both the per-frame Frame-phase drain and the
    // solver-phase drain accumulate here). Usually ~0 (empty queue); the max catches a hitch when a
    // burst of control commands actuates KSA on a frame.
    private readonly PerfStat _drainStats = new();

    // The magic HTTP transport (G5): same SnapshotStore + command pipeline as the 9p tree, on a
    // loopback port the guest reaches at 10.0.2.2. Volatile — read by the render thread (status)
    // and the VM boot's HttpPortProvider on their own threads.
    private volatile SimHttpServer? _httpServer;

    // The embedded MQTT broker (additional bridge): same store + command pipeline, loopback port,
    // guest reaches it at 10.0.2.2. Volatile for the same reasons as the HTTP server.
    private volatile SimMqttBroker? _mqttBroker;

    // The host-folder mounts 9p server: a second NinePServer whose root lists the configured
    // [[mounts]] as host-backed directories; the guest mounts it once at /mnt. Null when no mount
    // is configured. Volatile — read by the render thread (status) and the VM boot's MntPortProvider.
    private volatile NinePServer? _mountsServer;

    // The G7 serial bridge connector: unlike the slirp servers above this is tied to the VM
    // lifecycle (it connects to QEMU's gatos.serial chardev, which only exists while QEMU runs),
    // so it is started/stopped by OnVmStatusChanged under _serialLock. Volatile for status reads.
    private readonly object _serialLock = new();
    private volatile SerialBridgeConnector? _serialConnector;

    // The control pipeline (KSA_GAME_INTEGRATION_PLAN G1): transport threads submit commands here,
    // the game thread drains them through the KSA actuator catalog each frame. Game-free; the
    // game-coupled drain + executor live in Mod.Game.cs behind the DrainCommands seam.
    private CommandQueue? _commandQueue;

    // Written by background actions, read by the render thread (status window).
    private volatile string? _lastActionNote;

    /// <summary>StarMap contract: gatOS unloads in the normal (late) phase.</summary>
    public bool ImmediateUnload => false;

    /// <summary>Everything constructed and the shell registered; menu actions are available.</summary>
    internal bool IsInitialized { get; private set; }

    /// <summary>Result of the T6.2 asset validation, for the status window.</summary>
    internal AssetStatus? Assets => _assets;

    /// <summary>The loaded user config, for the status window.</summary>
    internal GatOsConfig Config => _config;

    /// <summary>The live, runtime-mutable telemetry cadence + stream gates (menu/status window edit it).</summary>
    internal TelemetrySettings Telemetry => _telemetrySettings;

    /// <summary>Timing of one telemetry sample, for the status window's perf readout.</summary>
    internal PerfStat SampleStats => _sampleStats;

    /// <summary>Timing of one command-queue drain (game thread), for the status window's perf readout.</summary>
    internal PerfStat DrainStats => _drainStats;

    /// <summary>Transient result of the last menu action, for the status window.</summary>
    internal string? LastActionNote => _lastActionNote;

    /// <summary>The volatile VM lifecycle snapshot (never blocks; threading rule 5).</summary>
    internal VmStatus CurrentVmStatus => _broker?.VmHost.Status ?? VmStatus.Stopped;

    /// <summary>The renderer is not live yet — nothing to do here (OS_PLAN.md T6.1).</summary>
    [StarMapImmediateLoad]
    public void OnImmediateLoad()
    {
    }

    /// <summary>
    ///     Initializes gatOS: game-backed logging, mod-dir resolution, asset validation (T6.2),
    ///     config (T6.3), the <see cref="VmHost"/> + <see cref="VmConnectionBroker"/> pair (no
    ///     boot!), and the shell registration. Never throws.
    /// </summary>
    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        try
        {
            TryInstallGameLogging();
            _instance = this;

            // Install the fallback "gatOS" menu now (before any early return below) so the menu is
            // reachable even when ModMenu is absent and even when init fails — mirroring the way the
            // [ModMenuEntry] path keeps the menu alive to surface the failure (partial seam: drops
            // out without the KSA assemblies).
            InstallMenuFallback();

            if (ResolveModDir() is not { } modDir)
                return; // already logged; _assets carries the reason for the status window

            GatOsPaths.ModDir = modDir;
            _assets = ModAssets.Validate();
            // Seed the data-dir config from the copy shipped in the mod folder on first run, so
            // common settings (memory/CPUs/disk) edited before launch take effect (T6.3 as-built).
            _config = GatOsConfig.LoadOrCreate(GatOsPaths.ConfigFile, GatOsPaths.BundledConfigFile);

            // T9.3: the /sim stack. The 9p server binds an ephemeral loopback port now, so a
            // VM booted at any later moment finds gatos.simport=<port> on its kernel cmdline
            // and the guest's sim-mount supervisor mounts /sim on its own. The store stays at
            // SimSnapshot.Empty until the sampler's first publish.
            _simStore = new SnapshotStore();
            _telemetrySettings = new TelemetrySettings(
                _config.SampleRateHz, _config.TelemetryEnabled, _config.TelemetryVesselDetail,
                _config.TelemetryBodies, _config.TelemetryEvents, _config.TelemetryVesselParts);
            _commandQueue = new CommandQueue(_config.ControlEnabled, _config.DebugNamespace,
                TimeSpan.FromMilliseconds(_config.CommandTimeoutMs));
            _displaySurface = new DisplaySurface(new DisplaySettings(
                _config.DisplayEnabled, _config.DisplayFps, _config.DisplayWidth, _config.DisplayHeight,
                DisplayEncodings.Parse(_config.DisplayEncoding) ?? DisplayEncoding.RgbaZlib));
            // Standing debug harness (STREAM_PLAN.md §11): setting PngDumpDirectory (e.g. to
            // Path.Combine(GatOsPaths.DataDir, ".tmp-screencaps")) makes the encode worker dump
            // 1 PNG + .kitty pair per second instead of publishing Kitty bytes — the tier-1/2
            // capture/encode validation used to corner the 2026-07 libghostty o=z corruption.
            _displaySurface.Start();
            _simRoot = SimFsTree.Build(_simStore, _commandQueue, SimTransportsStatus, _displaySurface);
            StartSimServer(port: 0);
            StartHttpServer();
            StartMqttBroker();
            StartMountsServer();

            _disks = new DiskManager();
            var vmHost = new VmHost(new VmHostOptions
            {
                Profile = Profile,
                MemoryMb = _config.MemoryMb,
                Cpus = _config.Cpus,
                DiskSizeBytes = (long)_config.DiskSizeGb * 1024 * 1024 * 1024,
                RestrictNetwork = _config.RestrictNetwork,
                AccelOverride = _config.AccelOverride,
                CpuModel = _config.CpuModel,
                BootTimeout = _config.BootTimeoutSeconds > 0
                    ? TimeSpan.FromSeconds(_config.BootTimeoutSeconds)
                    : null,
                // A failed/missing server returns null → the guest supervisor idles
                // (gatos.simport=0); the rest of gatOS works without /sim.
                SimPortProvider = () => _simServer is { Port: > 0 } server ? server.Port : null,
                // The guest reaches the host HTTP server outbound at 10.0.2.2:<port> (slirp);
                // gatos.httpport on the cmdline lets the guest discover it. Null = guest HTTP env unset.
                HttpPortProvider = () => _httpServer is { Port: > 0 } http ? http.Port : null,
                MqttPortProvider = () => _mqttBroker is { Port: > 0 } mqtt ? mqtt.Port : null,
                // Host folder mounts: the guest's mnt-mount supervisor mounts /mnt from this port.
                // Null (no mounts configured / bind failed) → gatos.mntport=0 → nothing under /mnt.
                MntPortProvider = () => _mountsServer is { Port: > 0 } mounts ? mounts.Port : null,
                // G7: allocate the gatos.serial chardev port when a serial direction is enabled.
                // The host bridge (OnVmStatusChanged) connects to it once the VM is Running.
                SerialEnabled = SerialEnabled(),
            });
            // The serial bridge follows the VM lifecycle (the chardev only exists while QEMU runs).
            vmHost.StatusChanged += OnVmStatusChanged;
            _broker = new VmConnectionBroker(vmHost);

            RegisterShell(_broker);

            // G4: drain solver-phase control commands (refills) inside the vehicle-solver step.
            // A no-op build without the KSA assemblies drops this call (partial seam).
            InstallSolverHook();

            // STREAM_PLAN.md: inject the screen-stream capture into KSA's render loop (records into
            // the engine's own frame command buffer). Default-off; drops out without the KSA assemblies.
            InstallDisplayHook();

            IsInitialized = true;
            ModLog.Log.Info("gatOS initialized (VM boots lazily on the first session).");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error("gatOS initialization failed; the mod is inactive for this session.", ex);
            _assets ??= new AssetStatus($"gatOS failed to initialize: {ex.Message}", null, null);
        }
    }

    /// <summary>
    ///     Per-frame game-thread hook: the telemetry sampler ticks and queued control commands
    ///     drain here (T9.1 + G1, threading rule 1 — both touch game state only on this thread).
    /// </summary>
    [StarMapBeforeGui]
    public void OnBeforeUi(double dt)
    {
        SampleTelemetry(dt);
        DrainCommands();
        UpdateThugLife(); // validate/re-resolve thug-life anchors on the game thread, before the scene renders
    }

    /// <summary>
    ///     The <c>/sim/status/transports</c> provider (read on 9p threads — reads only volatile
    ///     state). Reports the bound 9p port and whether control writes are enabled.
    /// </summary>
    private string SimTransportsStatus()
    {
        var server = _simServer;
        var ninep = server is { Port: > 0 } ? server.Port.ToString() : "unbound";
        var http = _httpServer is { Port: > 0 } h ? h.Port.ToString() : "off";
        var mqtt = _mqttBroker is { Port: > 0 } m ? m.Port.ToString() : "off";
        var mnt = _mountsServer is { Port: > 0 } mt ? mt.Port.ToString() : "off";
        var serial = SerialStatusText();
        var control = _commandQueue is { ControlEnabled: true } ? "on" : "off";
        return $"9p {ninep}\nhttp {http}\nmqtt {mqtt}\nmnt {mnt}\nserial {serial}\ncontrol {control}";
    }

    /// <summary>
    ///     Draws the diagnostics UI (T6.4) and drives active welds (both game-thread, no-ops without the
    ///     KSA assemblies). The weld drive runs first and independently of the UI so a UI fault never
    ///     stops welds — and it self-gates to nothing when no welds exist.
    /// </summary>
    [StarMapAfterGui]
    public void OnAfterUi(double dt)
    {
        if (!ReferenceEquals(_instance, this))
            return;

        DriveWelds(dt); // partial; self-gates to a no-op when no welds are active

        if (_uiDead)
            return;

        try
        {
            DrawGameUi(); // partial; per-frame errors are swallowed inside (Debug-logged)
        }
        catch (Exception ex)
        {
            // Reaching here means the UI cannot even be entered (e.g. a type-load failure);
            // disable it for the session instead of failing — and spamming — every frame.
            _uiDead = true;
            ModLog.Log.Error($"gatOS diagnostics UI disabled after a draw error: {ex.Message}");
        }
    }

    /// <summary>
    ///     Stops the VM synchronously bounded: the shutdown ladder gets <see cref="StopGrace"/>
    ///     (QGA → QMP → kill) and game exit is never held longer than <see cref="UnloadWaitBudget"/>.
    /// </summary>
    [StarMapUnload]
    public void Unload()
    {
        try
        {
            _instance = null;
            IsInitialized = false;
            TeardownGameCheats(); // partial: clears welds + restores IVA (unpatches the IVA hooks)
            RemoveSolverHook();   // partial: drops out without the KSA assemblies
            RemoveMenuFallback(); // partial: drops out without the KSA assemblies
            DisposeDisplayCapture(); // partial: frees the capture's Vulkan resources (drops out without KSA)
            _displaySurface?.Dispose();
            _displaySurface = null;

            // Stop the serial bridge first (it follows the VM); then drop our status subscription.
            StopSerialConnector();
            var broker = _broker;
            _broker = null;
            if (broker is not null)
            {
                broker.VmHost.StatusChanged -= OnVmStatusChanged;
                if (broker.DisposeAsync().AsTask().Wait(UnloadWaitBudget))
                    ModLog.Log.Info("gatOS unloaded; VM stopped.");
                else
                    ModLog.Log.Warn(
                        $"The VM stop did not finish within {UnloadWaitBudget.TotalSeconds:0} s at unload; "
                        + "QEMU will die with the game process (the overlay is crash-consistent qcow2).");
            }

            // The 9p server goes after the VM: its mounts die with the guest anyway.
            var simServer = _simServer;
            _simServer = null;
            if (simServer is not null && !simServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                ModLog.Log.Warn("The /sim server did not stop within 5 s at unload.");

            var httpServer = _httpServer;
            _httpServer = null;
            if (httpServer is not null && !httpServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                ModLog.Log.Warn("The HTTP server did not stop within 5 s at unload.");

            var mqttBroker = _mqttBroker;
            _mqttBroker = null;
            if (mqttBroker is not null && !mqttBroker.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                ModLog.Log.Warn("The MQTT broker did not stop within 5 s at unload.");

            var mountsServer = _mountsServer;
            _mountsServer = null;
            if (mountsServer is not null && !mountsServer.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)))
                ModLog.Log.Warn("The host-mounts server did not stop within 5 s at unload.");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error($"gatOS unload error: {ex.Message}");
        }
    }

    /// <summary>Menu action: boots the VM in the background (concurrent callers share one boot).</summary>
    internal void StartVm()
    {
        if (_broker is not { } broker)
        {
            Note("gatOS is not initialized.");
            return;
        }

        Note("Starting the VM…");
        _ = Task.Run(async () =>
        {
            try
            {
                await broker.VmHost.EnsureStartedAsync(CancellationToken.None);
                Note(null);
            }
            catch (VmStartException ex)
            {
                Note(null); // the status window already shows VmStatus.FaultReason
                ModLog.Log.Error($"VM start from the menu failed: {ex.UserMessage}");
            }
            catch (Exception ex)
            {
                Note($"VM start failed: {ex.Message}");
                ModLog.Log.Error("VM start from the menu failed.", ex);
            }
        });
    }

    /// <summary>Menu action: walks the shutdown ladder in the background. Sessions terminate.</summary>
    internal void StopVm()
    {
        if (_broker is not { } broker)
        {
            Note("gatOS is not initialized.");
            return;
        }

        Note("Shutting the VM down…");
        _ = Task.Run(async () =>
        {
            try
            {
                await broker.VmHost.StopAsync(StopGrace);
                Note(null);
            }
            catch (Exception ex)
            {
                Note($"VM stop failed: {ex.Message}");
                ModLog.Log.Error("VM stop from the menu failed.", ex);
            }
        });
    }

    /// <summary>
    ///     Confirmed reset (T6.4 modal): stops the VM, then deletes the <c>default</c> profile
    ///     overlay — everything inside the guest is lost; the next boot is factory-fresh.
    /// </summary>
    internal void ResetDisk()
    {
        if (_broker is not { } broker || _disks is not { } disks)
        {
            Note("gatOS is not initialized.");
            return;
        }

        if (Interlocked.Exchange(ref _resetInFlight, 1) == 1)
            return;

        Note("Resetting the gatOS disk…");
        _ = Task.Run(async () =>
        {
            try
            {
                await broker.VmHost.StopAsync(StopGrace);
                disks.DeleteOverlay(Profile);
                Note("Disk reset — the next session boots a fresh gatOS.");
            }
            catch (Exception ex)
            {
                Note($"Disk reset failed: {ex.Message}");
                ModLog.Log.Error("Disk reset failed.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _resetInFlight, 0);
            }
        });
    }

    /// <summary>
    ///     Menu action (T9.3 debug aid): bounces the /sim 9p server <b>on the same port</b> —
    ///     the port is baked into a running guest's kernel cmdline, so a rebind elsewhere
    ///     would orphan the mount. The guest's sim-mount supervisor remounts within ~4 s.
    /// </summary>
    internal void RestartSimFs()
    {
        Note("Restarting SimFs…");
        _ = Task.Run(async () =>
        {
            try
            {
                var old = _simServer;
                _simServer = null;
                var port = old is { Port: > 0 } ? old.Port : 0;
                if (old is not null)
                    await old.DisposeAsync();
                StartSimServer(port);
                Note(_simServer is null ? "SimFs failed to restart (see the log)." : null);
            }
            catch (Exception ex)
            {
                Note($"SimFs restart failed: {ex.Message}");
                ModLog.Log.Error("SimFs restart failed.", ex);
            }
        });
    }

    /// <summary>One status-window line for the /sim server (read on the render thread).</summary>
    internal string SimFsStatusText()
    {
        var server = _simServer;
        return server is null
            ? "not running"
            : $"port {server.Port}, {server.ActiveSessions} connection(s)";
    }

    private void StartHttpServer()
    {
        if (!_config.HttpEnabled || _simStore is not { } store)
            return;
        try
        {
            var server = new SimHttpServer(store, _commandQueue, SimTransportsStatus,
                _config.HttpFieldEndpoints ? _simRoot : null);
            server.StartAsync(_config.HttpPreferredPort).GetAwaiter().GetResult();
            _httpServer = server;
            ModLog.Log.Info($"gatOS HTTP API listening on 127.0.0.1:{server.Port} "
                            + "(guest: $GATOS_HTTP / http://sim:<port>/v1).");
        }
        catch (Exception ex)
        {
            // HTTP is an optional luxury like /sim: the VM, shells and 9p work without it.
            _httpServer = null;
            ModLog.Log.Error($"The magic HTTP server failed to start: {ex.Message}", ex);
        }
    }

    /// <summary>One status-window line for the HTTP server (read on the render thread).</summary>
    internal string HttpStatusText()
    {
        var server = _httpServer;
        return server is null
            ? (_config.HttpEnabled ? "not running" : "disabled")
            : $"port {server.Port}, {server.ActiveSessions} connection(s)";
    }

    private void StartMqttBroker()
    {
        if (!_config.MqttEnabled || _simStore is not { } store)
            return;
        try
        {
            var broker = new SimMqttBroker(store, _commandQueue, SimTransportsStatus,
                _config.MqttFieldTopics ? _simRoot : null, _config.FieldFeedHz);
            broker.StartAsync(_config.MqttPreferredPort).GetAwaiter().GetResult();
            _mqttBroker = broker;
            ModLog.Log.Info($"gatOS MQTT broker listening on 127.0.0.1:{broker.Port} "
                            + "(guest: $GATOS_MQTT / 10.0.2.2; topics under gatos/).");
        }
        catch (Exception ex)
        {
            _mqttBroker = null;
            ModLog.Log.Error($"The MQTT broker failed to start: {ex.Message}", ex);
        }
    }

    /// <summary>One status-window line for the MQTT broker (read on the render thread).</summary>
    internal string MqttStatusText()
    {
        var broker = _mqttBroker;
        return broker is null
            ? (_config.MqttEnabled ? "not running" : "disabled")
            : $"port {broker.Port}";
    }

    /// <summary>
    ///     Starts the host-folder mounts 9p server when <c>[[mounts]]</c> entries are configured: a
    ///     second <see cref="NinePServer"/> whose root lists each mount as a host-backed directory,
    ///     so the guest mounts it once at <c>/mnt</c> and finds <c>/mnt/&lt;name&gt;</c> for each.
    ///     A no-op when no mount is configured; failures are logged (mounts are optional like /sim).
    /// </summary>
    private void StartMountsServer()
    {
        if (_config.Mounts.Count == 0)
            return;
        try
        {
            var specs = new List<HostMountSpec>(_config.Mounts.Count);
            foreach (var mount in _config.Mounts)
            {
                if (!Directory.Exists(mount.Path))
                    ModLog.Log.Warn($"gatOS mount '{mount.Name}' path does not exist yet: '{mount.Path}' "
                                    + "(it will appear empty until the folder is created).");
                specs.Add(new HostMountSpec(mount.Name, mount.Path, Writable: !mount.ReadOnly));
            }

            var server = new NinePServer(HostMountTree.Build(specs));
            server.StartAsync(0).GetAwaiter().GetResult();
            _mountsServer = server;
            var names = string.Join(", ", specs.Select(s => $"/mnt/{s.Name}{(s.Writable ? " (rw)" : " (ro)")}"));
            ModLog.Log.Info($"gatOS host mounts on 127.0.0.1:{server.Port}: {names}.");
        }
        catch (Exception ex)
        {
            _mountsServer = null;
            ModLog.Log.Error($"The host-folder mounts server failed to start: {ex.Message}", ex);
        }
    }

    /// <summary>One status-window line for the host-folder mounts server (read on the render thread).</summary>
    internal string MountsStatusText()
    {
        var server = _mountsServer;
        if (server is null)
            return _config.Mounts.Count == 0 ? "none configured" : "not running";
        return $"port {server.Port}, {_config.Mounts.Count} folder(s), {server.ActiveSessions} connection(s)";
    }

    /// <summary>True when any serial direction is configured (G7).</summary>
    private bool SerialEnabled() => _config.SerialTelemetryPort || _config.SerialCommandPort;

    /// <summary>
    ///     Starts/stops the G7 serial bridge with the VM lifecycle: the QEMU <c>gatos.serial</c>
    ///     chardev only exists while QEMU runs, so we connect on Running (with the port from the
    ///     status) and tear down on every other transition. Fires on a VM thread (boot/stop) —
    ///     non-blocking: the teardown is fire-and-forget.
    /// </summary>
    private void OnVmStatusChanged(object? sender, VmStatus status)
    {
        if (status is { State: VmState.Running, SerialPort: { } port }
            && SerialEnabled() && _simStore is { } store)
        {
            lock (_serialLock)
            {
                if (_serialConnector is not null)
                    return; // already connected for this run
                var bridge = new SerialBridge(store, ParseSerialMode(_config.SerialMode),
                    TimeSpan.FromMilliseconds(_config.SerialIntervalMs),
                    emitTelemetry: _config.SerialTelemetryPort,
                    commands: _config.SerialCommandPort ? _commandQueue : null);
                var connector = new SerialBridgeConnector(port, bridge);
                _serialConnector = connector;
                connector.Start();
                ModLog.Log.Info($"gatOS serial bridge connecting to gatos.serial on 127.0.0.1:{port} "
                                + "(guest: /dev/virtio-ports/gatos.serial).");
            }
        }
        else
        {
            StopSerialConnector();
        }
    }

    /// <summary>Tears down the serial connector if running (fire-and-forget dispose; idempotent).</summary>
    private void StopSerialConnector()
    {
        SerialBridgeConnector? connector;
        lock (_serialLock)
        {
            connector = _serialConnector;
            _serialConnector = null;
        }

        if (connector is not null)
            _ = Task.Run(() => connector.DisposeAsync().AsTask());
    }

    private static SerialMode ParseSerialMode(string mode) => mode switch
    {
        "nmea" => SerialMode.Nmea,
        "ccsds" => SerialMode.Ccsds,
        _ => SerialMode.Ndjson,
    };

    /// <summary>One status line for the serial bridge (read on the render thread).</summary>
    internal string SerialStatusText()
    {
        if (!SerialEnabled())
            return "disabled";
        var directions = (_config.SerialTelemetryPort, _config.SerialCommandPort) switch
        {
            (true, true) => $"tlm+cmd {_config.SerialMode}",
            (true, false) => $"tlm {_config.SerialMode}",
            (false, true) => "cmd",
            _ => "off",
        };
        return _serialConnector is null ? $"{directions} (waiting)" : $"{directions} (connected)";
    }

    private void StartSimServer(int port)
    {
        if (_simRoot is not { } root)
            return;
        try
        {
            var server = new NinePServer(root);
            server.StartAsync(port).GetAwaiter().GetResult(); // a synchronous bind (T7.4)
            _simServer = server;
        }
        catch (Exception ex)
        {
            // /sim is an optional luxury: shells and VM management work without it.
            _simServer = null;
            ModLog.Log.Error($"The /sim 9p server failed to start: {ex.Message}", ex);
        }
    }

    /// <summary>Menu action: opens the gatOS data dir (disks, logs, config) in the OS file manager.</summary>
    internal void OpenDataFolder()
    {
        var dir = GatOsPaths.DataDir;
        _ = Task.Run(() =>
        {
            try
            {
                var psi = OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                    : OperatingSystem.IsMacOS() ? new ProcessStartInfo("open", $"\"{dir}\"")
                    : new ProcessStartInfo("xdg-open", $"\"{dir}\"");
                Process.Start(psi)?.Dispose();
            }
            catch (Exception ex)
            {
                ModLog.Log.Warn($"Could not open the data folder '{dir}': {ex.Message}");
            }
        });
    }

    private void Note(string? message) => _lastActionNote = message;

    /// <summary>
    ///     Persists the current config to disk on a background task so an in-game settings change
    ///     becomes the next session's startup default. Off the render thread (threading rule 5: no
    ///     file I/O in draw code); failures are logged, never thrown — the live change already took
    ///     effect via <see cref="Telemetry"/>.
    /// </summary>
    private void PersistConfig()
    {
        var config = _config;
        _ = Task.Run(() =>
        {
            try
            {
                config.Save(GatOsPaths.ConfigFile);
            }
            catch (Exception ex)
            {
                ModLog.Log.Warn($"Could not persist gatOS settings: {ex.Message}");
            }
        });
    }

    /// <summary>
    ///     Isolates the game-logging swap so its failure can never abort init: a missing game
    ///     assembly surfaces as a type-load error at the <i>call site</i> of the partial method
    ///     (JIT-time), which this wrapper catches — the same lesson the purrTTY registry's
    ///     <c>SafeLogDebug</c> documents. gatOS then simply stays on the console logger.
    /// </summary>
    private void TryInstallGameLogging()
    {
        try
        {
            InstallGameLogging(); // partial: compiled out when the KSA assemblies are absent
        }
        catch (Exception ex)
        {
            Console.WriteLine($"gatOS: game-backed logging unavailable, staying on console: {ex.Message}");
        }
    }

    private string? ResolveModDir()
    {
        var location = typeof(Mod).Assembly.Location;
        var dir = string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        if (dir is null)
        {
            const string error =
                "Could not locate the gatOS mod folder (the entry assembly has no on-disk location); "
                + "the bundled guest image and QEMU are unreachable.";
            ModLog.Log.Error(error);
            _assets = new AssetStatus(error, null, null);
        }

        return dir;
    }

    private static void RegisterShell(VmConnectionBroker broker)
    {
        try
        {
            // The registry probe-instantiates (and disposes) one session to validate Metadata —
            // SshShellSession's ctor is trivial by contract (T0.5), so this never touches the VM.
            CustomShellRegistry.Instance.RegisterShell(ShellId, () => new SshShellSession(broker));
        }
        catch (Exception ex)
        {
            // Duplicate id or probe failure — only the terminal UI is lost; VM management stays up.
            ModLog.Log.Error($"Could not register the '{ShellId}' shell: {ex.Message}", ex);
            return;
        }

        // D6: with purrTTY installed, the contract assembly resolved from purrTTY's ALC and the
        // registration above landed in the registry purrTTY's menus enumerate. If it loaded from
        // our own mod folder instead, purrTTY is absent and nobody consumes the registry.
        var contractDir = SafeAssemblyDir(typeof(CustomShellRegistry));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var ownCopy = contractDir is not null && GatOsPaths.ModDir is { } modDir
                      && string.Equals(
                          Path.TrimEndingDirectorySeparator(contractDir),
                          Path.TrimEndingDirectorySeparator(modDir),
                          comparison);
        if (ownCopy)
        {
            ModLog.Log.Warn(
                "purrTTY not detected: terminal UI unavailable. Install purrTTY to open gatOS "
                + "sessions (the VM can still be managed from the gatOS menu).");
        }
        else
        {
            ModLog.Log.Info($"Registered shell '{ShellId}' with purrTTY (contract: {contractDir ?? "unknown"}).");
        }
    }

    private static string? SafeAssemblyDir(Type type)
    {
        try
        {
            var location = type.Assembly.Location;
            return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
        }
        catch
        {
            return null; // dynamic/byte-loaded assemblies have no location — treat as unknown
        }
    }

    // Game-coupled seams, implemented in the Game/ partial files (compiled only with the KSA
    // assemblies; the calls drop out otherwise).
    partial void InstallGameLogging();
    partial void DrawGameUi();
    partial void SampleTelemetry(double dt);
    partial void DrainCommands();
    partial void InstallDisplayHook();
    partial void DisposeDisplayCapture();
    partial void DriveWelds(double dt);
    partial void UpdateThugLife();
    partial void TeardownGameCheats();
    partial void InstallSolverHook();
    partial void RemoveSolverHook();
    partial void InstallMenuFallback();
    partial void RemoveMenuFallback();
}
