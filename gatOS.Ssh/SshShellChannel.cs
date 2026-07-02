using gatOS.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace gatOS.Ssh;

/// <summary>
///     The real <see cref="IShellChannel"/>: one <see cref="ShellStream"/> on one dedicated
///     <see cref="SshClient"/> (per-session client, OS_PLAN.md T4.1). Owns both; disposing the
///     channel closes the stream and disconnects the client.
/// </summary>
/// <remarks>
///     <para><b>Output is pumped, not event-driven (PERF_IMPROVEMENT_PLAN.md P0.1).</b> SSH.NET's
///     <see cref="ShellStream"/> appends every inbound byte to an internal, unbounded read buffer
///     <i>regardless</i> of <c>DataReceived</c> subscriptions, and replenishes the SSH window on
///     arrival — so an event-only consumer leaks that buffer at wire rate forever (the gatOS
///     screen stream reaches tens of MB/s). A dedicated reader thread draining
///     <see cref="ShellStream.Read(byte[], int, int)"/> keeps the internal buffer empty, and a
///     slow downstream consumer (purrTTY's inbox backpressure) now parks <i>this</i> thread
///     instead of SSH.NET's message loop, so keepalives and window handling keep flowing.</para>
/// </remarks>
internal sealed class SshShellChannel : IShellChannel
{
    // Big enough to drain several SSH data packets per wakeup at video-stream rates, small
    // enough that the per-event copy stays a gen-0 allocation (well under the LOH threshold).
    private const int PumpBufferBytes = 64 * 1024;

    private readonly SshClient _client;
    private readonly ShellStream _stream;
    private readonly Thread _pump;
    private int _disposed;

    internal SshShellChannel(SshClient client, ShellStream stream)
    {
        _client = client;
        _stream = stream;
        _stream.ErrorOccurred += OnErrorOccurred;
        _stream.Closed += OnClosed;
        _pump = new Thread(PumpLoop) { IsBackground = true, Name = "gatOS-ssh-output" };
        _pump.Start();
    }

    public event EventHandler<byte[]>? DataReceived;
    public event EventHandler<Exception>? ErrorOccurred;
    public event EventHandler? Closed;

    public void Write(byte[] chunk)
    {
        _stream.Write(chunk, 0, chunk.Length);
        _stream.Flush();
    }

    public void ChangeWindowSize(int columns, int rows)
        => _stream.ChangeWindowSize((uint)columns, (uint)rows, width: 0, height: 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stream.ErrorOccurred -= OnErrorOccurred;
        _stream.Closed -= OnClosed;
        try
        {
            // Wakes a pump blocked in Read (it then drains what is buffered and returns 0).
            _stream.Dispose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"ShellStream dispose threw (connection already dead?): {ex.Message}");
        }

        try
        {
            _client.Dispose();
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"SshClient dispose threw (connection already dead?): {ex.Message}");
        }

        // Tidy join; skipped if teardown was somehow triggered from the pump's own event chain.
        if (Thread.CurrentThread != _pump)
            _pump.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    ///     Drains the stream until it reports EOF (disposed/closed and empty). The contract event
    ///     hands out a right-sized array per chunk ("owned by the receiver",
    ///     <see cref="IShellChannel.DataReceived"/>).
    /// </summary>
    private void PumpLoop()
    {
        var buffer = new byte[PumpBufferBytes];
        try
        {
            while (true)
            {
                var read = _stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    return; // stream disposed/closed and drained; Closed/ErrorOccurred drive teardown
                DataReceived?.Invoke(this, buffer[..read]);
            }
        }
        catch (Exception ex)
        {
            // Read paths only throw for connection-level failures (a plain dispose returns 0).
            if (Volatile.Read(ref _disposed) == 0)
                ErrorOccurred?.Invoke(this, ex);
        }
    }

    private void OnErrorOccurred(object? sender, ExceptionEventArgs e)
        => ErrorOccurred?.Invoke(this, e.Exception);

    private void OnClosed(object? sender, EventArgs e)
        => Closed?.Invoke(this, EventArgs.Empty);
}
