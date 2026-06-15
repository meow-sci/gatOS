using gatOS.Logging;

namespace gatOS.Vm;

/// <summary>
///     The async VM lifecycle state machine (OS_PLAN.md T3.6/T3.7) — the one owner of the QEMU
///     subprocess, its disk lock and its ports.
///     <list type="bullet">
///         <item><description><see cref="EnsureStartedAsync"/> is coalesced: concurrent callers
///             await the same in-flight boot (threading rule 4).</description></item>
///         <item><description><see cref="Status"/> is a volatile snapshot — UI reads it without
///             ever blocking (threading rule 5).</description></item>
///         <item><description><see cref="StopAsync"/> walks the shutdown ladder:
///             QGA <c>guest-shutdown</c> → QMP <c>quit</c> → kill. The overlay is
///             crash-consistent qcow2 either way; rung 1 just gives the guest a clean
///             unmount.</description></item>
///     </list>
/// </summary>
public sealed class VmHost : IAsyncDisposable
{
    private static readonly TimeSpan AcceleratedBootTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan TcgBootTimeout = TimeSpan.FromSeconds(300);
    private static readonly TimeSpan QmpQuitTimeout = TimeSpan.FromSeconds(3);

    private readonly VmHostOptions _options;
    private readonly IDiskManager _disks;
    private readonly Func<IQemuProcess> _processFactory;
    private readonly Func<int, TimeSpan, CancellationToken, Task> _sshProbe;
    private readonly Func<int, IQgaClient> _qgaFactory;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<VmEndpoints>? _bootTask;
    private IQemuProcess? _process;
    private IDisposable? _diskLock;
    private int _qgaPort;
    private volatile VmStatus _status = VmStatus.Stopped;

    /// <summary>Creates a host wired to the real QEMU/disk/probe/QGA implementations.</summary>
    public VmHost(VmHostOptions options)
        : this(options,
            new DiskManager(options.GuestAssetsDir),
            () => new QemuProcess(),
            ReadinessProbe.WaitForSshAsync,
            port => new QgaClient(port))
    {
    }

    internal VmHost(
        VmHostOptions options,
        IDiskManager disks,
        Func<IQemuProcess> processFactory,
        Func<int, TimeSpan, CancellationToken, Task> sshProbe,
        Func<int, IQgaClient> qgaFactory)
    {
        _options = options;
        _disks = disks;
        _processFactory = processFactory;
        _sshProbe = sshProbe;
        _qgaFactory = qgaFactory;
    }

    /// <summary>The current lifecycle snapshot (volatile read; never blocks).</summary>
    public VmStatus Status => _status;

    /// <summary>Raised after every status change, on whatever thread caused it.</summary>
    public event EventHandler<VmStatus>? StatusChanged;

    /// <summary>
    ///     Returns the endpoints of the running VM, booting it first if needed. Concurrent
    ///     callers share one boot; a Faulted host is retried from scratch.
    /// </summary>
    /// <exception cref="VmStartException">The boot failed (see <see cref="VmStartException.UserMessage"/>).</exception>
    public async Task<VmEndpoints> EnsureStartedAsync(CancellationToken ct)
    {
        Task<VmEndpoints> boot;
        await _gate.WaitAsync(ct);
        try
        {
            if (_bootTask is { } existing
                && (!existing.IsCompleted
                    || (existing.IsCompletedSuccessfully && _process is { IsRunning: true })))
            {
                boot = existing;
            }
            else
            {
                boot = _bootTask = BootAsync();
            }
        }
        finally
        {
            _gate.Release();
        }

        // The boot itself is shared and never cancelled by one caller; ct only abandons the wait.
        return await boot.WaitAsync(ct);
    }

