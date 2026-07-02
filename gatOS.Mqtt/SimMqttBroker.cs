using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
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
///     Topics (retained unless noted) — the same data the HTTP <c>/v1</c> reads serve, projected
///     through the shared <see cref="SimJson"/> layer so the two transports stay at parity:
///     <c>gatos/time</c>, <c>gatos/status</c>, <c>gatos/system</c>, <c>gatos/bodies</c>,
///     <c>gatos/snapshot</c> (whole world), <c>gatos/vessels/&lt;id&gt;/telemetry</c> (the compact
///     SDK-stable doc), <c>gatos/vessels/&lt;id&gt;/snapshot</c> (the full granular vessel record),
///     and <c>gatos/events</c> (not retained). Commands are published by clients to
///     <c>gatos/command</c> as the JSON <see cref="SimCommand"/> shape — the same action set the
///     other transports accept — and the outcome is published to <c>gatos/command/result</c>.
///     Threading: the publish pump and the command interceptor only read the latest snapshot and
///     enqueue commands (rules 1–2).
///     <para>When a <c>simRoot</c> is supplied, a second pump mirrors the <c>/sim</c> filesystem
///     leaf-by-leaf under <c>gatos/sim/&lt;path&gt;</c> (one retained topic per scalar/<c>ctl</c>/
///     <c>debug</c> field, so an MQTT explorer renders the whole device tree, not just JSON blobs).
///     It publishes only changed leaves (throttled to <c>fieldFeedHz</c>), clears vanished ones, and
///     exposes <c>gatos/sim/vessels/active_id</c> as a pointer in place of the duplicate <c>active</c>
///     alias subtree. A client writes one field by publishing its value to
///     <c>gatos/sim/&lt;path&gt;/set</c> (the same actuation path as a 9p <c>echo</c>; outcome on
///     <c>gatos/command/result</c>).</para>
///     <para>Retained per-vessel topics for a vessel that has since vanished are cleared with an
///     empty retained payload (the MQTT tombstone) on the next publish cycle — the same behavior
///     the field mirror has always had for disappeared leaves (GP2).</para>
///     <para><b>Subscription-aware (GP2):</b> a topic no live filter matches is neither serialized
///     nor injected; a new subscription (or connect) forces one full cycle so its retained baseline
///     lands within a cycle. With <c>mqtt_publish_hz</c> &gt; 0 the world pump is additionally
///     capped to that cadence (coalescing to the newest snapshot; default 0 = every snapshot).</para>
/// </remarks>
public sealed class SimMqttBroker : IAsyncDisposable
{
    /// <summary>The topic clients publish a JSON <see cref="SimCommand"/> to.</summary>
    public const string CommandTopic = "gatos/command";

    /// <summary>The topic the broker publishes each command's <c>{outcome}</c>/<c>{errno}</c> result to.</summary>
    public const string CommandResultTopic = "gatos/command/result";

    /// <summary>Topic prefix for the field-level <c>/sim</c> mirror (one topic per leaf).</summary>
    private const string FieldTopicPrefix = "gatos/sim/";

    /// <summary>Suffix a client appends to a field topic to write it (e.g. <c>.../ctl/throttle/set</c>).</summary>
    private const string SetSuffix = "/set";

