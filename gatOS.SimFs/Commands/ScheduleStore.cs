using System.Collections.Concurrent;
using System.Globalization;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Commands;

/// <summary>
///     The caps behind the <c>[schedule]</c> config section
///     (plans/CAMERA_CONTROLS_PLAN.md §8). Every one of them fails a commit with EINVAL rather than
///     silently truncating — a schedule that quietly lost its tail would be worse than one that
///     never committed.
/// </summary>
/// <param name="MaxLive"><c>schedule_max_live</c>: concurrent registry entries.</param>
/// <param name="MaxEntries"><c>schedule_max_entries</c>: entries in one schedule.</param>
/// <param name="MaxBytes"><c>schedule_max_bytes</c>: buffered payload bytes per open write handle.</param>
/// <param name="DefaultClock"><c>schedule_default_clock</c>: the base used when <c>@clock</c> is omitted.</param>
public sealed record ScheduleLimits(
    int MaxLive = 16,
    int MaxEntries = 8192,
    int MaxBytes = 1048576,
    ClockBase DefaultClock = ClockBase.Render);

/// <summary>
///     A live host-side player in the <c>/sim/ctl/schedules/</c> registry — today a timed schedule,
///     later a camera track (plans/CAMERA_CONTROLS_PLAN.md §3.4). Both wear the same transport
///     vocabulary (<c>state</c>/<c>t</c>/<c>duration</c>/<c>pause</c>/<c>scrub</c>/<c>rate</c>/
///     <c>loop</c>/<c>stop</c>) over the same <see cref="PlaybackClock"/>, so there is one registry to
///     inspect, one place to stop everything, and — critically — <b>one notion of "now"</b>, which is
///     what lets a camera move and its cue track stay locked together.
/// </summary>
/// <remarks>
///     Every member is read from transport threads while the game thread mutates the player, so
///     implementations must publish through volatile fields. Values are display state: a stale read
///     is fine, a torn one is not.
/// </remarks>
public interface IPlaybackPlayer
{
    /// <summary>The registry id — the <c>schedules/&lt;id&gt;/</c> directory name.</summary>
    string Id { get; }

    /// <summary>What kind of player this is: <c>schedule</c> today, <c>camera-track</c> later.</summary>
    string Kind { get; }

    /// <summary>The shared-clock group name; <c>""</c> when ungrouped (rendered as <c>-</c>).</summary>
    string Group { get; }

    /// <summary>The timeline this player rides — shared with every other member of its group.</summary>
    PlaybackClock Clock { get; }

    /// <summary>This player's own length in ms (a group clock's duration can be longer).</summary>
    double DurationMs { get; }

    /// <summary>The lifecycle state rendered by the <c>state</c> leaf.</summary>
    PlaybackState State { get; }

    /// <summary>Entries not yet fired; always 0 for kinds that do not fire discrete entries.</summary>
    int PendingCount { get; }

    /// <summary>How many entries catch-up coalescing has dropped over this player's life.</summary>
    long Dropped { get; }

    /// <summary>The first failing entry's description, or <c>-</c> when nothing has failed.</summary>
    string LastError { get; }

    /// <summary>Game thread: stops playback. The player stays in the registry until removed.</summary>
    void Stop();
}

/// <summary>
///     The live-player registry behind <c>/sim/ctl/schedules/</c> — the <c>AudioStore</c> role for the
///     generic timed-command scheduler (plans/CAMERA_CONTROLS_PLAN.md §3.3). It is the one object
///     shared between the transport threads that commit schedules (<see cref="TimedBatchFile"/>, and
///     through it HTTP/MQTT by construction) and the game thread that plays them.
/// </summary>
/// <remarks>
///     <para><b>Threading.</b> <see cref="ReserveId"/>/<see cref="Submit"/> are the only transport-thread
///     entry points: they touch a <see cref="ConcurrentDictionary{TKey,TValue}"/> of reserved ids and a
///     <see cref="ConcurrentQueue{T}"/> of committed-but-not-yet-activated schedules. Everything else —
///     <see cref="Activate"/>, <see cref="AdvanceAll"/>, <see cref="Tick"/>, <see cref="Execute"/> — is
///     game-thread only and mutates plain collections, republishing <see cref="Players"/> as one
///     volatile swap of an immutable array. The id is reserved <i>immediately</i> at commit (not at
///     activation) so a second commit racing with the first still sees the collision.</para>
///     <para><b>Completed players persist.</b> A finished or failed player stays in the registry with
///     its final <c>state</c>/<c>dropped</c>/<c>last_error</c> until something explicitly removes it
///     (<c>schedules/&lt;id&gt;/remove</c> or <c>schedules/clear</c>). A script that starts a take and
///     comes back to read the outcome must be able to find it; auto-pruning would race that read.
///     The cost is that completed players count against <see cref="ScheduleLimits.MaxLive"/>.</para>
/// </remarks>
public sealed class ScheduleStore
{
    /// <summary>The <c>kind</c> value of a timed-schedule player.</summary>
    public const string ScheduleKind = "schedule";

