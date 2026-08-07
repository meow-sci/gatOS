using System.Globalization;
using System.Text;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Commands;

/// <summary>
///     The <b>TIMED BATCH</b> control archetype (<c>/sim/ctl/timed_batch</c>): <see cref="BatchFile"/>
///     with a clock (plans/CAMERA_CONTROLS_PLAN.md §3). Each line gains a leading <b>absolute offset
///     in milliseconds</b>, and <c>commit</c> registers the whole thing as a live host-side player in
///     <c>/sim/ctl/schedules/</c> instead of firing it. Because path resolution is
///     <see cref="BatchFile"/>'s — any <c>/sim</c>-relative path to a <see cref="CommandFile"/> — a
///     timed batch inherits the <i>entire</i> control surface for free: staging sequences, light shows,
///     FX sweeps, audio cues, warp changes.
/// </summary>
/// <remarks>
///     <para>Grammar (one pass, terminated by a line whose trim is exactly <c>commit</c>):</para>
///     <list type="bullet">
///         <item>Blank lines and <c>#</c> comment lines are ignored.</item>
///         <item><c>@id</c> / <c>@clock</c> / <c>@rate</c> / <c>@loop</c> / <c>@group</c> directives,
///             which must all precede the first entry; unknown or duplicated ones are EINVAL.</item>
///         <item>Every other line is <c>&lt;offsetMs&gt; &lt;path&gt; &lt;value…&gt;</c>. Offsets are
///             fractional-friendly (<c>16.67</c> is legal) and <b>absolute from the schedule's start,
///             never deltas</b>, so rounding cannot accumulate over a long sequence.</item>
///         <item>Everything is validated before anything is registered — all-or-nothing, exactly like
///             <see cref="BatchFile"/>.</item>
///     </list>
///     <para><b>Phase mixing is allowed</b> — a deliberate <i>relaxation</i> of <see cref="BatchFile"/>'s
///     rule, not an inheritance of it. A batch forbids it because "same tick" is meaningless across
///     Frame and Solver; a schedule spans many ticks, so each entry simply routes to its own phase
///     queue when it comes due.</para>
///     <para><b>The commit is non-blocking.</b> A schedule outlives the <c>write(2)</c> that created it,
///     so the write validates and returns; runtime outcomes surface at
///     <c>/sim/ctl/schedules/&lt;id&gt;/</c> and in <c>/sim/events</c>. Reads return a one-line usage
///     hint.</para>
/// </remarks>
public sealed class TimedBatchFile : VfsFile
{
    /// <summary>The exact line that ends a schedule and registers it.</summary>
    public const string CommitToken = "commit";

    private static readonly byte[] Usage = Encoding.UTF8.GetBytes(
        "# '<offsetMs> <path> <value>' per line (@id/@clock/@rate/@loop/@group first), then 'commit'\n");

    private static readonly char[] Separators = [' ', '\t'];

    private readonly ICommandSink _sink;
    private readonly Func<VfsDirectory> _root;
    private readonly ScheduleStore _store;

    /// <param name="name">The entry name.</param>
    /// <param name="qidPath">The stable qid path number.</param>
    /// <param name="sink">Unused for the commit itself (the store owns registration); kept so the file
    ///     only exists when a control surface is wired, matching every other control archetype.</param>
    /// <param name="root">
    ///     The <c>/sim</c> root the entries' paths resolve against — the same tree the 9p server serves,
    ///     so a schedule reaches exactly the control files a direct write would.
    /// </param>
    /// <param name="store">The live-player registry the committed schedule is submitted to.</param>
    public TimedBatchFile(string name, ulong qidPath, ICommandSink sink, Func<VfsDirectory> root,
        ScheduleStore store)
        : base(name, qidPath)
    {
        _sink = sink;
        _root = root;
        _store = store;
    }

    /// <inheritdoc />
    public override bool IsWritable => true;

    /// <inheritdoc />
    public override long Size => Usage.Length;

    /// <inheritdoc />
    public override IVfsFileHandle Open() => new ReadHandle();

    /// <inheritdoc />
    public override IVfsWritableFileHandle OpenWrite() => new WriteHandle(this);

    /// <summary>Whether the wired control surface accepts writes at all (mirrors the sink's gate).</summary>
    internal bool ControlEnabled => _sink.ControlEnabled;

