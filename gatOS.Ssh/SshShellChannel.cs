using gatOS.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace gatOS.Ssh;

/// <summary>
///     The real <see cref="IShellChannel"/>: one <see cref="ShellStream"/> on one dedicated
///     <see cref="SshClient"/> (per-session client, OS_PLAN.md T4.1). Owns both; disposing the
///     channel closes the stream and disconnects the client.
/// </summary>
internal sealed class SshShellChannel : IShellChannel
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;
    private int _disposed;

    internal SshShellChannel(SshClient client, ShellStream stream)
    {
        _client = client;
        _stream = stream;
        _stream.DataReceived += OnDataReceived;
        _stream.ErrorOccurred += OnErrorOccurred;
        _stream.Closed += OnClosed;
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

        _stream.DataReceived -= OnDataReceived;
        _stream.ErrorOccurred -= OnErrorOccurred;
        _stream.Closed -= OnClosed;
        try
        {
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
    }

    // Copy the payload: the contract event hands out ReadOnlyMemory and SSH.NET gives no
    // guarantee the event buffer survives the handler.
    private void OnDataReceived(object? sender, ShellDataEventArgs e)
        => DataReceived?.Invoke(this, e.Data.ToArray());

    private void OnErrorOccurred(object? sender, ExceptionEventArgs e)
        => ErrorOccurred?.Invoke(this, e.Exception);

    private void OnClosed(object? sender, EventArgs e)
        => Closed?.Invoke(this, EventArgs.Empty);
}
