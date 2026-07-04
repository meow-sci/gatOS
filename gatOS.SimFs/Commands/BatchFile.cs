using System.Text;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Commands;

/// <summary>
///     The <b>BATCH</b> control archetype (<c>/sim/ctl/batch</c>): one write carrying many control
///     writes that must execute in the <i>same game tick</i>. Every ordinary control write blocks
///     until the game thread drains it, so sequential writes land one-per-frame — at orbital speeds
///     a multi-vessel setup (e.g. formation teleports) smears across frames. A batch fixes that:
///     each line is <c>&lt;path&gt; &lt;value&gt;</c> — the <c>/sim</c>-relative path of a control
///     file and exactly the value a direct write to it would take — and a terminating
///     <c>commit</c> line fires the whole group as one atomic unit through
///     <see cref="ICommandSink.SubmitBatchAsync"/> (one drain, in order, never split).
/// </summary>
/// <remarks>
///     Grammar and semantics:
///     <list type="bullet">
///         <item>Blank lines and <c>#</c> comment lines are ignored.</item>
///         <item>Every line is validated <i>before</i> anything is submitted: an unresolvable path
///             is ENOENT, a non-control target or unparseable value is EINVAL — all-or-nothing.</item>
///         <item>All commands must share one <see cref="CommandPhase"/> (mixed Frame/Solver cannot
///             mean "same tick"); at most <see cref="MaxCommands"/> commands per batch.</item>
///         <item>The write delivering <c>commit</c> blocks until the group has executed and carries
///             the first failure's errno (commands are independent — the rest still execute).</item>
///         <item>Closing the file without a <c>commit</c> line discards the batch (a free abort);
///             an <i>unterminated</i> trailing <c>commit</c> (no final newline) fires best-effort
///             on close, like any control file's unterminated write.</item>
///     </list>
///     Paths may be written bare (<c>debug/vessels/x/teleport</c>) or absolute
///     (<c>/sim/debug/vessels/x/teleport</c>). Reads return a one-line usage hint.
/// </remarks>
public sealed class BatchFile : VfsFile
{
    /// <summary>Upper bound on commands per batch — bounds the game-thread work of one drain.</summary>
    public const int MaxCommands = 64;

    /// <summary>Upper bound on buffered bytes per open write handle.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>The exact line that ends a batch and fires it.</summary>
    public const string CommitToken = "commit";

    private static readonly byte[] Usage = Encoding.UTF8.GetBytes(
        "# one '<path> <value>' per line, then 'commit' — executes atomically in one game tick\n");

    private static readonly char[] Separators = [' ', '\t'];

    private readonly ICommandSink _sink;
    private readonly Func<VfsDirectory> _root;

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="sink">Where the command group is submitted.</param>
    /// <param name="root">
    ///     The <c>/sim</c> root the batch lines' paths resolve against — the same tree the 9p
    ///     server serves, so a batch reaches exactly the control files a direct write would.
    /// </param>
    public BatchFile(string name, ulong qidPath, ICommandSink sink, Func<VfsDirectory> root)
        : base(name, qidPath)
    {
        _sink = sink;
        _root = root;
    }

    /// <inheritdoc />
    public override bool IsWritable => true;

    /// <inheritdoc />
    public override long Size => Usage.Length;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new ReadHandle();

    /// <inheritdoc />
    public override IVfsWritableFileHandle OpenWrite() => new WriteHandle(this);

