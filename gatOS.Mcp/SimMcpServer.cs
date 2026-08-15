using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using gatOS.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace gatOS.Mcp;

/// <summary>
/// Configurably bound, stateless Streamable HTTP host for the official MCP C# SDK. This adapter owns only
/// HTTP framing and request security; the SDK owns MCP discovery, negotiation, dispatch, and schemas.
/// </summary>
public sealed class SimMcpServer : IAsyncDisposable
{
    private const int MaximumHeaderBytes = 32 * 1024;
    private const int MaximumRequestBytes = 24 * 1024 * 1024;
    private const int MaximumActiveRequests = 32;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(3);

    private readonly McpRegistry _registry;
    private readonly string _version;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _requestSlots = new(MaximumActiveRequests, MaximumActiveRequests);
    private TcpListener? _listener;
    private Task? _acceptLoop;
    private int _activeConnections;
    private int _activeRequests;
    private long _requestCount;
    private long _errorCount;

    public SimMcpServer(McpRegistry registry, string version = "1.0.0")
    {
        _registry = registry;
        _version = version;
    }

    /// <summary>The actual TCP port after startup.</summary>
    public int Port { get; private set; }
    /// <summary>The IP address the listener is bound to.</summary>
    public string BindHost { get; private set; } = "127.0.0.1";
    /// <summary>Requests currently executing.</summary>
    public int ActiveRequests => Volatile.Read(ref _activeRequests);
    /// <summary>Accepted TCP connections that have not completed teardown.</summary>
    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    /// <summary>Total accepted MCP POST requests.</summary>
    public long RequestCount => Interlocked.Read(ref _requestCount);
    /// <summary>Total transport/protocol failures.</summary>
    public long ErrorCount => Interlocked.Read(ref _errorCount);

    /// <summary>Bind the configured address and preferred port, falling back to an ephemeral port.</summary>
    public Task StartAsync(int preferredPort = 4243, string bindHost = "127.0.0.1")
    {
        ObjectDisposedException.ThrowIf(_stop.IsCancellationRequested, this);
        var address = IPAddress.TryParse(bindHost, out var parsed)
            ? parsed
            : throw new ArgumentException("Bind host must be an IPv4 or IPv6 address.", nameof(bindHost));
        _listener = Bind(address, preferredPort);
        var endpoint = (IPEndPoint)_listener.LocalEndpoint;
        BindHost = endpoint.Address.ToString();
        Port = endpoint.Port;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
        return Task.CompletedTask;
    }

