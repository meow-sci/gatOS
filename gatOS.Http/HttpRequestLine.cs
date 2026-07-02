using System.Text;

namespace gatOS.Http;

/// <summary>
///     A minimally-parsed HTTP/1.1 request (method, path, decoded query, headers, body). The
///     server speaks raw HTTP over a loopback <c>TcpListener</c> rather than <c>HttpListener</c>:
///     <c>HttpListener</c> rides http.sys on Windows and needs a URL-ACL reservation or admin,
///     which a game mod cannot assume — and loopback sockets keep the same "no firewall prompt,
///     slirp routes guest→10.0.2.2→127.0.0.1" property the 9p server relies on.
/// </summary>
internal sealed class HttpRequestLine
{
    private HttpRequestLine(string method, string path, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        Method = method;
        Path = path;
        Query = query;
        Headers = headers;
        Body = body;
        Segments = Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    internal string Method { get; }
    internal string Path { get; }
    internal IReadOnlyDictionary<string, string> Query { get; }
    internal IReadOnlyDictionary<string, string> Headers { get; }
    internal byte[] Body { get; }

    /// <summary>The path split into non-empty segments (e.g. <c>/v1/vessels/x</c> → [v1, vessels, x]) — computed once (GP7).</summary>
    internal string[] Segments { get; }

    /// <summary>Whether the client asked the server to close the connection after this response.</summary>
    internal bool WantsClose
        => Headers.TryGetValue("Connection", out var connection)
           && connection.Contains("close", StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the request head (request line + headers, terminator excluded).</summary>
    internal static HttpRequestLine? Parse(string head, byte[] body)
    {
        var lines = head.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            return null;
        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return null;

        var method = parts[0].ToUpperInvariant();
        var target = parts[1];
        var queryIndex = target.IndexOf('?');
        var path = queryIndex >= 0 ? target[..queryIndex] : target;
        var query = queryIndex >= 0 ? ParseQuery(target[(queryIndex + 1)..]) : new Dictionary<string, string>();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon > 0)
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        return new HttpRequestLine(method, Decode(path), query, headers, body);
    }

    internal static int ContentLength(IReadOnlyDictionary<string, string> headers)
        => headers.TryGetValue("Content-Length", out var lenText)
           && int.TryParse(lenText, out var len) && len is > 0 and <= 1 << 20
            ? len
            : 0;

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq < 0)
                result[Decode(pair)] = "";
            else
                result[Decode(pair[..eq])] = Decode(pair[(eq + 1)..]);
        }

        return result;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}

/// <summary>
///     Reads HTTP requests off one connection with a persistent buffer
///     (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP7). The pre-GP7 head reader issued <b>one
///     <c>ReadAsync</c> per header byte</b> — hundreds of async round-trips per request — and every
///     request closed the connection. This reader fills a 16 KiB buffer in bulk, scans for the
///     head terminator, and hands leftover bytes to the body (and to the <b>next request</b> on the
///     same connection — keep-alive) without losing or re-reading anything.
/// </summary>
internal sealed class HttpConnectionReader(Stream stream)
{
    private const int MaxHeadBytes = 16 * 1024; // same hostile-client head cap as before

    private readonly byte[] _buffer = new byte[MaxHeadBytes];
    private int _start; // unconsumed region is [_start.._end)
    private int _end;

    /// <summary>
    ///     Reads one request (head + <c>Content-Length</c> body). Returns null on a clean EOF
    ///     (client closed between requests), a malformed head, or a head over the cap.
    /// </summary>
    internal async Task<HttpRequestLine?> ReadRequestAsync(CancellationToken ct)
    {
        var head = await ReadHeadAsync(ct).ConfigureAwait(false);
        if (head is null)
            return null;

        // Parse the head first (Content-Length lives in it); the body-free result is returned
        // as-is for the common GET case, and re-parsed with the body only for uploads.
        var request = HttpRequestLine.Parse(head, []);
        if (request is null)
            return null;

        var length = HttpRequestLine.ContentLength(request.Headers);
        if (length == 0)
            return request;

        var body = new byte[length];
        var copied = Math.Min(length, _end - _start);
        _buffer.AsSpan(_start, copied).CopyTo(body);
        _start += copied;
        while (copied < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(copied), ct).ConfigureAwait(false);
            if (n == 0)
                break;
            copied += n;
        }

        return HttpRequestLine.Parse(head, body);
    }

    private async Task<string?> ReadHeadAsync(CancellationToken ct)
    {
        while (true)
        {
            var window = _buffer.AsSpan(_start, _end - _start);
            var terminator = window.IndexOf("\r\n\r\n"u8);
            if (terminator >= 0)
            {
                var head = Encoding.ASCII.GetString(_buffer, _start, terminator);
                _start += terminator + 4;
                return head;
            }

            if (_end == _buffer.Length)
            {
                if (_start == 0)
                    return null; // head exceeds the cap
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
                if (_end == _buffer.Length)
                    return null; // still no terminator in a full buffer
            }

            var n = await stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct).ConfigureAwait(false);
            if (n == 0)
                return null; // EOF — clean between requests, malformed mid-head; either way, done
            _end += n;
        }
    }
}
