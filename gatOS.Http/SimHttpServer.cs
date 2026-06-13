using System.Buffers;
using System.Globalization;
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

namespace gatOS.Http;

/// <summary>
///     The "magic" HTTP transport (KSA_GAME_INTEGRATION_PLAN Part 6 T2 / G5): a loopback REST + SSE
///     server over the <b>same</b> game-free domain the 9p tree uses — <see cref="SnapshotStore"/>
///     for reads and the <see cref="ICommandSink"/> command pipeline for writes. No KSA coupling,
///     no third-party HTTP dependency (raw <c>TcpListener</c>, see <see cref="HttpRequestLine"/>).
///     The guest reaches it via slirp at <c>10.0.2.2:&lt;port&gt;</c>; the host binds 127.0.0.1.
/// </summary>
/// <remarks>
///     <para>Reads are a JSON projection of the published snapshot (atomicity is the headline
///     advantage over the file tree). Writes go through one generic <c>POST /v1/command</c> endpoint
///     carrying the transport-agnostic <see cref="SimCommand"/> shape — the same actions the
///     integration matrix documents, so HTTP adds no second routing table to keep in sync. A
///     non-success outcome maps to the matching HTTP status plus <c>{"errno","message"}</c>.</para>
///     <para>Threading: handlers only read the latest published snapshot and enqueue commands
///     (rules 1–2), exactly like the 9p server.</para>
/// </remarks>
public sealed class SimHttpServer : IAsyncDisposable
{
    private readonly SnapshotStore _store;
    private readonly ICommandSink? _commands;
    private readonly Func<string>? _transports;
    private readonly VfsDirectory? _simRoot;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _activeSessions;

    /// <param name="store">The published-snapshot exchange reads project from.</param>
    /// <param name="commands">The command sink writes submit to; null = read-only API.</param>
    /// <param name="transports">Optional provider for <c>/v1/status</c>'s transports line.</param>
    /// <param name="simRoot">
    ///     The <c>/sim</c> VFS tree (the same instance the 9p server serves), enabling the
    ///     field-level <c>/v1/fs/&lt;path&gt;</c> endpoints that mirror the filesystem leaf-by-leaf.
    ///     Null disables them (JSON surface only).
    /// </param>
    public SimHttpServer(SnapshotStore store, ICommandSink? commands = null, Func<string>? transports = null,
        VfsDirectory? simRoot = null)
    {
        _store = store;
        _commands = commands;
        _transports = transports;
        _simRoot = simRoot;
    }

    /// <summary>The bound TCP port (valid after <see cref="StartAsync"/>).</summary>
    public int Port { get; private set; }

    /// <summary>Open HTTP connections being served right now (for the sampler idle gate).</summary>
    public int ActiveSessions => Volatile.Read(ref _activeSessions);

