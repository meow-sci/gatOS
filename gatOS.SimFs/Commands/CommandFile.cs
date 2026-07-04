using System.Text;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Commands;

/// <summary>
///     Base for the two writable control-file archetypes (KSA_GAME_INTEGRATION_PLAN Part 2):
///     reads return the current value (one line, snapshotted per open — the sensor/STATE read
///     idiom), writes are line-buffered and actuate synchronously with errno feedback. The
///     <c>echo 1 &gt; file</c> common case (a single newline-terminated write) actuates the moment
///     the newline arrives, so the failing <c>write(2)</c> carries the real errno; an unterminated
///     write actuates best-effort on clunk (which cannot carry an errno).
/// </summary>
public abstract class CommandFile : VfsFile
{
    private readonly ICommandSink _sink;
    private readonly Func<string> _read;

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="sink">Where built commands are submitted.</param>
    /// <param name="read">Produces the current value text (no trailing newline); cheap, thread-safe.</param>
    protected CommandFile(string name, ulong qidPath, ICommandSink sink, Func<string> read)
        : base(name, qidPath)
    {
        _sink = sink;
        _read = read;
    }

    /// <inheritdoc />
    public sealed override bool IsWritable => true;

    /// <inheritdoc />
    public sealed override long Size => Encoding.UTF8.GetByteCount(_read()) + 1; // value + LF

    /// <inheritdoc />
    public sealed override IVfsFileHandle Open() => new ReadHandle(Encoding.UTF8.GetBytes(_read() + "\n"));

    /// <inheritdoc />
    public sealed override IVfsWritableFileHandle OpenWrite() => new WriteHandle(this);

    /// <summary>
    ///     Parses one written token into a command, or returns <c>null</c> to fail the write with
    ///     EINVAL. STATE files parse a value; TRIGGER files validate the fire token.
    /// </summary>
    protected abstract SimCommand? Parse(string token);

    /// <summary>
    ///     Parses one value token exactly as a direct write would (trim, then <see cref="Parse"/>),
    ///     without submitting — the seam <see cref="BatchFile"/> uses to build a same-tick command
    ///     group from <c>&lt;path&gt; &lt;value&gt;</c> lines. Null = unparseable (⇒ EINVAL).
    /// </summary>
    internal SimCommand? ParseToken(string token) => Parse(token.Trim());

    private async ValueTask ExecuteLineAsync(string line, CancellationToken ct)
    {
        var token = line.Trim();
        var command = Parse(token);
        if (command is null)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"control '{Name}': cannot parse '{token}'");

        var result = await _sink.SubmitAsync(command, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new VfsErrorException(result.ToErrno(),
                $"control '{Name}': {result.Outcome} ({result.Message ?? "no detail"})");
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

    private sealed class WriteHandle(CommandFile file) : IVfsWritableFileHandle
    {
        private readonly List<byte> _buffer = [];
        private bool _executed;

        public async ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!_executed)
            {
                _buffer.AddRange(data.Span);
                var newline = _buffer.IndexOf((byte)'\n');
                if (newline >= 0)
                {
                    var line = Encoding.UTF8.GetString(_buffer.GetRange(0, newline).ToArray());
                    _executed = true; // control files take exactly one value; ignore anything past the LF
                    await file.ExecuteLineAsync(line, ct).ConfigureAwait(false);
                }
            }

            return (uint)data.Length; // always consume the whole write
        }

        public void Dispose()
        {
            if (_executed || _buffer.Count == 0)
                return;

            var line = Encoding.UTF8.GetString(_buffer.ToArray()).Trim();
            if (line.Length == 0)
                return;

            // No newline was written (e.g. `printf '%s' 1`): actuate best-effort. A clunk cannot
            // carry an errno back to the writer, so failures are only logged. Fire-and-forget: a
            // sync wait here would stall the 9p clunk handler (and its Rclunk reply) for up to the
            // command timeout, e.g. when the game is paused — the command is still submitted on the
            // game thread, which is all "best-effort" promises.
            _executed = true;
            _ = ActuateOnClunkAsync(file, line);
        }

        private static async Task ActuateOnClunkAsync(CommandFile file, string line)
        {
            try
            {
                await file.ExecuteLineAsync(line, CancellationToken.None).ConfigureAwait(false);
            }
            catch (VfsErrorException ex)
            {
                ModLog.Log.Debug($"control '{file.Name}': unterminated write failed (errno {ex.Errno})");
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"control '{file.Name}': unterminated write error: {ex.Message}");
            }
        }
    }
}
