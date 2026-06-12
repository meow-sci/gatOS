using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using gatOS.Logging;

namespace gatOS.Vm;

/// <summary>
///     Spawns and supervises one QEMU subprocess (OS_PLAN.md T3.4). stdout/stderr are appended
///     to <c>LogsDir/qemu-&lt;utc&gt;.log</c> (last 5 logs retained), the last 100 stderr lines
///     are kept in memory for fault surfacing, and an early exit that looks accelerator-related
///     (<see cref="AccelFailureClassifier"/>) is retried once with TCG forced.
/// </summary>
public sealed class QemuProcess : IQemuProcess
{
    /// <summary>
    ///     How long a freshly spawned QEMU must survive before we call the launch good.
    ///     Accel-init failures exit within a few hundred ms; the readiness probe that follows
    ///     owns the rest of the wait, so this only bounds added boot latency.
    /// </summary>
    public static readonly TimeSpan SurvivalWindow = TimeSpan.FromSeconds(3);

    private const int StderrRingCapacity = 100;
    private const int RetainedLogFiles = 5;

    private readonly QemuBinaries? _binaries;
    private readonly QemuCommandBuilder _builder;
    private readonly object _gate = new();
    private readonly Queue<string> _stderrRing = new();

    private Process? _process;
    private TaskCompletionSource<int>? _exitTcs;
    private Task? _pumpTask;
    private StreamWriter? _logWriter;
    private int _qmpPort;
    private bool _exitRaised;

    /// <param name="binaries">QEMU binaries; defaults to <see cref="QemuLocator.Find"/> at start time.</param>
    /// <param name="builder">Command builder; defaults to the current host's.</param>
    public QemuProcess(QemuBinaries? binaries = null, QemuCommandBuilder? builder = null)
    {
        _binaries = binaries;
        _builder = builder ?? QemuCommandBuilder.ForCurrentHost;
    }

    /// <inheritdoc/>
    public bool IsRunning => _process is { HasExited: false };

    /// <inheritdoc/>
    public string? EffectiveAccel { get; private set; }

    /// <inheritdoc/>
    public string StderrTail
    {
        get
        {
            lock (_gate)
                return string.Join(Environment.NewLine, _stderrRing);
        }
    }

    /// <inheritdoc/>
    public string? QemuLogPath { get; private set; }

    /// <inheritdoc/>
    public event EventHandler<QemuProcessExitedEventArgs>? Exited;

    /// <inheritdoc/>
    public async Task StartAsync(VmLaunchSpec spec, CancellationToken ct)
    {
        if (_process is not null)
            throw new InvalidOperationException("This QemuProcess has already been started (one instance per launch).");

        QemuBinaries binaries;
        try
        {
            binaries = _binaries ?? QemuLocator.Find();
        }
        catch (QemuNotFoundException ex)
        {
            throw new VmStartException(ex.Message, inner: ex);
        }

        PruneOldLogs();
        QemuLogPath = Path.Combine(GatOsPaths.LogsDir, $"qemu-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.log");
        _logWriter = new StreamWriter(
            new FileStream(QemuLogPath, FileMode.Append, FileAccess.Write, FileShare.Read))
        { AutoFlush = true };
        _qmpPort = spec.QmpPort;

        var attempt = spec;
        for (var retried = false; ; retried = true)
        {
            ct.ThrowIfCancellationRequested();
            var ladder = _builder.ResolveAccelLadder(attempt);
            LaunchOnce(binaries, attempt, ladder[0]);

            // Survival window: an accel-init failure exits almost immediately; surviving it
            // means the launch took. The SSH readiness probe owns the rest of the boot wait.
            var exitTask = _exitTcs!.Task;
            if (await Task.WhenAny(exitTask, Task.Delay(SurvivalWindow, ct)) != exitTask)
            {
                EffectiveAccel = ladder[0];
                ModLog.Log.Info($"QEMU running (pid {_process!.Id}, accel {EffectiveAccel}, log {QemuLogPath})");
                return;
            }

            await DrainPumpsAsync();
            var exitCode = exitTask.Result;
            var tail = StderrTail;

            if (!retried && ladder[0] != "tcg" && AccelFailureClassifier.IsAccelInitFailure(tail))
            {
                ModLog.Log.Warn(
                    $"QEMU exited immediately (code {exitCode}) with an accelerator-looking error; retrying with TCG. "
                    + $"stderr: {tail}");
                _logWriter.WriteLine("=== gatOS: accel-init failure, retrying with -accel tcg ===");
                ResetForRetry();
                attempt = attempt with { AccelOverride = "tcg" };
                continue;
            }

            throw new VmStartException(
                $"The VM failed to start (QEMU exited with code {exitCode}). See log: {QemuLogPath}",
                detail: tail);
        }
    }