    /// <summary>
    ///     Binds the server. Tries <paramref name="preferredPort"/> first (the conventional 4242)
    ///     and falls back to an ephemeral port on a clash; <c>0</c> goes straight to ephemeral.
    /// </summary>
    public Task StartAsync(int preferredPort = 0)
    {
        _listener = BindAndStart(preferredPort);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private static TcpListener BindAndStart(int preferredPort)
    {
        if (preferredPort > 0)
        {
            try
            {
                var preferred = new TcpListener(IPAddress.Loopback, preferredPort);
                preferred.Start();
                return preferred;
            }
            catch (SocketException)
            {
                // Port in use — fall through to an ephemeral port.
            }
        }

        var ephemeral = new TcpListener(IPAddress.Loopback, 0);
        ephemeral.Start();
        return ephemeral;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"http: accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(() => ServeAsync(client, ct), ct);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeSessions);
        try
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            var request = await HttpRequestLine.ReadAsync(stream, ct).ConfigureAwait(false);
            if (request is null)
                return;
            await DispatchAsync(stream, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Server shutting down or client gone — nothing to report.
        }
        catch (Exception ex)
        {
            ModLog.Log.Debug($"http: request error: {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeSessions);
            client.Dispose();
        }
    }

    private async Task DispatchAsync(Stream stream, HttpRequestLine request, CancellationToken ct)
    {
        var seg = request.Segments;
        if (seg.Length == 0 || seg[0] != "v1")
        {
            await WriteJsonAsync(stream, 404, Error("ENOENT", "not found"), ct).ConfigureAwait(false);
            return;
        }

        // POST /v1/command — the one write endpoint (every control action).
        if (request.Method == "POST" && seg.Length == 2 && seg[1] == "command")
        {
            await HandleCommandAsync(stream, request, ct).ConfigureAwait(false);
            return;
        }

        // /v1/fs/<path...> — the field-level filesystem mirror (per-leaf read / SSE / write). Handled
        // before the GET-only gate below, since field writes are POST.
        if (seg.Length >= 2 && seg[1] == "fs")
        {
            await HandleFsAsync(stream, request, ct).ConfigureAwait(false);
            return;
        }

        if (request.Method != "GET")
        {
            await WriteJsonAsync(stream, 405, Error("EINVAL", "method not allowed"), ct).ConfigureAwait(false);
            return;
        }

        // GET /v1/events — Server-Sent Events.
        if (seg is ["v1", "events"])
        {
            await StreamEventsAsync(stream, ct).ConfigureAwait(false);
            return;
        }

        // GET /v1/time/wait?until=<ut> — long-poll sim-time alarm.
        if (seg is ["v1", "time", "wait"])
        {
            await HandleTimeWaitAsync(stream, request, ct).ConfigureAwait(false);
            return;
        }

        // GET /v1/vessels/<id>/stream — SSE of the per-vessel telemetry stream line (the HTTP twin
        // of the 9p growing-log `stream` file). MQTT serves the same stream via its republished
        // telemetry topic.
        if (seg is ["v1", "vessels", var streamId, "stream"])
        {
            await StreamVesselAsync(stream, streamId, ct).ConfigureAwait(false);
            return;
        }

        var (status, json) = Read(seg);
        await WriteRawJsonAsync(stream, status, json, ct).ConfigureAwait(false);
    }

    // ---- reads ---------------------------------------------------------------------------

    private (int Status, string Json) Read(string[] seg)
    {
        var snapshot = _store.Current;
        switch (seg)
        {
            case ["v1", "snapshot"]:
                return (200, Serialize(snapshot));
            case ["v1", "openapi.json"]:
                return (200, OpenApi.Document);
            case ["v1", "time"]:
                return (200, SimJson.Time(snapshot));
            case ["v1", "status"]:
                return (200, SimJson.Status(snapshot, _commands is { ControlEnabled: true },
                    _commands is { DebugEnabled: true }, _transports?.Invoke()));
            case ["v1", "system"]:
                return (200, Serialize(snapshot.System));
            case ["v1", "bodies"]:
                return (200, Serialize(snapshot.Bodies));
            case ["v1", "bodies", var id]:
                return Find(snapshot.Bodies, b => b.Id == id, $"body '{id}'");
            case ["v1", "vessels"]:
                return (200, Serialize(snapshot.Vessels.Select(v => v.Id)));
            case ["v1", "vessels", var id]:
                return Find(snapshot.Vessels, v => v.Id == id, $"vessel '{id}'");
            case ["v1", "vessels", var id, "telemetry"]:
                return snapshot.Vessels.FirstOrDefault(v => v.Id == id) is { } vessel
                    ? (200, Formats.VesselTelemetry(snapshot, vessel))
                    : (404, Error("ENOENT", $"vessel '{id}' is gone"));
            default:
                return (404, Error("ENOENT", "not found"));
        }
    }

    private static (int, string) Find<T>(IReadOnlyList<T> items, Func<T, bool> match, string what)
    {
        var item = items.FirstOrDefault(match);
        return item is null ? (404, Error("ENOENT", $"{what} is gone")) : (200, Serialize(item));
    }

    private async Task HandleTimeWaitAsync(Stream stream, HttpRequestLine request, CancellationToken ct)
    {
        if (!request.Query.TryGetValue("until", out var untilText)
            || !double.TryParse(untilText, NumberStyles.Float, CultureInfo.InvariantCulture, out var until))
        {
            await WriteJsonAsync(stream, 400, Error("EINVAL", "missing or invalid 'until' (sim time)"), ct)
                .ConfigureAwait(false);
            return;
        }

        var snapshot = _store.Current;
        while (snapshot.UtSeconds < until)
            snapshot = await _store.WaitForNextAsync(snapshot.Sequence, ct).ConfigureAwait(false);
        await WriteRawJsonAsync(stream, 200, Serialize(new { reached_ut = snapshot.UtSeconds }), ct)
            .ConfigureAwait(false);
    }

    private async Task StreamEventsAsync(Stream stream, CancellationToken ct)
    {
        // Snapshot the baseline sequence BEFORE flushing headers. The client's GetStream()
        // completes once the headers arrive, after which it may publish immediately; capturing
        // lastSeq first guarantees such a publish has a sequence > lastSeq and is delivered
        // (WaitForNextAsync rechecks Current), instead of being swallowed by the header-flush gap.
        var lastSeq = _store.Current.Sequence;

        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var snapshot = await _store.WaitForNextAsync(lastSeq, ct).ConfigureAwait(false);
            lastSeq = snapshot.Sequence;
            foreach (var simEvent in snapshot.NewEvents)
            {
                var line = "data: " + SimJson.Event(simEvent) + "\n\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
    }

    private async Task StreamVesselAsync(Stream stream, string vesselId, CancellationToken ct)
    {
        // Baseline before the header flush (see StreamEventsAsync): a publish racing the header
        // write is still delivered, since WaitForNextAsync rechecks Current against lastSeq.
        var lastSeq = _store.Current.Sequence;

        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested)
        {
            var snapshot = await _store.WaitForNextAsync(lastSeq, ct).ConfigureAwait(false);
            lastSeq = snapshot.Sequence;
            // Hold the frontier while the vessel is absent this frame (the 9p stream emits nothing
            // until it reappears); resume streaming when it is back.
            if (snapshot.Vessels.FirstOrDefault(v => v.Id == vesselId) is not { } vessel)
                continue;
            var json = Encoding.UTF8.GetString(Formats.StreamLine(snapshot, vessel)).TrimEnd('\n');
            var line = "data: " + json + "\n\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    // ---- field-level filesystem mirror (/v1/fs/<path>) ------------------------------------

    private async Task HandleFsAsync(Stream stream, HttpRequestLine request, CancellationToken ct)
    {
        if (_simRoot is not { } root)
        {
            await WriteJsonAsync(stream, 404, Error("ENOENT", "field endpoints are not enabled"), ct)
                .ConfigureAwait(false);
            return;
        }

        var path = string.Join('/', request.Segments[2..]);
        if (path.Length == 0)
        {
            await WriteJsonAsync(stream, 404, Error("ENOENT", "no field path"), ct).ConfigureAwait(false);
            return;
        }

        switch (request.Method)
        {
            case "GET" when request.Query.ContainsKey("stream"):
                await StreamFieldAsync(stream, root, path, ct).ConfigureAwait(false);
                return;
            case "GET":
                await ReadFieldAsync(stream, root, path, ct).ConfigureAwait(false);
                return;
            case "POST":
                await WriteFieldAsync(stream, root, path, request.Body, ct).ConfigureAwait(false);
                return;
            default:
                await WriteJsonAsync(stream, 405, Error("EINVAL", "method not allowed"), ct).ConfigureAwait(false);
                return;
        }
    }

    private async Task ReadFieldAsync(Stream stream, VfsDirectory root, string path, CancellationToken ct)
    {
        if (VfsScan.Resolve(root, path) is not { } file)
        {
            await WriteJsonAsync(stream, 404, Error("ENOENT", $"no field '{path}'"), ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var value = await VfsScan.ReadTextAsync(file, ct).ConfigureAwait(false);
            await WritePlainTextAsync(stream, 200, value, ct).ConfigureAwait(false);
        }
        catch (VfsErrorException ex)
        {
            await WriteJsonAsync(stream, StatusForErrno(ex.Errno), Error(LinuxErrno.Name(ex.Errno), ex.Message), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task StreamFieldAsync(Stream stream, VfsDirectory root, string path, CancellationToken ct)
    {
        var lastSeq = _store.Current.Sequence;
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\n"
            + "Connection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        string? last = null;
        while (!ct.IsCancellationRequested)
        {
            if (VfsScan.Resolve(root, path) is { } file)
            {
                string? value = null;
                try
                {
                    value = await VfsScan.ReadTextAsync(file, ct).ConfigureAwait(false);
                }
                catch (VfsErrorException)
                {
                    // The leaf's entity vanished this frame (e.g. vessel gone); hold the last value
                    // and resume if it reappears, like the per-vessel stream holds its frontier.
                }

                if (value is not null && value != last)
                {
                    last = value;
                    await stream.WriteAsync(SseData(value), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }

            var snapshot = await _store.WaitForNextAsync(lastSeq, ct).ConfigureAwait(false);
            lastSeq = snapshot.Sequence;
        }
    }

    private async Task WriteFieldAsync(Stream stream, VfsDirectory root, string path, byte[] body,
        CancellationToken ct)
    {
        if (VfsScan.Resolve(root, path) is not { } file)
        {
            await WriteJsonAsync(stream, 404, Error("ENOENT", $"no field '{path}'"), ct).ConfigureAwait(false);
            return;
        }

        // The VFS node enforces authority itself: a read-only sensor's OpenWrite throws EACCES, and a
        // control file submits to the command sink (which denies with EACCES when control is off).
        try
        {
            await VfsScan.WriteTextAsync(file, Encoding.UTF8.GetString(body), ct).ConfigureAwait(false);
            await WriteRawJsonAsync(stream, 200, "{\"outcome\":\"ok\"}", ct).ConfigureAwait(false);
        }
        catch (VfsErrorException ex)
        {
            await WriteJsonAsync(stream, StatusForErrno(ex.Errno), Error(LinuxErrno.Name(ex.Errno), ex.Message), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Encodes a (possibly multi-line) value as one SSE event, one <c>data:</c> line per line.</summary>
    private static byte[] SseData(string value)
    {
        var sb = new StringBuilder();
        foreach (var line in value.Split('\n'))
            sb.Append("data: ").Append(line).Append('\n');
        sb.Append('\n');
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int StatusForErrno(uint errno) => errno switch
    {
        LinuxErrno.EINVAL => 400,
        LinuxErrno.ENOENT => 404,
        LinuxErrno.EACCES => 403,
        LinuxErrno.EBUSY => 409,
        LinuxErrno.ETIMEDOUT => 504,
        LinuxErrno.EOPNOTSUPP => 501,
        _ => 500,
    };

    // ---- writes --------------------------------------------------------------------------

    private async Task HandleCommandAsync(Stream stream, HttpRequestLine request, CancellationToken ct)
    {
        if (_commands is not { } sink)
        {
            await WriteJsonAsync(stream, 403, Error("EACCES", "control is not available"), ct).ConfigureAwait(false);
            return;
        }

        SimCommand command;
        try
        {
            command = ParseCommand(request.Body);
        }
        catch (Exception ex)
        {
            await WriteJsonAsync(stream, 400, Error("EINVAL", ex.Message), ct).ConfigureAwait(false);
            return;
        }

        if (command.Action.StartsWith("debug.", StringComparison.Ordinal) && !sink.DebugEnabled)
        {
            await WriteJsonAsync(stream, 403, Error("EACCES", "debug namespace disabled"), ct).ConfigureAwait(false);
            return;
        }

        var result = await sink.SubmitAsync(command, ct).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            await WriteRawJsonAsync(stream, 200, "{\"outcome\":\"ok\"}", ct).ConfigureAwait(false);
            return;
        }

        await WriteJsonAsync(stream, StatusFor(result.Outcome),
            Error(result.Outcome.ErrnoName(), result.Message ?? result.Outcome.ToString()), ct)
            .ConfigureAwait(false);
    }

    private static SimCommand ParseCommand(byte[] body)
    {
        if (body.Length == 0)
            throw new ArgumentException("empty body");
        using var doc = JsonDocument.Parse(body);
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
        return new SimCommand(vessel, action, ordinal, value, SimCommand.PhaseFor(action))
            { Values = values, Token = token };
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    // ---- errno / status mapping ----------------------------------------------------------

    private static int StatusFor(CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Invalid => 400,
        CommandOutcome.NotFound => 404,
        CommandOutcome.Denied => 403,
        CommandOutcome.Busy => 409,
        CommandOutcome.TimedOut => 504,
        CommandOutcome.Unsupported => 501,
        _ => 500,
    };

    // ---- response plumbing ---------------------------------------------------------------

    private static string Serialize<T>(T value) => SimJson.Serialize(value);

    private static string Error(string errno, string message)
        => SimJson.Serialize(new { errno, message });

    private static Task WriteJsonAsync(Stream stream, int status, string json, CancellationToken ct)
        => WriteRawJsonAsync(stream, status, json, ct);

    private static async Task WriteRawJsonAsync(Stream stream, int status, string json, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: application/json\r\n"
            + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        var buffer = new ArrayBufferWriter<byte>(head.Length + payload.Length);
        buffer.Write(head);
        buffer.Write(payload);
        await stream.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task WritePlainTextAsync(Stream stream, int status, string text, CancellationToken ct)
    {
        var payload = Encoding.UTF8.GetBytes(text + "\n"); // trailing LF mirrors the /sim file convention
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/plain; charset=utf-8\r\n"
            + $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
        var buffer = new ArrayBufferWriter<byte>(head.Length + payload.Length);
        buffer.Write(head);
        buffer.Write(payload);
        await stream.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found",
        405 => "Method Not Allowed", 409 => "Conflict", 500 => "Internal Server Error",
        501 => "Not Implemented", 504 => "Gateway Timeout", _ => "OK",
    };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            _listener?.Stop();
        }
        catch
        {
            // ignored
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected
            }
        }

        _cts.Dispose();
    }
}
