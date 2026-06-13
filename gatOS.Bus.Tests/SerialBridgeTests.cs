using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.Bus.Tests;

/// <summary>
///     The host-side G7 serial bridge (<see cref="SerialBridge"/> + <see cref="SerialBridgeConnector"/>)
///     over a real loopback socket pair standing in for QEMU's <c>gatos.serial</c> chardev: telemetry
///     frames out, SCPI command lines in with <c>OK</c>/<c>ERR</c> responses, and connect-with-retry.
/// </summary>
[TestFixture]
public sealed class SerialBridgeTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    [Test]
    public async Task Telemetry_PumpsNdjsonFramesForTheActiveVessel()
    {
        using var pair = await SocketPair.CreateAsync();
        var store = new SnapshotStore();
        store.Publish(Snapshot(3, Vessel("v1")));

        var bridge = new SerialBridge(store, SerialMode.Ndjson, Tick, emitTelemetry: true, commands: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = bridge.RunAsync(pair.HostEnd, cts.Token);

        using var reader = new StreamReader(pair.GuestEnd);
        var line = await reader.ReadLineAsync(cts.Token);

        Assert.That(line, Is.Not.Null);
        using var json = JsonDocument.Parse(line!);
        Assert.That(json.RootElement.GetProperty("seq").GetInt64(), Is.EqualTo(3));

        await cts.CancelAsync();
        await Swallow(run);
    }

    [Test]
    public async Task Telemetry_EmitsNmeaSentences()
    {
        using var pair = await SocketPair.CreateAsync();
        var store = new SnapshotStore();
        store.Publish(Snapshot(1, Vessel("v1")));

        var bridge = new SerialBridge(store, SerialMode.Nmea, Tick, emitTelemetry: true, commands: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = bridge.RunAsync(pair.HostEnd, cts.Token);

        using var reader = new StreamReader(pair.GuestEnd);
        var line = await reader.ReadLineAsync(cts.Token);
        Assert.That(line, Does.StartWith("$KSSTA,"), "an NMEA state sentence with the KS talker");

        await cts.CancelAsync();
        await Swallow(run);
    }

    [Test]
    public async Task Commands_ParseScpiAndAnswerOkThenErr()
    {
        using var pair = await SocketPair.CreateAsync();
        var store = new SnapshotStore();
        store.Publish(Snapshot(1, Vessel("v1")));
        var sink = new StubSink();

        var bridge = new SerialBridge(store, SerialMode.Ndjson, Tick, emitTelemetry: false, commands: sink);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = bridge.RunAsync(pair.HostEnd, cts.Token);

        var writer = new StreamWriter(pair.GuestEnd) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pair.GuestEnd);

        await writer.WriteLineAsync("CTL:IGNITE");
        Assert.That(await reader.ReadLineAsync(cts.Token), Is.EqualTo("OK"));
        Assert.That(sink.Last, Is.EqualTo(new SimCommand("v1", "vessel.ignite", SimCommand.NoOrdinal, 1)));

        await writer.WriteLineAsync("CTL:BOGUS");
        Assert.That(await reader.ReadLineAsync(cts.Token), Is.EqualTo("ERR EINVAL"));

        await cts.CancelAsync();
        await Swallow(run);
    }

    [Test]
    public async Task Commands_WithNoActiveVessel_AnswerEnoent()
    {
        using var pair = await SocketPair.CreateAsync();
        var store = new SnapshotStore(); // Empty: no active vessel
        var sink = new StubSink();

        var bridge = new SerialBridge(store, SerialMode.Ndjson, Tick, emitTelemetry: false, commands: sink);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = bridge.RunAsync(pair.HostEnd, cts.Token);

        var writer = new StreamWriter(pair.GuestEnd) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pair.GuestEnd);
        await writer.WriteLineAsync("CTL:IGNITE");
        Assert.That(await reader.ReadLineAsync(cts.Token), Is.EqualTo("ERR ENOENT"));
        Assert.That(sink.Last, Is.Null, "no vessel target → the command never reached the sink");

        await cts.CancelAsync();
        await Swallow(run);
    }

    [Test]
    public async Task Commands_RetargetTheActiveVesselPerLine()
    {
        // The bridge resolves the active vessel for every command line, so a vessel switch
        // mid-session retargets subsequent commands (SerialBridge.cs active-vessel resolution).
        using var pair = await SocketPair.CreateAsync();
        var store = new SnapshotStore();
        store.Publish(Snapshot(1, Vessel("v1"), Vessel("v2"))); // active = vessels[0] = v1
        var sink = new StubSink();

        var bridge = new SerialBridge(store, SerialMode.Ndjson, Tick, emitTelemetry: false, commands: sink);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = bridge.RunAsync(pair.HostEnd, cts.Token);

        var writer = new StreamWriter(pair.GuestEnd) { AutoFlush = true, NewLine = "\n" };
        using var reader = new StreamReader(pair.GuestEnd);

        await writer.WriteLineAsync("CTL:IGNITE");
        Assert.That(await reader.ReadLineAsync(cts.Token), Is.EqualTo("OK"));
        Assert.That(sink.Last!.VesselId, Is.EqualTo("v1"));

        store.Publish(Snapshot(2, Vessel("v2"), Vessel("v1"))); // active switches to v2
        await writer.WriteLineAsync("CTL:IGNITE");
        Assert.That(await reader.ReadLineAsync(cts.Token), Is.EqualTo("OK"));
        Assert.That(sink.Last!.VesselId, Is.EqualTo("v2"), "the next command targets the new active vessel");

        await cts.CancelAsync();
        await Swallow(run);
    }

    [Test]
    public async Task Connector_ReconnectsAfterTheConnectionDrops()
    {
        // The connector's whole purpose: re-establish the feed when the guest closes the port (or a
        // hiccup drops it). Read a frame, drop the connection, then prove a second connection forms
        // and serves unaided (SerialBridgeConnector retry loop).
        using var listener = new LoopbackListener();
        var store = new SnapshotStore();
        store.Publish(Snapshot(1, Vessel("v1")));
        var bridge = new SerialBridge(store, SerialMode.Ndjson, Tick, emitTelemetry: true, commands: null);

        await using var connector = new SerialBridgeConnector(listener.Port, bridge);
        connector.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var first = await listener.AcceptAsync(cts.Token);
        var firstReader = new StreamReader(first.GetStream());
        Assert.That(await firstReader.ReadLineAsync(cts.Token), Is.Not.Null);
        firstReader.Dispose();
        first.Dispose(); // drop → the bridge errors, the connector waits then reconnects

        using var second = await listener.AcceptAsync(cts.Token);
        using var reader = new StreamReader(second.GetStream());
        var line = await reader.ReadLineAsync(cts.Token);
        Assert.That(line, Is.Not.Null, "the connector reconnected after the drop");
        using var json = JsonDocument.Parse(line!);
        Assert.That(json.RootElement.GetProperty("seq").GetInt64(), Is.EqualTo(1));
    }

    [Test]
    public async Task Connector_ConnectsToTheChardevAndRunsTheBridge()
    {
        // A loopback listener stands in for QEMU's gatos.serial chardev (server=on).
        using var listener = new LoopbackListener();
        var store = new SnapshotStore();
        store.Publish(Snapshot(7, Vessel("v1")));
        var bridge = new SerialBridge(store, SerialMode.Ndjson, Tick, emitTelemetry: true, commands: null);

        await using var connector = new SerialBridgeConnector(listener.Port, bridge);
        connector.Start();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var guest = await listener.AcceptAsync(cts.Token);
        using var reader = new StreamReader(guest.GetStream());
        var line = await reader.ReadLineAsync(cts.Token);

        Assert.That(line, Is.Not.Null);
        using var json = JsonDocument.Parse(line!);
        Assert.That(json.RootElement.GetProperty("seq").GetInt64(), Is.EqualTo(7));
    }

    private static async Task Swallow(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static SimSnapshot Snapshot(long seq, params VesselSnapshot[] vessels)
        => new(seq, seq * 0.1, 1, vessels.Length > 0 ? vessels[0].Id : null, vessels, [], "test", 10, []);

    private static VesselSnapshot Vessel(string id) => new(
        id, id, "Freefall", new double3Snap(0, 0, 0), 0, 0, 0, 0, 0, new QuatSnap(0, 0, 0, 1),
        new double3Snap(0, 0, 0), 0, 0, 1, 1, 0, null, [], [], null, "Kerth", false, []);

    /// <summary>A connected loopback TCP stream pair: the bridge runs on <see cref="HostEnd"/>.</summary>
    private sealed class SocketPair : IDisposable
    {
        private readonly TcpClient _a = null!;
        private readonly TcpClient _b = null!;

        private SocketPair(TcpClient a, TcpClient b)
        {
            _a = a;
            _b = b;
        }

        public NetworkStream HostEnd => _a.GetStream();
        public NetworkStream GuestEnd => _b.GetStream();

        public static async Task<SocketPair> CreateAsync()
        {
            using var listener = new LoopbackListener();
            var client = new TcpClient();
            var connect = client.ConnectAsync(IPAddress.Loopback, listener.Port);
            var accepted = await listener.AcceptAsync(CancellationToken.None);
            await connect;
            client.NoDelay = true;
            accepted.NoDelay = true;
            return new SocketPair(client, accepted);
        }

        public void Dispose()
        {
            _a.Dispose();
            _b.Dispose();
        }
    }

    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _listener;

        public LoopbackListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<TcpClient> AcceptAsync(CancellationToken ct) => _listener.AcceptTcpClientAsync(ct).AsTask();

        public void Dispose() => _listener.Stop();
    }

    private sealed class StubSink : ICommandSink
    {
        public bool ControlEnabled => true;
        public bool DebugEnabled => false;
        public CommandResult Result { get; set; } = CommandResult.Ok;
        public SimCommand? Last { get; private set; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Last = command;
            return Task.FromResult(Result);
        }
    }
}
