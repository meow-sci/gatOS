namespace gatOS.NineP.Server;

/// <summary>Tuning knobs for <see cref="NinePServer"/> (OS_PLAN.md T7.3/T7.4).</summary>
public sealed record NinePServerOptions
{
    /// <summary>
    ///     The msize ceiling offered in <c>Rversion</c>; the negotiated value is
    ///     <c>min(client, this)</c>. 128 KiB comfortably covers the kernel's 131096 request
    ///     (observed in the spike) while bounding per-read buffers.
    /// </summary>
    public uint MaxMsize { get; init; } = 131072;

    /// <summary>
    ///     The timestamp reported for atime/mtime/ctime in every <c>Rgetattr</c>;
    ///     <c>null</c> = server construction time. Injectable for golden-byte tests.
    /// </summary>
    public DateTimeOffset? AttrTime { get; init; }
}
