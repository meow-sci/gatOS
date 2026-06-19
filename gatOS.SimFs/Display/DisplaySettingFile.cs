using System.Text;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Display;

/// <summary>
///     A writable scalar control for one <see cref="DisplaySettings"/> field
///     (<c>/sim/display/{enabled,fps,width,height,encoding}</c>). Unlike the vessel control files it
///     does <b>not</b> funnel a <c>SimCommand</c> through the game thread — these tune the host-side
///     capture directly — but it shares their UX: a read shows the live value, a write actuates on the
///     first newline with real errno feedback (a bad value fails the <c>write(2)</c> with EINVAL).
/// </summary>
/// <remarks>
///     A plain scalar (<see cref="VfsFile.IsStreaming"/> stays false), so it is picked up by the bulk
///     VFS walk and mirrored leaf-by-leaf to HTTP <c>/v1/fs/display/*</c> and MQTT
///     <c>gatos/sim/display/*</c> for free — clients can retune over any transport.
/// </remarks>
public sealed class DisplaySettingFile : VfsFile
{
    private readonly Func<string> _read;
    private readonly Func<string, bool> _apply;

    private DisplaySettingFile(string name, ulong qidPath, Func<string> read, Func<string, bool> apply)
        : base(name, qidPath)
    {
        _read = read;
        _apply = apply;
    }

    /// <summary>
    ///     Creates a setting file. <paramref name="read"/> yields the current value (no trailing
    ///     newline); <paramref name="apply"/> parses a written token and returns false to fail the
    ///     write with EINVAL.
    /// </summary>
    public static DisplaySettingFile Create(string name, ulong qidPath, Func<string> read, Func<string, bool> apply)
        => new(name, qidPath, read, apply);

    /// <inheritdoc />
    public override bool IsWritable => true;

    /// <inheritdoc />
    public override long Size => Encoding.UTF8.GetByteCount(_read()) + 1; // value + LF

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new ReadHandle(Encoding.UTF8.GetBytes(_read() + "\n"));

    /// <inheritdoc />
    public override IVfsWritableFileHandle OpenWrite() => new WriteHandle(this);

    private void Execute(string token)
    {
        if (!_apply(token.Trim()))
            throw new VfsErrorException(LinuxErrno.EINVAL, $"display '{Name}': cannot accept '{token.Trim()}'");
    }

    private sealed class ReadHandle(byte[] content) : IVfsFileHandle
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

    private sealed class WriteHandle(DisplaySettingFile file) : IVfsWritableFileHandle
    {
        private readonly List<byte> _buffer = [];
        private bool _executed;

        public ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!_executed)
            {
                _buffer.AddRange(data.Span);
                var newline = _buffer.IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    var line = Encoding.UTF8.GetString(_buffer.GetRange(0, newline).ToArray());
                    _executed = true; // exactly one value; ignore anything past the LF
                    file.Execute(line);
                }
            }

            return ValueTask.FromResult((uint)data.Length); // always consume the whole write
        }

        public void Dispose()
        {
            if (_executed || _buffer.Count == 0)
                return;
            // No newline written (e.g. `printf '%s' 1`): actuate best-effort. A clunk carries no errno,
            // but the apply is synchronous and host-local, so a failure only throws here and is dropped.
            _executed = true;
            var line = Encoding.UTF8.GetString(_buffer.ToArray()).Trim();
            if (line.Length == 0)
                return;
            try
            {
                file.Execute(line);
            }
            catch (VfsErrorException)
            {
                // Unterminated write with a bad value; nothing to report on clunk.
            }
        }
    }
}
