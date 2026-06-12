using gatOS.Logging;
using gatOS.Vm;
using purrTTY.Core.Terminal;

namespace gatOS.Ssh;

/// <summary>
///     purrTTY's <see cref="ICustomShell"/> implemented as an SSH session into the gatOS guest
///     (OS_PLAN.md T4.2). The constructor is trivial by contract — purrTTY's registry
///     instantiates a probe instance at registration, validates <see cref="Metadata"/> and
///     disposes it — so all real work (boot the VM, connect, open the PTY channel) happens in
///     <see cref="StartAsync"/>. Stopping a session never stops the VM: other tabs share it.
/// </summary>
/// <remarks>
///     Threading: input is queued and written by a dedicated writer thread
///     (<see cref="ShellInputQueue"/>); <see cref="OutputReceived"/> fires on connection-layer
///     threads (purrTTY tolerates this — threading rule 3). All mutable state sits behind one
///     lock; events are raised outside it.
/// </remarks>
public sealed class SshShellSession : ICustomShell
{
    private const string TerminalType = "xterm-256color";
    private const string ShellId = "gatos";

    private static readonly CustomShellMetadata ShellMetadata = CustomShellMetadata.Create(
        name: "gatOS",
        description: "Shell session into the gatOS virtual computer",
        version: new Version(1, 0, 0),
        author: "meow sci",
        supportedFeatures: ["colors", "resize"]);

    private readonly IShellBroker _broker;
    private readonly object _lock = new();
    private IShellChannel? _channel;
    private ShellInputQueue? _input;
    private (int Columns, int Rows)? _pendingResize;
    private bool _startRequested;
    private bool _terminated;
    private bool _disposed;
    private volatile bool _isRunning;

    /// <summary>Creates an idle session on the shared broker. Trivial by contract (T0.5).</summary>
    public SshShellSession(VmConnectionBroker broker)
        : this((IShellBroker)broker)
    {
    }

    internal SshShellSession(IShellBroker broker) => _broker = broker;

    /// <inheritdoc/>
    public CustomShellMetadata Metadata => ShellMetadata;

    /// <inheritdoc/>
    public bool IsRunning => _isRunning;

    /// <inheritdoc/>
    public event EventHandler<ShellOutputEventArgs>? OutputReceived;

    /// <inheritdoc/>
    public event EventHandler<ShellTerminatedEventArgs>? Terminated;

