using Brutal.ImGuiApi;
using gatOS.GameMod.Game;
using gatOS.GameMod.Game.Ksa;
using gatOS.Logging;
using gatOS.SimFs;
using gatOS.SimFs.Commands;
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
            _health ??= new KsaHealth();
            _telemetry ??= new TelemetrySampler(store, _telemetrySettings, _health, _sampleStats);
            // Sample only while something can actually read /sim: the VM is up, or a host-side
            // transport client is connected (9p / HTTP / MQTT). Otherwise the sampler idles for free.
            var state = CurrentVmStatus.State;
            var active = state is VmState.Starting or VmState.Running
                         || (_simServer?.ActiveSessions ?? 0) > 0
                         || (_httpServer?.ActiveSessions ?? 0) > 0
                         || (_mqttBroker?.ConnectedClients ?? 0) > 0;
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
        _catalog ??= new KsaCatalog(_health, Config.ControlAllVessels);
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

        if (_mqttBroker is { } mqtt && mqtt.PublishStats.Count > 0)
        {
            var p = mqtt.PublishStats;
            ImGui.Text($"MQTT publish: avg {p.AvgMicros / 1000:F3} ms, max {p.MaxMicros / 1000:F3} ms "
                       + "(background)");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Cost of serializing + injecting the world topics per snapshot, on a "
                                 + "background pump (never blocks the game; only while an MQTT client is connected).");
        }

        if (ImGui.SmallButton("Reset perf##gatos_reset_perf"))
        {
            _sampleStats.Reset();
            _drainStats.Reset();
            _mqttBroker?.PublishStats.Reset();
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
