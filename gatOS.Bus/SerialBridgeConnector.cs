using System.Net;
using System.Net.Sockets;
using gatOS.Logging;

namespace gatOS.Bus;

/// <summary>
///     Connects the host <see cref="SerialBridge"/> to QEMU's <c>gatos.serial</c> chardev socket
///     (QEMU listens with <c>server=on,wait=off</c>; the host is the client, mirroring
///     <c>QgaClient</c>). Runs the bridge while connected and reconnects after a drop, until
///     disposed — so a guest that opens/closes <c>/dev/virtio-ports/gatos.serial</c>, or a brief
///     transport hiccup, re-establishes the feed without intervention.
/// </summary>
/// <remarks>
///     Lifetime is owned by the caller (the game mod): create + <see cref="Start"/> when the VM
///     reaches Running with a serial port, <see cref="DisposeAsync"/> when it stops. While the VM
///     is down the connect simply keeps failing and retrying harmlessly; the caller stops it.
/// </remarks>
public sealed class SerialBridgeConnector : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly int _port;
    private readonly SerialBridge _bridge;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <param name="port">The loopback port of the QEMU <c>gatos.serial</c> chardev.</param>
    /// <param name="bridge">The bridge to run on each established connection.</param>
    public SerialBridgeConnector(int port, SerialBridge bridge)
    {
        _port = port;
        _bridge = bridge;
    }

    /// <summary>Starts the connect/run/reconnect loop (idempotent).</summary>
    public void Start() => _loop ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, _port, ct).ConfigureAwait(false);
                client.NoDelay = true;
                await using var stream = client.GetStream();
                ModLog.Log.Debug($"serial bridge: connected to gatos.serial on 127.0.0.1:{_port}");
                await _bridge.RunAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
            {
                ModLog.Log.Debug($"serial bridge: connect/run failed (will retry): {ex.Message}");
            }

            try
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is { } loop)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch
            {
                // The loop only ends via cancellation; nothing to surface.
            }
        }

        _cts.Dispose();
    }
}
