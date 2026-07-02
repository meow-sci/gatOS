namespace gatOS.NineP.Server;

/// <summary>Tuning knobs for <see cref="NinePServer"/> (OS_PLAN.md T7.3/T7.4).</summary>
public sealed record NinePServerOptions
{
    /// <summary>
    ///     The msize ceiling offered in <c>Rversion</c>; the negotiated value is
    ///     <c>min(client, this)</c>. 512 KiB (PERF_IMPROVEMENT_PLAN.md P4): a 1440x900 raw video
    ///     frame is ~7 MB, and at the old 128 KiB ceiling it took ~53 serial Tread round-trips
    ///     through slirp per frame — 512 KiB cuts that ~4x and makes a 128 KiB `cat` read exactly
    ///     one Tread. The kernel's default request is only ~128 KiB, so the guest mounts pass
    ///     msize=524288 explicitly (guest v15 sim-mount/mnt-mount); older guests negotiate down
    ///     automatically.
    /// </summary>
    public uint MaxMsize { get; init; } = 524288;

    /// <summary>
    ///     The timestamp reported for atime/mtime/ctime in every <c>Rgetattr</c>;
    ///     <c>null</c> = server construction time. Injectable for golden-byte tests.
    /// </summary>
    public DateTimeOffset? AttrTime { get; init; }
}