    /// <summary>
    ///     Parses the committed lines into a command group and submits it atomically. Throws
    ///     <see cref="VfsErrorException"/> — before submitting on any bad line, after on a failed
    ///     result — so the guest's failing <c>write(2)</c> carries the real errno.
    /// </summary>
    private async ValueTask ExecuteAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        var commands = ParseCommands(lines);
        var result = await _sink.SubmitBatchAsync(commands, ct).ConfigureAwait(false);
        if (!result.IsSuccess)
            throw new VfsErrorException(result.ToErrno(),
                $"batch: {result.Outcome} ({result.Message ?? "no detail"})");
    }

    private List<SimCommand> ParseCommands(IReadOnlyList<string> lines)
    {
        var commands = new List<SimCommand>();
        var root = _root();
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (commands.Count == MaxCommands)
                throw new VfsErrorException(LinuxErrno.EINVAL, $"batch: more than {MaxCommands} commands");

            var cut = line.IndexOfAny(Separators);
            var path = cut < 0 ? line : line[..cut];
            var payload = cut < 0 ? "" : line[(cut + 1)..];

            var target = VfsScan.Resolve(root, Normalize(path))
                         ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"batch: no such file '{path}'");
            if (target is not CommandFile control)
                throw new VfsErrorException(LinuxErrno.EINVAL, $"batch: '{path}' is not a control file");

            var command = control.ParseToken(payload)
                          ?? throw new VfsErrorException(LinuxErrno.EINVAL,
                              $"batch: control '{path}': cannot parse '{payload.Trim()}'");
            if (commands.Count > 0 && command.Phase != commands[0].Phase)
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    "batch: commands mix Frame- and Solver-phase actions (cannot share a tick)");
            commands.Add(command);
        }

        if (commands.Count == 0)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"batch: no commands before '{CommitToken}'");
        return commands;
    }

    /// <summary>Accepts bare, <c>/</c>-rooted, and <c>/sim/</c>-rooted spellings of a tree path.</summary>
    private static string Normalize(string path)
    {
        var p = path.TrimStart('/');
        return p.StartsWith("sim/", StringComparison.Ordinal) ? p[4..] : p;
    }

    /// <summary>The newline-terminated (complete) lines of <paramref name="text"/>, in order.</summary>
    private static List<string> CompleteLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        int nl;
        while ((nl = text.IndexOf('\n', start)) >= 0)
        {
            lines.Add(text[start..nl]);
            start = nl + 1;
        }

        return lines;
    }

    private sealed class ReadHandle : IVfsFileHandle
    {
        public long Size => Usage.Length;

        public ValueTask<ReadOnlyMemory<byte>> ReadAsync(ulong offset, uint count, CancellationToken ct)
        {
            if (offset >= (ulong)Usage.Length)
                return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
            var start = (int)offset;
            var length = (int)Math.Min(count, (uint)(Usage.Length - start));
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Usage.AsMemory(start, length));
        }

        public void Dispose()
        {
        }
    }

    private sealed class WriteHandle(BatchFile file) : IVfsWritableFileHandle
    {
        private readonly List<byte> _buffer = [];
        private bool _done;

        public async ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!_done)
            {
                if (_buffer.Count + data.Length > MaxBytes)
                {
                    _done = true;
                    throw new VfsErrorException(LinuxErrno.EINVAL, $"batch: more than {MaxBytes} buffered bytes");
                }

                _buffer.AddRange(data.Span);
                var lines = CompleteLines(Encoding.UTF8.GetString([.. _buffer]));
                var commit = lines.FindIndex(l => l.Trim() == CommitToken);
                if (commit >= 0)
                {
                    _done = true; // one batch per open handle; ignore anything past the commit
                    await file.ExecuteAsync(lines.GetRange(0, commit), ct).ConfigureAwait(false);
                }
            }

            return (uint)data.Length; // always consume the whole write
        }

        public void Dispose()
        {
            if (_done || _buffer.Count == 0)
                return;
            _done = true;

            // No committed batch was fired. A trailing unterminated 'commit' (e.g. `printf` with
            // no final newline) actuates best-effort on clunk — which cannot carry an errno, so
            // failures are only logged (the CommandFile convention). Anything else is an abort:
            // closing without commit deliberately discards the batch.
            var text = Encoding.UTF8.GetString([.. _buffer]);
            var lines = CompleteLines(text.EndsWith('\n') ? text : text + "\n");
            var commit = lines.FindIndex(l => l.Trim() == CommitToken);
            if (commit < 0)
                return;
            _ = ExecuteOnClunkAsync(file, lines.GetRange(0, commit));
        }

        private static async Task ExecuteOnClunkAsync(BatchFile file, IReadOnlyList<string> lines)
        {
            try
            {
                await file.ExecuteAsync(lines, CancellationToken.None).ConfigureAwait(false);
            }
            catch (VfsErrorException ex)
            {
                ModLog.Log.Debug($"batch '{file.Name}': unterminated commit failed (errno {ex.Errno})");
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"batch '{file.Name}': unterminated commit error: {ex.Message}");
            }
        }
    }
}
