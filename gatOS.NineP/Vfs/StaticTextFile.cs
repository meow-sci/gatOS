using System.Text;

namespace gatOS.NineP.Vfs;

/// <summary>
///     A read-only text file whose content is produced by a provider and <b>snapshotted per
///     open</b>, so one <c>cat</c> sees a single consistent value even while the underlying
///     data changes (OS_PLAN.md T7.1). Under the guest's <c>cache=none</c> mount every open
///     re-reads, so consecutive opens observe live values (verified in the M1 spike).
/// </summary>
public sealed class StaticTextFile : VfsFile
{
    private readonly Func<string> _contentProvider;

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="contentProvider">
    ///     Produces the full file content (UTF-8 encoded on use). Called once per open and on
    ///     every unopened-fid <c>Tgetattr</c>; must be cheap and thread-safe.
    /// </param>
    public StaticTextFile(string name, ulong qidPath, Func<string> contentProvider)
        : base(name, qidPath)
    {
        _contentProvider = contentProvider;
    }

    /// <inheritdoc />
    public override long Size => Encoding.UTF8.GetByteCount(_contentProvider());

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new Handle(Encoding.UTF8.GetBytes(_contentProvider()));

    private sealed class Handle : IVfsFileHandle
    {
        private readonly byte[] _content;

        internal Handle(byte[] content) => _content = content;

        public long Size => _content.Length;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            if (offset >= (ulong)_content.Length)
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            var start = (int)offset;
            var length = (int)Math.Min(count, (uint)(_content.Length - start));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(_content.AsMemory(start, length));
        }

        public void Dispose()
        {
        }
    }
}
