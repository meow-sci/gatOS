using System.Text;
using System.Threading.Channels;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using MQTTnet;
using MQTTnet.Client;

namespace gatOS.Mqtt.Tests;

/// <summary>
///     The MQTT transport exercised against the embedded broker with a real MQTTnet client:
///     telemetry publish (retained) and the command topic routing into the sink with a result.
/// </summary>
[TestFixture]
[NonParallelizable]
public sealed class MqttBrokerTests
{
    private SnapshotStore _store = null!;
    private RecordingSink _sink = null!;
    private SimMqttBroker _broker = null!;
    private IMqttClient _client = null!;
    private Channel<(string Topic, string Payload)> _messages = null!;

    [SetUp]
    public async Task SetUp()
    {
        _store = new SnapshotStore();
        _sink = new RecordingSink();
        _broker = new SimMqttBroker(_store, _sink);
        await _broker.StartAsync(0); // ephemeral

        _messages = Channel.CreateUnbounded<(string, string)>();
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            var payload = Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray());
            _messages.Writer.TryWrite((e.ApplicationMessage.Topic, payload));
            return Task.CompletedTask;
        };
        await _client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer("127.0.0.1", _broker.Port).Build());
        await _client.SubscribeAsync("gatos/#");
    }

    [TearDown]
    public async Task TearDown()
    {
        await _client.DisconnectAsync();
        _client.Dispose();
        await _broker.DisposeAsync();
    }

    [Test]
    public async Task Telemetry_IsPublishedToVesselTopic()
    {
        _store.Publish(Snapshot(1, Vessel("v1")));
        var payload = await WaitForAsync(t => t == "gatos/vessels/v1/telemetry");
        Assert.That(payload, Does.Contain("\"id\":\"v1\""));
    }

    [Test]
    public async Task Command_RoutesToSink_AndPublishesResult()
    {
        await _client.PublishStringAsync(
            SimMqttBroker.CommandTopic,
            """{"vessel_id":"v1","action":"engine.active","ordinal":0,"value":1}""");

        var result = await WaitForAsync(t => t == SimMqttBroker.CommandResultTopic);
        Assert.That(result, Does.Contain("ok"));
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("v1", "engine.active", 0, 1)));
    }

    [Test]
    public async Task Command_FailureMapsErrno()
    {
        _sink.Result = new CommandResult(CommandOutcome.Busy, "no");
        await _client.PublishStringAsync(
            SimMqttBroker.CommandTopic, """{"vessel_id":"v1","action":"decoupler.fire","ordinal":0}""");
        var result = await WaitForAsync(t => t == SimMqttBroker.CommandResultTopic);
        Assert.That(result, Does.Contain("EBUSY"));
    }

    private async Task<string> WaitForAsync(Func<string, bool> topicMatch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await _messages.Reader.WaitToReadAsync(cts.Token))
        {
            while (_messages.Reader.TryRead(out var msg))
            {
                if (topicMatch(msg.Topic))
                    return msg.Payload;
            }
        }

        throw new TimeoutException("no matching message");
    }

    private static SimSnapshot Snapshot(long seq, params VesselSnapshot[] vessels)
        => new(seq, seq * 0.1, 1, vessels.Length > 0 ? vessels[0].Id : null, vessels, [], "test", 10, []);

    private static VesselSnapshot Vessel(string id) => new(
        id, id, "Freefall", new double3Snap(0, 0, 0), 0, 0, 0, 0, 0, new QuatSnap(0, 0, 0, 1),
        new double3Snap(0, 0, 0), 0, 0, 1, 1, 0, null, [], [], null, "Kerth", false, []);

    private sealed class RecordingSink : ICommandSink
    {
        public bool ControlEnabled => true;
        public bool DebugEnabled => true;
        public CommandResult Result { get; set; } = CommandResult.Ok;
        public SimCommand? Last { get; private set; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Last = command;
            return Task.FromResult(Result);
        }
    }
}