    /// <inheritdoc/>
    public async Task StartAsync(CustomShellStartOptions options, CancellationToken cancellationToken = default)
    {
        int columns, rows;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_startRequested)
                throw new InvalidOperationException("This gatOS shell session has already been started.");
            _startRequested = true;
            // A resize that arrived before start wins over the launch options.
            (columns, rows) = _pendingResize ?? (options.InitialWidth, options.InitialHeight);
        }

        IShellChannel channel;
        try
        {
            // Boots the VM lazily — may take seconds; purrTTY tolerates slow shell starts.
            channel = await _broker.OpenShellAsync(TerminalType, columns, rows, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            lock (_lock)
            {
                _startRequested = false;
            }

            throw;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                _startRequested = false;
            }

            var userMessage = ex is VmStartException vmEx
                ? vmEx.UserMessage
                : $"Could not open an SSH session into the gatOS VM: {ex.Message}";
            throw new CustomShellStartException(userMessage, ex, ShellId);
        }

        var input = new ShellInputQueue(
            "gatOS-ssh-input",
            channel.Write,
            message => ModLog.Log.Warn(message),
            ex => Terminate(1, $"SSH input write failed: {ex.Message}", deferDisposal: false));

        // Hook before publishing so a channel that dies instantly is not missed; a resulting
        // early Terminate() marks the session terminated and the publish below backs out.
        channel.DataReceived += OnChannelData;
        channel.ErrorOccurred += OnChannelError;
        channel.Closed += OnChannelClosed;

        bool dead;
        (int Columns, int Rows)? lateResize = null;
        lock (_lock)
        {
            dead = _disposed || _terminated;
            if (!dead)
            {
                _channel = channel;
                _input = input;
                _isRunning = true;
                if (_pendingResize is { } pending && pending != (columns, rows))
                    lateResize = pending;
            }
        }

        if (dead)
        {
            // Disposed mid-connect (tab closed) or the channel died before we published.
            channel.DataReceived -= OnChannelData;
            channel.ErrorOccurred -= OnChannelError;
            channel.Closed -= OnChannelClosed;
            input.Dispose();
            channel.Dispose();
            ObjectDisposedException.ThrowIf(_disposed, this);
            throw new CustomShellStartException("The session ended before it finished starting.", ShellId);
        }

        _broker.VmStatusChanged += OnVmStatusChanged;
        if (lateResize is { } resize)
            TryResize(channel, resize.Columns, resize.Rows);
    }

    /// <inheritdoc/>
    /// <remarks>Never stops the VM — other sessions and tabs share it.</remarks>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Terminate(0, "closed", deferDisposal: false);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ShellInputQueue? input;
        lock (_lock)
        {
            if (!_startRequested)
                throw new InvalidOperationException("This gatOS shell session has not been started.");
            // Null once terminated: input racing the session's end is dropped, not an error
            // (purrTTY's bridge tracks liveness on its own thread and may write a beat late).
            input = _input;
        }

        input?.Write(data.Span);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void NotifyTerminalResize(int width, int height)
    {
        IShellChannel? channel;
        lock (_lock)
        {
            _pendingResize = (width, height);
            channel = _channel;
        }

        // Before start the size is only recorded; StartAsync opens the PTY with it.
        if (channel is not null)
            TryResize(channel, width, height);
    }

    /// <inheritdoc/>
    /// <remarks>No-op: Ctrl-C travels in-band as 0x03 through <see cref="WriteInputAsync"/>.</remarks>
    public void RequestCancellation()
    {
    }

    /// <inheritdoc/>
    /// <remarks>No-op: the guest motd and prompt are the banner.</remarks>
    public void SendInitialOutput()
    {
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        // Safe on a never-started instance (registry probe): Terminate no-ops then.
        Terminate(0, "closed", deferDisposal: false);
    }

    /// <summary>
    ///     The single termination path: tears the channel down once and raises
    ///     <see cref="Terminated"/> once. <paramref name="deferDisposal"/> is set when called
    ///     from a connection-layer callback — disposing SSH.NET objects from inside their own
    ///     event would risk re-entrancy, so the teardown moves to the thread pool.
    /// </summary>
    private void Terminate(int exitCode, string? reason, bool deferDisposal)
    {
        IShellChannel? channel;
        ShellInputQueue? input;
        lock (_lock)
        {
            if (_terminated || !_startRequested)
                return;
            _terminated = true;
            channel = _channel;
            input = _input;
            _channel = null;
            _input = null;
            _isRunning = false;
        }

        _broker.VmStatusChanged -= OnVmStatusChanged;
        if (channel is not null)
        {
            channel.DataReceived -= OnChannelData;
            channel.ErrorOccurred -= OnChannelError;
            channel.Closed -= OnChannelClosed;
        }

        if (channel is not null || input is not null)
        {
            if (deferDisposal)
            {
                _ = Task.Run(() =>
                {
                    input?.Dispose();
                    channel?.Dispose();
                });
            }
            else
            {
                input?.Dispose(); // joins the writer thread ≤2 s (drains pending input first)
                channel?.Dispose();
            }
        }

        Terminated?.Invoke(this, new ShellTerminatedEventArgs(exitCode, reason));
    }

    private static void TryResize(IShellChannel channel, int columns, int rows)
    {
        try
        {
            channel.ChangeWindowSize(columns, rows);
        }
        catch (Exception ex)
        {
            ModLog.Log.Warn($"Terminal resize to {columns}x{rows} failed: {ex.Message}");
        }
    }

    private void OnChannelData(object? sender, byte[] data)
        => OutputReceived?.Invoke(this, new ShellOutputEventArgs(data));

    private void OnChannelError(object? sender, Exception ex)
        => Terminate(1, $"SSH connection lost: {ex.Message}", deferDisposal: true);

    private void OnChannelClosed(object? sender, EventArgs e)
        => Terminate(0, "closed", deferDisposal: true);

    private void OnVmStatusChanged(object? sender, VmStatus status)
    {
        if (status.State == VmState.Faulted)
            Terminate(1, status.FaultReason ?? "The gatOS VM faulted.", deferDisposal: true);
    }
}
