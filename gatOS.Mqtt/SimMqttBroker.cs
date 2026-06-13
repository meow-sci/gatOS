using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using gatOS.Logging;
using gatOS.SimFs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using MQTTnet;
using MQTTnet.Server;

namespace gatOS.Mqtt;

/// <summary>
///     An MQTT transport (an additional game-data bridge alongside 9p / HTTP / serial): an embedded
///     MQTTnet broker over the <b>same</b> <see cref="SnapshotStore"/> + command pipeline. Telemetry
///     is published as retained topics under <c>gatos/…</c>; a command topic is subscribed and routed
///     into the <see cref="ICommandSink"/>. Guest MQTT clients reach the broker at
///     <c>10.0.2.2:&lt;port&gt;</c> (slirp), exactly like the 9p and HTTP servers — loopback only, no
///     firewall prompt, no external broker required.
/// </summary>
/// <remarks>
///     Topics (retained unless noted): <c>gatos/time</c>, <c>gatos/status</c>,
///     <c>gatos/vessels/&lt;id&gt;/telemetry</c>, and <c>gatos/events</c> (not retained). Commands are
///     published by clients to <c>gatos/command</c> as the JSON <see cref="SimCommand"/> shape; the
///     outcome is published to <c>gatos/command/result</c>. Threading: the publish pump and the
///     command interceptor only read the latest snapshot and enqueue commands (rules 1–2).
/// </remarks>
public sealed class SimMqttBroker : IAsyncDisposable
{
    /// <summary>The topic clients publish a JSON <see cref="SimCommand"/> to.</summary>
    public const string CommandTopic = "gatos/command";

    /// <summary>The topic the broker publishes each command's <c>{outcome}</c>/<c>{errno}</c> result to.</summary>
    public const string CommandResultTopic = "gatos/command/result";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly HashSet<string> SolverActions = ["debug.refill_fuel", "debug.refill_battery"];

    private readonly SnapshotStore _store;
    private readonly ICommandSink? _commands;
    private readonly CancellationTokenSource _cts = new();
    private MqttServer? _server;
    private Task? _pump;

    /// <param name="store">The published-snapshot exchange telemetry is read from.</param>
    /// <param name="commands">The command sink the command topic routes to; null = telemetry only.</param>
    public SimMqttBroker(SnapshotStore store, ICommandSink? commands = null)
    {
        _store = store;
        _commands = commands;
    }

    /// <summary>The bound TCP port (valid after <see cref="StartAsync"/>).</summary>
    public int Port { get; private set; }

    /// <summary>
    ///     Starts the broker. Tries <paramref name="preferredPort"/> (the conventional 1883) and
    ///     falls back to an ephemeral port on a clash; <c>0</c> goes straight to ephemeral.
    /// </summary>
    public async Task StartAsync(int preferredPort = 1883)
    {
        Port = await TryStartOnAsync(preferredPort).ConfigureAwait(false);
        _pump = Task.Run(() => PublishPumpAsync(_cts.Token));
    }

    private async Task<int> TryStartOnAsync(int preferredPort)
    {
        var port = preferredPort > 0 ? preferredPort : FreePort();
        try
        {
            _server = BuildServer(port);
            await _server.StartAsync().ConfigureAwait(false);
            return port;
        }
        catch (Exception) when (preferredPort > 0)
        {
            // Preferred port in use — retry once on a probed free port.
            _server?.Dispose();
            port = FreePort();
            _server = BuildServer(port);
            await _server.StartAsync().ConfigureAwait(false);
            return port;
        }
    }