    private readonly SnapshotStore _store;
    private readonly ICommandSink? _commands;
    private readonly Func<string>? _transports;
    private readonly VfsDirectory? _simRoot;
    private readonly int _fieldFeedHz;
    private readonly int _publishHz;
    private readonly Dictionary<string, byte[]> _lastWorld = new(); // owned by the world pump task only
    private Dictionary<string, string> _lastFields = new(); // owned by the field pump task only
    private Dictionary<string, string> _scratchFields = new(); // ditto (swapped with _lastFields per cycle)
    private readonly ConcurrentDictionary<string, string> _fieldTopics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Telemetry, string Snapshot)> _vesselTopics
        = new(StringComparer.Ordinal);
    private HashSet<string> _publishedVesselIds = []; // owned by the world pump task only
    private HashSet<string> _scratchVesselIds = []; // ditto (swapped per cycle)
    private readonly CancellationTokenSource _cts = new();
    // Per-pump wake signals: a client connect (or a new subscription) releases these so a parked
    // pump republishes the current state immediately, rather than waiting for the next snapshot
    // (which, with no sampler, might never come — and the new client needs its retained baseline now).
    private readonly SemaphoreSlim _worldWake = new(0, int.MaxValue);
    private readonly SemaphoreSlim _fieldWake = new(0, int.MaxValue);
    // Live subscription filters (GP2): topics no filter matches are neither serialized nor
    // injected. Mutated on the (rare) subscribe/unsubscribe/disconnect events under _subLock; the
    // pumps read the volatile flattened array — no per-topic enumeration allocation.
    private readonly object _subLock = new();
    private readonly Dictionary<string, List<string>> _subscriptions = new(StringComparer.Ordinal);
    private volatile string[] _activeFilters = [];
    private int _connectedClients;
    private int _worldForce;
    private int _fieldForce;
    // Reference-identity of the last-serialized system/bodies sources (GP2 delta-at-source): the
    // sampler re-publishes unchanged bodies/system BY REFERENCE (GP3), so this skips even the
    // serialize-then-compare for the static topics. Owned by the world pump task.
    private object? _lastSystemSource;
    private object? _lastBodiesSource;
    private MqttServer? _server;
    private Task? _pump;
    private Task? _fieldPump;

    /// <param name="store">The published-snapshot exchange telemetry is read from.</param>
    /// <param name="commands">The command sink the command topic routes to; null = telemetry only.</param>
    /// <param name="transports">Optional provider for the <c>gatos/status</c> transports line.</param>
    /// <param name="simRoot">
    ///     The <c>/sim</c> VFS tree (the same instance the 9p server serves); when supplied, the
    ///     field-level <c>gatos/sim/&lt;path&gt;</c> mirror runs. Null = JSON topics only.
    /// </param>
    /// <param name="fieldFeedHz">Field-mirror publish cadence, throttled below the sample rate.</param>
    /// <param name="publishHz">
    ///     World-topic publish cadence cap in Hz (GP2); <c>0</c> (the default) publishes on every
    ///     snapshot. Below the sample rate the pump coalesces to the newest snapshot — nothing is
    ///     queued, the final state always lands.
    /// </param>
    public SimMqttBroker(SnapshotStore store, ICommandSink? commands = null, Func<string>? transports = null,
        VfsDirectory? simRoot = null, int fieldFeedHz = 4, int publishHz = 0)
    {
        _store = store;
        _commands = commands;
        _transports = transports;
        _simRoot = simRoot;
        _fieldFeedHz = fieldFeedHz;
        _publishHz = publishHz;
    }

    /// <summary>The bound TCP port (valid after <see cref="StartAsync"/>).</summary>
    public int Port { get; private set; }

    /// <summary>
    ///     MQTT clients connected right now (for the sampler idle gate and the publish-when-consumed
    ///     optimization). Zero ⇒ the publish pumps do no serialization work.
    /// </summary>
    public int ConnectedClients => Volatile.Read(ref _connectedClients);

    /// <summary>
    ///     Timing of one world-publish cycle (serialize every retained topic + inject the changed
    ///     ones), recorded only while a client is connected. This runs on a background pump — it never
    ///     blocks the game thread — but it is the heaviest CPU gatOS spends, so the status window
    ///     surfaces it. Allocation-free.
    /// </summary>
    public PerfStat PublishStats { get; } = new();

    /// <summary>
    ///     Starts the broker. Tries <paramref name="preferredPort"/> (the conventional 1883) and
    ///     falls back to an ephemeral port on a clash; <c>0</c> goes straight to ephemeral.
    /// </summary>
    public async Task StartAsync(int preferredPort = 1883)
    {
        Port = await TryStartOnAsync(preferredPort).ConfigureAwait(false);
        _pump = Task.Run(() => PublishPumpAsync(_cts.Token));
        if (_simRoot is not null)
            _fieldPump = Task.Run(() => FieldPumpAsync(_cts.Token));
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
        server.ClientConnectedAsync += OnClientConnectedAsync;
        server.ClientDisconnectedAsync += OnClientDisconnectedAsync;
        server.ClientSubscribedTopicAsync += OnClientSubscribedAsync;
        server.ClientUnsubscribedTopicAsync += OnClientUnsubscribedAsync;
        return server;
    }

    private Task OnClientConnectedAsync(ClientConnectedEventArgs e)
    {
        Interlocked.Increment(ref _connectedClients);
        ForceRepublish();
        return Task.CompletedTask;
    }

    private Task OnClientDisconnectedAsync(ClientDisconnectedEventArgs e)
    {
        Interlocked.Decrement(ref _connectedClients);
        lock (_subLock)
        {
            if (_subscriptions.Remove(e.ClientId))
                RebuildFiltersLocked();
        }

        return Task.CompletedTask;
    }

    private Task OnClientSubscribedAsync(ClientSubscribedTopicEventArgs e)
    {
        lock (_subLock)
        {
            if (!_subscriptions.TryGetValue(e.ClientId, out var filters))
                _subscriptions[e.ClientId] = filters = [];
            if (!filters.Contains(e.TopicFilter.Topic))
                filters.Add(e.TopicFilter.Topic);
            RebuildFiltersLocked();
        }

        // A topic the pumps skipped (no matching filter) may never have been injected, so the new
        // subscription forces a full cycle to lay its retained baseline down (GP2).
        ForceRepublish();
        return Task.CompletedTask;
    }

    private Task OnClientUnsubscribedAsync(ClientUnsubscribedTopicEventArgs e)
    {
        lock (_subLock)
        {
            if (_subscriptions.TryGetValue(e.ClientId, out var filters) && filters.Remove(e.TopicFilter))
                RebuildFiltersLocked();
        }

        return Task.CompletedTask;
    }

    private void RebuildFiltersLocked()
        => _activeFilters = _subscriptions.Values.SelectMany(f => f).Distinct().ToArray();

    /// <summary>Force one full (re)publish on both pumps and wake them.</summary>
    private void ForceRepublish()
    {
        Interlocked.Exchange(ref _worldForce, 1);
        Interlocked.Exchange(ref _fieldForce, 1);
        _worldWake.Release();
        _fieldWake.Release();
    }

    /// <summary>
    ///     Whether any live subscription filter matches <paramref name="topic"/> (GP2): a topic
    ///     nobody subscribed to is neither serialized nor injected. Allocation-free — iterates the
    ///     flattened filter array the subscription events maintain.
    /// </summary>
    private bool HasSubscriber(string topic)
    {
        var filters = _activeFilters;
        foreach (var filter in filters)
            if (MqttTopicFilterComparer.Compare(topic, filter) == MqttTopicFilterCompareResult.IsMatch)
                return true;
        return false;
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // ---- inbound: gatos/command + gatos/sim/<path>/set -> the command pipeline ------------

    private async Task OnClientPublishAsync(InterceptingPublishEventArgs args)
    {
        var topic = args.ApplicationMessage.Topic;

        // Field-level actuation: a client publish to gatos/sim/<path>/set writes one /sim field
        // through the same VFS node path as a 9p `echo` (the field value topics gatos/sim/<path>
        // are injected by the field pump — empty client id — and fall through to be delivered).
        if (_simRoot is { } root && topic.StartsWith(FieldTopicPrefix, StringComparison.Ordinal)
            && topic.EndsWith(SetSuffix, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(args.ClientId))
                return; // not a client publish
            args.ProcessPublish = false; // consume the set; never rebroadcast it
            await HandleFieldSetAsync(root, topic, args.ApplicationMessage.PayloadSegment.ToArray())
                .ConfigureAwait(false);
            return;
        }

        if (topic != CommandTopic)
            return;

        // The broker injects its own retained messages with an empty client id; ignore those so we
        // only handle genuine client publishes to the command topic.
        if (string.IsNullOrEmpty(args.ClientId))
            return;

        // A command is consumed by the broker, not relayed: stop MQTTnet from re-broadcasting the
        // raw command payload to every other gatos/# subscriber (it would leak one client's
        // commands to all, and a client-set retain flag would make it stick). The reply goes out
        // on gatos/command/result instead.
        args.ProcessPublish = false;

        if (_commands is not { } sink)
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
                    : Result(result.Outcome.ErrnoName(), result.Message ?? result.Outcome.ToString());
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
        return new SimCommand(vessel, action, ordinal, value) { Values = values, Token = token };
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    // ---- outbound: telemetry pump --------------------------------------------------------

    private async Task PublishPumpAsync(CancellationToken ct)
    {
        // Emit-then-rate-limit when a cadence cap is configured (GP2): the store coalesces (it
        // keeps only Current), so after the delay the pump publishes the newest snapshot — capped
        // at publish_hz, never dropping the final state.
        var interval = _publishHz > 0 ? TimeSpan.FromSeconds(1.0 / _publishHz) : TimeSpan.Zero;
        var lastSeq = -1L;
        Task? wakeWaiter = null;
        while (!ct.IsCancellationRequested)
        {
            SimSnapshot snapshot;
            try
            {
                (snapshot, wakeWaiter) = await WaitForWorkAsync(lastSeq, _worldWake, wakeWaiter, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lastSeq = snapshot.Sequence;
            var force = Interlocked.Exchange(ref _worldForce, 0) != 0;
            // Nobody is listening and no forced refresh is pending: skip the whole serialize+inject.
            // The last-published retained messages stay valid, so a future subscriber still gets them
            // (MQTTnet redelivers retained topics on subscribe) — we just stop doing work for no one.
            if (!force && ConnectedClients <= 0)
                continue;

            var startTs = Stopwatch.GetTimestamp();
            try
            {
                // World-level retained topics — the same projections the HTTP /v1 reads serve, via
                // the shared SimJson layer (cross-transport parity). Each is serialized only when
                // some filter subscribes to it (GP2) and injected only when its bytes changed.
                await PublishChangedAsync("gatos/time", () => SimJson.TimeBytes(snapshot), force)
                    .ConfigureAwait(false);
                await PublishChangedAsync("gatos/status", () => SimJson.StatusBytes(snapshot,
                    _commands is { ControlEnabled: true }, _commands is { DebugEnabled: true },
                    _transports?.Invoke()), force).ConfigureAwait(false);
                // Delta-at-source (GP2): the sampler re-publishes an unchanged system/bodies by
                // REFERENCE (GP3 static caches + bodies sub-cadence), so identity short-circuits
                // even the serialize-then-compare. A new subscriber's force bypasses it.
                if (force || !ReferenceEquals(snapshot.System, _lastSystemSource))
                {
                    await PublishChangedAsync("gatos/system", () => SimJson.SerializeToUtf8Bytes(snapshot.System),
                        force).ConfigureAwait(false);
                    _lastSystemSource = snapshot.System;
                }

                if (force || !ReferenceEquals(snapshot.Bodies, _lastBodiesSource))
                {
                    await PublishChangedAsync("gatos/bodies", () => SimJson.SerializeToUtf8Bytes(snapshot.Bodies),
                        force).ConfigureAwait(false);
                    _lastBodiesSource = snapshot.Bodies;
                }

                await PublishChangedAsync("gatos/snapshot", () => SimJson.SerializeToUtf8Bytes(snapshot), force)
                    .ConfigureAwait(false);

                // Per vessel: the compact telemetry doc (SDK-stable) and the full granular snapshot.
                var liveIds = _scratchVesselIds;
                liveIds.Clear();
                foreach (var vessel in snapshot.Vessels)
                {
                    liveIds.Add(vessel.Id);
                    var topics = _vesselTopics.GetOrAdd(vessel.Id,
                        static id => ($"gatos/vessels/{id}/telemetry", $"gatos/vessels/{id}/snapshot"));
                    await PublishChangedAsync(topics.Telemetry,
                        () => Formats.VesselTelemetryUtf8(snapshot, vessel), force).ConfigureAwait(false);
                    await PublishChangedAsync(topics.Snapshot,
                        () => SimJson.SerializeToUtf8Bytes(vessel), force).ConfigureAwait(false);
                }

                // Clear the retained topics of vessels that vanished (GP2): an empty retained
                // payload is the MQTT tombstone — mirrors the field mirror's behavior for
                // disappeared leaves, so clients no longer see ghosts until a broker restart.
                foreach (var id in _publishedVesselIds)
                {
                    if (liveIds.Contains(id) || !_vesselTopics.TryRemove(id, out var topics))
                        continue;
                    _lastWorld.Remove(topics.Telemetry);
                    _lastWorld.Remove(topics.Snapshot);
                    await PublishAsync(topics.Telemetry, [], retain: true).ConfigureAwait(false);
                    await PublishAsync(topics.Snapshot, [], retain: true).ConfigureAwait(false);
                }

                (_publishedVesselIds, _scratchVesselIds) = (liveIds, _publishedVesselIds);

                // Events are discrete (not retained) — always emit, never changed-only-suppressed.
                if (snapshot.NewEvents.Count > 0 && (force || HasSubscriber("gatos/events")))
                    foreach (var simEvent in snapshot.NewEvents)
                        await PublishAsync("gatos/events", SimJson.EventBytes(simEvent), retain: false)
                            .ConfigureAwait(false);

                PublishStats.Add(Stopwatch.GetTimestamp() - startTs); // a complete cycle only
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ModLog.Log.Debug($"mqtt: publish failed: {ex.Message}");
            }

            if (interval > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    ///     Injects a retained world topic only when some live filter subscribes to it (the payload
    ///     is not even serialized otherwise — GP2) and its bytes changed since the last publish (or
    ///     <paramref name="force"/> — a fresh client's/subscription's baseline). Static topics
    ///     (<c>system</c>/<c>bodies</c>) and a paused sim thus publish once and then go quiet.
    ///     Owned by the world pump task, so <see cref="_lastWorld"/> needs no lock.
    /// </summary>
    private async Task PublishChangedAsync(string topic, Func<byte[]> payload, bool force)
    {
        if (!force && !HasSubscriber(topic))
            return;
        var bytes = payload();
        if (!force && _lastWorld.TryGetValue(topic, out var last) && last.AsSpan().SequenceEqual(bytes))
            return;
        _lastWorld[topic] = bytes;
        await PublishAsync(topic, bytes, retain: true).ConfigureAwait(false);
    }

    /// <summary>
    ///     Waits for either the next snapshot or a connect/subscribe wake. The wake waiter persists
    ///     across iterations instead of being cancelled per loop (GP2) — an unconsumed waiter just
    ///     means one spurious republish check, which costs nothing; the pre-GP2 version allocated a
    ///     linked CTS + cancel + two fault-observers on <b>every snapshot</b>, even with no clients.
    /// </summary>
    /// <returns>The snapshot to publish, and the still-pending wake waiter (null when consumed).</returns>
    private async Task<(SimSnapshot Snapshot, Task? PendingWake)> WaitForWorkAsync(
        long lastSeq, SemaphoreSlim wake, Task? pendingWake, CancellationToken ct)
    {
        pendingWake ??= wake.WaitAsync(ct);
        var nextTask = _store.WaitForNextAsync(lastSeq, ct).AsTask();
        var winner = await Task.WhenAny(nextTask, pendingWake).ConfigureAwait(false);
        if (winner == pendingWake)
        {
            Observe(nextTask); // abandoned; completes with the next publish (or shutdown cancel)
            await pendingWake.ConfigureAwait(false); // propagate a cancellation fault
            return (_store.Current, null);
        }

        return (await nextTask.ConfigureAwait(false), pendingWake);
    }

    private static void Observe(Task task)
        => _ = task.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    // ---- outbound: field-level /sim mirror (gatos/sim/<path>) ----------------------------

    private async Task FieldPumpAsync(CancellationToken ct)
    {
        if (_simRoot is not { } root)
            return;

        // Emit-then-rate-limit: publish the latest snapshot's changed leaves, then sleep one
        // interval before looking for the next change. The store coalesces (it keeps only Current),
        // so after the sleep WaitForNextAsync returns the newest snapshot — we publish at most
        // field_feed_hz times a second yet never drop the final state when the sim pauses.
        var interval = TimeSpan.FromSeconds(1.0 / Math.Max(1, _fieldFeedHz));
        var lastSeq = -1L;
        Task? wakeWaiter = null;
        while (!ct.IsCancellationRequested)
        {
            SimSnapshot snapshot;
            try
            {
                (snapshot, wakeWaiter) = await WaitForWorkAsync(lastSeq, _fieldWake, wakeWaiter, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lastSeq = snapshot.Sequence;
            var force = Interlocked.Exchange(ref _fieldForce, 0) != 0;
            // The field walk reads every /sim leaf — skip it entirely when no client is connected.
            if (force || ConnectedClients > 0)
            {
                try
                {
                    await PublishFieldsAsync(root, snapshot, force, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    ModLog.Log.Debug($"mqtt: field feed failed: {ex.Message}");
                }
            }

            try
            {
                await Task.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PublishFieldsAsync(VfsDirectory root, SimSnapshot snapshot, bool force, CancellationToken ct)
    {
        if (force)
            _lastFields.Clear(); // a fresh client/subscription: republish every leaf as its baseline
        var current = _scratchFields; // reused, not re-allocated (GP2)
        current.Clear();
        // Canonical vessels/by-id only — the `active` alias would duplicate a whole subtree; expose
        // the active vessel id as a small pointer instead.
        current["vessels/active_id"] = snapshot.ActiveVesselId ?? "";
        foreach (var (path, file) in VfsScan.Leaves(root, p => p == "vessels/active"))
        {
            try
            {
                current[path] = await VfsScan.ReadTextAsync(file, ct).ConfigureAwait(false);
            }
            catch (VfsErrorException)
            {
                // The leaf's entity vanished mid-walk; omit it (cleared below if previously published).
            }
        }

        // Publish new/changed leaves the live filters actually subscribe to (GP2 — _lastFields is
        // still recorded for skipped ones; a later subscription forces a full republish); clear
        // (empty retained) the ones that disappeared. Topic strings are interned per path.
        foreach (var (path, value) in current)
            if (!_lastFields.TryGetValue(path, out var old) || old != value)
            {
                var topic = _fieldTopics.GetOrAdd(path, static p => FieldTopicPrefix + p);
                if (force || HasSubscriber(topic))
                    await PublishAsync(topic, value, retain: true).ConfigureAwait(false);
            }

        foreach (var path in _lastFields.Keys)
            if (!current.ContainsKey(path))
                await PublishAsync(_fieldTopics.GetOrAdd(path, static p => FieldTopicPrefix + p), "", retain: true)
                    .ConfigureAwait(false);

        (_lastFields, _scratchFields) = (current, _lastFields);
    }

    private async Task HandleFieldSetAsync(VfsDirectory root, string topic, byte[] payload)
    {
        var inner = topic[FieldTopicPrefix.Length..^SetSuffix.Length];
        string resultJson;
        try
        {
            if (VfsScan.Resolve(root, inner) is not { } file)
                resultJson = Result("ENOENT", $"no field '{inner}'");
            else
            {
                await VfsScan.WriteTextAsync(file, Encoding.UTF8.GetString(payload), _cts.Token)
                    .ConfigureAwait(false);
                resultJson = "{\"outcome\":\"ok\"}";
            }
        }
        catch (VfsErrorException ex)
        {
            resultJson = Result(LinuxErrno.Name(ex.Errno), ex.Message);
        }
        catch (Exception ex)
        {
            resultJson = Result("EINVAL", ex.Message);
        }

        await PublishAsync(CommandResultTopic, resultJson, retain: false).ConfigureAwait(false);
    }

    private Task PublishAsync(string topic, string payload, bool retain)
        => PublishAsync(topic, Encoding.UTF8.GetBytes(payload), retain);

    private async Task PublishAsync(string topic, byte[] payload, bool retain)
    {
        if (_server is not { } server)
            return;
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithRetainFlag(retain)
            .Build();
        await server.InjectApplicationMessage(new InjectedMqttApplicationMessage(message)).ConfigureAwait(false);
    }

    private static string Result(string errno, string message)
        => SimJson.Serialize(new { errno, message });

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var pump in new[] { _pump, _fieldPump })
        {
            if (pump is null)
                continue;
            try
            {
                await pump.ConfigureAwait(false);
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
        _worldWake.Dispose();
        _fieldWake.Dispose();
    }
}
