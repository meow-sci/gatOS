namespace gatOS.SimFs.Commands;

/// <summary>
///     One committed entry of a <see cref="Schedule"/>: fire <see cref="Command"/> once the player's
///     clock reaches <see cref="DeadlineMs"/>.
/// </summary>
/// <param name="DeadlineMs">
///     Absolute offset from the schedule's start, in ms. <b>Absolute, never a delta</b>, so a 1 ms
///     rounding error cannot accumulate over a long sequence.
/// </param>
/// <param name="Path">
///     The normalized <c>/sim</c>-relative path of the control file this entry writes. It is the
///     <b>coalescing key</b>: when a hitch makes several entries come due at once, only the last
///     state-control write per path survives (see <see cref="Scheduler"/>).
/// </param>
/// <param name="Command">The pre-parsed command — parsing happens at commit, never on the tick path.</param>
/// <param name="IsTrigger">
///     Whether the target was a <see cref="TriggerFile"/>. This is the archetype signal that drives
///     the catch-up policy: a trigger is an <i>impulse</i> (stage, ignite, fire) and must always
///     execute, in order; a state control is a <i>setpoint</i> and only its final value matters.
///     Captured at commit because the archetype is a property of the target file, not of the command.
/// </param>
public readonly record struct ScheduleEntry(double DeadlineMs, string Path, SimCommand Command, bool IsTrigger);

/// <summary>
///     An immutable committed schedule: the flat, deadline-sorted entry array a <see cref="Scheduler"/>
///     walks, plus the clock options the player was authored with
///     (plans/CAMERA_CONTROLS_PLAN.md §3). Everything is resolved and validated at commit time
///     (<see cref="TimedBatchFile"/>) so the game-thread tick never parses, allocates, or resolves a path.
/// </summary>
public sealed class Schedule
{
    /// <param name="id">The registry id assigned at commit (<c>@id</c>, or an auto <c>#N</c>).</param>
    /// <param name="group">The shared-clock group name, or <c>""</c> when ungrouped.</param>
    /// <param name="clock">The requested clock base (ignored when joining an existing group).</param>
    /// <param name="rate">The requested playback rate (ignored when joining an existing group).</param>
    /// <param name="loop">The requested loop flag (ignored when joining an existing group).</param>
    /// <param name="entries">The authored entries, in authored order.</param>
    public Schedule(string id, string group, ClockBase clock, double rate, bool loop,
        IReadOnlyList<ScheduleEntry> entries)
    {
        Id = id;
        Group = group;
        Clock = clock;
        Rate = rate;
        Loop = loop;
        // OrderBy is documented-stable, which is load-bearing here: entries sharing a deadline must
        // keep their authored order so a group of `0`-offset lines behaves exactly like a ctl/batch.
        // Array.Sort/List.Sort are NOT stable and would silently scramble them.
        Entries = entries.OrderBy(e => e.DeadlineMs).ToArray();
        DurationMs = Entries.Length == 0 ? 0 : Entries[^1].DeadlineMs;
    }

    /// <summary>The registry id — the <c>/sim/ctl/schedules/&lt;id&gt;/</c> directory name.</summary>
    public string Id { get; }

    /// <summary>The shared-clock group name; <c>""</c> when this player owns its clock alone.</summary>
    public string Group { get; }

    /// <summary>The clock base requested at commit.</summary>
    public ClockBase Clock { get; }

    /// <summary>The playback rate requested at commit.</summary>
    public double Rate { get; }

    /// <summary>Whether the schedule was authored to loop.</summary>
    public bool Loop { get; }

    /// <summary>The entries, sorted by <see cref="ScheduleEntry.DeadlineMs"/>, stable within equal deadlines.</summary>
    public ScheduleEntry[] Entries { get; }

    /// <summary>The last deadline (0 when empty) — the schedule's length.</summary>
    public double DurationMs { get; }
}
