using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace gatOS.Vm.Tests;

/// <summary>
///     Covers <see cref="QgaClient"/> against a fake guest agent speaking the documented
///     protocol: sentinel-delimited sync, JSON-line commands, no response to shutdown (T3.5).
/// </summary>
[TestFixture]
public sealed class QgaClientTests
{
    private FakeQgaServer _server = null!;

    [SetUp]
    public void SetUp() => _server = new FakeQgaServer();

    [TearDown]
    public void TearDown() => _server.Dispose();

    [Test]
    public async Task Ping_SyncsThenSucceeds()
    {
        await using var client = new QgaClient(_server.Port, TimeSpan.FromSeconds(5));
        Assert.That(await client.PingAsync(), Is.True);
        Assert.That(_server.SyncCount, Is.EqualTo(1), "one connection ⇒ one sync preamble");
    }

    [Test]
    public async Task Shutdown_SucceedsWithoutAResponse()
    {
        await using var client = new QgaClient(_server.Port, TimeSpan.FromSeconds(5));
        Assert.That(await client.ShutdownAsync(), Is.True);
        Assert.That(await _server.WaitForCommandAsync("guest-shutdown", TimeSpan.FromSeconds(5)), Is.True);
    }

    [Test]
    public async Task CommandsReuseOneConnection_AndOneSync()
    {
        await using var client = new QgaClient(_server.Port, TimeSpan.FromSeconds(5));
        Assert.That(await client.PingAsync(), Is.True);
        Assert.That(await client.PingAsync(), Is.True);
        Assert.That(_server.SyncCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Failures_AreSoft()
    {
        var deadPort = PortAllocator.AllocateLoopbackPort();
        await using var client = new QgaClient(deadPort, TimeSpan.FromMilliseconds(500));
        Assert.That(await client.PingAsync(), Is.False);
        Assert.That(await client.ShutdownAsync(), Is.False);
    }

    /// <summary>
    ///     Speaks just enough QGA: answers <c>guest-sync-delimited</c> with the 0xFF sentinel +
    ///     id, <c>guest-ping</c> with an empty return, and swallows <c>guest-shutdown</c>.
    /// </summary>
    private sealed class FakeQgaServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<string> _commands = [];
        private int _syncCount;

        public FakeQgaServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        public int Port { get; }

        public int SyncCount => Volatile.Read(ref _syncCount);

        public async Task<bool> WaitForCommandAsync(string command, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                lock (_commands)
                {
                    if (_commands.Contains(command))
                        return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.UTF8);
                try
                {
                    while (await reader.ReadLineAsync(_cts.Token) is { } raw)
                    {
                        // The client's 0xFF sentinel is invalid UTF-8 (decodes to U+FFFD);
                        // cut to the JSON start instead of string-matching it.
                        var start = raw.IndexOf('{');
                        if (start < 0)
                            continue;
                        var line = raw[start..];
                        using var json = JsonDocument.Parse(line);
                        var execute = json.RootElement.GetProperty("execute").GetString();
                        lock (_commands)
                        {
                            _commands.Add(execute!);
                        }

                        switch (execute)
                        {
                            case "guest-sync-delimited":
                                Interlocked.Increment(ref _syncCount);
                                var id = json.RootElement.GetProperty("arguments").GetProperty("id").GetInt64();
                                await stream.WriteAsync(new byte[] { 0xFF }, _cts.Token);
                                await WriteLineAsync(stream, "{\"return\":" + id + "}");
                                break;
                            case "guest-ping":
                                await WriteLineAsync(stream, """{"return":{}}""");
                                break;
                            case "guest-shutdown":
                                break; // documented: no response on success
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException
                                               or ObjectDisposedException)
                {
                }
            }
        }

        private async Task WriteLineAsync(NetworkStream stream, string json)
            => await stream.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), _cts.Token);
    }
}
