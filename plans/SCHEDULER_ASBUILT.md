# SCHEDULER_ASBUILT — tasks S.0–S.4 of `plans/CAMERA_CONTROLS_PLAN.md`, as built

> **Scope:** the generic host-side timed-command scheduler. 100 % game-free, entirely in
> `gatOS.SimFs/Commands/` + `gatOS.SimFs.Tests/Commands/`. **No `gatOS.GameMod` change is part of
> this work item** — the game-thread driver (task **S.5**, `Mod.TickSchedules`) and the
> `KsaCatalog` routing of the `schedule.*` family are a separate agent's.
>
> This note is the input for the docs lockstep pass (AGENTS.md §9): SPEC, `scope/`, `AGENTS.md`,
> `docs/MILESTONES.md`, `docs/VALIDATION.md`, and the `gatos` skill. Nothing in `SPEC_9P_FILESYSTEM.md`,
> `scope/` or `docs/` was touched here.

---

## 1. Files added / changed

### Added — `gatOS.SimFs/Commands/`

| File | Contents |
|---|---|
| `PlaybackClock.cs` | `ClockBase`, `PlaybackState`, `PlaybackClock` — the one timeline primitive (plan §3.4) |
| `Schedule.cs` | `ScheduleEntry`, `Schedule` — the immutable committed schedule |
| `Scheduler.cs` | `DueCommand`, `Scheduler` — one live player's cursor + catch-up policy |
| `ScheduleStore.cs` | `ScheduleLimits`, `IPlaybackPlayer`, `ScheduleStore`, `ScheduleRunner` (internal) |
| `TimedBatchFile.cs` | `TimedBatchFile` — the `/sim/ctl/timed_batch` write handle + grammar |
| `ScheduleTree.cs` | `ScheduleTree` (internal) — the `/sim/ctl/{timed_batch,schedules/}` nodes |

### Changed

| File | Change |
|---|---|
| `gatOS.SimFs/Commands/CommandQueue.cs` | new `IPostObserver` interface + `CommandQueue.Post`; `Pending` refactored to a nullable TCS with two ctors. **The awaiting path is behaviourally unchanged.** |
| `gatOS.SimFs/SimFsTree.cs` | new optional `ScheduleStore? schedules = null` param on `Build`; `ctl` registration moved into a private `GlobalCtlDir(sink)` |

### Added — tests (`gatOS.SimFs.Tests/Commands/`)

`PlaybackClockTests.cs`, `SchedulerTests.cs`, `TimedBatchFileTests.cs`, `ScheduleTreeTests.cs`;
`CommandQueueTests.cs` extended with 5 `Post` tests; `SimFsTreeTests` control-enabled tree-crawl
guard extended with every new path (it now builds with a `ScheduleStore` and one live player, id
`crawl`).

**107 tests** across the five fixtures. Full suite green, **0 warnings**.

---

## 2. New public API surface

### `gatOS.SimFs.Commands.ClockBase` (enum)
`Render` (default) | `Wall` | `Ut`.

### `gatOS.SimFs.Commands.PlaybackState` (enum)
`Pending` | `Running` | `Paused` | `Done` | `Failed`. Rendered **lower-case** in the `state` leaf.

### `gatOS.SimFs.Commands.PlaybackClock` (sealed class)
| Member | Notes |
|---|---|
| `const double MinRate = 0.0`, `MaxRate = 100.0` | rate clamp |
| `PlaybackClock(ClockBase clockBase)` | |
| `ClockBase Base { get; }` | fixed for the clock's life |
| `double PositionMs { get; }` | current offset, ms |
| `double DurationMs { get; set; }` | setter takes the **max**, never shrinks; ignores non-finite |
| `double Rate { get; set; }` | clamped `[0,100]`; non-finite ⇒ `1`. **`0` is legal = frozen** |
| `bool Loop { get; set; }`, `bool Paused { get; set; }` | |
| `bool Started { get; }`, `void Start()` | idempotent |
| `int LoopCount { get; }` | bumped per wrap; consumers diff it to detect a wrap |
| `int ScrubGeneration { get; }` | bumped per `Scrub`; consumers diff it to re-seat a cursor |
| `void Advance(double renderDeltaMs, double wallDeltaMs, double utDeltaMs)` | no-op while paused/unstarted/rate 0/non-positive delta; wraps keeping the remainder when looping, else clamps at duration |
| `void Scrub(double ms)` | clamps at 0 below; seeking **past** the duration is allowed; non-finite ignored |

