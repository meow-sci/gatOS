using System.Collections.Concurrent;

namespace gatOS.Ssh;

/// <summary>
///     Bounded input queue drained by one dedicated writer thread — purrTTY's
///     <c>PtyInputQueue</c> discipline applied to the SSH channel (OS_PLAN.md T4.2). An SSH
///     write can block when the guest stops reading (flow-controlled channel window), and
///     <c>WriteInputAsync</c> is called from purrTTY's render-tick path, so the caller must
///     never issue the blocking write itself. Enqueue never blocks; overflow drops the incoming
///     chunk and reports once per overflow episode; the first write failure is reported and all
///     subsequent chunks are dropped (the session tears down via its own error paths).
/// </summary>
internal sealed class ShellInputQueue : IDisposable
{
    /// <summary>
    ///     Cap on bytes queued but not yet written. Interactive input is tiny; only a huge
    ///     paste into a stalled guest can hit this, and dropping beats unbounded growth.
    /// </summary>
    internal const int MaxPendingBytes = 1024 * 1024;

    private readonly BlockingCollection<byte[]> _queue = new();
    private readonly Thread _thread;
    private readonly Action<byte[]> _writeChunk;
    private readonly Action<string> _onOverflow;
    private readonly Action<Exception> _onWriteFailure;
    private int _pendingBytes;
    private bool _overflowReported;
    private volatile bool _failed;
    private bool _disposed;

    /// <param name="name">Writer thread name (diagnostics).</param>
    /// <param name="writeChunk">
    ///     Performs the actual blocking write; runs on the writer thread only. May throw — the
    ///     first failure goes to <paramref name="onWriteFailure"/> and later chunks are dropped.
    /// </param>
    /// <param name="onOverflow">Receives one message per overflow episode (caller thread).</param>
    /// <param name="onWriteFailure">Receives the first write failure (writer thread).</param>
    internal ShellInputQueue(
        string name,
        Action<byte[]> writeChunk,
        Action<string> onOverflow,
        Action<Exception> onWriteFailure)
    {
        _writeChunk = writeChunk;
        _onOverflow = onOverflow;
        _onWriteFailure = onWriteFailure;
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Queues a copy of <paramref name="data"/> for the writer thread. Never blocks.</summary>
    internal void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _failed)
            return;

        if (Volatile.Read(ref _pendingBytes) + data.Length > MaxPendingBytes)
        {
            if (!_overflowReported)
            {
                _overflowReported = true;
                _onOverflow($"SSH input queue overflow (> {MaxPendingBytes} bytes pending); "
                            + "dropping input — the guest is not reading");
            }

            return;
        }

        _overflowReported = false;
        Interlocked.Add(ref _pendingBytes, data.Length);
        try
        {
            _queue.Add(data.ToArray());
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding raced this enqueue during teardown — the session is going
            // away, dropping the chunk is fine.
            Interlocked.Add(ref _pendingBytes, -data.Length);
        }
    }

    /// <summary>
    ///     Completes the queue and joins the writer thread (it drains pending chunks first).
    ///     Returns false if the thread did not exit in time or when called from the writer
    ///     thread itself (the write-failure path) — the background thread is then abandoned.
    /// </summary>
    internal bool Shutdown(int timeoutMs)
    {
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            return true;
        }

        if (Thread.CurrentThread == _thread)
            return false; // never self-join: Dispose can arrive via onWriteFailure → teardown

        return _thread.Join(timeoutMs);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        // Only reclaim the collection once the writer thread is out of it; on a timed-out
        // join the (background) thread is abandoned and the collection leaked with it.
        if (Shutdown(2000))
            _queue.Dispose();
    }

    private void WriteLoop()
    {
        foreach (var chunk in _queue.GetConsumingEnumerable())
        {
            Interlocked.Add(ref _pendingBytes, -chunk.Length);

            if (_failed)
                continue; // drain-and-drop after a fatal write error

            try
            {
                _writeChunk(chunk);
            }
            catch (Exception ex)
            {
                _failed = true;
                _onWriteFailure(ex);
            }
        }
    }
}
