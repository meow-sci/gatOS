using System.Text;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs;

/// <summary>
///     A read-only text leaf whose content is a pure function of the <b>published snapshot</b>,
///     memoized per snapshot sequence (GREENFIELD_PERFORMANCE_IMPROVEMENT_PLANS.md GP1). The
///     pre-GP1 <see cref="StaticTextFile"/> pattern re-ran the provider (and re-encoded UTF-8) on
///     every unopened-fid <c>Tgetattr</c> <b>and</b> every open — so one <c>cat</c> formatted the
///     value at least twice, and N concurrent readers (or a field-mirror sweep) formatted it N
///     times. This file formats once per published snapshot and serves the cached bytes to every
///     stat/open until the next publish.
/// </summary>
/// <remarks>
///     <b>Only for snapshot-derived content.</b> The cache key is
///     <see cref="SimSnapshot.Sequence"/>, so a provider that reads anything that can change
///     <i>without</i> a publish (live transport state, display settings) must keep using
///     <see cref="StaticTextFile"/> — its value would otherwise go stale while the sampler idles.
///     Open-snapshot semantics are unchanged: a handle wraps one immutable byte image, and under
///     the guest's <c>cache=none</c> mount every open re-reads (a fresh open after a publish sees
///     the new value).
/// </remarks>
public sealed class SnapshotTextFile : VfsFile
{
    private readonly SnapshotStore _store;
    private readonly Func<byte[]> _utf8Provider;
    private volatile Cached? _cached;

    private sealed record Cached(long Sequence, byte[] Utf8);

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="store">The snapshot exchange the provider projects (the memo key source).</param>
    /// <param name="contentProvider">
    ///     Produces the full file content — a pure function of <see cref="SnapshotStore.Current"/>.
    ///     Called at most once per published snapshot; must be thread-safe.
    /// </param>
    public SnapshotTextFile(string name, ulong qidPath, SnapshotStore store, Func<string> contentProvider)
        : this(name, qidPath, store, () => Encoding.UTF8.GetBytes(contentProvider()))
    {
    }

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="store">The snapshot exchange the provider projects (the memo key source).</param>
    /// <param name="utf8Provider">
    ///     Produces the full file content as UTF-8 bytes — a pure function of
    ///     <see cref="SnapshotStore.Current"/>. The byte form avoids the encode→string→re-encode
    ///     round-trip for JSON leaves (the <c>telemetry</c> doc). Called at most once per published
    ///     snapshot; must be thread-safe. The returned array is served as-is — do not mutate it.
    /// </param>
    public SnapshotTextFile(string name, ulong qidPath, SnapshotStore store, Func<byte[]> utf8Provider)
        : base(name, qidPath)
    {
        _store = store;
        _utf8Provider = utf8Provider;
    }

    /// <inheritdoc />
    public override long Size => Content().Length;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new Handle(Content());

    private byte[] Content()
    {
        // Capture the sequence BEFORE running the provider: if a publish lands mid-format, the
        // fresh content gets tagged with the older sequence and the next access recomputes —
        // never stale-forever, at worst one redundant format.
        var sequence = _store.Current.Sequence;
        var cached = _cached;
        if (cached is not null && cached.Sequence == sequence)
            return cached.Utf8;

        var utf8 = _utf8Provider();
        _cached = new Cached(sequence, utf8);
        return utf8;
    }

    private sealed class Handle(byte[] content) : IVfsFileHandle
    {
        public long Size => content.Length;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            if (offset >= (ulong)content.Length)
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            var start = (int)offset;
            var length = (int)Math.Min(count, (uint)(content.Length - start));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(content.AsMemory(start, length));
        }

        public void Dispose()
        {
        }
    }
}