    /// <summary>
    ///     Parses, validates and registers the committed lines. Throws <see cref="VfsErrorException"/>
    ///     on the first bad line — before anything is reserved or submitted — so the guest's failing
    ///     <c>write(2)</c> carries the real errno.
    /// </summary>
    /// <returns>The assigned schedule id.</returns>
    internal string Commit(IReadOnlyList<string> lines)
    {
        var root = _root();
        var entries = new List<ScheduleEntry>();

        string? id = null;
        string? group = null;
        ClockBase? clock = null;
        double? rate = null;
        bool? loop = null;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '@')
            {
                if (entries.Count > 0)
                    throw new VfsErrorException(LinuxErrno.EINVAL,
                        $"timed_batch: '{line}' — directives must precede entries");
                ParseDirective(line, ref id, ref group, ref clock, ref rate, ref loop);
                continue;
            }

            if (entries.Count >= _store.Limits.MaxEntries)
                throw new VfsErrorException(LinuxErrno.EINVAL,
                    $"timed_batch: more than {_store.Limits.MaxEntries} entries");
            entries.Add(ParseEntry(root, line));
        }

        if (entries.Count == 0)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"timed_batch: no entries before '{CommitToken}'");

        // Reserved last, so a validation failure never leaks an id — and so a duplicate-id race
        // between two concurrent commits is decided by one atomic insert.
        var assigned = _store.ReserveId(id);
        return _store.Submit(new Schedule(assigned, group ?? "",
            clock ?? _store.Limits.DefaultClock, rate ?? 1.0, loop ?? false, entries));
    }

    /// <summary>Parses one <c>@name value</c> directive, rejecting unknown and repeated ones.</summary>
    private static void ParseDirective(string line, ref string? id, ref string? group, ref ClockBase? clock,
        ref double? rate, ref bool? loop)
    {
        var cut = line.IndexOfAny(Separators);
        var name = (cut < 0 ? line : line[..cut])[1..];
        var value = cut < 0 ? "" : line[(cut + 1)..].Trim();
        if (value.Length == 0)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"timed_batch: '@{name}' needs a value");

        switch (name)
        {
            case "id":
                Once(id is null, "id");
                if (!ScheduleStore.IsValidId(value))
                    throw new VfsErrorException(LinuxErrno.EINVAL,
                        $"timed_batch: '{value}' is not a valid id ([A-Za-z0-9_.-], max 64)");
                id = value;
                return;

            case "group":
                Once(group is null, "group");
                if (!ScheduleStore.IsValidId(value))
                    throw new VfsErrorException(LinuxErrno.EINVAL,
                        $"timed_batch: '{value}' is not a valid group name ([A-Za-z0-9_.-], max 64)");
                group = value;
                return;

            case "clock":
                Once(clock is null, "clock");
                clock = value.ToLowerInvariant() switch
                {
                    "render" => ClockBase.Render,
                    "wall" => ClockBase.Wall,
                    "ut" => ClockBase.Ut,
                    _ => throw new VfsErrorException(LinuxErrno.EINVAL,
                        $"timed_batch: '@clock {value}' — expected render|wall|ut"),
                };
                return;

            case "rate":
                Once(rate is null, "rate");
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate)
                    || !double.IsFinite(parsedRate)
                    || parsedRate < PlaybackClock.MinRate || parsedRate > PlaybackClock.MaxRate)
                    throw new VfsErrorException(LinuxErrno.EINVAL,
                        $"timed_batch: '@rate {value}' — expected {PlaybackClock.MinRate}..{PlaybackClock.MaxRate}");
                rate = parsedRate;
                return;

            case "loop":
                Once(loop is null, "loop");
                loop = value switch
                {
                    "0" => false,
                    "1" => true,
                    _ => throw new VfsErrorException(LinuxErrno.EINVAL, $"timed_batch: '@loop {value}' — expected 0|1"),
                };
                return;

            default:
                throw new VfsErrorException(LinuxErrno.EINVAL, $"timed_batch: unknown directive '@{name}'");
        }
    }

    /// <summary>Rejects a repeated directive (each may appear at most once, so intent is unambiguous).</summary>
    private static void Once(bool free, string what)
    {
        if (!free)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"timed_batch: duplicate '@{what}' directive");
    }

    /// <summary>Parses one <c>&lt;offsetMs&gt; &lt;path&gt; &lt;value…&gt;</c> entry against the live tree.</summary>
    private static ScheduleEntry ParseEntry(VfsDirectory root, string line)
    {
        var cut = line.IndexOfAny(Separators);
        if (cut < 0)
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"timed_batch: '{line}' — expected '<offsetMs> <path> <value>'");

        var offsetText = line[..cut];
        if (!double.TryParse(offsetText, NumberStyles.Float, CultureInfo.InvariantCulture, out var offset)
            || !double.IsFinite(offset) || offset < 0)
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"timed_batch: '{offsetText}' is not a non-negative finite offset in ms");

        var rest = line[(cut + 1)..].TrimStart(Separators);
        if (rest.Length == 0)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"timed_batch: '{line}' — no path after the offset");
        var cut2 = rest.IndexOfAny(Separators);
        var path = cut2 < 0 ? rest : rest[..cut2];
        var payload = cut2 < 0 ? "" : rest[(cut2 + 1)..];

        var normalized = Normalize(path);
        var target = VfsScan.Resolve(root, normalized)
                     ?? throw new VfsErrorException(LinuxErrno.ENOENT, $"timed_batch: no such file '{path}'");
        if (target is not CommandFile control)
            throw new VfsErrorException(LinuxErrno.EINVAL, $"timed_batch: '{path}' is not a control file");

        var command = control.ParseToken(payload)
                      ?? throw new VfsErrorException(LinuxErrno.EINVAL,
                          $"timed_batch: control '{path}': cannot parse '{payload.Trim()}'");
        // The normalized path is the coalescing key, and `target is TriggerFile` is the archetype
        // signal the catch-up policy is derived from — both captured here, never re-derived at tick.
        return new ScheduleEntry(offset, normalized, command, target is TriggerFile);
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

    private sealed class WriteHandle(TimedBatchFile file) : IVfsWritableFileHandle
    {
        private readonly List<byte> _buffer = [];
        private bool _done;

        public ValueTask<uint> WriteAsync(ulong offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!_done)
            {
                if (_buffer.Count + data.Length > file._store.Limits.MaxBytes)
                {
                    _done = true;
                    throw new VfsErrorException(LinuxErrno.EINVAL,
                        $"timed_batch: more than {file._store.Limits.MaxBytes} buffered bytes");
                }

                _buffer.AddRange(data.Span);
                var lines = CompleteLines(Encoding.UTF8.GetString([.. _buffer]));
                var commit = lines.FindIndex(l => l.Trim() == CommitToken);
                if (commit >= 0)
                {
                    _done = true; // one schedule per open handle; ignore anything past the commit
                    if (!file.ControlEnabled)
                        throw new VfsErrorException(LinuxErrno.EACCES, "timed_batch: control is disabled in gatos.toml");
                    file.Commit(lines.GetRange(0, commit));
                }
            }

            return ValueTask.FromResult((uint)data.Length); // always consume the whole write
        }

        public void Dispose()
        {
            if (_done || _buffer.Count == 0)
                return;
            _done = true;

            // No committed schedule was registered. A trailing unterminated 'commit' (e.g. `printf`
            // with no final newline) commits best-effort on clunk — which cannot carry an errno, so
            // failures are only logged (the CommandFile convention). Anything else is an abort:
            // closing without commit deliberately discards the schedule.
            var text = Encoding.UTF8.GetString([.. _buffer]);
            var lines = CompleteLines(text.EndsWith('\n') ? text : text + "\n");
            var commit = lines.FindIndex(l => l.Trim() == CommitToken);
            if (commit < 0)
                return;

            try
            {
                if (file.ControlEnabled)
                    file.Commit(lines.GetRange(0, commit));
            }
            catch (VfsErrorException ex)
            {
                ModLog.Log.Debug($"timed_batch '{file.Name}': unterminated commit failed (errno {ex.Errno})");
            }
            catch (Exception ex)
            {
                ModLog.Log.Debug($"timed_batch '{file.Name}': unterminated commit error: {ex.Message}");
            }
        }
    }
}