    // Emitted events await the next telemetry sample; bounded exactly like AudioStore's queue so a
    // disabled sampler can never grow it (these are signals, not a ledger).
    private const int MaxPendingEvents = 64;

    private readonly ConcurrentDictionary<string, byte> _ids = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Schedule> _pending = new();
    private readonly Queue<SimEvent> _events = new();

    // Game-thread only.
    private readonly List<ScheduleRunner> _runners = [];
    private readonly Dictionary<string, PlaybackClock> _groups = new(StringComparer.Ordinal);

    private volatile IReadOnlyList<IPlaybackPlayer> _players = [];
    private int _nextAutoId;
    private double _utSeconds;

    /// <param name="limits">The caps from <c>[schedule]</c>.</param>
    public ScheduleStore(ScheduleLimits limits) => Limits = limits;

    /// <summary>Creates a store with the default caps.</summary>
    public ScheduleStore()
        : this(new ScheduleLimits())
    {
    }

    /// <summary>The caps this store enforces.</summary>
    public ScheduleLimits Limits { get; }

    /// <summary>
    ///     The live players, newest last (volatile immutable array — swapped by the game thread on
    ///     every registry mutation, read lock-free by the tree and the transports).
    /// </summary>
    public IReadOnlyList<IPlaybackPlayer> Players => _players;

    /// <summary>How many players are in the registry (the <c>schedules/count</c> leaf).</summary>
    public int Count => _players.Count;

    /// <summary>
    ///     Registry id rules: 1..64 chars of <c>[A-Za-z0-9_.-]</c>. Auto-assigned ids use a leading
    ///     <c>#</c>, which is deliberately <i>outside</i> this set so they can never collide with an
    ///     author-chosen one.
    /// </summary>
    public static bool IsValidId(string id)
    {
        if (id.Length is 0 or > 64)
            return false;
        foreach (var c in id)
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'))
                return false;
        return true;
    }

    /// <summary>Whether an id is taken (reserved at commit or live in the registry).</summary>
    public bool IsIdLive(string id) => _ids.ContainsKey(id);

    /// <summary>The player with this id, or null (used by the tree to render an entry directory).</summary>
    public IPlaybackPlayer? Find(string id)
    {
        var players = _players;
        for (var i = 0; i < players.Count; i++)
            if (string.Equals(players[i].Id, id, StringComparison.Ordinal))
                return players[i];
        return null;
    }

    /// <summary>
    ///     Transport thread: claims a registry id for a schedule about to be committed. Reserving is
    ///     separate from — and happens just before — <see cref="Submit"/> so the live-count cap and
    ///     the duplicate-id check are decided atomically, before activation and before any racing
    ///     commit of the same name.
    /// </summary>
    /// <param name="requested">The <c>@id</c> token, or null to auto-assign <c>#N</c>.</param>
    /// <returns>The assigned id.</returns>
    /// <exception cref="VfsErrorException">EINVAL: bad id, duplicate id, or the live cap is reached.</exception>
    public string ReserveId(string? requested)
    {
        if (_ids.Count >= Limits.MaxLive)
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"schedule: {Limits.MaxLive} live players already; remove one from /sim/ctl/schedules first");

        if (requested is null)
        {
            // '#' is outside the valid-id charset, so an auto id can never collide with a user's.
            while (true)
            {
                var auto = "#" + Interlocked.Increment(ref _nextAutoId).ToString(CultureInfo.InvariantCulture);
                if (_ids.TryAdd(auto, 0))
                    return auto;
            }
        }

