using System.Net;
using System.Net.Sockets;
using System.Text;

namespace gatOS.Vm.Tests;

/// <summary>Covers <see cref="ReadinessProbe"/> against fake TCP servers (T3.5).</summary>
[TestFixture]
public sealed class ReadinessProbeTests
{
    [Test]
    public async Task WaitForSsh_Completes_WhenTheServerPresentsAnSshBanner()
    {
        using var server = new FakeServer(async (client, _) =>
        {
            var banner = Encoding.ASCII.GetBytes("SSH-2.0-test\r\n");
            await client.GetStream().WriteAsync(banner);
        });

        await ReadinessProbe.WaitForSshAsync(server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    [Test]
    public async Task WaitForSsh_KeepsPolling_PastConnectionsClosedWithoutABanner()
    {
        var connections = 0;
        using var server = new FakeServer(async (client, _) =>
        {
            // First connection: slirp-style accept-then-drop. Second: real banner.
            if (Interlocked.Increment(ref connections) >= 2)
                await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("SSH-2.0-late\r\n"));
            client.Close();
        });

        await ReadinessProbe.WaitForSshAsync(server.Port, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.That(connections, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public void WaitForSsh_TimesOut_WhenNothingListens()
    {
        var port = PortAllocator.AllocateLoopbackPort();
        Assert.ThrowsAsync<TimeoutException>(() =>
            ReadinessProbe.WaitForSshAsync(port, TimeSpan.FromMilliseconds(600), CancellationToken.None));
    }

    [Test]
    public void WaitForSsh_TimesOut_WhenTheServerStaysSilent()
    {
        using var server = new FakeServer((_, ct) => Task.Delay(Timeout.Infinite, ct));
        Assert.ThrowsAsync<TimeoutException>(() =>
            ReadinessProbe.WaitForSshAsync(server.Port, TimeSpan.FromMilliseconds(600), CancellationToken.None));
    }

    [Test]
    public void WaitForSsh_HonorsCancellation()
    {
        using var server = new FakeServer((_, ct) => Task.Delay(Timeout.Infinite, ct));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Assert.CatchAsync<OperationCanceledException>(() =>
            ReadinessProbe.WaitForSshAsync(server.Port, TimeSpan.FromSeconds(30), cts.Token));
    }

    /// <summary>A loopback TCP server running one handler per accepted connection.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public FakeServer(Func<TcpClient, CancellationToken, Task> handler)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(handler);
        }

        public int Port { get; }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync(Func<TcpClient, CancellationToken, Task> handler)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                await handler(client, _cts.Token);
                            }
                            catch (OperationCanceledException)
                            {
                            }
                        }
                    });
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                // listener stopped
            }
        }
    }
}
