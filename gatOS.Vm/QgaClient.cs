using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using gatOS.Logging;

namespace gatOS.Vm;

/// <summary>
///     The qemu-guest-agent surface <c>VmHost</c> uses — extracted as an interface for
///     state-machine unit tests (OS_PLAN.md T3.6). QGA is strictly best-effort: every failure
///     is soft (<c>false</c> + log), callers escalate down the shutdown ladder.
/// </summary>
public interface IQgaClient : IAsyncDisposable
{
    /// <summary>True when the agent answers <c>guest-ping</c>.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);

    /// <summary>
    ///     Sends <c>guest-shutdown</c>. Per the QGA protocol this returns <b>no response on
    ///     success</b> — the success signal is the QEMU process exiting (we run
    ///     <c>-no-reboot</c>), so true only means "request delivered".
    /// </summary>
    Task<bool> ShutdownAsync(CancellationToken ct = default);
}

/// <summary>
///     Minimal qemu-guest-agent client (OS_PLAN.md T3.5). QEMU exposes the agent's
///     virtio-serial port as a TCP chardev with <c>server=on</c>, so the host connects as the
///     socket client. Messages are newline-delimited JSON; each fresh connection is
///     re-synchronized with the documented escape preamble: a <c>0xFF</c> sentinel byte +
///     <c>guest-sync-delimited</c> with a random id, discarding everything until the
///     <c>0xFF</c>-prefixed response carrying the same id (verified against
///     https://www.qemu.org/docs/master/interop/qemu-ga-ref.html).
/// </summary>
public sealed class QgaClient(int port, TimeSpan? commandTimeout = null) : IQgaClient
{
    private const byte Sentinel = 0xFF;

    private readonly TimeSpan _timeout = commandTimeout ?? TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1); // one in-flight command at a time
    private TcpClient? _client;
    private NetworkStream? _stream;

    /// <inheritdoc/>
    public Task<bool> PingAsync(CancellationToken ct = default)
        => ExecuteAsync("""{"execute":"guest-ping"}""", expectResponse: true, ct);

    /// <inheritdoc/>
    public Task<bool> ShutdownAsync(CancellationToken ct = default)
        => ExecuteAsync("""{"execute":"guest-shutdown"}""", expectResponse: false, ct);

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            DropConnection();
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
    }

    private async Task<bool> ExecuteAsync(string commandJson, bool expectResponse, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeout);

            var stream = await EnsureConnectedAndSyncedAsync(cts.Token);
            await WriteLineAsync(stream, commandJson, cts.Token);
            if (!expectResponse)
                return true;

            var response = await ReadJsonLineAsync(stream, cts.Token);
            return response.RootElement.TryGetProperty("return", out _);
        }
        catch (Exception ex) when (ex is SocketException or IOException or JsonException
                                       or OperationCanceledException or ObjectDisposedException
                                       or EndOfStreamException)
        {
            ModLog.Log.Debug($"QGA command failed (soft): {ex.Message}");
            DropConnection(); // next command reconnects + re-syncs
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<NetworkStream> EnsureConnectedAndSyncedAsync(CancellationToken ct)
    {
        if (_stream is { } existing)
            return existing;

        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, ct);
            var stream = client.GetStream();

            var syncId = Random.Shared.NextInt64(1, long.MaxValue);
            await stream.WriteAsync(new[] { Sentinel }, ct);
            await WriteLineAsync(stream,
                "{\"execute\":\"guest-sync-delimited\",\"arguments\":{\"id\":" + syncId + "}}", ct);

            // Discard everything until the sentinel-delimited response with our id.
            while (true)
            {
                await ReadUntilSentinelAsync(stream, ct);
                using var response = await ReadJsonLineAsync(stream, ct);
                if (response.RootElement.TryGetProperty("return", out var idProp)
                    && idProp.ValueKind == JsonValueKind.Number
                    && idProp.GetInt64() == syncId)
                    break;
            }

            _client = client;
            return _stream = stream;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private void DropConnection()
    {
        _stream = null;
        _client?.Dispose();
        _client = null;
    }

    private static async Task WriteLineAsync(NetworkStream stream, string json, CancellationToken ct)
        => await stream.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"), ct);

    private static async Task ReadUntilSentinelAsync(NetworkStream stream, CancellationToken ct)
    {
        while (await ReadByteAsync(stream, ct) != Sentinel)
        {
        }
    }

    private static async Task<JsonDocument> ReadJsonLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var line = new List<byte>(256);
        while (true)
        {
            var b = await ReadByteAsync(stream, ct);
            if (b == (byte)'\n')
                break;
            if (b != Sentinel) // tolerate a stray sentinel mid-stream (parser-reset semantics)
                line.Add(b);
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(line.ToArray()));
    }

    private static async Task<byte> ReadByteAsync(NetworkStream stream, CancellationToken ct)
    {
        var one = new byte[1];
        return await stream.ReadAsync(one.AsMemory(), ct) == 0
            ? throw new EndOfStreamException("QGA socket closed.")
            : one[0];
    }
}
