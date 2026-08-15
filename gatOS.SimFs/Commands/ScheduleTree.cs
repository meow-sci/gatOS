using System.Globalization;
using gatOS.NineP.Vfs;

namespace gatOS.SimFs.Commands;

/// <summary>
///     Builds the global scheduler surface under <c>/sim/ctl</c>: the <c>timed_batch</c> write handle
///     and the <c>schedules/</c> live-player registry (plans/CAMERA_CONTROLS_PLAN.md §3.3). It follows
///     the registry template of AGENTS.md §4 mode 2 — a writer, a <c>clear</c> trigger, a
///     <c>count</c>, a <c>help</c>, and one <c>&lt;id&gt;/</c> subdirectory per live entry with
///     per-field leaves plus <c>stop</c>/<c>remove</c> — which is what gives the simple path (write
///     and forget) and full transport reach from one archetype.
/// </summary>
/// <remarks>
///     Addressing is <b>global</b> (AGENTS.md §4 mode 1): <c>VesselId</c> is <c>""</c> and the player
///     id rides in <see cref="SimCommand.Token"/>, because a player is a host-side object with no
///     vessel. Every <c>schedule.*</c> action is Frame phase — none of them touches the vehicle
///     solver — so none appears in <c>SimCommand.SolverActions</c>.
///     <para>Status leaves are <b>live</b>, never snapshot-memoized: a player's <c>t</c> advances every
///     rendered frame, which is far faster than the telemetry publish cadence, and a stale <c>t</c>
///     would make a scrub-based preview loop unusable.</para>
/// </remarks>
internal static class ScheduleTree
{
    /// <summary>The <c>/sim/ctl</c> children the scheduler contributes: <c>timed_batch</c> and <c>schedules/</c>.</summary>
    /// <param name="sink">The command sink the registry controls submit to.</param>
    /// <param name="store">The live-player registry.</param>
    /// <param name="root">The <c>/sim</c> root a timed batch's paths resolve against (deferred).</param>
    /// <param name="qid">The tree's path→qid interner, so ids stay stable across rebuilds.</param>
    internal static VfsNode[] Nodes(ICommandSink sink, ScheduleStore store, Func<VfsDirectory> root,
        Func<string, ulong> qid)
        =>
        [
            new TimedBatchFile("timed_batch", qid("ctl/timed_batch"), sink, root, store),
            RegistryDir(sink, store, qid),
        ];

    private static VfsDirectory RegistryDir(ICommandSink sink, ScheduleStore store, Func<string, ulong> qid)
    {
        var help = new StaticTextFile("help", qid("ctl/schedules/help"), () => Help);
        var count = new StaticTextFile("count", qid("ctl/schedules/count"),
            () => store.Count.ToString(CultureInfo.InvariantCulture) + "\n");
        var clear = new TriggerFile("clear", qid("ctl/schedules/clear"), sink,
            new SimCommand("", SimActions.ScheduleClear, SimCommand.NoOrdinal, 1));

        return new DelegateDirectory("schedules", qid("ctl/schedules"),
            () =>
            {
                var players = store.Players;
                var children = new List<VfsNode>(players.Count + 3) { help, clear, count };
                for (var i = 0; i < players.Count; i++)
                    children.Add(EntryDir(sink, store, qid, players[i].Id));
                return children;
            },
            name => name switch
            {
                "help" => help,
                "clear" => clear,
                "count" => count,
                _ => store.Find(name) is not null ? EntryDir(sink, store, qid, name) : null,
            });
    }

    /// <summary>One live player's directory: its status leaves plus its transport controls.</summary>
    private static VfsDirectory EntryDir(ICommandSink sink, ScheduleStore store, Func<string, ulong> qid, string id)
    {
        var q = $"ctl/schedules/{id}";

        // Resolved on every access: the player can vanish (remove/clear) between walk and read, and a
        // captured reference would keep rendering a corpse. Absent ⇒ the neutral rendering below.
        IPlaybackPlayer? Player() => store.Find(id);

        StaticTextFile Status(string name, Func<IPlaybackPlayer, string> render, string absent)
            => new(name, qid($"{q}/{name}"), () => (Player() is { } p ? render(p) : absent) + "\n");

        return DelegateDirectory.Fixed(id, qid(q),
            Status("kind", p => p.Kind, "-"),
            Status("group", p => p.Group.Length == 0 ? "-" : p.Group, "-"),
            Status("state", p => p.State.ToString().ToLowerInvariant(), "done"),
            Status("t", p => p.Clock.PositionMs.ToString("F1", CultureInfo.InvariantCulture), "0.0"),
            Status("duration", p => p.DurationMs.ToString("F1", CultureInfo.InvariantCulture), "0.0"),
            Status("pending", p => p.PendingCount.ToString(CultureInfo.InvariantCulture), "0"),
            Status("dropped", p => p.Dropped.ToString(CultureInfo.InvariantCulture), "0"),
            Status("clock", p => p.Clock.Base.ToString().ToLowerInvariant(), "-"),
            Status("last_error", p => p.LastError, "-"),
            ControlFile.Flag("pause", qid($"{q}/pause"), sink,
                () => Player() is { } p && p.Clock.Paused ? "1" : "0",
                v => new SimCommand("", SimActions.SchedulePause, SimCommand.NoOrdinal, v) { Token = id }),
            ControlFile.Number("scrub", qid($"{q}/scrub"), sink,
                () => Player() is { } p ? p.Clock.PositionMs.ToString("F1", CultureInfo.InvariantCulture) : "0.0",
                v => new SimCommand("", SimActions.ScheduleScrub, SimCommand.NoOrdinal, v) { Token = id }),
            ControlFile.Number("rate", qid($"{q}/rate"), sink,
                () => Player() is { } p ? Formats.Scalar(p.Clock.Rate) : "1",
                v => new SimCommand("", SimActions.ScheduleRate, SimCommand.NoOrdinal, v) { Token = id }),
            ControlFile.Flag("loop", qid($"{q}/loop"), sink,
                () => Player() is { } p && p.Clock.Loop ? "1" : "0",
                v => new SimCommand("", SimActions.ScheduleLoop, SimCommand.NoOrdinal, v) { Token = id }),
            new TriggerFile("stop", qid($"{q}/stop"), sink,
                new SimCommand("", SimActions.ScheduleStop, SimCommand.NoOrdinal, 1) { Token = id }),
            new TriggerFile("remove", qid($"{q}/remove"), sink,
                new SimCommand("", SimActions.ScheduleRemove, SimCommand.NoOrdinal, 1) { Token = id }));
    }

