namespace gatOS.Vm;

/// <summary>The lifecycle states of the (one) gatOS VM.</summary>
public enum VmState
{
    /// <summary>No VM process; a boot may be requested.</summary>
    Stopped,

    /// <summary>A boot is in flight (disk install → spawn → SSH readiness).</summary>
    Starting,

    /// <summary>The guest is reachable over SSH.</summary>
    Running,

    /// <summary>A stop is in flight (shutdown ladder).</summary>
    Stopping,

    /// <summary>The last boot failed or the VM died unexpectedly; retryable.</summary>
    Faulted,
}

/// <summary>
///     An immutable snapshot of the VM lifecycle for UI/diagnostics — read via a single
///     volatile field, never blocking (threading rule 5).
/// </summary>
/// <param name="State">Current lifecycle state.</param>
/// <param name="EffectiveAccel">Accelerator the running VM was launched with (diagnostics).</param>
/// <param name="SshPort">Forwarded loopback SSH port while Starting/Running.</param>
/// <param name="SimPort">The 9p port handed to the guest, when one was provided.</param>
/// <param name="StartedUtc">When the VM reached Running.</param>
/// <param name="FaultReason">Player-readable reason while Faulted.</param>
public sealed record VmStatus(
    VmState State,
    string? EffectiveAccel,
    int? SshPort,
    int? SimPort,
    DateTime? StartedUtc,
    string? FaultReason)
{
    /// <summary>The initial (and post-stop) status.</summary>
    public static VmStatus Stopped { get; } = new(VmState.Stopped, null, null, null, null, null);
}

/// <summary>
///     What a session needs to connect to the running guest (OS_PLAN.md T3.6) — consumed by
///     <c>VmConnectionBroker</c> in M4.
/// </summary>
/// <param name="SshPort">Forwarded loopback port to guest :22.</param>
/// <param name="SshUser">Guest user (from the manifest).</param>
/// <param name="PrivateKeyPath">Installed private key file.</param>
/// <param name="HostKeySha256">Pinned host key (sha256 hex of the raw public key blob).</param>
public sealed record VmEndpoints(int SshPort, string SshUser, string PrivateKeyPath, string HostKeySha256);