Doubles are published as bit-cast `long`s through `Volatile` so transport threads read them
torn-free without a lock.

### `gatOS.SimFs.Commands.ScheduleEntry` (readonly record struct)
`(double DeadlineMs, string Path, SimCommand Command, bool IsTrigger)`.
`Path` is the **normalized** `/sim`-relative path and is the coalescing key. `IsTrigger` is captured
at commit from `target is TriggerFile` — **no archetype property was added to `CommandFile`**.

### `gatOS.SimFs.Commands.Schedule` (sealed class)
`Schedule(string id, string group, ClockBase clock, double rate, bool loop, IReadOnlyList<ScheduleEntry> entries)`;
`Id`, `Group`, `Clock`, `Rate`, `Loop`, `ScheduleEntry[] Entries`, `double DurationMs`.
Entries are sorted with LINQ `OrderBy` (documented-**stable**) so authored order survives within one
deadline — that is what makes a group of `0`-offset lines behave exactly like a `ctl/batch`.

### `gatOS.SimFs.Commands.DueCommand` (readonly record struct)
`(SimCommand Command, IPostObserver? Observer, int Token)`.

### `gatOS.SimFs.Commands.Scheduler` (sealed class)
`Scheduler(Schedule, PlaybackClock)`; `Schedule Schedule`, `int Pending`, `bool IsComplete`,
`int Tick(List<DueCommand> due, IPostObserver observer)` → returns entries **dropped** by coalescing.

### `gatOS.SimFs.Commands.IPostObserver` (interface)
`void OnCommandResult(int token, CommandResult result)` — invoked **inline on the game thread**
inside `CommandQueue.Drain`.

### `gatOS.SimFs.Commands.CommandQueue` (added member)
`void Post(SimCommand command, IPostObserver? observer = null, int token = 0)` — fire-and-forget, no
TCS. Honours `ControlEnabled` (reports `Denied` to the observer, does not enqueue). Routes by
`command.Phase`, so **phase mixing across posts is free**.

### `gatOS.SimFs.Commands.ScheduleLimits` (sealed record)
`(int MaxLive = 16, int MaxEntries = 8192, int MaxBytes = 1048576, ClockBase DefaultClock = ClockBase.Render)`.

### `gatOS.SimFs.Commands.IPlaybackPlayer` (interface)
`Id`, `Kind`, `Group`, `PlaybackClock Clock`, `DurationMs`, `PlaybackState State`, `PendingCount`,
`long Dropped`, `string LastError`, `void Stop()`.

### `gatOS.SimFs.Commands.ScheduleStore` (sealed class)
| Member | Thread | Notes |
|---|---|---|
| `const string ScheduleKind = "schedule"` | | the `kind` leaf value |
| `ScheduleStore(ScheduleLimits)` / `ScheduleStore()` | | |
| `ScheduleLimits Limits { get; }` | | |
| `IReadOnlyList<IPlaybackPlayer> Players { get; }` | any | volatile immutable array |
| `int Count { get; }` | any | `Players.Count` |
| `static bool IsValidId(string)` | | `[A-Za-z0-9_.-]`, 1..64 |
| `bool IsIdLive(string)` | any | reserved **or** live |
| `IPlaybackPlayer? Find(string)` | any | |
| `string ReserveId(string?)` | transport | EINVAL on bad id / duplicate / `MaxLive`; `null` ⇒ auto `#N` |
| `void ReleaseId(string)` | transport | |
| `string Submit(Schedule)` | transport | non-blocking; visible at the next `Activate` |
| `void Activate(double utSeconds = 0)` | **game** | evicts finished players **under cap pressure only** (§6), then drains pending → live players and starts their clocks |
| `static bool IsFinished(IPlaybackPlayer)` *(internal)* | | the one "can never fire again" test — `done`, or `failed` **and** exhausted/not-looping/past duration (§6) |
| `void AdvanceAll(double render, double wall, double ut)` | **game** | advances **each distinct clock once** |
| `void Tick(List<DueCommand> due, double utSeconds = 0)` | **game** | fills the due list |
| `CommandResult Execute(SimCommand)` | **game** | the game-free `schedule.*` executor |
| `void Clear()` | **game** | stop + remove everything, incl. un-activated commits |
| `void EmitEvent(SimEvent)` / `IReadOnlyList<SimEvent> DrainEvents()` | | the `AudioStore` event pattern verbatim |