    /// <summary>The console-friendly grammar reference behind <c>/sim/ctl/schedules/help</c>.</summary>
    private const string Help =
        """
        schedules — host-side timed command playback. Write a schedule to /sim/ctl/timed_batch;
        it becomes a live player under /sim/ctl/schedules/<id>/. The host owns the clock, so
        timing does not depend on the guest staying connected, and the same schedule replays
        identically every time.

        WRITE A SCHEDULE  (any /sim control file is a valid target — the whole control surface)
          cat > /sim/ctl/timed_batch <<'EOF'
          @id      launch-seq        # optional; auto "#N" otherwise
          @clock   render            # render | wall | ut          (default render)
          @rate    1.0               # 0..100; 0 freezes
          @loop    0                 # 0 | 1
          @group   take-3            # optional shared-clock group
          0        vessels/by-id/apollo11/ctl/throttle  1
          1200     vessels/by-id/apollo11/ctl/ignite    1
          3400     debug/time/warp                      1
          commit
          EOF

          Offsets are ABSOLUTE ms from the schedule's start (fractional allowed), never deltas.
          Directives must precede entries. Blank and '#' lines are ignored. Everything is
          validated before anything registers: bad path = ENOENT, bad value = EINVAL, and
          nothing is left half-committed. Unlike ctl/batch, Frame- and Solver-phase actions
          may be mixed — each entry routes itself when it comes due.

        CLOCKS
          render  accumulated rendered-frame time. Lags true wall time after a hitch and never
                  catches up — which is what you want for footage, not for a host recorder.
          wall    true elapsed time; may demand a catch-up burst after a stall.
          ut      sim time; right for mission events, which diverge wildly under warp.

        CATCH-UP
          When many entries come due at once, every TRIGGER fires (in order) but state controls
          coalesce to the last write per path — an intermediate setpoint nobody could observe is
          dropped, and the count shows up at <id>/dropped. Cross-path order is preserved.

        INSPECT / DRIVE  (per player <id>)
          cat schedules/<id>/state          pending|running|paused|done|failed
          cat schedules/<id>/t              current offset, ms
          cat schedules/<id>/duration       total, ms
          cat schedules/<id>/pending        entries not yet fired
          cat schedules/<id>/dropped        coalesced entries so far
          cat schedules/<id>/last_error     first failing entry + errno, or "-"
          echo 1   > schedules/<id>/pause   0|1
          echo 500 > schedules/<id>/scrub   seek to ms; fires nothing, just re-seats the cursor
          echo 2   > schedules/<id>/rate    playback multiplier
          echo 1   > schedules/<id>/loop    0|1
          echo 1   > schedules/<id>/stop    stop playback (stays listed)
          echo 1   > schedules/<id>/remove  drop it from the registry
          echo 1   > schedules/clear        stop + remove everything
          cat        schedules/count        how many players are live

        GROUPS
          Members of the same @group share ONE clock, so pause/scrub/rate/loop on any member
          moves them all together — that is what makes several schedules one take. The group's
          clock base, rate and loop come from the FIRST member to create it; a later joiner's
          @clock/@rate/@loop are ignored for the shared clock.

        NOTES
          A finished or failed player STAYS listed with its final state until you remove or
          clear it, so a script can come back and read the outcome. It still counts against the
          live-player cap. A failed entry does not stop the schedule: the rest still runs and
          the FIRST error is kept. Events schedule.started / finished / failed / dropped land in
          /sim/events. Everything here works over HTTP /v1 and MQTT too.

        """;
}
