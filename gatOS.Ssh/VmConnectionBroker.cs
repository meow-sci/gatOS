using System.Net.Sockets;
using System.Security.Cryptography;
using gatOS.Logging;
using gatOS.Vm;
using Renci.SshNet;

namespace gatOS.Ssh;

/// <summary>
///     The one broker per process that owns the shared <see cref="Vm.VmHost"/> and hands out SSH
///     connections into the guest (OS_PLAN.md T4.1). Each call to <see cref="ConnectAsync"/> boots
///     the VM if needed and returns a <b>new</b> connected <see cref="SshClient"/> — one client
///     per session keeps channel handling simple and matches dropbear's per-connection model.
/// </summary>
/// <remarks>
///     The host key presented by the guest is pinned against the manifest fingerprint
///     (<see cref="VmEndpoints.HostKeySha256"/>, D8): a mismatch fails the connection with
///     <see cref="HostKeyMismatchException"/>. Disposing the broker stops the VM.
/// </remarks>
/// <param name="vmHost">The VM lifecycle owner; the broker assumes ownership for disposal.</param>
public sealed class VmConnectionBroker(VmHost vmHost) : IShellBroker, IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan RefusedRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>The shared VM lifecycle state machine (UI reads <see cref="VmHost.Status"/>).</summary>
    public VmHost VmHost => vmHost;

    event EventHandler<VmStatus>? IShellBroker.VmStatusChanged
    {
        add => vmHost.StatusChanged += value;
        remove => vmHost.StatusChanged -= value;
    }

    /// <summary>
    ///     Returns a new connected (and host-key-verified) SSH client into the running guest,
    ///     booting the VM first when needed. The caller owns the client's disposal.
    /// </summary>
    /// <exception cref="VmStartException">The VM failed to boot.</exception>
    /// <exception cref="HostKeyMismatchException">The guest presented a host key that does not match the manifest pin.</exception>
    public async Task<SshClient> ConnectAsync(CancellationToken ct)
    {
        var endpoints = await vmHost.EnsureStartedAsync(ct);
        try
        {
            return await ConnectOnceAsync(endpoints, ct);
        }
        catch (Exception ex) when (IsConnectionRefused(ex))
        {
            // The readiness probe saw the SSH banner, but dropbear can drop one connection
            // while it forks per-client — a single short retry covers it.
            ModLog.Log.Warn("SSH connection refused right after readiness; retrying once.");
            await Task.Delay(RefusedRetryDelay, ct);
            return await ConnectOnceAsync(endpoints, ct);
        }
    }

    async Task<IShellChannel> IShellBroker.OpenShellAsync(
        string terminal, int columns, int rows, CancellationToken ct)
    {
        var client = await ConnectAsync(ct);
        try
        {
            var stream = client.CreateShellStream(
                terminal, (uint)columns, (uint)rows, width: 0, height: 0, bufferSize: 8192);
            return new SshShellChannel(client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>Stops the VM (shutdown ladder) and releases its disk lock.</summary>
    public ValueTask DisposeAsync() => vmHost.DisposeAsync();

    // Internal so the integration test can drive the host-key pin with tampered endpoints.
    internal static async Task<SshClient> ConnectOnceAsync(VmEndpoints endpoints, CancellationToken ct)
    {
        var auth = new PrivateKeyAuthenticationMethod(
            endpoints.SshUser, new PrivateKeyFile(endpoints.PrivateKeyPath));
        var info = new ConnectionInfo("127.0.0.1", endpoints.SshPort, endpoints.SshUser, auth)
        {
            Timeout = ConnectTimeout,
        };

        var client = new SshClient(info);
        string? mismatch = null;
        client.HostKeyReceived += (_, e) =>
        {
            var actual = Convert.ToHexStringLower(SHA256.HashData(e.HostKey));
            e.CanTrust = actual == endpoints.HostKeySha256;
            if (!e.CanTrust)
                mismatch = actual;
        };

        try
        {
            await client.ConnectAsync(ct);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            if (mismatch is not null)
            {
                throw new HostKeyMismatchException(
                    $"The gatOS guest presented an unexpected SSH host key (sha256 {mismatch}, "
                    + $"expected {endpoints.HostKeySha256}). The guest image and its manifest are "
                    + "out of sync — re-fetch or rebuild the guest artifacts.", ex);
            }

            throw;
        }
    }

    private static bool IsConnectionRefused(Exception ex)
        => ex is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
           || ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused };
}

/// <summary>
///     Thrown when the guest's SSH host key does not match the fingerprint pinned in the guest
///     manifest (<see cref="VmEndpoints.HostKeySha256"/>).
/// </summary>
public sealed class HostKeyMismatchException(string message, Exception? inner = null)
    : Exception(message, inner);
