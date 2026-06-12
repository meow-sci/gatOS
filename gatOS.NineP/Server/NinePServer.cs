using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using gatOS.Logging;
using gatOS.NineP.Vfs;

namespace gatOS.NineP.Server;

/// <summary>
///     The 9P2000.L TCP server (OS_PLAN.md T7.4): accepts connections on the loopback
///     interface and runs one <see cref="Session"/> per connection over the given VFS root.
/// </summary>
/// <remarks>
///     Loopback is deliberate (as-built deviation from the plan's <c>IPAddress.Any</c>
///     sketch): QEMU's slirp delivers guest connections to <c>10.0.2.2</c> as host-side
///     connections to <c>127.0.0.1</c>, so loopback suffices and never trips the Windows
///     Firewall consent dialog.
/// </remarks>
public sealed class NinePServer : IAsyncDisposable
{
    private readonly VfsDirectory _root;
    private readonly NinePServerOptions _options;
    private readonly DateTimeOffset _attrTime;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Session, byte> _sessions = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;

    /// <param name="root">The tree to serve (e.g. M8's <c>SimFsTree.Build</c> result).</param>
    /// <param name="options">Optional tuning; defaults are production values.</param>
    public NinePServer(VfsDirectory root, NinePServerOptions? options = null)
    {
        _root = root;
        _options = options ?? new NinePServerOptions();
        _attrTime = _options.AttrTime ?? DateTimeOffset.UtcNow;
    }

    /// <summary>The bound port (valid after <see cref="StartAsync"/>; 0 before).</summary>
    public int Port { get; private set; }

    /// <summary>Binds the listener (port 0 = ephemeral) and starts accepting connections.</summary>
    public Task StartAsync(int port = 0)
    {
        if (_listener is not null)
            throw new InvalidOperationException("The server is already started.");
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        ModLog.Log.Info($"9p server listening on 127.0.0.1:{Port}");
        _acceptLoop = Task.Run(AcceptLoopAsync);
        return Task.CompletedTask;
    }

    /// <summary>Stops the listener and tears down every session.</summary>
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        foreach (var session in _sessions.Keys)
            session.Teardown();
        if (_acceptLoop is { } loop)
            await loop.ConfigureAwait(false);
        _cts.Dispose();
        ModLog.Log.Info("9p server stopped");
    }

    private async Task AcceptLoopAsync()
    {
        var listener = _listener!;
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException) when (ct.IsCancellationRequested)
            {
                break;
            }

            client.NoDelay = true;
            var session = new Session(client, _root, _options, _attrTime, ct);
            _sessions.TryAdd(session, 0);
            _ = Task.Run(async () =>
            {
                ModLog.Log.Debug("9p server: connection accepted");
                try
                {
                    await session.RunAsync().ConfigureAwait(false);
                }
                finally
                {
                    _sessions.TryRemove(session, out _);
                    ModLog.Log.Debug("9p server: connection closed");
                }
            }, CancellationToken.None);
        }
    }
}
