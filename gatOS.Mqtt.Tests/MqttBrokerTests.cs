using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using gatOS.SimFs;
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
        // Share the real /sim tree (built with the sink, so ctl/ exists) so the field-level
        // gatos/sim/<path> mirror runs; a high field cadence keeps the tests prompt.
        var simRoot = SimFsTree.Build(_store, _sink, null);
        _broker = new SimMqttBroker(_store, _sink, simRoot: simRoot, fieldFeedHz: 50);
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

    [Test]
    public async Task TimeStatusAndTelemetry_AreAllPublished()
    {
        _store.Publish(Snapshot(1, Vessel("v1")));
        await WaitForTopicsAsync("gatos/time", "gatos/status", "gatos/vessels/v1/telemetry");
    }

    [Test]
    public async Task FullSurface_TopicsArePublished()
    {
        // Parity with the /sim tree and the HTTP /v1 reads: the broker publishes the world-level
        // and per-vessel-full topics, not just the compact telemetry doc.
        _store.Publish(Snapshot(1, Vessel("v1")) with
        {
            System = new SystemSnapshot("Kerbol", "Kerth", "Kerbol"),
            Bodies = [new BodySnapshot("Kerth", "Planet", null, [], 1, 1, 1, 1, 0,
                new double3Snap(0, 0, 0), new double3Snap(0, 0, 0), null, null, null)],
        });
        await WaitForTopicsAsync("gatos/system", "gatos/bodies", "gatos/snapshot", "gatos/vessels/v1/snapshot");
    }

    [Test]
    public async Task Time_TopicCarriesWarpSpeedsAndAutoWarp()
    {
        _store.Publish(Snapshot(1, Vessel("v1")) with { WarpSpeeds = [1, 10, 100], AutoWarpActive = true });
        // Skip the retained projection of the pre-first-sample Empty snapshot (warp_speeds = []).
        var payload = await WaitForPayloadAsync("gatos/time",
            p => JsonDocument.Parse(p).RootElement.GetProperty("warp_speeds").GetArrayLength() > 0);
        using var json = JsonDocument.Parse(payload);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("warp_speeds").GetArrayLength(), Is.EqualTo(3));
            Assert.That(json.RootElement.GetProperty("auto_warp_active").GetBoolean(), Is.True);
        });
    }

    [Test]
    public async Task Status_TopicCarriesControlAndDebugFlags()
    {
        _store.Publish(Snapshot(1, Vessel("v1")));
        // Skip the retained Empty-snapshot projection (game_version = "").
        var payload = await WaitForPayloadAsync("gatos/status",
            p => JsonDocument.Parse(p).RootElement.GetProperty("game_version").GetString() is { Length: > 0 });
        using var json = JsonDocument.Parse(payload);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("control").GetBoolean(), Is.True);
            Assert.That(json.RootElement.TryGetProperty("debug", out _), Is.True);
            Assert.That(json.RootElement.GetProperty("game_version").GetString(), Is.EqualTo("test"));
        });
    }

    [Test]
    public async Task VesselSnapshot_TopicIsTheFullRecordShape()
    {
        _store.Publish(Snapshot(1, Vessel("v1")));
        var payload = await WaitForAsync(t => t == "gatos/vessels/v1/snapshot");
        using var json = JsonDocument.Parse(payload);
        Assert.Multiple(() =>
        {
            Assert.That(json.RootElement.GetProperty("id").GetString(), Is.EqualTo("v1"));
            Assert.That(json.RootElement.TryGetProperty("situation", out _), Is.True,
                "the full record uses 'situation', not the compact doc's 'sit'");
        });
    }

    [Test]
    public async Task Field_TopicsMirrorTheSimTree()
    {
        _store.Publish(Snapshot(1, Vessel("v1")));
        // Both topics publish in one batch; collect them together (order is not guaranteed, and the
        // active alias subtree is pruned — a pointer stands in, prune is covered by VfsScan tests).
        var found = await WaitForPayloadsAsync(new Dictionary<string, Func<string, bool>>
        {
            ["gatos/sim/vessels/by-id/v1/situation"] = p => p == "Freefall",
            ["gatos/sim/vessels/active_id"] = p => p == "v1",
        });
        Assert.Multiple(() =>
        {
            Assert.That(found["gatos/sim/vessels/by-id/v1/situation"], Is.EqualTo("Freefall"),
                "one topic per /sim leaf, the bare value");
            Assert.That(found["gatos/sim/vessels/active_id"], Is.EqualTo("v1"));
        });
    }

    [Test]
    public async Task Field_SetActuatesAControlPoint()
    {
        _store.Publish(Snapshot(1, Vessel("v1"))); // so vessels/by-id/v1/ctl/throttle resolves
        await _client.PublishStringAsync("gatos/sim/vessels/by-id/v1/ctl/throttle/set", "0.8");
        var result = await WaitForAsync(t => t == SimMqttBroker.CommandResultTopic);
        Assert.That(result, Does.Contain("ok"));
        Assert.That(_sink.Last, Is.EqualTo(new SimCommand("v1", "vessel.throttle", SimCommand.NoOrdinal, 0.8)));
    }

    [Test]
    public async Task Command_IsNotRebroadcastToSubscribers()
    {
        // The broker consumes commands; the raw command JSON must never reach gatos/# subscribers
        // (it would leak one client's commands to all). Only the result is published.
        await _client.PublishStringAsync(SimMqttBroker.CommandTopic,
            """{"vessel_id":"v1","action":"engine.active","ordinal":0,"value":1}""");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sawResult = false;
        while (!sawResult && await _messages.Reader.WaitToReadAsync(cts.Token))
            while (_messages.Reader.TryRead(out var msg))
            {
                Assert.That(msg.Topic, Is.Not.EqualTo(SimMqttBroker.CommandTopic),
                    "the consumed command must not be relayed");
                if (msg.Topic == SimMqttBroker.CommandResultTopic)
                    sawResult = true;
            }

        Assert.That(sawResult, Is.True, "the result is still published");
    }

    [Test]
    public async Task RetainedTopics_ReachALateSubscriber()
    {
        _store.Publish(Snapshot(1, Vessel("v1")));
        await WaitForTopicsAsync("gatos/time"); // ensure the broker has published + retained

        // A subscriber that connects after the publish still gets the retained snapshot topics.
        var late = new MqttFactory().CreateMqttClient();
        var lateMessages = Channel.CreateUnbounded<(string Topic, string Payload)>();
        late.ApplicationMessageReceivedAsync += e =>
        {
            lateMessages.Writer.TryWrite((e.ApplicationMessage.Topic,
                Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray())));
            return Task.CompletedTask;
        };
        await late.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", _broker.Port).Build());
        try
        {
            await late.SubscribeAsync("gatos/#");
            var seen = new HashSet<string>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (!seen.IsSupersetOf(new[] { "gatos/time", "gatos/status", "gatos/vessels/v1/telemetry" })
                   && await lateMessages.Reader.WaitToReadAsync(cts.Token))
                while (lateMessages.Reader.TryRead(out var msg))
                    seen.Add(msg.Topic);

            Assert.That(seen, Does.Contain("gatos/time").And.Contain("gatos/status")
                .And.Contain("gatos/vessels/v1/telemetry"));
        }
        finally
        {
            await late.DisconnectAsync();
            late.Dispose();
        }
    }

    [Test]
    public async Task Command_MalformedJson_ReturnsEinval()
    {
        await _client.PublishStringAsync(SimMqttBroker.CommandTopic, "{ not json");
        var result = await WaitForAsync(t => t == SimMqttBroker.CommandResultTopic);
        Assert.That(result, Does.Contain("EINVAL"));
    }

    [Test]
    public async Task Command_DebugGated_ReturnsEacces_WhenDebugDisabled()
    {
        // A dedicated broker whose sink reports debug disabled: a debug.* command is gated.
        await using var broker = new SimMqttBroker(_store, new RecordingSink { DebugEnabled = false });
        await broker.StartAsync(0);

        var client = new MqttFactory().CreateMqttClient();
        var messages = Channel.CreateUnbounded<(string Topic, string Payload)>();
        client.ApplicationMessageReceivedAsync += e =>
        {
            messages.Writer.TryWrite((e.ApplicationMessage.Topic,
                Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment.ToArray())));
            return Task.CompletedTask;
        };
        await client.ConnectAsync(new MqttClientOptionsBuilder().WithTcpServer("127.0.0.1", broker.Port).Build());
        try
        {
            await client.SubscribeAsync(SimMqttBroker.CommandResultTopic);
            await client.PublishStringAsync(SimMqttBroker.CommandTopic,
                """{"vessel_id":"v1","action":"debug.refill_fuel","value":1}""");

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            string? result = null;
            while (result is null && await messages.Reader.WaitToReadAsync(cts.Token))
                while (messages.Reader.TryRead(out var msg))
                    if (msg.Topic == SimMqttBroker.CommandResultTopic)
                        result = msg.Payload;

            Assert.That(result, Does.Contain("EACCES"));
        }
        finally
        {
            await client.DisconnectAsync();
            client.Dispose();
        }
    }

    private async Task WaitForTopicsAsync(params string[] topics)
    {
        var remaining = new HashSet<string>(topics);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (remaining.Count > 0 && await _messages.Reader.WaitToReadAsync(cts.Token))
            while (_messages.Reader.TryRead(out var msg))
                remaining.Remove(msg.Topic);
        Assert.That(remaining, Is.Empty, $"never saw topics: {string.Join(", ", remaining)}");
    }

    private async Task<string> WaitForAsync(Func<string, bool> topicMatch)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
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

    /// <summary>
    ///     Waits for a message on <paramref name="topic"/> whose payload satisfies
    ///     <paramref name="payloadOk"/> — used to skip the retained projection of the pre-first-sample
    ///     Empty snapshot the broker publishes at startup and select the snapshot under test.
    /// </summary>
    private async Task<string> WaitForPayloadAsync(string topic, Func<string, bool> payloadOk)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await _messages.Reader.WaitToReadAsync(cts.Token))
            while (_messages.Reader.TryRead(out var msg))
                if (msg.Topic == topic && payloadOk(msg.Payload))
                    return msg.Payload;

        throw new TimeoutException($"no matching payload on {topic}");
    }

    /// <summary>
    ///     Collects the latest payload satisfying each topic's predicate, in a single pass over the
    ///     stream, so waiting for one topic never discards another that arrives in the same batch.
    /// </summary>
    private async Task<Dictionary<string, string>> WaitForPayloadsAsync(
        Dictionary<string, Func<string, bool>> wanted)
    {
        var found = new Dictionary<string, string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (found.Count < wanted.Count && await _messages.Reader.WaitToReadAsync(cts.Token))
            while (_messages.Reader.TryRead(out var msg))
                if (!found.ContainsKey(msg.Topic) && wanted.TryGetValue(msg.Topic, out var ok) && ok(msg.Payload))
                    found[msg.Topic] = msg.Payload;
        Assert.That(found, Has.Count.EqualTo(wanted.Count),
            $"never saw: {string.Join(", ", wanted.Keys.Except(found.Keys))}");
        return found;
    }

    private static SimSnapshot Snapshot(long seq, params VesselSnapshot[] vessels)
        => new(seq, seq * 0.1, 1, vessels.Length > 0 ? vessels[0].Id : null, vessels, [], "test", 10, []);

    private static VesselSnapshot Vessel(string id) => new(
        id, id, "Freefall", new double3Snap(0, 0, 0), 0, 0, 0, 0, 0, new QuatSnap(0, 0, 0, 1),
        new double3Snap(0, 0, 0), 0, 0, 1, 1, 0, null, [], [], null, "Kerth", false, []);

    private sealed class RecordingSink : ICommandSink
    {
        public bool ControlEnabled { get; init; } = true;
        public bool DebugEnabled { get; init; } = true;
        public CommandResult Result { get; set; } = CommandResult.Ok;
        public SimCommand? Last { get; private set; }

        public Task<CommandResult> SubmitAsync(SimCommand command, CancellationToken ct)
        {
            Last = command;
            return Task.FromResult(Result);
        }
    }
}