### `gatOS.SimFs.SimFsTree.Build` (signature change, source-compatible)
```csharp
Build(SnapshotStore store, ICommandSink? commands, Func<string>? transports,
      DisplaySurface? display = null, AudioStore? audio = null, ScheduleStore? schedules = null)
```

---

## 3. Action keys

All **global** addressing (AGENTS.md §4 mode 1): `VesselId = ""`, `Ordinal = NoOrdinal`, the player
id in `Token`. All **Frame** phase — **none was added to `SimCommand.SolverActions`** (nothing here
is visible to the vehicle solver).

| Action key | Argument | Errnos from `ScheduleStore.Execute` |
|---|---|---|
| `schedule.pause` | `Value` `0`\|`1`, `Token` = id | EINVAL (not 0/1), ENOENT (no such id) |
| `schedule.scrub` | `Value` = ms ≥ 0 finite, `Token` = id | EINVAL, ENOENT |
| `schedule.rate` | `Value` ∈ `[0,100]` finite, `Token` = id | EINVAL, ENOENT |
| `schedule.loop` | `Value` `0`\|`1`, `Token` = id | EINVAL, ENOENT |
| `schedule.stop` | `Value` `1`, `Token` = id | ENOENT |
| `schedule.remove` | `Value` `1`, `Token` = id | ENOENT |
| `schedule.clear` | `Value` `1`, no `Token` | — (always Ok) |

`ScheduleStore.Execute` also answers `EOPNOTSUPP` for an unrecognized `schedule.*` action, and
`EINVAL` when the id token is missing on a per-player action.

> **For the GameMod agent:** `KsaCatalog` should route the whole `schedule.*` family straight to
> `ScheduleStore.Execute` — there is nothing game-side to re-validate, and re-implementing it would
> create a second definition.

---

## 4. New `/sim` paths

Archetypes per SPEC §3: **S** = status read, **St** = state control, **T** = trigger, **D** = directory.
Everything below exists **only when a `ScheduleStore` is wired** (`[schedule] schedule_enabled`);
`/sim/ctl/batch` is unaffected either way.

| Path | A | Format |
|---|---|---|
| `/sim/ctl/timed_batch` | St | write: the grammar of §5; read: one-line usage hint |
| `/sim/ctl/schedules/` | D | the live-player registry |
| `/sim/ctl/schedules/count` | S | integer, live player count |
| `/sim/ctl/schedules/clear` | T | write `1` → `schedule.clear` |
| `/sim/ctl/schedules/help` | S | multi-line grammar reference |
| `/sim/ctl/schedules/<id>/` | D | one per live player |
| `…/<id>/kind` | S | `schedule` (later `camera-track`) |
| `…/<id>/group` | S | group name, or `-` when ungrouped |
| `…/<id>/state` | S | `pending`\|`running`\|`paused`\|`done`\|`failed` (lower-case) |
| `…/<id>/t` | S | current offset ms, `F1` invariant (e.g. `250.0`) |
| `…/<id>/duration` | S | total ms, `F1` invariant |
| `…/<id>/pending` | S | integer, entries not yet fired |
| `…/<id>/dropped` | S | integer, entries dropped by coalescing |
| `…/<id>/clock` | S | `render`\|`wall`\|`ut` |
| `…/<id>/last_error` | S | `entry <n>: <ERRNO> (<message>)`, or `-` |
| `…/<id>/pause` | St | Flag `0`\|`1` → `schedule.pause` |
| `…/<id>/scrub` | St | Number ms → `schedule.scrub`; read-back is the current position (`F1`) |
| `…/<id>/rate` | St | Number → `schedule.rate`; read-back `Formats.Scalar` (G9) |
| `…/<id>/loop` | St | Flag `0`\|`1` → `schedule.loop` |
| `…/<id>/stop` | T | write `1` → `schedule.stop` |
| `…/<id>/remove` | T | write `1` → `schedule.remove` |

Every status leaf is a **`LiveLine`-equivalent** (`StaticTextFile`, formatted per access, never
snapshot-memoized): `t` advances every rendered frame, far faster than the telemetry publish cadence.

Qid strings are `ctl/timed_batch`, `ctl/schedules`, `ctl/schedules/count`, `ctl/schedules/<id>/<leaf>`.

