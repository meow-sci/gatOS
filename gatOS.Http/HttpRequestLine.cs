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
    }

    internal string Method { get; }
    internal string Path { get; }
    internal IReadOnlyDictionary<string, string> Query { get; }
    internal IReadOnlyDictionary<string, string> Headers { get; }
    internal byte[] Body { get; }

    /// <summary>The path split into non-empty segments (e.g. <c>/v1/vessels/x</c> → [v1, vessels, x]).</summary>
    internal string[] Segments => Path.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    ///     Reads one request from <paramref name="stream"/>: the request line, headers up to the
    ///     blank line, then a <c>Content-Length</c> body. Returns null on a clean EOF (client
    ///     closed) or a malformed head. Caps the head and body to keep a hostile client bounded.
    /// </summary>
    internal static async Task<HttpRequestLine?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var head = await ReadHeadAsync(stream, ct).ConfigureAwait(false);
        if (head is null)
            return null;

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

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var lenText)
            && int.TryParse(lenText, out var len) && len is > 0 and <= 1 << 20)
        {
            body = new byte[len];
            var read = 0;
            while (read < len)
            {
                var n = await stream.ReadAsync(body.AsMemory(read), ct).ConfigureAwait(false);
                if (n == 0)
                    break;
                read += n;
            }
        }

        return new HttpRequestLine(method, Decode(path), query, headers, body);
    }

    private static async Task<string?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 16 * 1024)
        {
            var n = await stream.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0)
                return buffer.Length == 0 ? null : Encoding.ASCII.GetString(buffer.ToArray());
            buffer.WriteByte(one[0]);
            var len = buffer.Length;
            if (len >= 4)
            {
                var span = buffer.GetBuffer();
                if (span[len - 4] == '\r' && span[len - 3] == '\n' && span[len - 2] == '\r' && span[len - 1] == '\n')
                    return Encoding.ASCII.GetString(span, 0, (int)len - 4);
            }
        }

        return null;
    }

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
