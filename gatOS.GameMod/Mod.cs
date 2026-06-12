using System.Diagnostics;
using gatOS.GameMod.Configuration;
using gatOS.Logging;
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

            if (ResolveModDir() is not { } modDir)
                return; // already logged; _assets carries the reason for the status window

            GatOsPaths.ModDir = modDir;
            _assets = ModAssets.Validate();
            _config = GatOsConfig.LoadOrCreate(GatOsPaths.ConfigFile);

            _disks = new DiskManager();
            var vmHost = new VmHost(new VmHostOptions
            {
                Profile = Profile,
                MemoryMb = _config.MemoryMb,
                Cpus = _config.Cpus,
                RestrictNetwork = _config.RestrictNetwork,
                AccelOverride = _config.AccelOverride,
                BootTimeout = _config.BootTimeoutSeconds > 0
                    ? TimeSpan.FromSeconds(_config.BootTimeoutSeconds)
                    : null,
                // SimPortProvider arrives with the 9p server (M8); absent keeps the guest's
                // sim-mount supervisor idle (gatos.simport=0).
            });
            _broker = new VmConnectionBroker(vmHost);

            RegisterShell(_broker);
            IsInitialized = true;
            ModLog.Log.Info("gatOS initialized (VM boots lazily on the first session).");
        }
        catch (Exception ex)
        {
            ModLog.Log.Error("gatOS initialization failed; the mod is inactive for this session.", ex);
            _assets ??= new AssetStatus($"gatOS failed to initialize: {ex.Message}", null, null);
        }
    }

    /// <summary>Per-frame game-thread hook — the M9 telemetry sampler ticks here; idle until then.</summary>
    [StarMapBeforeGui]
    public void OnBeforeUi(double dt)
    {
    }

    /// <summary>Draws the diagnostics UI (T6.4); a no-op when built without the KSA assemblies.</summary>
    [StarMapAfterGui]
    public void OnAfterUi(double dt)
    {
        if (_uiDead || !ReferenceEquals(_instance, this))
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
            var broker = _broker;
            _broker = null;
            if (broker is null)
                return;

            if (broker.DisposeAsync().AsTask().Wait(UnloadWaitBudget))
                ModLog.Log.Info("gatOS unloaded; VM stopped.");
            else
                ModLog.Log.Warn(
                    $"The VM stop did not finish within {UnloadWaitBudget.TotalSeconds:0} s at unload; "
                    + "QEMU will die with the game process (the overlay is crash-consistent qcow2).");
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

    // Game-coupled seams, implemented in Game/Mod.Game.cs (compiled only with the KSA assemblies).
    partial void InstallGameLogging();
    partial void DrawGameUi();
}
