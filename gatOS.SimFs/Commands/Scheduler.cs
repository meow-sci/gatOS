namespace gatOS.SimFs.Commands;

/// <summary>
///     One command a player wants fired this tick, with everything the driver needs to post it
///     fire-and-forget: the pre-parsed command, the player to report the outcome to, and the entry
///     index that identifies it in <c>last_error</c>. A <c>readonly record struct</c> so the whole
///     due list costs no allocations on the game-thread tick path.
/// </summary>
/// <param name="Command">The command to post (its phase routes it — no phase is passed anywhere).</param>
/// <param name="Observer">Who to hand the result to, or null to discard it.</param>
/// <param name="Token">The originating entry index, echoed back to <paramref name="Observer"/>.</param>
public readonly record struct DueCommand(SimCommand Command, IPostObserver? Observer, int Token);

/// <summary>
///     One live schedule player: walks a committed <see cref="Schedule"/> against a
///     <see cref="PlaybackClock"/> and reports which commands are due
///     (plans/CAMERA_CONTROLS_PLAN.md §3.2). Game-thread only, and deliberately dumb — it owns a
///     cursor and the catch-up policy, nothing else.
/// </summary>
/// <remarks>
///     <para><b>The catch-up policy is derived, not declared.</b> On a hitch many entries come due in
///     one tick, and the drain budget is finite. Rather than invent syntax for it, the policy falls
///     out of the target's archetype, captured at commit as <see cref="ScheduleEntry.IsTrigger"/>:
///     every trigger fires, in order (an impulse that is skipped is a missed stage separation);
///     state controls coalesce to the last write per path (an intermediate setpoint that is
///     immediately superseded was never observable). Cross-path order is preserved throughout —
///     entries are emitted in their original order, with the superseded ones simply skipped. What is
///     dropped is counted, never silent.</para>
///     <para><b>The steady state allocates nothing.</b> The overwhelmingly common tick has nothing
///     due, and the next most common has exactly one entry due; both take a branch-only path. Only
///     the ≥2 case touches the two scratch collections, which are allocated once per player and
///     <c>Clear()</c>ed — never reallocated.</para>
/// </remarks>
public sealed class Scheduler
{
    private readonly Schedule _schedule;
    private readonly PlaybackClock _clock;

    // Reused across ticks (allocated on the first ≥2-due tick, then Clear()ed forever after).
    private List<int>? _due;
    private Dictionary<string, int>? _survivor;

    private int _cursor;
    private int _lastLoopCount;
    private int _lastScrubGeneration;

    /// <param name="schedule">The committed schedule to play.</param>
    /// <param name="clock">
    ///     The timeline to play it against — possibly shared with other players (a group), in which
    ///     case this scheduler must <b>not</b> advance it; the registry does that once per clock.
    /// </param>
    public Scheduler(Schedule schedule, PlaybackClock clock)
    {
        _schedule = schedule;
        _clock = clock;
        _lastLoopCount = clock.LoopCount;
        _lastScrubGeneration = clock.ScrubGeneration;
    }

    /// <summary>The schedule being played.</summary>
    public Schedule Schedule => _schedule;

    /// <summary>How many entries have not fired yet.</summary>
    public int Pending => _schedule.Entries.Length - _cursor;

    /// <summary>
    ///     Whether the player has nothing left to do: not looping, every entry fired, and the clock
    ///     has reached the schedule's own length (which can be shorter than a shared group clock's).
    /// </summary>
    public bool IsComplete
        => !_clock.Loop && _cursor >= _schedule.Entries.Length && _clock.PositionMs >= _schedule.DurationMs;

    /// <summary>
    ///     Game thread, once per frame <b>after</b> the clock has been advanced: appends every command
    ///     that came due to <paramref name="due"/> and returns how many entries this tick's coalescing
    ///     <i>dropped</i>.
    /// </summary>
    /// <param name="due">The driver's reusable due-list; commands are appended, never removed.</param>
    /// <param name="observer">Who each posted command reports its outcome to (this player's runner).</param>
    public int Tick(List<DueCommand> due, IPostObserver observer)
    {
        var entries = _schedule.Entries;
        if (entries.Length == 0)
            return 0;

        // A seek re-seats the cursor and fires NOTHING: scrubbing is navigation, not playback.
        var scrub = _clock.ScrubGeneration;
        if (scrub != _lastScrubGeneration)
        {
            _lastScrubGeneration = scrub;
            _lastLoopCount = _clock.LoopCount;
            _cursor = Seek(entries, _clock.PositionMs);
            return 0;
        }

        var loops = _clock.LoopCount;
        var wrapped = loops != _lastLoopCount;
        _lastLoopCount = loops;

        var position = _clock.PositionMs;
        var tailStart = _cursor;

        if (!wrapped)
        {
            if (tailStart >= entries.Length || entries[tailStart].DeadlineMs > position)
                return 0; // the steady state: one comparison, zero allocations

            var end = tailStart + 1;
            while (end < entries.Length && entries[end].DeadlineMs <= position)
                end++;
            _cursor = end;

            if (end - tailStart == 1)
            {
                due.Add(new DueCommand(entries[tailStart].Command, observer, tailStart));
                return 0; // the common non-idle tick: still zero allocations
            }

            return Emit(due, observer, tailStart, end, 0, 0);
        }

        // The clock wrapped this tick. The tail of the finished cycle fires first (in order), then
        // the cursor restarts and the new cycle's already-due head follows — so a loop boundary is
        // indistinguishable from any other tick that happened to span many entries.
        var head = 0;
        while (head < entries.Length && entries[head].DeadlineMs <= position)
            head++;
        _cursor = head;
        return Emit(due, observer, tailStart, entries.Length, 0, head);
    }

    /// <summary>
    ///     Applies the coalescing policy over the two index ranges <c>[aStart,aEnd)</c> and
    ///     <c>[bStart,bEnd)</c> (the second is the post-wrap head; pass an empty range when there is
    ///     none), appending the survivors in original order. Returns the dropped count.
    /// </summary>
    private int Emit(List<DueCommand> due, IPostObserver observer, int aStart, int aEnd, int bStart, int bEnd)
    {
        var entries = _schedule.Entries;
        var indices = _due ??= [];
        indices.Clear();
        for (var i = aStart; i < aEnd; i++)
            indices.Add(i);
        for (var i = bStart; i < bEnd; i++)
            indices.Add(i);

        if (indices.Count == 0)
            return 0;
        if (indices.Count == 1)
        {
            due.Add(new DueCommand(entries[indices[0]].Command, observer, indices[0]));
            return 0;
        }

        // Last state-control write per path wins; triggers are exempt (every impulse must fire).
        var survivor = _survivor ??= new Dictionary<string, int>(StringComparer.Ordinal);
        survivor.Clear();
        foreach (var i in indices)
        {
            var entry = entries[i];
            if (!entry.IsTrigger)
                survivor[entry.Path] = i;
        }

        var dropped = 0;
        foreach (var i in indices)
        {
            var entry = entries[i];
            if (!entry.IsTrigger && survivor[entry.Path] != i)
            {
                dropped++;
                continue;
            }

            due.Add(new DueCommand(entry.Command, observer, i));
        }

        return dropped;
    }

    /// <summary>The index of the first entry still in the future at <paramref name="position"/>.</summary>
    private static int Seek(ScheduleEntry[] entries, double position)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (entries[mid].DeadlineMs > position)
                hi = mid;
            else
                lo = mid + 1;
        }

        return lo;
    }
}