**Transport parity is structural:** these are ordinary VFS leaves, so `VfsScan` mirrors them to
`GET/POST /v1/fs/ctl/schedules/<id>/rate` and `gatos/sim/ctl/schedules/<id>/rate` with no new code,
and `POST /v1/command` / `gatos/command` reach every `schedule.*` action by construction — passing
`"vessel_id": ""`, which the JSON command parser **requires as a present string** even for a globally
addressed action (`SimHttpServer.ParseCommand`; the field may be empty but never omitted or null).

### Events (host-side, via `ScheduleStore.EmitEvent` → the sampler's `DrainEvents`)

| Type | `Detail` |
|---|---|
| `schedule.started` | `<id> kind=schedule entries=<n> duration_ms=<F1>` |
| `schedule.finished` | `<id> kind=schedule dropped=<n>` |
| `schedule.failed` | `<id> entry=<n> <ERRNO>` |
| `schedule.dropped` | `<id> dropped=<n> total=<n>` — **throttled to ≤1 per player per second** |
| `schedule.evicted` | `<id> kind=schedule reason=max_live` — one per reclaimed slot (see §6) |

`VesselId` is always `null` (a player has no vessel).

> **For the GameMod agent:** the telemetry sampler must drain this store exactly like
> `AudioStore`/`IvaPhysicsManager` (`gatOS.GameMod/Game/TelemetrySampler.cs:198-201`), gated by
> `_settings.Events`.

---

## 5. `/sim/ctl/timed_batch` grammar (as implemented)

```
# comment; blank lines ignored
@id      launch-seq     # optional; auto "#N" otherwise. [A-Za-z0-9_.-]{1,64}
@clock   render         # render | wall | ut   (case-insensitive; default = ScheduleLimits.DefaultClock)
@rate    1.0            # 0..100
@loop    0              # 0 | 1
@group   take-3         # optional; same charset as @id
<offsetMs> <path> <payload…>
…
commit
```

- Directives must **precede** every entry (EINVAL otherwise: "directives must precede entries").
  Each may appear at most once (EINVAL: "duplicate '@x' directive"). Unknown ⇒ EINVAL. Missing
  value ⇒ EINVAL.
- Offsets: `double.TryParse(NumberStyles.Float, InvariantCulture)`, must be **finite and ≥ 0**
  (`16.67` is legal). **Absolute from the schedule's start, never deltas.**
