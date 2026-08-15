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

    /// <summary>
    ///     Whether this player created — and is therefore responsible for advancing — its own
    ///     <see cref="Clock"/>, as opposed to sharing a group's. The registry advances each distinct
    ///     clock exactly once per tick (group clocks from its own table, ungrouped ones through this
    ///     flag); advancing per member instead would run a group of N players at N× speed.
    /// </summary>
    bool OwnsClock { get; }

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
///     They therefore count against <see cref="ScheduleLimits.MaxLive"/> — which is why
///     <see cref="Activate"/> evicts them <i>under cap pressure only</i> (see
///     <see cref="IsFinished"/>). Below the cap nothing is ever reclaimed, so the read a script came
///     back for is still there; at the cap the oldest finished player yields its slot rather than
///     wedging the registry on its own history.</para>
///     <para><b>Why eviction lives on the game thread, eagerly.</b> The obvious place to reclaim a
///     slot is <see cref="ReserveId"/>, where the cap is tested — but that runs on a transport thread
///     and the runner list, the group table and <see cref="Players"/> are game-thread-only, and a
///     commit may not block waiting for a game tick. So the direction is inverted: the game thread
///     relieves the pressure <i>before</i> a commit ever arrives, on every tick, and
///     <see cref="ReserveId"/> stays a single unguarded cap test. That removes the race rather than
///     retrying around it — a lazy scheme would still fail the first commit into a full registry,
///     because the reserved-id set counts commits that have not activated yet and that no eviction
///     pass can see.</para>
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

    // Game-thread only. Typed as the interface, not as ScheduleRunner, because the registry also holds
    // foreign kinds (a camera track — plan §3.4): one registry, one place to see what is running, and
    // one place to stop it all.
    private readonly List<IPlaybackPlayer> _runners = [];
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
    ///     Game thread: relieves cap pressure (see the type's remarks), then turns every
    ///     committed-but-not-yet-started schedule into a live player, wiring it to its own clock or to
    ///     its group's shared one, and starting it.
    /// </summary>
    /// <remarks>
    ///     Eviction runs <b>before</b> the drain and <b>unconditionally</b> — not only when something
    ///     is pending — because its whole job is to have already made room by the time a transport
    ///     thread calls <see cref="ReserveId"/>. Both of its guards are one integer comparison, so the
    ///     overwhelmingly common tick (idle registry, nothing pending) is still branch-only.
    /// </remarks>
    /// <param name="utSeconds">Current sim time, stamped onto emitted events.</param>
    public void Activate(double utSeconds = 0)
    {
        _utSeconds = utSeconds;
        EvictCompletedLocked(utSeconds);
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
            var player = _runners[i];
            if (player.OwnsClock)
                player.Clock.Advance(renderDeltaMs, wallDeltaMs, utDeltaMs);
        }
    }

    /// <summary>
    ///     Game thread: joins a player of a <i>foreign</i> kind — today a camera track
    ///     (plan §3.4) — to the registry, so it inherits the whole
    ///     <c>/sim/ctl/schedules/&lt;id&gt;/</c> transport verbatim.
    /// </summary>
    /// <remarks>
    ///     The caller owns the two steps this deliberately does not do: claiming the id with
    ///     <see cref="ReserveId"/> (so the cap and the duplicate check stay one atomic insert, exactly
    ///     as for a schedule) and starting the clock. A grouped player must take its clock from
    ///     <see cref="ResolveGroupClock"/> so it shares the group's single instance — that sharing is
    ///     the whole point of a group, and constructing a second clock that merely agrees would drift.
    /// </remarks>
    /// <param name="player">The player, with an id already reserved.</param>
    public void Register(IPlaybackPlayer player)
    {
        ArgumentNullException.ThrowIfNull(player);
        _runners.Add(player);
        Publish();
    }

    /// <summary>
    ///     Game thread: stops and drops a player, releasing its id (and its group clock when it was the
    ///     last member). The <c>schedule.remove</c> path by another name.
    /// </summary>
    /// <param name="id">The registry id.</param>
    /// <returns>True when a player of that id was live.</returns>
    public bool Unregister(string id)
    {
        if (FindPlayer(id) is not { } player)
            return false;
        Remove(player);
        return true;
    }

    /// <summary>
    ///     Game thread: the clock a player of <paramref name="group"/> must ride — the group's shared
    ///     instance, created on first use. An empty group name yields a fresh private clock.
    /// </summary>
    /// <remarks>
    ///     The group's base, rate and loop belong to whoever created it: a later joiner's would
    ///     otherwise retroactively re-time everyone already playing in that take. A joiner starts at the
    ///     group's <i>current</i> position — joining a take in progress.
    /// </remarks>
    /// <param name="group">The group name, or <c>""</c> for an ungrouped player.</param>
    /// <param name="clockBase">The base to use if this call creates the group's clock.</param>
    /// <param name="rate">The rate to use if this call creates the group's clock.</param>
    /// <param name="loop">The loop flag to use if this call creates the group's clock.</param>
    /// <param name="who">An id for the "clock base ignored" debug note, or null.</param>
    public PlaybackClock ResolveGroupClock(
        string group, ClockBase clockBase, double rate, bool loop, string? who = null)
    {
        if (group.Length == 0)
            return new PlaybackClock(clockBase) { Rate = rate, Loop = loop };

        if (_groups.TryGetValue(group, out var shared))
        {
            if (shared.Base != clockBase)
                ModLog.Log.Debug(
                    $"schedule '{who ?? "?"}': group '{group}' already runs on the "
                    + $"{shared.Base} clock; ignoring {clockBase}");
            return shared;
        }

        shared = new PlaybackClock(clockBase) { Rate = rate, Loop = loop };
        _groups[group] = shared;
        return shared;
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
            // Only entry-firing kinds have anything to do here; a camera track produces a pose, which
            // the director samples off its own clock, not a command.
            if (_runners[i] is ScheduleRunner runner)
                runner.Tick(due);
    }

    /// <summary>
    ///     Game thread: the game-free executor for the <c>schedule.*</c> action family. The game mod's
    ///     catalog routes these here rather than re-implementing them, because nothing about pausing
    ///     or scrubbing a host-side player touches KSA.
    /// </summary>
    /// <param name="command">The command to apply (id in <see cref="SimCommand.Token"/>).</param>
    public CommandResult Execute(SimCommand command)
    {
        if (command.Action == SimActions.ScheduleClear)
        {
            Clear();
            return CommandResult.Ok;
        }

        var id = command.Token;
        if (string.IsNullOrEmpty(id))
            return new CommandResult(CommandOutcome.Invalid, $"{command.Action}: no schedule id");
        if (FindPlayer(id) is not { } runner)
            return new CommandResult(CommandOutcome.NotFound, $"no live schedule '{id}'");

        switch (command.Action)
        {
            case SimActions.SchedulePause:
                if (command.Value is not (0 or 1))
                    return new CommandResult(CommandOutcome.Invalid, "schedule.pause takes 0 or 1");
                runner.Clock.Paused = command.Value != 0;
                return CommandResult.Ok;

            case SimActions.ScheduleLoop:
                if (command.Value is not (0 or 1))
                    return new CommandResult(CommandOutcome.Invalid, "schedule.loop takes 0 or 1");
                runner.Clock.Loop = command.Value != 0;
                return CommandResult.Ok;

            case SimActions.ScheduleScrub:
                if (!double.IsFinite(command.Value) || command.Value < 0)
                    return new CommandResult(CommandOutcome.Invalid, "schedule.scrub takes a non-negative ms offset");
                runner.Clock.Scrub(command.Value);
                return CommandResult.Ok;

            case SimActions.ScheduleRate:
                if (!double.IsFinite(command.Value)
                    || command.Value < PlaybackClock.MinRate || command.Value > PlaybackClock.MaxRate)
                    return new CommandResult(CommandOutcome.Invalid,
                        $"schedule.rate takes {PlaybackClock.MinRate}..{PlaybackClock.MaxRate}");
                runner.Clock.Rate = command.Value;
                return CommandResult.Ok;

            case SimActions.ScheduleStop:
                runner.Stop();
                return CommandResult.Ok;

            case SimActions.ScheduleRemove:
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

    /// <summary>
    ///     The sim time last handed to <see cref="Activate"/>/<see cref="Tick"/>. Foreign players stamp
    ///     their own events with it so the whole registry shares one notion of "when".
    /// </summary>
    public double UtSeconds => _utSeconds;

    private IPlaybackPlayer? FindPlayer(string id)
    {
        foreach (var runner in _runners)
            if (string.Equals(runner.Id, id, StringComparison.Ordinal))
                return runner;
        return null;
    }

    private void Remove(IPlaybackPlayer runner)
    {
        runner.Stop();
        _runners.Remove(runner);
        ReleaseSlot(runner);
        Publish();
    }

    /// <summary>
    ///     Drops a runner already removed from <see cref="_runners"/> out of the reserved-id set and,
    ///     if it was the last of its group, out of the group table. Releasing the <i>id</i> is the
    ///     load-bearing half: <see cref="ReserveId"/>'s cap counts ids, not runners.
    /// </summary>
    private void ReleaseSlot(IPlaybackPlayer runner)
    {
        _ids.TryRemove(runner.Id, out _);
        // A group clock outlives its members only as long as one is left; otherwise it would keep
        // advancing forever and a re-created group would inherit a stale position.
        if (runner.Group.Length == 0 || GroupInUse(runner.Group))
            return;
        _groups.Remove(runner.Group);
    }

    private bool GroupInUse(string group)
    {
        for (var i = 0; i < _runners.Count; i++)
            if (string.Equals(_runners[i].Group, group, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    ///     Game thread, from <see cref="Activate"/>: under cap pressure <i>only</i>, reclaims slots
    ///     held by players that can never fire again — oldest first, stopping the instant the registry
    ///     is back under <see cref="ScheduleLimits.MaxLive"/>.
    /// </summary>
    /// <remarks>
    ///     Oldest-first is the whole policy: <see cref="_runners"/> is in activation order, so the
    ///     reading a script is most likely to still want (the take it just started) is the last to go,
    ///     and a just-activated player is never the victim. Nothing is dropped silently — each
    ///     eviction emits a <c>schedule.evicted</c> event naming the id and the reason.
    /// </remarks>
    /// <param name="utSeconds">Sim time to stamp the eviction events with.</param>
    private void EvictCompletedLocked(double utSeconds)
    {
        // Two integer compares on the idle path. The cap counts *ids* (reserved ones included), which
        // is exactly the quantity ReserveId tests, so freeing runners against any other count would
        // leave the cap still tripped.
        if (_runners.Count == 0 || _ids.Count < Limits.MaxLive)
            return;

        var evicted = false;
        for (var i = 0; i < _runners.Count && _ids.Count >= Limits.MaxLive; i++)
        {
            var runner = _runners[i];
            if (!IsFinished(runner))
                continue;

            _runners.RemoveAt(i);
            i--;
            ReleaseSlot(runner);
            evicted = true;
            EmitEvent(new SimEvent(utSeconds, "schedule.evicted", null,
                $"{runner.Id} kind={runner.Kind} reason=max_live"));
            ModLog.Log.Debug($"schedule '{runner.Id}': evicted to free a slot "
                             + $"({Limits.MaxLive.ToString(CultureInfo.InvariantCulture)} live max)");
        }

        if (evicted)
            Publish();
    }

    /// <summary>
    ///     Whether a player can never fire another command — the <b>one</b> definition of "finished",
    ///     so eviction and any status reporting can never disagree about it.
    /// </summary>
    /// <remarks>
    ///     <para><b><c>failed</c> is not a terminal state</b>, and that is the subtle part. A
    ///     <see cref="ScheduleRunner"/> reports <see cref="PlaybackState.Failed"/> the instant its
    ///     <i>first</i> entry fails and then deliberately keeps running — the remaining entries are
    ///     still authored intent (see the runner's remarks). Treating <c>failed</c> as terminal would
    ///     evict a live take mid-flight and silently truncate it, which is precisely the failure mode
    ///     the runner was written to avoid. So a failed player qualifies only once it is <i>also</i>
    ///     out of entries, not looping, and past its own duration — the same test
    ///     <see cref="Scheduler.IsComplete"/> applies, restated over the public
    ///     <see cref="IPlaybackPlayer"/> surface because that is all eviction can see.</para>
    ///     <para><c>done</c>, by contrast, <i>is</i> conclusive: it is latched (a stopped or exhausted
    ///     runner early-returns from <c>Tick</c> forever after), so it needs no further test.</para>
    /// </remarks>
    /// <param name="player">The player to test.</param>
    internal static bool IsFinished(IPlaybackPlayer player) => player.State switch
    {
        PlaybackState.Done => true,
        PlaybackState.Failed => !player.Clock.Loop && player.PendingCount == 0
                                && player.Clock.PositionMs >= player.DurationMs,
        _ => false,
    };

    private PlaybackClock ResolveClock(Schedule schedule)
        => ResolveGroupClock(schedule.Group, schedule.Clock, schedule.Rate, schedule.Loop, schedule.Id);

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

    /// <inheritdoc />
    public bool OwnsClock => _schedule.Group.Length == 0;

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