    private MqttServer BuildServer(int port)
    {
        var options = new MqttServerOptionsBuilder()
            .WithDefaultEndpoint()
            .WithDefaultEndpointBoundIPAddress(IPAddress.Loopback)
            .WithDefaultEndpointPort(port)
            .Build();
        var server = new MqttFactory().CreateMqttServer(options);
        server.InterceptingPublishAsync += OnClientPublishAsync;
        return server;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ---- inbound: gatos/command -> the command pipeline ----------------------------------

    private async Task OnClientPublishAsync(InterceptingPublishEventArgs args)
    {
        if (args.ApplicationMessage.Topic != CommandTopic || _commands is not { } sink)
            return;

        // The broker injects its own retained messages with an empty client id; ignore those so we
        // only handle genuine client publishes to the command topic.
        if (string.IsNullOrEmpty(args.ClientId))
            return;

        var payload = args.ApplicationMessage.PayloadSegment.ToArray();
        string resultJson;
        try
        {
            var command = ParseCommand(payload);
            if (command.Action.StartsWith("debug.", StringComparison.Ordinal) && !sink.DebugEnabled)
                resultJson = Result("EACCES", "debug namespace disabled");
            else
            {
                var result = await sink.SubmitAsync(command, _cts.Token).ConfigureAwait(false);
                resultJson = result.IsSuccess
                    ? "{\"outcome\":\"ok\"}"
                    : Result(ErrnoName(result.Outcome), result.Message ?? result.Outcome.ToString());
            }
        }
        catch (Exception ex)
        {
            resultJson = Result("EINVAL", ex.Message);
        }

        await PublishAsync(CommandResultTopic, resultJson, retain: false).ConfigureAwait(false);
    }

    private static SimCommand ParseCommand(byte[] payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        var vessel = GetString(root, "vessel_id") ?? GetString(root, "vessel")
            ?? throw new ArgumentException("missing 'vessel_id'");
        var action = GetString(root, "action") ?? throw new ArgumentException("missing 'action'");
        var ordinal = root.TryGetProperty("ordinal", out var ord) && ord.ValueKind == JsonValueKind.Number
            ? ord.GetInt32()
            : SimCommand.NoOrdinal;
        var value = root.TryGetProperty("value", out var val) && val.ValueKind == JsonValueKind.Number
            ? val.GetDouble()
            : 0;
        double[]? values = null;
        if (root.TryGetProperty("values", out var arr) && arr.ValueKind == JsonValueKind.Array)
            values = arr.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var token = GetString(root, "token");
        var phase = SolverActions.Contains(action) ? CommandPhase.Solver : CommandPhase.Frame;
        return new SimCommand(vessel, action, ordinal, value, phase) { Values = values, Token = token };
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    // ---- outbound: telemetry pump --------------------------------------------------------

    private async Task PublishPumpAsync(CancellationToken ct)
    {
        var lastSeq = -1L;
        while (!ct.IsCancellationRequested)
        {
            SimSnapshot snapshot;
            try
            {
                snapshot = await _store.WaitForNextAsync(lastSeq, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lastSeq = snapshot.Sequence;
            try
            {
                await PublishAsync("gatos/time", Serialize(new
                {
                    ut = snapshot.UtSeconds, warp = snapshot.WarpFactor, sim_dt = snapshot.SimDtSeconds,
                }), retain: true).ConfigureAwait(false);
                await PublishAsync("gatos/status", Serialize(new
                {
                    game_version = snapshot.GameVersion, sample_rate_hz = snapshot.SampleRateHz,
                }), retain: true).ConfigureAwait(false);
                foreach (var vessel in snapshot.Vessels)
                    await PublishAsync($"gatos/vessels/{vessel.Id}/telemetry",
                        Formats.VesselTelemetry(snapshot, vessel), retain: true).ConfigureAwait(false);
                foreach (var simEvent in snapshot.NewEvents)
                    await PublishAsync("gatos/events", Serialize(new
                    {
                        ut = simEvent.UtSeconds, type = simEvent.Type, vessel = simEvent.VesselId,
                        detail = simEvent.Detail,
                    }), retain: false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ModLog.Log.Debug($"mqtt: publish failed: {ex.Message}");
            }
        }
    }

    private async Task PublishAsync(string topic, string payload, bool retain)
    {
        if (_server is not { } server)
            return;
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(payload))
            .WithRetainFlag(retain)
            .Build();
        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(message)).ConfigureAwait(false);
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);

    private static string Result(string errno, string message)
        => JsonSerializer.Serialize(new { errno, message }, Json);

    private static string ErrnoName(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Invalid => "EINVAL",
        CommandOutcome.NotFound => "ENOENT",
        CommandOutcome.Denied => "EACCES",
        CommandOutcome.Busy => "EBUSY",
        CommandOutcome.TimedOut => "ETIMEDOUT",
        CommandOutcome.Unsupported => "EOPNOTSUPP",
        _ => "EIO",
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        if (_server is not null)
        {
            try
            {
                await _server.StopAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _server.Dispose();
        }

        _cts.Dispose();
    }
}