- Whitespace between the three fields is collapsed, so column-aligned scripts parse.
- Path spellings: bare, `/`-rooted, and `/sim/`-rooted all resolve (`Normalize` is `BatchFile`'s).
  Unresolvable ⇒ **ENOENT**; non-`CommandFile` target ⇒ EINVAL "not a control file"; unparseable
  payload ⇒ EINVAL "cannot parse".
- **Phase mixing IS allowed** — the deliberate relaxation of `BatchFile`'s rule.
- Caps ⇒ EINVAL: `MaxEntries` entries, `MaxBytes` buffered bytes per open handle, `MaxLive` live
  players ("… live players already; remove one …"), duplicate id ("schedule id 'x' already live").
  Zero entries ⇒ EINVAL "no entries before 'commit'".
- Validation is **up-front and all-or-nothing**; the id is reserved *last*, so a rejected commit
  never burns the name.
- `commit` is **non-blocking** — it validates, registers and returns.
- Control disabled (`[control] enabled = false`) ⇒ **EACCES** on commit.
- Handle semantics match `BatchFile`: one schedule per open handle, lines past `commit` ignored,
  close-without-`commit` discards silently, an unterminated trailing `commit` fires best-effort on
  clunk (logged only — a clunk carries no errno).

---

## 6. Semantics worth documenting in the SPEC

- **Catch-up / coalescing is derived, not declared.** Among the entries due in one tick: every
  `IsTrigger` entry is emitted in order; for non-trigger entries only the **last per path** is
  emitted; **cross-path order is preserved**. Dropped entries are counted at `<id>/dropped` and
  reported as `schedule.dropped` — never silent. This bounds a hitch's burst by *distinct leaves*
  rather than entries (a test drives 3 600 authored entries down to 3 emitted commands).
- **Scrub fires nothing.** A seek re-seats the cursor by binary search; it is navigation, not
  playback. Scrubbing backwards makes the passed entries replay on subsequent ticks.
- **Loop drains the tail first.** On a wrap, the finished cycle's remaining entries fire (in order)
  before the new cycle's already-due head — a loop boundary is indistinguishable from any other tick
  that spanned many entries. The clock keeps the remainder, so a loop does not drift.
- **Shared-clock groups.** Members of one `@group` hold the *same* `PlaybackClock` instance, so
  `pause`/`scrub`/`rate`/`loop` on **any** member moves them all. The group clock's `Base`, `Rate`
  and `Loop` come from the **first** member to create it; a later joiner's `@clock`/`@rate`/`@loop`
  are ignored for the shared clock (a mismatched `@clock` logs a Debug note). Its `DurationMs` is the
  **max** over members. A joiner starts at the group's *current* position, so its already-past
  entries fire on its first tick — joining a take in progress.
  The group clock is dropped when its last member is removed.
- **Completed players persist — until the cap is reached.** A `done`/`failed` player stays listed
  with its final `state`/`dropped`/`last_error` until `remove` or `clear`. Rationale: a script that
  starts a take and comes back to read the outcome must be able to find it; auto-pruning would race
  that read. They therefore count against `schedule_max_live`, which on its own would let a long
  session of one-shot schedules wedge the registry on its own history. So **`ScheduleStore.Activate`
  evicts under cap pressure only**: while the registry is at `schedule_max_live`, it reclaims
  *finished* players **oldest first** (activation order — the take a script just started is the last
  to go), stopping the instant the count is back under the cap. Each reclaimed slot emits a
  `schedule.evicted` event; nothing is ever dropped silently. **Below the cap nothing is ever
  reclaimed**, so the persist-for-reading property is intact in the normal case.
  - **"Finished" is `ScheduleStore.IsFinished`, and `failed` is not terminal.** `done` is latched and
    conclusive. `failed` is set on the *first* failing entry while the schedule deliberately keeps
    running, so a failed player qualifies only when it is also `!Clock.Loop && PendingCount == 0 &&
    Clock.PositionMs >= DurationMs` (the runner's own completion test). Evicting on `state == failed`
    alone would truncate a live take. **A looping player is never finished.**
  - **Why eviction is eager, on the game thread.** `ReserveId` runs on a transport thread and cannot
    touch the runner list, the group table or `Players`, nor block waiting for a game tick. Reclaiming
    lazily at the moment a commit trips the cap therefore *cannot* work: the cap counts **reserved**
    ids (claimed the instant a commit validates) while an eviction pass can only see **activated**
    runners, so the first commit into a full registry would still fail EINVAL and only a retry a frame
    later would succeed. Evicting eagerly, before the pending drain, removes the race instead of
    papering over it. Both guards (`_runners.Count == 0`, `_ids.Count < MaxLive`) are integer compares,
    so the idle tick stays branch-only and allocation-free.
- **A failed entry does not stop the schedule.** The remaining entries still run; only the **first**
  failure is recorded (the cause, not the last symptom), and `state` becomes `failed` and stays
  there.
- **Clock-base caveats** (plan §3.2, unchanged): `render` lags true wall time after a hitch and never
  catches up; `wall` may demand a catch-up burst; `ut` diverges wildly under warp.
- **A just-committed schedule appears at the next game tick.** `count`/`ls` reflect *activated*
  players; the id is reserved immediately, so a duplicate-id commit in between is still rejected.

---

## 7. Config keys the caps map to (`[schedule]`, plan §8 — **not yet added to `GatOsConfig.cs`**)

| Key | Default | Maps to |
|---|---|---|
| `schedule_enabled` | `true` | pass `schedules: null` to `SimFsTree.Build` when false |
| `schedule_max_live` | `16` | `ScheduleLimits.MaxLive` |
| `schedule_max_entries` | `8192` | `ScheduleLimits.MaxEntries` |
| `schedule_max_bytes` | `1048576` | `ScheduleLimits.MaxBytes` |
| `schedule_default_clock` | `"render"` | `ScheduleLimits.DefaultClock` |

Task **C0.2** owns the `GatOsConfig.cs` property + `Sections` row + clamp-and-warn + the hand-synced
`Configuration/gatos.default.toml` block.

---

## 8. Deviations from the plan (with justification)

1. **`ICommandSink.SubmitScheduleAsync` was NOT added** (plan §3.3 proposed it). Instead
   `TimedBatchFile` holds the `ScheduleStore` directly — the `AudioStore` precedent (AGENTS.md §6
   "dedicated store"). *Why:* a schedule is host-side state, not a game mutation, so it does not
   belong on the game-thread command seam; and `SubmitScheduleAsync` would have forced every one of
   the ten `ICommandSink` implementors (incl. test doubles) to carry a member they cannot implement.
   **Transport parity still holds by construction:** `/sim/ctl/timed_batch` is an ordinary writable
   VFS leaf, so `VfsScan` mirrors it to HTTP `/v1/fs/ctl/timed_batch` and MQTT
   `gatos/sim/ctl/timed_batch/set` with no new code.

2. **`Scheduler.Tick` takes `List<DueCommand>` + an `IPostObserver`, not `List<SimCommand>`.**
   `CommandQueue.Post` carries a single `int` token, so the due list must hand back the entry index
   *and* the observer to correlate a failure with the entry that caused it.

3. **`ScheduleStore.Activate`/`Tick` take an optional `double utSeconds = 0`.** The brief's
   signatures are preserved (`Tick(due)` compiles), but `SimEvent` requires a UT stamp and the store
   has no other way to obtain one. The driver should pass the sampler's UT.

4. **`ScheduleStore.Prune()` was not implemented** as public surface. Per the brief's own resolution,
   completed players persist until `remove`/`clear`; `schedule.remove`/`schedule.clear` cover
   cleanup, so an unused public method would be dead surface. What *did* land later is the private,
   cap-pressure-only eviction inside `Activate` (§6) — it is not a general prune, it never runs below
   the cap, and it is not callable from a transport.

5. **`PlaybackClock.Rate` clamps to `[0, 100]`, not `[0.01, 100]`** — rate `0` is a legal "frozen"
   state, per the brief's own amendment.

6. **`ScheduleStore.ReserveId` is a separate public step from `Submit`.** `Schedule` is immutable and
   carries its `Id`, but the store owns id assignment; splitting the two keeps `Schedule` immutable,
   makes the cap + duplicate check one atomic insert, and lets the commit path reserve *last* so a
   validation failure cannot leak a name.

7. **`ScheduleTree` is `internal`, and lives in `gatOS.SimFs/Commands/` rather than inside
   `SimFsTree.Builder`.** It matches the plan's §6.1 file list and keeps `SimFsTree.cs` from growing
   by ~150 lines; it takes the tree's `Qid` interner as a `Func<string, ulong>` so qids stay stable.

8. **`TimedBatchFile` enforces `[control] enabled` itself (EACCES).** It does not go through
   `ICommandSink.SubmitAsync`, which is where every other archetype's `Denied` originates, so the
   gate had to be re-asserted or a disabled control surface would still accept schedules.

---

## 9. Threading (for the `AGENTS.md` threading-rules paragraph)

- Transport threads only ever call `ScheduleStore.ReserveId`/`Submit`/`Find`/`Players`/`IsIdLive`/
  `Count` — a `ConcurrentDictionary`, a `ConcurrentQueue`, and one volatile immutable array.
- `Activate`, `AdvanceAll`, `Tick`, `Execute`, `Clear` are **game-thread only** and mutate plain
  `List`/`Dictionary`.
- `IPostObserver.OnCommandResult` runs **inline on the game thread** inside `CommandQueue.Drain`.
- `Scheduler.Tick` allocates **nothing** on a tick with 0 or 1 entries due (asserted by
  `SchedulerTests.IdleTick_AllocatesNothing` over 1 000 ticks, budget < 64 bytes). The ≥2 case uses
  two per-`Scheduler` scratch collections that are `Clear()`ed, never reallocated.
- No Harmony patch, no KSA type, no game reference anywhere in this work item.

---

## 10. Still open (not this work item)

- **S.5** — `Mod.TickSchedules` in the (F2-proof, task C0.1) command drain: call
  `Activate(ut)` → `AdvanceAll(renderMs, wallMs, utMs)` → `Tick(due, ut)` → `queue.Post(d.Command,
  d.Observer, d.Token)` for each; drain `DrainEvents()` in the telemetry sampler; route
  `schedule.*` in `KsaCatalog` to `ScheduleStore.Execute`; tear down via `Mod.TeardownGameCheats`
  (`ScheduleStore.Clear()`).
- **C0.2** — the `[schedule]` config section.
- Docs lockstep (AGENTS.md §9) from this note.
- `docs/VALIDATION.md` — a `## schedules — validation pass — **NOT YET RUN**` section: the three
  clock bases under load, a hitch's `dropped` accounting against `max_commands_per_frame`, a
  group scrub staying aligned, and the schedule surviving a guest disconnect.