        if (!IsValidId(requested))
            throw new VfsErrorException(LinuxErrno.EINVAL,
                $"schedule: '{requested}' is not a valid id ([A-Za-z0-9_.-], max 64)");
        if (!_ids.TryAdd(requested, 0))
            throw new VfsErrorException(LinuxErrno.EINVAL, $"schedule id '{requested}' already live");
        return requested;
    }

    /// <summary>Releases an id reserved by <see cref="ReserveId"/> that will not be submitted after all.</summary>
    public void ReleaseId(string id) => _ids.TryRemove(id, out _);

    /// <summary>
    ///     Transport thread: hands a committed schedule to the game thread. Non-blocking by
    ///     construction — a schedule outlives the write that created it, so the commit cannot wait
    ///     for its lifetime; runtime outcomes surface through <c>schedules/&lt;id&gt;/</c> and
    ///     <c>/sim/events</c> instead. The schedule becomes visible in <see cref="Players"/> at the
    ///     next <see cref="Activate"/> (the next game tick).
    /// </summary>
    /// <param name="schedule">A schedule whose <see cref="Schedule.Id"/> came from <see cref="ReserveId"/>.</param>
    /// <returns>The assigned id.</returns>
    public string Submit(Schedule schedule)
    {
        _pending.Enqueue(schedule);
        return schedule.Id;
    }

    // ---- game thread -------------------------------------------------------------------------

    /// <summary>
    ///     Game thread: turns every committed-but-not-yet-started schedule into a live player, wiring
    ///     it to its own clock or to its group's shared one, and starting it.
    /// </summary>
    /// <param name="utSeconds">Current sim time, stamped onto emitted events.</param>
    public void Activate(double utSeconds = 0)
    {
        _utSeconds = utSeconds;
        if (_pending.IsEmpty)
            return;

        var changed = false;
        while (_pending.TryDequeue(out var schedule))
        {
            var clock = ResolveClock(schedule);
            clock.DurationMs = schedule.DurationMs; // group duration = max over members
            var runner = new ScheduleRunner(this, schedule, clock);
            _runners.Add(runner);
            clock.Start();
            changed = true;
            EmitEvent(new SimEvent(_utSeconds, "schedule.started", null,
                $"{schedule.Id} kind={ScheduleKind} entries={schedule.Entries.Length} "
                + $"duration_ms={schedule.DurationMs.ToString("F1", CultureInfo.InvariantCulture)}"));
        }

        if (changed)
            Publish();
    }

    /// <summary>
    ///     Game thread: advances <b>each distinct clock exactly once</b> — group clocks from the group
    ///     table, ungrouped ones from their owning player. Advancing per member instead would run a
    ///     group of N players at N× speed.
    /// </summary>
    /// <param name="renderDeltaMs">This frame's rendered-time delta, ms.</param>
    /// <param name="wallDeltaMs">This frame's true wall-time delta, ms.</param>
    /// <param name="utDeltaMs">This frame's sim-time delta, ms.</param>
    public void AdvanceAll(double renderDeltaMs, double wallDeltaMs, double utDeltaMs)
    {
        foreach (var clock in _groups.Values)
            clock.Advance(renderDeltaMs, wallDeltaMs, utDeltaMs);
        for (var i = 0; i < _runners.Count; i++)
        {
            var runner = _runners[i];
            if (runner.OwnsClock)
                runner.Clock.Advance(renderDeltaMs, wallDeltaMs, utDeltaMs);
        }
    }

    /// <summary>
    ///     Game thread, after <see cref="AdvanceAll"/>: collects every command that came due this tick
    ///     into <paramref name="due"/>. The caller posts them (<see cref="CommandQueue.Post"/>), which
    ///     keeps phase routing, the executor, the health latches and the per-frame command budget
    ///     exactly as they are for every other write.
    /// </summary>
    /// <param name="due">The driver's reusable due list; entries are appended.</param>
    /// <param name="utSeconds">Current sim time, stamped onto emitted events.</param>
    public void Tick(List<DueCommand> due, double utSeconds = 0)
    {
        _utSeconds = utSeconds;
        for (var i = 0; i < _runners.Count; i++)
            _runners[i].Tick(due);
    }

    /// <summary>
    ///     Game thread: the game-free executor for the <c>schedule.*</c> action family. The game mod's
    ///     catalog routes these here rather than re-implementing them, because nothing about pausing
    ///     or scrubbing a host-side player touches KSA.
    /// </summary>
    /// <param name="command">The command to apply (id in <see cref="SimCommand.Token"/>).</param>
    public CommandResult Execute(SimCommand command)
    {
        if (command.Action == "schedule.clear")
        {
            Clear();
            return CommandResult.Ok;
        }

        var id = command.Token;
        if (string.IsNullOrEmpty(id))
            return new CommandResult(CommandOutcome.Invalid, $"{command.Action}: no schedule id");
        if (FindRunner(id) is not { } runner)
            return new CommandResult(CommandOutcome.NotFound, $"no live schedule '{id}'");

        switch (command.Action)
        {
            case "schedule.pause":
                if (command.Value is not (0 or 1))
                    return new CommandResult(CommandOutcome.Invalid, "schedule.pause takes 0 or 1");
                runner.Clock.Paused = command.Value != 0;
                return CommandResult.Ok;

            case "schedule.loop":
                if (command.Value is not (0 or 1))
                    return new CommandResult(CommandOutcome.Invalid, "schedule.loop takes 0 or 1");
                runner.Clock.Loop = command.Value != 0;
                return CommandResult.Ok;

            case "schedule.scrub":
                if (!double.IsFinite(command.Value) || command.Value < 0)
                    return new CommandResult(CommandOutcome.Invalid, "schedule.scrub takes a non-negative ms offset");
                runner.Clock.Scrub(command.Value);
                return CommandResult.Ok;

            case "schedule.rate":
                if (!double.IsFinite(command.Value)
                    || command.Value < PlaybackClock.MinRate || command.Value > PlaybackClock.MaxRate)
                    return new CommandResult(CommandOutcome.Invalid,
                        $"schedule.rate takes {PlaybackClock.MinRate}..{PlaybackClock.MaxRate}");
                runner.Clock.Rate = command.Value;
                return CommandResult.Ok;

            case "schedule.stop":
                runner.Stop();
                return CommandResult.Ok;

            case "schedule.remove":
                Remove(runner);
                return CommandResult.Ok;

            default:
                return new CommandResult(CommandOutcome.Unsupported, $"unknown action '{command.Action}'");
        }
    }

    /// <summary>Game thread: stops and removes every player, and discards anything not yet activated.</summary>
    public void Clear()
    {
        while (_pending.TryDequeue(out _))
        {
            // drop committed-but-unstarted schedules too, so `clear` really means "nothing is queued"
        }

        foreach (var runner in _runners)
            runner.Stop();
        _runners.Clear();
        _groups.Clear();
        _ids.Clear();
        Publish();
    }

    // ---- events (drained into the next telemetry snapshot, exactly like AudioStore's) ----------

    /// <summary>
    ///     Queues an event for the sampler to fold into the next published snapshot (so it reaches
    ///     <c>/sim/events</c>, SSE and <c>gatos/events</c>). Bounded: past 64 pending the oldest is
    ///     dropped.
    /// </summary>
    public void EmitEvent(SimEvent simEvent)
    {
        lock (_events)
        {
            if (_events.Count >= MaxPendingEvents)
                _events.Dequeue();
            _events.Enqueue(simEvent);
        }
    }

    /// <summary>Takes every pending event (the telemetry sampler, once per sample). Empty when none.</summary>
    public IReadOnlyList<SimEvent> DrainEvents()
    {
        lock (_events)
        {
            if (_events.Count == 0)
                return [];
            var drained = _events.ToArray();
            _events.Clear();
            return drained;
        }
    }

    // ---- internals ---------------------------------------------------------------------------

    /// <summary>The sim time last handed to <see cref="Activate"/>/<see cref="Tick"/> (event stamping).</summary>
    internal double UtSeconds => _utSeconds;

    private ScheduleRunner? FindRunner(string id)
    {
        foreach (var runner in _runners)
            if (string.Equals(runner.Id, id, StringComparison.Ordinal))
                return runner;
        return null;
    }

    private void Remove(ScheduleRunner runner)
    {
        runner.Stop();
        _runners.Remove(runner);
        _ids.TryRemove(runner.Id, out _);
        // A group clock outlives its members only as long as one is left; otherwise it would keep
        // advancing forever and a re-created group would inherit a stale position.
        if (runner.Group.Length > 0 && !_runners.Any(r => r.Group == runner.Group))
            _groups.Remove(runner.Group);
        Publish();
    }

    private PlaybackClock ResolveClock(Schedule schedule)
    {
        if (schedule.Group.Length == 0)
            return new PlaybackClock(schedule.Clock) { Rate = schedule.Rate, Loop = schedule.Loop };

        if (_groups.TryGetValue(schedule.Group, out var shared))
        {
            // The group's timeline belongs to whoever created it: a joiner's @clock/@rate/@loop would
            // otherwise retroactively re-time everyone already playing in that take.
            if (shared.Base != schedule.Clock)
                ModLog.Log.Debug(
                    $"schedule '{schedule.Id}': group '{schedule.Group}' already runs on the "
                    + $"{shared.Base} clock; ignoring @clock {schedule.Clock}");
            return shared;
        }

        shared = new PlaybackClock(schedule.Clock) { Rate = schedule.Rate, Loop = schedule.Loop };
        _groups[schedule.Group] = shared;
        return shared;
    }

    private void Publish() => _players = _runners.ToArray();

    /// <summary>
    ///     Game thread: a player ran to the end. It stays in the registry (its <c>state</c> is read
    ///     live off the runner, so no republish is needed) until something removes it.
    /// </summary>
    internal void OnFinished(ScheduleRunner runner)
        => EmitEvent(new SimEvent(_utSeconds, "schedule.finished", null,
            $"{runner.Id} kind={ScheduleKind} dropped={runner.Dropped.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>Game thread: a player's first entry failure.</summary>
    internal void OnFailed(ScheduleRunner runner, int entryIndex, CommandResult result)
        => EmitEvent(new SimEvent(_utSeconds, "schedule.failed", null,
            $"{runner.Id} entry={entryIndex.ToString(CultureInfo.InvariantCulture)} {result.Outcome.ErrnoName()}"));

    /// <summary>Game thread: catch-up coalescing dropped entries (throttled to one event per second).</summary>
    internal void OnDropped(ScheduleRunner runner, int dropped)
        => EmitEvent(new SimEvent(_utSeconds, "schedule.dropped", null,
            $"{runner.Id} dropped={dropped.ToString(CultureInfo.InvariantCulture)} "
            + $"total={runner.Dropped.ToString(CultureInfo.InvariantCulture)}"));
}

/// <summary>
///     One live schedule in the registry: a <see cref="Schedule"/>, its <see cref="Scheduler"/> and the
///     <see cref="PlaybackClock"/> they ride, wearing the <see cref="IPlaybackPlayer"/> transport
///     vocabulary and collecting the outcomes of its own fire-and-forget posts.
/// </summary>
/// <remarks>
///     A failed entry does <b>not</b> stop the schedule: the remaining entries are still authored
///     intent, and a take that silently truncated at the first EINVAL would be far harder to debug
///     than one that runs to the end and reports where it first went wrong. Only the <i>first</i>
///     failure is recorded, so <c>last_error</c> names the cause rather than the last symptom.
/// </remarks>
internal sealed class ScheduleRunner : IPlaybackPlayer, IPostObserver
{
    private readonly ScheduleStore _store;
    private readonly Schedule _schedule;
    private readonly Scheduler _scheduler;

    private long _dropped;
    private long _lastDroppedEventTick;
    private volatile string _lastError = "-";
    private volatile bool _failed;
    private volatile bool _stopped;
    private volatile bool _done;

    internal ScheduleRunner(ScheduleStore store, Schedule schedule, PlaybackClock clock)
    {
        _store = store;
        _schedule = schedule;
        _scheduler = new Scheduler(schedule, clock);
        Clock = clock;
    }

    /// <inheritdoc />
    public string Id => _schedule.Id;

    /// <inheritdoc />
    public string Kind => ScheduleStore.ScheduleKind;

    /// <inheritdoc />
    public string Group => _schedule.Group;

    /// <inheritdoc />
    public PlaybackClock Clock { get; }

    /// <inheritdoc />
    public double DurationMs => _schedule.DurationMs;

    /// <inheritdoc />
    public PlaybackState State
        => _failed ? PlaybackState.Failed
            : _stopped || _done ? PlaybackState.Done
            : !Clock.Started ? PlaybackState.Pending
            : Clock.Paused ? PlaybackState.Paused
            : PlaybackState.Running;

    /// <inheritdoc />
    public int PendingCount => _scheduler.Pending;

    /// <inheritdoc />
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public string LastError => _lastError;

    /// <summary>Whether this player created (and therefore must advance) its own clock.</summary>
    internal bool OwnsClock => _schedule.Group.Length == 0;

    /// <inheritdoc />
    public void Stop() => _stopped = true;

    /// <summary>Game thread: fires this tick's due entries and updates completion/drop accounting.</summary>
    internal void Tick(List<DueCommand> due)
    {
        if (_stopped || _done)
            return;

        var dropped = _scheduler.Tick(due, this);
        if (dropped > 0)
        {
            Interlocked.Add(ref _dropped, dropped);
            // Throttled: a sustained hitch would otherwise emit an event per frame, and the count is
            // already readable at schedules/<id>/dropped.
            var now = Environment.TickCount64;
            if (now - _lastDroppedEventTick >= 1000)
            {
                _lastDroppedEventTick = now;
                _store.OnDropped(this, dropped);
            }
        }

        if (!_scheduler.IsComplete)
            return;
        _done = true;
        _store.OnFinished(this);
    }

    /// <inheritdoc />
    public void OnCommandResult(int token, CommandResult result)
    {
        if (result.IsSuccess || _failed)
            return;
        _lastError = $"entry {token.ToString(CultureInfo.InvariantCulture)}: "
                     + $"{result.Outcome.ErrnoName()} ({result.Message ?? "no detail"})";
        _failed = true;
        _store.OnFailed(this, token, result);
    }
}