    /// <summary>
    ///     Stops the VM via the shutdown ladder (QGA → QMP → kill), waiting an in-flight boot
    ///     out first. Idempotent; always releases the disk lock; ends Stopped.
    /// </summary>
    public async Task StopAsync(TimeSpan grace)
    {
        // Let an in-flight boot settle (outside the gate — the boot needs nothing from us).
        Task<VmEndpoints>? inflight;
        await _gate.WaitAsync();
        try
        {
            inflight = _bootTask;
        }
        finally
        {
            _gate.Release();
        }

        if (inflight is { IsCompleted: false })
        {
            try
            {
                await inflight;
            }
            catch (Exception)
            {
                // Boot failed — its own cleanup ran; nothing left to stop.
            }
        }

        IQemuProcess? process;
        IDisposable? diskLock;
        int qgaPort;
        await _gate.WaitAsync();
        try
        {
            process = _process;
            diskLock = _diskLock;
            qgaPort = _qgaPort;
            _process = null;
            _diskLock = null;
            _bootTask = null;
            if (process is null && diskLock is null && _status.State is VmState.Stopped)
                return; // already stopped — idempotent
        }
        finally
        {
            _gate.Release();
        }

        // Raised outside the gate: a StatusChanged subscriber may call back into this host.
        SetStatus(new VmStatus(VmState.Stopping, _status.EffectiveAccel, _status.SshPort,
            _status.SimPort, _status.StartedUtc, null));
        if (process is not null)
            await RunShutdownLadderAsync(process, qgaPort, grace);
        diskLock?.Dispose();
        SetStatus(VmStatus.Stopped);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync() => await StopAsync(TimeSpan.FromSeconds(10));

    private async Task<VmEndpoints> BootAsync()
    {
        SetStatus(new VmStatus(VmState.Starting, null, null, null, null, null));
        IDisposable? diskLock = null;
        IQemuProcess? process = null;
        try
        {
            var installed = _disks.EnsureBaseInstalled();
            var overlay = _disks.GetOrCreateOverlay(_options.Profile);
            diskLock = _disks.AcquireOverlayLock(_options.Profile);

            // Grow the overlay to the configured size while we hold the lock and before QEMU opens
            // it (grow-only). Best-effort: a resize failure must not block a boot that would still
            // succeed at the current size — the guest's resize2fs just has less room to grow into.
            if (_options.DiskSizeBytes > 0)
            {
                try
                {
                    _disks.EnsureOverlaySize(_options.Profile, _options.DiskSizeBytes);
                }
                catch (DiskOperationException ex)
                {
                    ModLog.Log.Warn($"Disk resize skipped (booting at the current size): {ex.Message}");
                }
            }
            var simPort = _options.SimPortProvider?.Invoke();
            var httpPort = _options.HttpPortProvider?.Invoke();
            var mqttPort = _options.MqttPortProvider?.Invoke();
            var mntPort = _options.MntPortProvider?.Invoke();
            var serialEnabled = _options.SerialEnabled;

            // The port-reuse race window is real but tiny (T3.1): one retry with fresh ports.
            for (var portRetry = false; ; portRetry = true)
            {
                // ports[0..2] = ssh/qga/qmp; ports[3] = the gatos.serial chardev when enabled (G7).
                var ports = PortAllocator.AllocatePorts(serialEnabled ? 4 : 3);
                var serialPort = serialEnabled ? (int?)ports[3] : null;
                var spec = new VmLaunchSpec(
                    OverlayPath: overlay,
                    KernelPath: installed.KernelPath,
                    InitrdPath: installed.InitrdPath,
                    KernelCmdlineBase: installed.Manifest.KernelCmdline,
                    MemoryMb: _options.MemoryMb,
                    Cpus: _options.Cpus,
                    SshHostPort: ports[0], QgaPort: ports[1], QmpPort: ports[2],
                    SimPort: simPort,
                    RestrictNetwork: _options.RestrictNetwork,
                    SerialLogPath: Path.Combine(GatOsPaths.LogsDir, $"serial-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.log"),
                    AccelOverride: _options.AccelOverride,
                    CpuModel: _options.CpuModel,
                    HttpPort: httpPort,
                    MqttPort: mqttPort,
                    MntPort: mntPort,
                    SerialPort: serialPort);

                process = _processFactory();
                try
                {
                    await process.StartAsync(spec, CancellationToken.None);
                }
                catch (VmStartException ex) when (!portRetry && LooksLikePortClash(ex))
                {
                    ModLog.Log.Warn($"VM start hit a port clash; retrying once with fresh ports. ({ex.Message})");
                    await process.DisposeAsync();
                    process = null;
                    continue;
                }

                SetStatus(new VmStatus(VmState.Starting, process.EffectiveAccel, ports[0], simPort, null, null,
                    serialPort));
                var timeout = _options.BootTimeout
                              ?? (process.EffectiveAccel == "tcg" ? TcgBootTimeout : AcceleratedBootTimeout);
                await WaitForSshOrDeathAsync(process, ports[0], timeout);

                await _gate.WaitAsync();
                try
                {
                    _process = process;
                    _diskLock = diskLock;
                    _qgaPort = ports[1];
                }
                finally
                {
                    _gate.Release();
                }

                process.Exited += OnProcessExited;
                if (!process.IsRunning)
                {
                    // Cover the race where it died between the probe and the subscription.
                    OnProcessExited(process, new QemuProcessExitedEventArgs(-1, process.StderrTail));
                    throw new VmStartException("The VM exited right after boot.", process.StderrTail);
                }

                SetStatus(new VmStatus(VmState.Running, process.EffectiveAccel, ports[0], simPort,
                    DateTime.UtcNow, null, serialPort));
                ModLog.Log.Info($"VM running: ssh 127.0.0.1:{ports[0]}, accel {process.EffectiveAccel}, "
                                + $"profile '{_options.Profile}'");
                return new VmEndpoints(ports[0], installed.Manifest.SshUser, installed.PrivateKeyPath,
                    installed.Manifest.HostKeySha256);
            }
        }
        catch (Exception ex)
        {
            diskLock?.Dispose();
            await _gate.WaitAsync();
            try
            {
                if (ReferenceEquals(_diskLock, diskLock))
                    _diskLock = null;
                if (ReferenceEquals(_process, process))
                    _process = null;
            }
            finally
            {
                _gate.Release();
            }

            if (process is not null)
            {
                process.Kill();
                await process.DisposeAsync();
            }

            var start = ex as VmStartException ?? Classify(ex, process);
            SetStatus(new VmStatus(VmState.Faulted, process?.EffectiveAccel, null, null, null, start.UserMessage));
            ModLog.Log.Error($"VM boot failed: {start.Message}", ex);
            throw start;
        }
    }

    /// <summary>Races the SSH readiness probe against the VM dying mid-boot.</summary>
    private async Task WaitForSshOrDeathAsync(IQemuProcess process, int sshPort, TimeSpan timeout)
    {
        using var probeCts = new CancellationTokenSource();
        var probe = _sshProbe(sshPort, timeout, probeCts.Token);
        var death = process.WaitForExitAsync(timeout);
        var winner = await Task.WhenAny(probe, death);
        if (winner == probe)
        {
            await probe; // propagate TimeoutException / probe faults
            return;
        }

        if (await death)
        {
            await probeCts.CancelAsync();
            try
            {
                await probe; // observe the cancellation so the task never goes unobserved
            }
            catch (OperationCanceledException)
            {
            }

            throw new VmStartException(
                $"The VM exited during boot. See log: {process.QemuLogPath}", process.StderrTail);
        }

        await probe; // death-watch timed out together with the probe — surface the probe result
    }

    private async Task RunShutdownLadderAsync(IQemuProcess process, int qgaPort, TimeSpan grace)
    {
        process.Exited -= OnProcessExited; // expected exit — not a fault

        string rung;
        await using var qga = _qgaFactory(qgaPort);
        if (await qga.ShutdownAsync() && await process.WaitForExitAsync(grace))
        {
            rung = "QGA guest-shutdown";
        }
        else if (await process.TryQuitViaQmpAsync(QmpQuitTimeout))
        {
            rung = "QMP quit";
        }
        else
        {
            process.Kill();
            await process.WaitForExitAsync(TimeSpan.FromSeconds(5));
            rung = "kill";
        }

        ModLog.Log.Info($"VM stopped via {rung}");
        await process.DisposeAsync();
    }

    private void OnProcessExited(object? sender, QemuProcessExitedEventArgs e)
    {
        IDisposable? diskLock = null;
        var faulted = false;
        _gate.Wait();
        try
        {
            // Only an exit of the *current* process while Running is a fault; StopAsync
            // unsubscribes first, and a stale process's late event must be ignored.
            if (ReferenceEquals(sender, _process) && _status.State == VmState.Running)
            {
                diskLock = _diskLock;
                _process = null;
                _diskLock = null;
                _bootTask = null;
                faulted = true;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (!faulted)
            return;

        diskLock?.Dispose();
        var reason = $"VM exited unexpectedly (code {e.ExitCode})."
                     + (e.StderrTail.Length > 0 ? $" {e.StderrTail}" : "");
        ModLog.Log.Warn(reason);
        SetStatus(new VmStatus(VmState.Faulted, _status.EffectiveAccel, null, null, null, reason));
    }

    private static bool LooksLikePortClash(VmStartException ex)
        => ex.Message.Contains("Could not set up host forwarding", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase);

    private VmStartException Classify(Exception ex, IQemuProcess? process)
    {
        var logHint = process?.QemuLogPath is { } log ? $" See log: {log}" : "";
        var userMessage = ex switch
        {
            QemuNotFoundException => ex.Message,
            DiskOperationException or InvalidDataException => $"VM disk setup failed: {ex.Message}",
            TimeoutException => $"The VM did not become reachable in time " +
                                $"(accel: {process?.EffectiveAccel ?? "unknown"}).{logHint}",
            _ => $"The VM failed to start: {ex.Message}{logHint}",
        };
        return new VmStartException(userMessage, inner: ex);
    }

    private void SetStatus(VmStatus status)
    {
        _status = status;
        StatusChanged?.Invoke(this, status);
    }
}
