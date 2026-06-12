using System.Net;
using System.Net.Sockets;
using System.Text;

namespace gatOS.Vm;

/// <summary>
///     Detects guest boot completion by watching for the SSH banner on the forwarded port
///     (OS_PLAN.md T3.5). A bare TCP connect is not enough: slirp accepts on the host side from
///     the moment QEMU starts and only then tries to reach the guest, so readiness means
///     actually receiving bytes that start with <c>SSH-</c>.
/// </summary>
public static class ReadinessProbe
{
    private static readonly TimeSpan AttemptDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan PerAttemptReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    ///     Polls until sshd on the guest presents its banner through the forwarded
    ///     <paramref name="port"/>.
    /// </summary>
    /// <exception cref="TimeoutException">No banner within <paramref name="timeout"/>.</exception>
    public static async Task WaitForSshAsync(int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException($"No SSH banner on 127.0.0.1:{port} within {timeout.TotalSeconds:0} s.");

            if (await TryReadBannerAsync(port, Min(remaining, PerAttemptReadTimeout), ct))
                return;

            await Task.Delay(AttemptDelay, ct);
        }
    }

    private static async Task<bool> TryReadBannerAsync(int port, TimeSpan attemptTimeout, CancellationToken ct)
    {
        try
        {
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(attemptTimeout);
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, attemptCts.Token);

            var stream = client.GetStream();
            var buffer = new byte[4];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), attemptCts.Token);
                if (n == 0)
                    return false; // connection closed before a banner (guest sshd not up yet)
                read += n;
            }

            return Encoding.ASCII.GetString(buffer) == "SSH-";
        }
        catch (Exception ex) when (ex is SocketException or IOException
                                       || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return false; // refused / reset / attempt timeout — keep polling
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