    /// <inheritdoc/>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        if (_exitTcs is not { } tcs)
            return true;
        return await Task.WhenAny(tcs.Task, Task.Delay(timeout)) == tcs.Task;
    }

    /// <inheritdoc/>
    public async Task<bool> TryQuitViaQmpAsync(TimeSpan timeout)
    {
        if (!IsRunning)
            return true;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, _qmpPort, cts.Token);
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, leaveOpen: true);

            // Protocol: greeting → qmp_capabilities handshake → quit. Responses are JSON lines;
            // we only need them consumed, not parsed.
            if (await reader.ReadLineAsync(cts.Token) is not { } greeting || !greeting.Contains("QMP"))
                return false;
            await WriteLineAsync(stream, """{"execute":"qmp_capabilities"}""", cts.Token);
            await reader.ReadLineAsync(cts.Token);
            await WriteLineAsync(stream, """{"execute":"quit"}""", cts.Token);

            return await WaitForExitAsync(timeout);
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException
                                       or ObjectDisposedException or InvalidOperationException)
        {
            ModLog.Log.Debug($"QMP quit failed (soft): {ex.Message}");
            return false;
        }
    }

    /// <inheritdoc/>
    public void Kill()
    {
        try
        {
            if (_process is { HasExited: false } process)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            ModLog.Log.Debug($"QEMU kill no-op: {ex.Message}");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Kill();
        if (_exitTcs is not null)
            await WaitForExitAsync(TimeSpan.FromSeconds(5));
        await DrainPumpsAsync();
        StreamWriter? writer;
        lock (_gate)
        {
            // Detach under the pump lock: a straggling pump line just becomes a no-op.
            writer = _logWriter;
            _logWriter = null;
        }

        writer?.Dispose();
        _process?.Dispose();
    }

    private void LaunchOnce(QemuBinaries binaries, VmLaunchSpec spec, string requestedAccel)
    {
        var psi = new ProcessStartInfo(binaries.SystemEmulator)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in _builder.Build(spec))
            psi.ArgumentList.Add(arg);

        _logWriter!.WriteLine($"=== gatOS: {DateTime.UtcNow:O} launching {binaries.SystemEmulator} "
                              + $"(accel request: {requestedAccel}) ===");
        _logWriter.WriteLine(string.Join(' ', psi.ArgumentList));

        var exitTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Process process;
        try
        {
            process = Process.Start(psi)
                      ?? throw new VmStartException($"Failed to start QEMU ('{binaries.SystemEmulator}').");
        }
        catch (SystemException ex)
        {
            throw new VmStartException($"Failed to start QEMU ('{binaries.SystemEmulator}'): {ex.Message}", inner: ex);
        }

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            int code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                code = -1;
            }

            exitTcs.TrySetResult(code);
            OnExited(code);
        };

        _process = process;
        _exitTcs = exitTcs;
        _pumpTask = Task.WhenAll(
            PumpAsync(process.StandardOutput, isStderr: false),
            PumpAsync(process.StandardError, isStderr: true));
        // Cover the race where the process died before EnableRaisingEvents took effect.
        if (process.HasExited)
        {
            exitTcs.TrySetResult(process.ExitCode);
            OnExited(process.ExitCode);
        }
    }

    private async Task PumpAsync(StreamReader reader, bool isStderr)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            lock (_gate)
            {
                _logWriter?.WriteLine(line);
                if (isStderr)
                {
                    _stderrRing.Enqueue(line);
                    while (_stderrRing.Count > StderrRingCapacity)
                        _stderrRing.Dequeue();
                }
            }
        }
    }

    private async Task DrainPumpsAsync()
    {
        if (_pumpTask is { } pump)
            await Task.WhenAny(pump, Task.Delay(TimeSpan.FromSeconds(1)));
    }

    private void ResetForRetry()
    {
        _process?.Dispose();
        _process = null;
        _exitTcs = null;
        _pumpTask = null;
        _exitRaised = false;
        lock (_gate)
            _stderrRing.Clear();
    }

    private void OnExited(int exitCode)
    {
        // The Exited event is only for a process that was accepted as running; start-time
        // failures surface via StartAsync's exception instead.
        if (EffectiveAccel is null)
            return;
        lock (_gate)
        {
            if (_exitRaised)
                return;
            _exitRaised = true;
        }

        Exited?.Invoke(this, new QemuProcessExitedEventArgs(exitCode, StderrTail));
    }

    private static async Task WriteLineAsync(NetworkStream stream, string json, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, ct);
    }

    private static void PruneOldLogs()
    {
        foreach (var pattern in new[] { "qemu-*.log", "serial-*.log" })
        {
            var stale = Directory.EnumerateFiles(GatOsPaths.LogsDir, pattern)
                .OrderByDescending(f => f, StringComparer.Ordinal) // timestamped names sort chronologically
                .Skip(RetainedLogFiles - 1);                       // a new log is about to be written
            foreach (var file in stale)
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // best effort — a locked log just survives one more round
                }
            }
        }
    }
}