    private static TcpListener Bind(IPAddress address, int preferredPort)
    {
        if (preferredPort > 0)
        {
            try
            {
                var preferred = new TcpListener(address, preferredPort);
                preferred.Start();
                return preferred;
            }
            catch (SocketException) { }
        }
        var ephemeral = new TcpListener(address, 0);
        ephemeral.Start();
        return ephemeral;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _errorCount);
                ModLog.Log.Debug($"mcp: accept failed: {ex.Message}");
                continue;
            }

            if (!await _requestSlots.WaitAsync(0, ct).ConfigureAwait(false))
            {
                await RejectBusyAsync(client, ct).ConfigureAwait(false);
                continue;
            }
            Interlocked.Increment(ref _activeConnections);
            _ = Task.Run(() => ServeAsync(client, ct), CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken shutdown)
    {
        try
        {
            client.NoDelay = true;
            await using var stream = client.GetStream();
            using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            var monitor = MonitorDisconnectAsync(client.Client, disconnected);
            try
            {
                while (!disconnected.IsCancellationRequested)
                {
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(disconnected.Token);
                    requestTimeout.CancelAfter(RequestTimeout);
                    var request = await ReadRequestAsync(stream, requestTimeout.Token).ConfigureAwait(false);
                    if (request is null) return;
                    Interlocked.Increment(ref _requestCount);
                    if (!ValidateHttp(request, out var status, out var error))
                    {
                        Interlocked.Increment(ref _errorCount);
                        await WritePlainAsync(stream, status, error, requestTimeout.Token).ConfigureAwait(false);
                        return;
                    }

                    var keepAlive = !string.Equals(request.Header("connection"), "close", StringComparison.OrdinalIgnoreCase);
                    Interlocked.Increment(ref _activeRequests);
                    try
                    {
                        await HandleMcpAsync(stream, request, keepAlive, requestTimeout.Token).ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _activeRequests);
                    }
                    if (!keepAlive) return;
                }
            }
            finally
            {
                disconnected.Cancel();
                try { await monitor.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errorCount);
            ModLog.Log.Debug($"mcp: request failed: {ex.Message}");
        }
        finally
        {
            client.Dispose();
            _requestSlots.Release();
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private async Task HandleMcpAsync(Stream network, HttpRequest request, bool keepAlive, CancellationToken ct)
    {
        JsonRpcMessage message;
        JsonNode? raw;
        try
        {
            raw = JsonNode.Parse(request.Body);
            message = JsonSerializer.Deserialize<JsonRpcMessage>(request.Body, McpJsonUtilities.DefaultOptions)
                ?? throw new JsonException("empty JSON-RPC message");
        }
        catch (JsonException)
        {
            await WritePlainAsync(network, 400, "invalid JSON-RPC body", ct).ConfigureAwait(false);
            return;
        }

        var protocol = request.Header("mcp-protocol-version");
        var method = raw?["method"]?.GetValue<string>();
        var name = ExtractName(raw, method);
        if (string.Equals(protocol, "2026-07-28", StringComparison.Ordinal))
        {
            var headerMethod = request.Header("mcp-method");
            var headerName = request.Header("mcp-name");
            if (!string.Equals(headerMethod, method, StringComparison.Ordinal) ||
                (name is not null && !string.Equals(headerName, name, StringComparison.Ordinal)))
            {
                await WritePlainAsync(network, 400, "MCP standard headers do not match the request body", ct).ConfigureAwait(false);
                return;
            }
        }

        message.Context = new JsonRpcMessageContext { ProtocolVersion = protocol };
#pragma warning disable MCPEXP002 // Core 2.2.0 low-level HTTP adapter: required to carry Mcp-Name into stateless routing.
        message.Context.RoutingName = name;
#pragma warning restore MCPEXP002

        await using var transport = new StreamableHttpServerTransport(ModLogLoggerFactory.Instance)
        {
            Stateless = true,
            FlowExecutionContextFromRequests = false,
        };
        await using var server = McpServer.Create(transport, _registry.CreateOptions(_version), ModLogLoggerFactory.Instance, null);
        using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var serverTask = server.RunAsync(run.Token);
        await using var response = new ChunkedResponseStream(network, keepAlive);
        try
        {
            var wrote = await transport.HandlePostRequestAsync(message, response, ct).ConfigureAwait(false);
            if (!wrote) await response.CompleteAsync(202, ct).ConfigureAwait(false);
            else await response.CompleteAsync(200, ct).ConfigureAwait(false);
        }
        finally
        {
            run.Cancel();
            try { await serverTask.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
    }

    private bool ValidateHttp(HttpRequest r, out int status, out string error)
    {
        status = 400;
        error = "bad request";
        if (r.Method != "POST") { status = 405; error = "only POST /mcp is supported"; return false; }
        if (r.Target != "/mcp") { status = 404; error = "not found"; return false; }
        if (r.Header("mcp-session-id") is not null) { error = "MCP sessions are not supported"; return false; }
        var contentType = r.Header("content-type");
        if (contentType is null || !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        { status = 415; error = "Content-Type must be application/json"; return false; }
        var accept = r.Header("accept");
        if (accept is not null && !accept.Contains("application/json", StringComparison.OrdinalIgnoreCase) && !accept.Contains("*/*", StringComparison.Ordinal))
        { status = 406; error = "Accept must allow application/json"; return false; }
        var expectedPort = Port.ToString(CultureInfo.InvariantCulture);
        var host = r.Header("host");
        if (!IsAllowedAuthority(host, expectedPort))
        { status = 403; error = "Host is not the configured endpoint"; return false; }
        var origin = r.Header("origin");
        if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                                   || uri.Scheme != Uri.UriSchemeHttp
                                   || !IsAllowedHost(uri.Host)
                                   || uri.Port != Port))
        { status = 403; error = "Origin is not the configured endpoint"; return false; }
        return true;
    }

    private bool IsAllowedAuthority(string? authority, string expectedPort)
    {
        if (string.IsNullOrWhiteSpace(authority)) return false;
        return Uri.TryCreate("http://" + authority, UriKind.Absolute, out var uri)
               && uri.Port.ToString(CultureInfo.InvariantCulture) == expectedPort
               && IsAllowedHost(uri.Host);
    }

    private bool IsAllowedHost(string host)
    {
        var bound = ((IPEndPoint)_listener!.LocalEndpoint).Address;
        if (IPAddress.Any.Equals(bound) || IPAddress.IPv6Any.Equals(bound))
            return true;
        if (string.Equals(host, bound.ToString(), StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.IsLoopback(bound) && string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractName(JsonNode? body, string? method) => method switch
    {
        "tools/call" or "prompts/get" => body?["params"]?["name"]?.GetValue<string>(),
        "resources/read" => body?["params"]?["uri"]?.GetValue<string>(),
        _ => null,
    };

    private static async Task<HttpRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var header = new List<byte>(1024);
        var matched = 0;
        while (header.Count < MaximumHeaderBytes)
        {
            var one = new byte[1];
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
            header.Add(one[0]);
            matched = (matched, one[0]) switch { (0, 13) => 1, (1, 10) => 2, (2, 13) => 3, (3, 10) => 4, (_, 13) => 1, _ => 0 };
            if (matched == 4) break;
        }
        if (matched != 4) throw new InvalidDataException("HTTP headers too large");
        var lines = Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(header)).Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ', 3);
        if (first.Length != 3 || first[2] != "HTTP/1.1") throw new InvalidDataException("invalid HTTP request line");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0) break;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidDataException("invalid HTTP header");
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        byte[] body;
        if (headers.TryGetValue("Content-Length", out var lengthText))
        {
            if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0 || length > MaximumRequestBytes)
                throw new InvalidDataException("invalid Content-Length");
            body = new byte[length];
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        }
        else if (headers.TryGetValue("Transfer-Encoding", out var transfer) && transfer.Equals("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedBodyAsync(stream, ct).ConfigureAwait(false);
        }
        else
        {
            body = [];
        }
        return new(first[0], first[1], headers, body);
    }

    private static async Task<byte[]> ReadChunkedBodyAsync(Stream stream, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var line = await ReadAsciiLineAsync(stream, 128, ct).ConfigureAwait(false);
            var semicolon = line.IndexOf(';');
            var sizeText = semicolon >= 0 ? line[..semicolon] : line;
            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
                throw new InvalidDataException("invalid chunk size");
            if (size == 0)
            {
                // Consume optional trailer fields through their terminating blank line.
                while ((await ReadAsciiLineAsync(stream, MaximumHeaderBytes, ct).ConfigureAwait(false)).Length > 0) { }
                return body.ToArray();
            }
            if (body.Length + size > MaximumRequestBytes)
                throw new InvalidDataException("MCP request body too large");
            var chunk = new byte[size];
            await stream.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);
            await body.WriteAsync(chunk, ct).ConfigureAwait(false);
            if (await ReadAsciiLineAsync(stream, 2, ct).ConfigureAwait(false) != "")
                throw new InvalidDataException("invalid chunk terminator");
        }
    }

    private static async Task<string> ReadAsciiLineAsync(Stream stream, int maximumBytes, CancellationToken ct)
    {
        var bytes = new List<byte>();
        while (bytes.Count <= maximumBytes)
        {
            var one = new byte[1];
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0)
                throw new EndOfStreamException();
            if (one[0] == (byte)'\n')
            {
                if (bytes.Count == 0 || bytes[^1] != (byte)'\r')
                    throw new InvalidDataException("invalid HTTP line ending");
                bytes.RemoveAt(bytes.Count - 1);
                return Encoding.ASCII.GetString(CollectionsMarshal.AsSpan(bytes));
            }
            bytes.Add(one[0]);
        }
        throw new InvalidDataException("HTTP line too large");
    }

    private static async Task MonitorDisconnectAsync(Socket socket, CancellationTokenSource cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            if (socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0) { cancellation.Cancel(); return; }
            await Task.Delay(250, cancellation.Token).ConfigureAwait(false);
        }
    }

    private static async Task RejectBusyAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        await WritePlainAsync(stream, 503, "MCP request limit reached", ct).ConfigureAwait(false);
        client.Dispose();
    }

    private static async Task WritePlainAsync(Stream stream, int status, string text, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(head, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
    }

    private static string Reason(int status) => status switch { 200 => "OK", 202 => "Accepted", 400 => "Bad Request", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 406 => "Not Acceptable", 415 => "Unsupported Media Type", 503 => "Service Unavailable", _ => "Error" };

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_stop.IsCancellationRequested) return;
        _stop.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or SocketException) { }
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (ActiveConnections > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10).ConfigureAwait(false);
        if (ActiveConnections == 0)
        {
            _requestSlots.Dispose();
            _stop.Dispose();
        }
        else
        {
            // A game-thread command already executing is intentionally non-reversible. Keep these
            // tiny synchronization objects alive rather than racing a late connection teardown.
            ModLog.Log.Warn($"mcp: {ActiveConnections} connection(s) exceeded the bounded shutdown wait");
        }
    }

    private sealed record HttpRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        internal string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    private sealed class ChunkedResponseStream : Stream
    {
        private readonly Stream _inner;
        private readonly bool _keepAlive;
        private bool _started;
        private bool _completed;
        internal ChunkedResponseStream(Stream inner, bool keepAlive)
        {
            _inner = inner;
            _keepAlive = keepAlive;
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (buffer.Length == 0) return;
            await StartAsync(200, ct).ConfigureAwait(false);
            await _inner.WriteAsync(Encoding.ASCII.GetBytes(buffer.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n"), ct).ConfigureAwait(false);
            await _inner.WriteAsync(buffer, ct).ConfigureAwait(false);
            await _inner.WriteAsync("\r\n"u8.ToArray(), ct).ConfigureAwait(false);
        }
        internal async Task CompleteAsync(int status, CancellationToken ct)
        {
            if (_completed) return;
            await StartAsync(status, ct).ConfigureAwait(false);
            await _inner.WriteAsync("0\r\n\r\n"u8.ToArray(), ct).ConfigureAwait(false);
            await _inner.FlushAsync(ct).ConfigureAwait(false);
            _completed = true;
        }
        private async Task StartAsync(int status, CancellationToken ct)
        {
            if (_started) return;
            var connection = _keepAlive ? "keep-alive" : "close";
            var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: {connection}\r\nCache-Control: no-store\r\n\r\n");
            await _inner.WriteAsync(head, ct).ConfigureAwait(false);
            _started = true;
        }
    }
}
