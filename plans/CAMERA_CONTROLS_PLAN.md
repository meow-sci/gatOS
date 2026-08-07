# CAMERA_CONTROLS_PLAN — programmable cinematic camera over `/sim`, and the generic timed-command scheduler

> **Goal.** Make gatOS able to *shoot a trailer*. A guest program (any language) must be able to
> author and run scripted camera sequences — free-flying the camera, following and framing vessels,
> kittenauts and celestials, skimming an ocean, flying through cloud decks, pushing in on a hero
> shot — with motion smooth enough to record, and hand the camera back to the game cleanly.
>
> **Secondary goal, equal weight:** the *scheduling* half of that must be a **generic `/sim`
> primitive**, not a camera feature. `TimedBatchFile` is `BatchFile` with a clock: any control leaf,
> any time offset, replayed host-side. And every camera capability must be reachable as an ordinary
> `/sim` leaf (hence HTTP `/v1/fs/…` and MQTT `gatos/sim/…` by construction), so `batch` and
> `timed_batch` can drive the camera exactly like anything else — the JSON track is an *additional*
> path, never the only one.
>
> **Status:** PLAN. Nothing here is built. Baseline: KSA `2026.8.5.5168`, gatOS `main` @ `b3bc6cb`.
>
> Companion references: [`AGENTS.md`](../AGENTS.md) (the `/sim` schema-change constitution — this
> plan obeys it), [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md), [`scope/`](../scope/FULL_SCOPE.md),
> `.claude/skills/ksa/camera.md` (**stale — see §11.3**).

---

## 0. Executive summary

Four decisions carry the design.

1. **Three layers, not one mechanism.** Discrete control is a *generic* problem and continuous
   interpolation is a *typed* one. So: **L1** direct leaf writes (what exists), **L2** a generic
   host-side scheduler (`TimedBatchFile` — new, works on every control leaf), **L3** a typed camera
   track evaluator (interpolating, camera-only). They **compose**: L2 can drive FOV and cuts while
   L3 interpolates position. (§2)

2. **No Harmony patch. The driver hangs off `[StarMapAfterOnFrame]`.** gatOS's existing per-frame
   hooks (`[StarMapBeforeGui]` / `[StarMapAfterGui]`) sit **inside `if (Program.DrawUI)`** in
   `Program.OnFrame` — so **pressing F2 to hide the UI kills every gatOS game-thread driver**,
   including the command drain. F2 is the first thing a director presses. `[StarMapAfterOnFrame]`
   (a postfix on `Program.OnFrame`) runs unconditionally, exactly once per rendered frame. Writing
   the camera there lets the *game's own* `Camera.OnFrame` rebuild the view/projection matrices on
   the next frame — no matrix surgery, no patch, correct in every camera mode. (§5)

3. **Ownership is a mode park, not a fight.** `Viewport.Mode = CameraMode.Fixed` +
   `Camera.Unfollow(changeControl: false)` makes `FixedController.OnFrame` a **literal no-op** (it
   early-outs when `Following == null`). The game's camera solver then produces nothing and gatOS is
   the only writer — no per-frame race, no input contention, and release is a state restore. (§5.2)

4. **Full leaf-level granularity is a requirement, not a nicety.** Every channel the JSON track can
   animate has a corresponding writable `/sim` leaf. That is what makes the camera reachable from
   `batch`, `timed_batch`, HTTP and MQTT, and it is what keeps the JSON track an *option* rather
   than a walled garden. (§4)

Everything except the actuators is game-free math in `gatOS.SimFs/`, unit-testable on a bare host,
following the `CabinPhysics` precedent.

---

## 1. Gap analysis

### 1.1 What gatOS has today

The **entire** camera surface is one action:

| Path | Action | Binding | File |
|---|---|---|---|
| `vessels/by-id/<id>/ctl/focus`, `bodies/<id>/focus`, `debug/focus` | `camera.focus` | `Program.GetMainCamera().SetFollow(Astronomical, tidalLocking: true, changeControl: false)` | `Game/Ksa/Actuators/CameraActuator.cs:21` |

Two incidental camera reads exist elsewhere: `IvaPhysicsManager` gates on
`Program.MainViewport.Mode != CameraMode.IVA` (`Game/Ksa/Iva/IvaPhysicsManager.cs:655`), and
`ThugLifeQuadRenderer` reads `Program.GetMainCamera().MVP.viewProjection`.

There is **no** way to read the camera, position it, orient it, set its FOV, unlock it, or restore it.

For scheduling, `/sim/ctl/batch` exists but is **atomic-same-tick only** — many leaves, one frame.
There is no way to say "and then, 400 ms later, this".

### 1.2 Requirement → gap

| # | Requirement | Today | Gap |
|---|---|---|---|
| R1 | Smooth animation, ≥1 update / 16 ms | — | **Blocking.** Blocking control writes cap at 1 cmd/frame + 1 frame latency + guest jitter. Needs host-side pacing (**L2**) and, for continuous motion, host-side interpolation (**L3**). |
| R2 | Move the camera freely (unlock) | — | **Missing.** No pose write; no ownership/mode park; no release. |
| R3 | Restore camera to game control | — | **Missing.** Nothing to restore *from*; needs a captured restore-state (mode, follow, tidal, FOV, `NoRotation`, sim speed). |
| R4 | Change view/follow target (vessel & celestial) | `camera.focus` | **Partial.** No `unfollow`, no tidal-lock control, no **`MapCamera` sync** (map view desyncs — §11.1), no read-back of the current target. |
| R5 | Fly around a planetoid — skim ocean/land, fly through clouds | — | **Missing**, but *natively easy*: `Camera.LocalPosition` is already stored in the target's **body-fixed rotating frame** (§4.2). Also wants a `lat lon alt` placement leaf so ground shots aren't hand-computed vectors. |
| R6 | "Look at" a target with a vector/XYZ offset (kittenaut's head) | — | **Missing.** Needs an aim channel: `target + offset` resolved **in the target's own live frame** every frame, plus `up` and `roll`. Kittenauts are free: `KittenEva` **is a `Vehicle`**. |
| — | FOV / roll / ortho | — | Missing. `SetFieldOfView(deg)` is unclamped; `GetFieldOfView()` returns **radians** (§11.2). |
| — | Shot sequencing, blends, scrub/preview | — | Missing. |
| — | Time as a shot channel (pause / slow-mo / warp) | `debug.warp` | **Primitive present** (`Universe.SetSimulationSpeed(x, alert: false)`, arbitrary factors incl. `0` = pause). Missing: putting it on a clock. |
| — | **Timed multi-leaf sequences (any feature)** | `ctl/batch` (same-tick only) | **Missing — and generic.** Task **S** (§3). |
| — | Drivers survive `F2` (hide UI) | — | **Broken today for all of gatOS** — §5.1 / task **C0.1**. |

### 1.3 What `unscience/camera-controller-override` gives us, and what it does not

Worth porting (concepts, not code):

- **Track-a-moving-target every frame.** Every animation re-reads `Camera.Following.GetPositionEcl()`
  per frame rather than baking a start position.
- **Absolute-from-start, never incremental.** Orbits rotate the *captured start offset* by the
  *total* angle (Rodrigues) each frame — no cumulative drift. A 360° orbit lands on 360°.
- **Accumulate time before evaluating**, and **snap eased progress to exactly 1.0** on the completing
  frame. Both are documented bug fixes in `unscience/plans/done/TIMING_ANALYSIS_AND_FIX.md`; both
  are load-bearing.
- **Return-to-start** as an explicit, eased hand-back rather than a cut.
- Easing with **separate start/end powers**.

What it deliberately does **not** do, and we must:

- No camera **unlock / free placement** — it never calls `SetFollow`/`Unfollow`, never switches
  `CameraMode`; every motion is relative to whatever the game is already following.
- No **target changing**, no **FOV** (that is the separate `glass` mod, which pokes the private
  `Camera._fovRadians` — the highest-churn binding in that repo).
- No **roll**, no **map/IVA/fixed** coverage (it patches only `OrbitController`/`FlyController`), no
  persistence, no scrub/preview, no time control.
- Its `Remove()` calls `harmony.UnpatchAll(harmony.Id)` — in a shared-Harmony host that removes
  *everyone's* patches. Avoided entirely here by using no patch.
- Its `IKeyframeAnimation.LookAtTargetProvider` extension point is **never assigned anywhere** — aim
  is always dead-centre on the follow target. R6 is genuinely unsolved there.

**The historical lesson** (`unscience/camera-controller-override.lib/CameraControllerOverridePatches.cs:48-53`):
its prefix once declared a Harmony field injector `Transform3D ___Transform`. **No such field exists**
on `KSA.Controller` — the camera is the public field `Controller.Camera`. Harmony validates injected
field names at patch time, so `Patch()` **threw**, which killed the feature *and silently aborted every
feature patched after it in the chain*. Two repo docs still describe the wrong shape (§11.3).

---

## 2. The three-layer model (and why all three exist)

| | **L1 — leaves** | **L2 — schedule** | **L3 — track** |
|---|---|---|---|
| Mechanism | direct writes to `/sim` leaves; `ctl/batch` for atomic groups | `ctl/timed_batch` + `ctl/schedules/` | `/sim/camera/track/` + evaluator |
| Clock | the guest's | **host, render-locked** | **host, render-locked** |
| Rate ceiling | 1 cmd/frame, guest-paced | 1 frame quantized, host-paced | evaluated **every** frame |
| Interpolates | no | no (floor resample) | **yes** |
| Scope | **every** control leaf | **every** control leaf | camera channels only |
| Blocking | yes (write carries errno) | validate-then-return + status | validate-then-return + status |

**Why L2.** A host-side scheduler removes four of the five failure modes of guest-pushed writes:
network/scheduler **jitter** (the clock now lives next to the drain), **latency** (the entry is
already in the mod), **throughput** (one upload, not 3 600 round trips) and **non-determinism**
(same schedule, same result — repeatable takes). It also lets the guest disconnect during playback.
Because `BatchFile` already resolves any `/sim`-relative path to a `CommandFile`, a timed variant
inherits the *entire* control surface for free — staging sequences, light shows, FX sweeps, audio
cues, warp changes, camera cuts. That is a far better return on the code than a camera-only device.

**Why L2 is not enough.** Commands apply on the game thread, which ticks once per rendered frame, so
frame quantization survives and a scheduler can only do **floor resampling** — take the most recent
entry whose deadline passed. That is a step function. Author at 60 Hz, render at 60 fps with normal
±2 ms variance, entries at 0 / 16.67 / 33.33 / 50 and frames landing at 17 / 33 / 51:

| frame | due | applied |
|---|---|---|
| 17 ms | 0, 16.67 | 16.67 |
| 33 ms | — | **hold (duplicate frame)** |
| 51 ms | 33.33, 50 | 50 (**33.33 dropped**) |

Hold, then double-step — the same beat frequency as guest-push, now deterministic instead of random.
Authoring at 240 Hz shrinks the error to ≤4 ms but still steps, at 4× the command volume.
Interpolating instead requires knowing the *shape* of each value — is `1 0 0 0` a quaternion to
slerp, a flag, or a token? A generic scheduler cannot know.

**So: L2 gives you *when*; L3 gives you *between*.** L3 stays typed and camera-specific for exactly
that reason, and stays a dumb spline evaluator — userland still authors every curve.

**Corollary (this is the point of §4):** because L1/L2 reach *any leaf*, the camera must be **fully
decomposed into leaves**. Anything only reachable through the JSON track would be invisible to
`batch`, `timed_batch`, HTTP and MQTT — a violation of the transport-parity rule in spirit if not
in letter.

---

## 3. `TimedBatchFile` — the generic scheduler (feature **S**, not camera-specific)

### 3.1 Grammar

Sibling of `BatchFile`: same path resolution, same up-front all-or-nothing validation, same
`commit` terminator. Each line gains a leading **absolute offset in milliseconds** (fractional
allowed — 16.67 is legal). `@`-prefixed lines are directives.

```
# clean footage: hide HUD, cut to the pad cam, ignite, pull focus
@id      launch-seq          # optional; auto "#N" otherwise
@clock   render              # render | wall | ut          (default render)
@rate    1.0
@loop    0

0        camera/enabled                     1
0        camera/pose/aim                    vessel:apollo11 off 0 12 0 frame bodyfixed up world
0        camera/pose/geo                    28.573 -80.649 45  body:earth
0        camera/pose/fov                    52
1200     vessels/by-id/apollo11/ctl/ignite  1
1200     debug/time/warp                    0.25
3400     camera/pose/fov                    28
3400     debug/time/warp                    1
9000     camera/release                     1
commit
```

- **Offsets are absolute from schedule start**, never deltas — 1 ms rounding cannot accumulate over
  a long sequence.
- Sorted by deadline at commit; **stable within equal deadlines** so authored order is preserved
  (that is what makes `0`-offset groups behave exactly like a `ctl/batch`).
- Blank and `#` lines ignored, `@` directives parsed before entries.
- **Phase mixing is allowed** — a *relaxation* of `BatchFile`'s rule, not an inheritance of it.
  `BatchFile` forbids mixing Frame and Solver because "same tick" is meaningless across phases; a
  schedule spans many ticks, so each entry simply routes to its own phase queue when due.

### 3.2 Semantics

- **Non-blocking.** The `commit` write validates everything (unresolvable path → ENOENT,
  non-control target or unparseable value → EINVAL, cap exceeded → EINVAL) and returns. It cannot
  block for the schedule's lifetime, so runtime outcomes surface via `schedules/<id>/` and
  `/sim/events`. Up-front all-or-nothing validation is preserved — the good property of `BatchFile`.
- **Catch-up policy is derived, not declared.** On a hitch, many entries come due at once — and
  `max_commands_per_frame` defaults to 64. `CommandFile` already knows its archetype:
  **`TriggerFile` ⇒ impulse ⇒ execute all, in order**; **state controls ⇒ last-wins ⇒ coalesce per
  path** (keeping cross-path order). No syntax, no caller decision — the same discipline by which
  `SimCommand.Phase` is derived from the action key rather than passed at construction sites.
  Coalesced/dropped counts are exposed (`schedules/<id>/dropped`), never silent.
- **Clock base is an explicit choice**, because the three genuinely differ:
  - `render` *(default)* — accumulated `dtPlayer`. **This is not true wall time**: `KSA/App.cs`
    clamps `dtPlayer = min(realDelta, 1/MinTargetFrameRate)`, so during a hitch the game runs in
    slow motion and this clock *lags real time and never catches up*. Correct for cinematics (the
    schedule stays in sync with rendered motion), wrong for syncing to a host recorder.
  - `wall` — a `Stopwatch`. True elapsed time; can demand a catch-up burst after a stall.
  - `ut` — sim time. Right for mission events, which under 100× warp diverge wildly from both.
- **Zero-alloc tick.** Everything is pre-parsed at commit into a flat, sorted
  `(deadlineMs, SimCommand)` array; ticking advances an index. No parsing on the hot path.
- **Dispatch reuses everything.** The scheduler runs on the game thread inside the (now F2-proof)
  command drain, immediately *before* the queue drain, and `Post`s due commands into the existing
  `CommandQueue` with **no waiter**. Phase routing, the executor, health latches and the
  `max_commands_per_frame` budget all come for free.

### 3.3 Surface

A committed schedule becomes a live registry entry — AGENTS.md §4 mode 2, "the template for any
editor feature". This gives the simple path (write and forget) and full transport from one archetype.

```
/sim/ctl/
  batch                    St   (existing) atomic same-tick group
  timed_batch              St   write lines + `commit` → creates a schedule; read = usage hint
  schedules/                    every live player — schedules AND camera tracks (§3.4)
    count                  S    live player count
    clear                  T    stop + remove all
    help                   S    grammar reference
    <id>/
      kind                 S    schedule | camera-track
      group                S    shared-clock group name, or "-"
      state                S    pending | running | paused | done | failed
      t                    S    current offset, ms
      duration             S    total, ms
      pending              S    entries not yet fired (schedules only)
      dropped              S    coalesced / skipped count
      clock                S    render | wall | ut
      last_error           S    first failing entry + errno, or "-"
      pause                St   0|1
      scrub                St   ms  (seek; re-fires nothing, sets the cursor)
      rate                 St   playback rate multiplier
      loop                 St   0|1
      stop                 T    1
      remove               T    1
```

Events emitted to `/sim/events`: `schedule.started`, `schedule.finished`, `schedule.failed`
(carrying the entry index + errno), `schedule.dropped` (throttled).

**Action keys:** `schedule.pause`, `schedule.scrub`, `schedule.rate`, `schedule.loop`,
`schedule.stop`, `schedule.remove`, `schedule.clear`. The commit itself rides a new
`ICommandSink.SubmitScheduleAsync(entries, options, ct)` — the `SubmitBatchAsync` sibling — which
returns the assigned id.

**S is independently valuable.** It is worth building even if the camera work were cancelled:
`dancy-party-rs`-style light shows, timed staging sequences, FX-editor parameter sweeps, audio cues,
AGC mission replays, and repeatable test fixtures all want exactly this.

### 3.4 `PlaybackClock` — one timeline primitive for both players *(resolves Q4)*

A schedule (L2) and a camera track (L3) are the same object wearing different hats: a host-side,
render-locked player with `state` / `t` / `duration` / `pause` / `scrub` / `rate` / `loop` / `stop`.
Building two would mean **two notions of "now"** — and any drift between them shows up as a camera
move that slides against its own cue track. So there is exactly one:

- **`PlaybackClock`** (in `Commands/`, game-free) owns clock base, rate, loop, scrub, pause and
  state. `Scheduler` and camera `Playback` are both *consumers* of it, not peers with their own.
- **One transport vocabulary.** The camera player's transport leaves are the *same names with the
  same semantics* as `schedules/<id>/`. Learn it once.
- **Both appear in one registry.** `/sim/ctl/schedules/` lists every live player, schedules and
  track players alike, each with a `kind` field (`schedule` | `camera-track`). One place to see
  what is running, one place to stop it all (`clear`).
- **Shared-clock groups.** A player may join a named group (`@group take-3`, or
  `camera/play … group take-3`); members of a group advance off one `PlaybackClock` instance, so
  `pause` / `scrub` / `rate` on any member moves them **together**. That is what makes "the dolly
  move, the light cues and the slow-mo beat are one take" true rather than approximately true —
  and it is what makes a scrub-based preview loop usable at all.

`camera/play` / `set` / `stop` remain as the camera-flavoured spelling (mirroring `/sim/audio`), but
they are thin wrappers over the same registry entry — never a parallel mechanism.

---

## 4. The camera `/sim` surface

Top-level (not under `debug/`): a first-class creative feature with its own caps, gated by its own
`[camera] camera_enabled` key — the `/sim/audio` precedent. The **`time` channel alone** additionally
requires `[control] debug_namespace` (it is `debug.warp`'s power under a new name).

Archetype legend matches SPEC §3: **S** = snapshot read, **St** = state control, **T** = trigger,
**D** = directory.

### 4.1 Leaves (the granularity requirement)

**Every channel L3 can animate appears here as a leaf.** One leaf per scalar/vector knob so clients
write only what changed (AGENTS.md §7); composite line controls exist alongside as conveniences,
never as the only route.

```
/sim/camera/
  enabled            St  0|1     camera.enabled       take / release ownership
  release            T   1       camera.release       hard restore to game control (§5.3)
  status             S           —                    live: owned mode state track t dur shot
  info               S           —                    caps + grammar (static)

  mode               St  enum    camera.mode          orbit|free|map|iva|fixed
  follow             St  token   camera.follow        target id | "none"
  tidal              St  0|1     camera.tidal         tidal-locking flag for follow
  target             S           —                    current follow target id (or "-")

  pose/
    position         St  vec3+   camera.position      "x y z [<frame>]"
    frame            St  enum    camera.frame         default frame for position writes
    anchor           St  token   camera.anchor        the target that frames resolve against
    geo              St  line    camera.geo           "lat lon alt [body:<id>]"   ← R5 ergonomics
    orbit/radius     St  number  camera.orbit_radius      ┐ spherical placement about the anchor
    orbit/azimuth    St  number  camera.orbit_azimuth     ├ the granular twin of track "mode":"orbit"
    orbit/elevation  St  number  camera.orbit_elevation   ┘
    rotation         St  vec4    camera.rotation      ECL quaternion x y z w
    aim              St  line    camera.aim           composite convenience (all four fields at once)
    aim_target       St  token   camera.aim_target    "vessel:<id>" | "body:<id>" | "part:<v>/<iid>" | "none"
    aim_offset       St  vec3    camera.aim_offset    offset resolved in aim_frame
    aim_frame        St  enum    camera.aim_frame     ecl|cce|bodyfixed|enu|lvlh|chase
    aim_up           St  enum    camera.aim_up        world|target|velocity|free
    roll             St  number  camera.roll          degrees, applied after aim
    fov              St  number  camera.fov           degrees
    ortho            St  0|1     camera.ortho         orthographic projection
    ortho_height     St  number  camera.ortho_height  ortho half-height, metres
    smoothing        St  number  camera.smoothing     seconds, critically damped; 0 = raw
    reset            T   1       camera.pose_reset    drop live overrides

  track/             D           —                    writable dir: upload .json tracks
  play               St  line    camera.play          "<track> [at <t>] [rate <x>] [loop 0|1]"
  set                St  line    camera.set           "[t <sec>] [rate <x>] [loop 0|1] [paused 0|1]"
  stop               T   1       camera.stop          stop playback (optional blend back)
  playback           S           —                    live: state t duration shot index rate loop
```

`play` / `set` / `stop` mirror `/sim/audio`'s three-verb grammar rather than inventing one.
Multi-knob atomic updates ride `/sim/ctl/batch`; timed sequences ride `/sim/ctl/timed_batch` —
**no bespoke batch file** (AGENTS.md §7).

Transport parity is structural: these are ordinary VFS leaves, so `VfsScan` lights up
`GET/PUT /v1/fs/sim/camera/pose/fov` and `gatos/sim/camera/pose/fov` with no extra code. Read-back
reflects the **live** value so a client can resync after a restart. Pose leaves change every frame
while a track plays, so their MQTT field mirror publishes **changed-only and quantized** (the
`AudioChannelStatus.PositionMs` ~100 ms precedent) to avoid churning the broker.

### 4.2 Frames (the crux of R5 and R6)

Everything reduces to *"express a point in a chosen frame and let the host resolve it every frame."*
All six frames are natively available in KSA:

| token | meaning | resolution |
|---|---|---|
| `ecl` | absolute ecliptic, metres | identity |
| `cce` | **inertial** ECL-axes offset from the anchor — does *not* spin with it | anchor `GetPositionEcl()` + offset |
| `bodyfixed` | the anchor's **rotating** body-fixed frame — rides a planet's rotation, or bolts to a hull | `IFollowable.GetBodyFixed2Ecl()` |
| `enu` | local horizon at the anchor (east / north / up) | `Vehicle.GetEnu2Cce()` / `Celestial.GetCcf2Cce()` |
| `lvlh` | orbital frame (prograde / radial / normal) | `Vehicle.GetLvlh2Cce()` |
| `chase` | vessel body frame, game chase convention | `Vehicle.Body2Cce` |

**R5 falls out of `bodyfixed`.** `Camera.LocalPosition` is *already* stored in the followed object's
body-fixed frame (`Camera.PositionCce`'s get/set transform through `GetBodyFixed2Ecl()`) — which is
why the stock camera rides a rotating planet. gatOS composes the same transform itself, so "hold this
spot 30 m above the ocean at 12°N 40°W and let the world turn under the shot" is a *static*
`bodyfixed` position, not a keyframed curve chasing rotation. `pose/geo` makes that literal:

```sh
echo "12.4 -40.2 30 body:earth" > /sim/camera/pose/geo
```

(KSA has `Camera.SetLatLon` / `SetAltitude`, but they require `_following is Celestial` and we
deliberately unfollow — so gatOS reproduces the arithmetic, which is just
`surfacePosFromDirCce + dir × altitude`.)

**R6 falls out of the aim channel**, and is animatable leaf-by-leaf:

```sh
echo "vessel:kitten-01 off 0 0.9 0 frame bodyfixed up world" > /sim/camera/pose/aim   # composite
echo "0 1.1 0"                                              > /sim/camera/pose/aim_offset  # or granular
```

`+0.9 m` on the kittenaut's own Y axis is its head, and it *stays* its head as the kittenaut walks
and turns, because the offset is re-resolved in the live body frame every frame. Targets are
`vessel:<id>` | `body:<id>` | `part:<vessel-id>/<instance-id>`. **Kittenauts need no special case** —
`KittenEva` is a `Vehicle`. Part resolution reuses the weld anchor resolver
(`Game/Ksa/Welds/WeldEngine.cs`), which already maps `instance_id` → live part pose.

### 4.3 Compositing — how L1/L2/L3 coexist on one camera

The director composes a pose each frame in a fixed order, so the three layers layer instead of
fighting:

```
committed value  ←  live override (L1/L2 leaf writes)  ←  track channel (L3, only channels the active shot declares)
```

- A shot claims **only the channels it declares**. Undeclared channels fall through to live
  overrides, then to the last committed value.
- So a `timed_batch` can pull focus (`pose/fov`) and cut time (`debug/time/warp`) **while** a track
  interpolates `pose/position` — the composable case, and the reason §4.1 exists.
- Writing a channel a shot *is* driving is accepted but superseded on the next frame. Honest and
  self-explanatory; no error, no hidden lock.
- `pose/reset` clears live overrides; `camera/release` clears everything and restores.

### 4.4 Track format (L3)

JSON, uploaded into `track/` exactly like an audio clip (streamed upload, committed on clunk /
`complete=1`, capped, never touches disk).

```jsonc
{
  "loop": false,
  "defaults": { "frame": "cce", "anchor": "vessel:apollo11", "ease": "in-out" },
  "shots": [
    {
      "name": "pad-rise",
      "t": 0.0, "duration": 8.0,
      "anchor": "body:earth",
      "blend_in": 0.5,                          // eased cross-fade from the previous pose

      "position": {
        "mode": "cartesian",                    // cartesian | orbit | attach
        "curve": "catmull-rom",                 // step | linear | catmull-rom (centripetal) | bezier
        "frame": "bodyfixed",
        "keys": [
          { "t": 0.0, "v": [-40, 8, 12], "ease": "out", "ease_power": 3 },
          { "t": 4.0, "v": [-18, 14,  6] },
          { "t": 8.0, "v": [ -6, 22,  2] }
        ]
      },

      "aim": {
        "target": "vessel:apollo11",
        "offset": [0, 1.2, 0], "frame": "bodyfixed",
        "up": "world",
        "roll": { "keys": [ {"t":0,"v":0}, {"t":8,"v":-6,"ease":"in-out"} ] }
      },

      "fov":  { "keys": [ {"t":0,"v":42}, {"t":8,"v":24,"ease":"out"} ] },
      "time": { "keys": [ {"t":0,"v":1.0}, {"t":6,"v":0.15,"ease":"in-out"} ] }
    }
  ]
}
```

- **Every channel name here is a `pose/` leaf name** — that correspondence is deliberate and must be
  maintained (a new track channel ⇒ a new leaf, same work item).
- **`"mode": "orbit"`** replaces cartesian `keys` with `radius` / `azimuth` / `elevation` channels —
  spherical coordinates about the anchor, the granular twin of `pose/orbit/*`. "Circle the ship while
  pushing in" becomes three scalar curves. This is what unscience's `Orbit`/`SpiralZoom` animations
  are, generalized.
- **`"mode": "attach"`** = a rigid offset in a frame — the locked-off tripod and the bolted-on chase cam.
- **Centripetal** Catmull-Rom, not uniform: unevenly spaced keys must not cusp or overshoot.
- `rotation` (quaternion keys, slerp/**squad**) is accepted instead of `aim`; `aim` wins if both.
- Easing accepts named forms (`linear|in|out|in-out` + `ease_power`, the unscience shape) **and**
  explicit cubic-Bézier handles `"ease": [x1,y1,x2,y2]`.
- The **`time`** channel writes `Universe.SetSimulationSpeed(v, alert: false)` — `0` pauses, `0.15`
  is slow-mo, `>1` warps. Requires `debug_namespace`; ignored with a warning otherwise.
- The playback clock is **wall-clock `dtPlayer`**, never sim time — a slow-mo shot keeps moving the
  camera at real speed. Same `render` clock as the scheduler default, and the same caveat (§3.2).

---

## 5. Runtime architecture

### 5.1 The hook — and the `DrawUI` defect it fixes

Verified frame order in `Program.OnFrame` (`KSA/Program.cs:1965-2059`):

```
PrepareFrame                       solvers, input events
PrepareImGui / OnFrameEditor
OnFrameViewports(dt)          ──►  controller.OnFrame(); Camera.OnFrame() builds _vp + frustum
if (DrawUI) {
    OnDrawUiFrame(dt)         ──►  [StarMapBeforeGui]  = gatOS OnBeforeUi        ◄── F2-gated!
    OnDrawUiViewports(dt)     ──►  [StarMapAfterGui]   = gatOS OnAfterUi         ◄── F2-gated!
}
ImGui.Render()
OnFrameHoveredOrbiters / LightSystem.OnFrame / Cursor.UpdateInputRay
OnFrameCelestials(dt)              NearbyCelestial, altitude, planet-renderer LOD
OnPreRender / Render(dt)           UpdateShaderData reads camera.MVP + PositionEcl
    └─ RenderGame
PostRender ; FrameNumber++
                              ──►  [StarMapAfterOnFrame] = postfix on Program.OnFrame  ◄── ALWAYS runs
```

`F2` toggles `Program.DrawUI` (`KSA/Input.cs:317` → `Program.cs:1596`). Both StarMap GUI hooks live
inside `if (DrawUI)`, so with the UI hidden gatOS's telemetry sampler, command drain, welds driver,
IVA physics driver, audio tick and thug_life updater **all stop**. Pre-existing defect, not a camera
one — but a director hides the UI, and a *scheduler* that silently stops is worse than one that never
existed.

**Cadence, verified:** `OnDrawUiFrame` and `OnDrawUiViewports` are `private` with **exactly one call
site each**, both in `Program.OnFrame`; and `KSA/App.cs`'s `Run()` is a plain `while` loop calling
`OnFrame` once per iteration, with `Program.OnFrame` containing `Render`/`PostRender` and the engine
*defining* `FramesPerSecond` as calls-per-second. So the GUI hooks fire **exactly once per rendered
frame** — no fixed-timestep sub-loop, no decoupled update/render. (Contrast: a Harmony prefix on
`Controller.OnFrame` fires up to **4×** per frame, once per viewport — §11.9.)

`StarMapAfterOnFrameAttribute` is confirmed present in the exact `StarMap.API 0.3.6` gatOS
references, and `ProgramPatcher` postfixes `Program.OnFrame` unconditionally.

**Task C0.1** adds `[StarMapAfterOnFrame] Mod.OnAfterFrame(double, double)` and re-runs the per-frame
partials from there, guarded by a `Program.FrameNumber` "already ran this frame" latch. UI visible ⇒
behaviour bit-for-bit unchanged; UI hidden ⇒ everything keeps ticking. **Both** the scheduler and
the camera director depend on it.

### 5.2 Why writing after the render is correct

Writing the pose at the end of frame *N* means frame *N+1* does:

`OnFrameViewports` → `FixedController.OnFrame` (**no-op**, below) → `Camera.OnFrame(dt)` rebuilds
`_vp` / `_vpInv` / frustum planes **from gatOS's transform** → celestial LOD, lights, cursor ray,
`UpdateShaderData` and `RenderGame` all consume it consistently.

gatOS never touches a matrix, never calls `camera.OnFrame` by hand, and needs no patch. The cost is
one frame of pipeline latency — **constant**, therefore invisible.

**Ownership** (`enabled 1`):

1. Snapshot restore state: `Viewport.Mode`, `Camera.Following` + `TidalLocking`, `GetFieldOfView()`
   (radians → degrees), `Camera.NoRotation`, `LocalPosition`, `LocalRotation`, and
   `Universe.GetSimulationSpeed()` if the `time` channel is used.
2. `Viewport.Mode = CameraMode.Fixed`, **assigned directly** rather than via `SetCameraMode` —
   `FixedController.OnSwitchOn` fires `TimedAlert.Create("Fixed Camera", …)`, which would appear in
   the footage. **Exception:** leaving `CameraMode.Map` *must* go through `SetCameraMode` (or clear
   the flag explicitly), because `MapController.OnSwitchOff` is what resets `Camera.NoRotation`.
3. `Camera.Unfollow(changeControl: false)` — `changeControl: true` would null
   `Program.ControlledVehicle` and drop the player's vessel mid-flight.

With `Following == null`, `FixedController.OnFrame` early-outs on its first line and writes nothing.
The game's camera solver is fully inert; gatOS is the sole writer. `FixedController.OnKey` /
`OnCursorEnter` / `OnGamepadConnected` all return `false`, so player input cannot perturb it either.

**The trade of unfollowing** (stated honestly): `Camera.GetPositionEgo` special-cases
`astronomical == _following` (exact `-PositionCce`) and has a physics-bubble path for co-bubble
vehicles. Unfollowed, every object takes the plain `GetPositionEcl() - PositionEcl` path. Double
precision at 1 AU is ~2 × 10⁻⁵ m so absolute precision is a non-issue, and because *every* object now
takes the *same* path the result is self-consistent — which is what matters visually. The open
question is whether a co-bubble vessel's orbit-propagated `GetPositionEcl()` differs measurably from
its `KinematicStates.PositionPhys` at close range. **This is a VALIDATION item (§9), not an
assumption**; if it shows, the fix is to reproduce the bubble composition in `CameraTargets`.

### 5.3 Release

`enabled 0` / `release` / `Mod.TeardownGameCheats` funnel through one restore:

- `SetFollow(saved, savedTidal, changeControl: false, alert: false)` — `alert: false` keeps
  "Following X" off screen; note `SetFollow` **teleports** the camera to
  `target + 2.5 × MeanRadius × forward`, so order matters: follow first, then transform.
- restore `LocalPosition` / `LocalRotation` / FOV / ortho / `NoRotation`, then `Viewport.Mode`.
- restore sim speed if the `time` channel drove it.
- optional eased blend-back over `camera_release_blend_s` (the unscience return-to-start idea, done
  properly) so the hand-off is not a cut.

Prune on vessel despawn by riding the sampler's vehicle enumeration, like `VesselForceRender.Prune`.

### 5.4 Threading

Two new game-thread work sites, both self-gating:

- **`Mod.TickSchedules`** — inside the (F2-proof) command drain, immediately before
  `CommandQueue.Drain`. `Post`s due commands with no waiter. No-ops when no schedule is live.
- **`Mod.DriveCamera`** — in `OnAfterFrame`. No-ops while gatOS does not own the camera (the
  default), so the feature costs nothing until a guest turns it on.

No Harmony patch for either. Transport threads only ever *enqueue* `SimCommand`s and read
volatile-published status. Teardown rides `Mod.TeardownGameCheats`. This is the `DriveWelds` /
`DriveIvaPhysics` shape exactly.

---

## 6. Code layout

### 6.1 Game-free — `gatOS.SimFs/` (unit-tested, no KSA)

Follows the `Iva/CabinPhysics.cs` precedent: the maths lives outside the game-coupled half.

**Scheduler (`Commands/`)** — generic, no camera dependency:

| File | Contents |
|---|---|
| **`PlaybackClock.cs`** | **the one timeline primitive** (§3.4): clock base (`render`/`wall`/`ut`), `rate`, `loop`, `scrub`, `pause`, `state`. Shared by the scheduler **and** the camera track player — resolved Q4. |
| `TimedBatchFile.cs` | the write handle + grammar (`BatchFile` sibling): directives, offsets, validation |
| `Schedule.cs` | the immutable committed schedule: sorted `(deadlineMs, SimCommand)[]` + options |
| `Scheduler.cs` | dispatch over a `PlaybackClock`: `Tick() → due commands`, archetype-derived coalescing |
| `ScheduleStore.cs` | live-schedule registry + volatile status publish (the `AudioStore` role) |
| `ScheduleTree.cs` | the `/sim/ctl/schedules/` registry dir; also renders **track players** (§3.4) |

**Camera (`Camera/`)** — typed, camera-only:

| File | Contents |
|---|---|
| `CameraTrack.cs` | records: `Track`, `Shot`, `Channel<T>`, `Key<T>`, `CurveKind`, `EaseSpec`, `PositionMode`, `AimSpec`, `FrameKind`, `TargetRef` |
| `TrackParser.cs` | JSON → `Track`, full validation + caps, precise EINVAL messages |
| `Splines.cs` | centripetal Catmull-Rom, cubic Bézier, slerp, squad |
| `Easing.cs` | named eases (+ powers) and cubic-Bézier handles |
| `TrackEvaluator.cs` | `Sample(track, t) → CameraSample` |
| `CameraSample.cs` | evaluated frame: position + `FrameKind` + anchor, aim-or-rotation, fov, roll, ortho, optional time-scale |
| `Playback.cs` | clock + state machine: play/pause/scrub/rate/loop, shot selection, blend-in |
| `PoseSmoother.cs` | critically-damped spring (mirrors KSA `MathEx.SpringInterp`) |
| `CameraState.cs` | **the compositing model of §4.3** — committed ← override ← track |
| `CameraStore.cs` | uploaded tracks + live overrides + volatile-published status |
| `CameraDirectory.cs` | writable `track/` dir + upload handles (the `AudioDirectory` role) |
| `CameraCommands.cs` | the line grammars (`play`/`set`/`aim`/`geo`/`position`) → `SimCommand` |

### 6.2 KSA-coupled — `gatOS.GameMod/Game/Ksa/Camera/`

Every member touching a KSA type carries `[KsaAnchor]`.

| File | Contents |
|---|---|
| `CameraDirector.cs` | per-frame driver: evaluate → resolve frame → write pose; ownership take/release; restore capture |
| `CameraFrames.cs` | `ecl`/`cce`/`bodyfixed`/`enu`/`lvlh`/`chase` resolution + the `geo` arithmetic |
| `CameraTargets.cs` | `vessel:` / `body:` / `part:` → live position + frame basis (reuses the weld anchor resolver) |
| `CameraActuator.cs` | **extend** the existing file; `camera.focus` stays |
| `CameraReader.cs` | samples live camera state for read-back leaves |

The scheduler needs **no** `Game/Ksa/` code at all — it dispatches through the existing executor.

### 6.3 KSA binding table (new `[KsaAnchor]`s)

| Member | Kind | Decomp @5168 | Risk |
|---|---|---|---|
| `Program.MainViewport` / `GetMainCamera()` | property | `Program.cs:437`, `:543` | Medium (already bound) |
| `Viewport.Mode` (`CameraMode`) | **public field** | `Viewport.cs:14` | Medium |
| `Viewport.SetCameraMode(CameraMode)` | method | `Viewport.cs:335` | Medium |
| `Viewport.GetCamera()` | method | `Viewport.cs:326` | Medium — returns `MapCamera` in Map mode |
| `Viewport.BaseCamera` / `MapCamera` | public fields | `Viewport.cs:38,40` | Medium |
| `Transform3D.LocalPosition` / `LocalRotation` | **public fields** | `Transform3D.cs:9,13` | Medium |
| `Camera.PositionEcl` / `PositionCce` | virtual props | `Camera.cs:110`, `:81` | Medium |
| `Camera.NoRotation` | public field | `Camera.cs:51` | Medium |
| `Camera.LookAtRotation(double3,double3)` | static | `Camera.cs:198` | Low |
| `Camera.SetFieldOfView(float deg)` / `GetFieldOfView() → rad` | methods | `Camera.cs:420`, `:782` | Medium — **unit asymmetry** |
| `Camera.SetOrthographic(bool)` / `SetOrthoHalfHeight(float)` | methods | `Camera.cs:426`, `:435` | Low |
| `Camera.SetFollow(...)` / `Unfollow(bool)` / `Following` / `TidalLocking` | methods/props | `Camera.cs:605,623,158,160` | Medium |
| `IFollowable.GetBodyFixed2Ecl()` / `IPosition.GetPositionEcl()` | interface | `IFollowable.cs`, `IPosition.cs:7` | Low |
| `Celestial.GetSurfacePositionEclFromDirCce` / `GetTerrainHeightFromDirCce` | methods | `Celestial.cs` | Medium (`geo`) |
| `Vehicle.GetEnu2Cce()` / `GetLvlh2Cce()` / `Body2Cce` | methods/prop | `Vehicle.cs` | Low (already read) |
| `Celestial.GetCcf2Cce()` / `GetCci2Cce()` | methods | `Celestial.cs` | Low |
| `Universe.SetSimulationSpeed(double, alert:false)` | static | `Universe.cs:1998` | Medium (**already bound** by `debug.warp`) |
| `Program.FrameNumber` | `public static ulong` | `Program.cs:268` | Low (C0.1 latch) |
| `CameraMode` enum | enum | `CameraMode.cs:5` | Low |

**Zero Harmony patches.** Compare unscience: 2 string-named prefixes + 1 private-field poke.

---

## 7. Phased tasks

### C0 — foundation

- **C0.1** `[StarMapAfterOnFrame] Mod.OnAfterFrame(double, double)` + the `Program.FrameNumber`
  once-per-frame latch; route the per-frame partials through it so gatOS keeps working with the UI
  hidden. Regression: unchanged behaviour when `DrawUI` is true. **Fixes a live defect independent
  of this feature, and both S and C depend on it.**
- **C0.2** `[camera]` + `[schedule]` config sections + `gatos.default.toml` blocks (§8).

### S — the generic scheduler *(independently shippable; no camera dependency)*

- **S.0** **`PlaybackClock`** — the shared timeline primitive (§3.4), built first because C3 depends
  on it: clock bases, rate, loop, scrub, pause, state, shared-clock groups. Game-free + unit tested.
- **S.1** `Scheduler` + `Schedule` over that clock + unit tests: ordering, stability at equal
  deadlines, archetype-derived coalescing, catch-up under a simulated hitch.
- **S.2** `CommandQueue.Post` / `PostBatch` (fire-and-forget submit, no TCS) and
  `ICommandSink.SubmitScheduleAsync`.
- **S.3** `TimedBatchFile` grammar + `@` directives + up-front validation.
- **S.4** `ScheduleStore` + `/sim/ctl/schedules/` registry tree + transport leaves + events.
- **S.5** `Mod.TickSchedules` in the F2-proof drain; the three clock bases; `dropped` accounting.

### C1 — ownership + live pose *(R2, R3, R4)*

- **C1.1** `CameraDirector` skeleton: ownership take/release, restore capture, `Mode`/`Unfollow`
  park, `Mod.DriveCamera` in `OnAfterFrame`, teardown in `TeardownGameCheats`.
- **C1.2** `/sim/camera/{enabled,release,status,info,mode,follow,tidal,target}` + actuators.
- **C1.3** `pose/{position,frame,anchor,rotation,fov,ortho,ortho_height,smoothing,reset}` +
  `CameraFrames` (`ecl`/`cce`/`bodyfixed`); `CameraState` compositing (§4.3).
- **C1.4** Fix R4's latent gap: `SetFollow` on **both** `BaseCamera` and `MapCamera` so map view
  does not desync (§11.1). Apply to the existing `camera.focus` too.

### C2 — aim + geo *(R6, R5)*

- **C2.1** `CameraTargets`: `vessel:` / `body:` / `part:` resolution.
- **C2.2** `pose/{aim,aim_target,aim_offset,aim_frame,aim_up,roll}`; remaining frames
  (`enu`, `lvlh`, `chase`).
- **C2.3** `pose/geo` + `pose/orbit/{radius,azimuth,elevation}`.
- **Milestone:** at this point **the full camera is drivable from `timed_batch` alone** — L3 is
  still unbuilt, and every requirement except perfectly-smooth interpolation is met. Good place to
  shoot a test sequence and validate before committing to L3.

### C3 — the track evaluator *(R1)*

- **C3.1** `CameraStore` + writable `track/` dir (upload/commit/caps, `AudioDirectory` shape).
- **C3.2** `TrackParser` + `play` / `set` / `stop` / `playback`. The player is a **`PlaybackClock`
  consumer registered in `/sim/ctl/schedules/`** (`kind = camera-track`), so it inherits the S
  transport verbatim and can join a shared-clock group with a schedule (§3.4).
- **C3.3** Wire the evaluator into the compositor; blend-in; `mode: "orbit"` / `"attach"`.
- **C3.4** `camera.shot` / `camera.finished` events.

### C4 — time channel

- **C4.1** `time` channel → `Universe.SetSimulationSpeed(v, alert: false)`, gated on
  `debug_namespace`; restore on release; `Universe.IsAutoWarpActive` interaction documented.
  (Note: `debug/time/warp` is *already* schedulable via S — C4 only adds the interpolated channel.)

### C5 — extra camera contexts *(IVA + Map)*

- **C5.1** IVA: park `Mode = IVA` with gatOS still writing the transform (the seat-pin and cone-clamp
  are bypassed because gatOS writes last). Pairs with the existing `always_render_iva` cheat.
- **C5.2** Map: honour `Camera.NoRotation`, drive `MapCamera`, expose `MapController.Scope`.
- **C5.3** Re-check the bubble-relative ego question (§5.2); implement bubble composition in
  `CameraTargets` if validation shows drift.

### C6 — docs, SDK, tutorial

- **C6.1** Docs lockstep (§10) — **same work item as each phase**, not a trailing task.
- **C6.2** `examples/` reference program (a shot builder emitting **both** a `timed_batch` and a
  JSON track from one description) + a `site/guides/` tutorial via the `tutorials` skill.
- **C6.3** Fix the two stale docs in §11.3.

---

## 8. Config

### `[camera]`

| key | default | meaning |
|---|---|---|
| `camera_enabled` | `true` | master; `false` removes `/sim/camera` entirely (SPEC stays truthful) |
| `camera_max_tracks` | `32` | uploaded track count cap |
| `camera_max_track_bytes` | `1048576` | per-track cap |
| `camera_max_total_bytes` | `8388608` | total track memory cap |
| `camera_max_keys` | `4096` | keys per channel |
| `camera_fov_min` / `camera_fov_max` | `1` / `179` | deliberately **wider** than the game's 15/120 — `SetFieldOfView` is unclamped, so fisheye/telephoto are available (the `glass` precedent) |
| `camera_release_blend_s` | `0.6` | default eased hand-back (game default `CameraJumpTime` is 0.6) |
| `camera_allow_time_channel` | `true` | additionally requires `[control] debug_namespace` |

### `[schedule]`

| key | default | meaning |
|---|---|---|
| `schedule_enabled` | `true` | master; `false` removes `ctl/timed_batch` + `ctl/schedules/` |
| `schedule_max_live` | `16` | concurrent live schedules |
| `schedule_max_entries` | `8192` | entries per schedule |
| `schedule_max_bytes` | `1048576` | per-schedule payload cap |
| `schedule_default_clock` | `"render"` | `render` \| `wall` \| `ut` |

Per AGENTS.md §8: property + XML doc in `GatOsConfig.cs`, a row in its `Sections` table,
clamp-and-warn on load, and a hand-synced block in `Configuration/gatos.default.toml`.

---

## 9. Tests & validation

**Unit — scheduler (game-free):** deadline ordering and **stability at equal offsets**; coalescing
picks the last state-control per path while executing *every* trigger, preserving cross-path order;
catch-up under a simulated 500 ms hitch stays within `max_commands_per_frame` after coalescing;
loop/rate/scrub arithmetic; the three clock bases advance independently; **shared-clock groups —
`pause`/`scrub`/`rate` on any member moves every member, and a track player grouped with a schedule
stays sample-aligned across a scrub**; zero allocation on a tick with nothing due (assert via the
existing `PerfStat` tripwire pattern).

**Unit — camera (game-free):** easing monotonicity and exactly 1.0 at `t = duration`; centripetal
Catmull-Rom passes through its keys and does not overshoot on uneven spacing; slerp/squad continuity;
playback state machine; `TrackParser` accept/reject matrix incl. every cap; `PoseSmoother`
convergence without overshoot; **the §4.3 compositing precedence**, including a track and a live
override on disjoint channels; determinism — the same `t` always yields the same sample.

**Tree (`ScheduleTreeTests`, `CameraTreeTests` — standard 9p-client fixture + `FakeCommandSink`):**
every leaf's archetype; errno matrix (ENOENT / EINVAL / EACCES / EOPNOTSUPP); read-back after write;
`camera_enabled=false` / `schedule_enabled=false` removal; **`timed_batch` reaching every camera leaf**;
`ctl/batch` reaching camera leaves; HTTP/MQTT field-mirror parity (free by construction via
`VfsScan`, but assert it).

**In-game (`docs/VALIDATION.md`, new `## camera` and `## schedules` sections, both **NOT YET RUN**):**
ownership + release round-trip leaves the game camera exactly as found; **F2 (hide UI) does not stall
the director, the scheduler or the command drain**; a `timed_batch`-driven sequence fires on time
under load; a scripted flyby is judder-free at 60 fps; `bodyfixed` placement rides a rotating planet;
`pose/geo` ocean skim down to the `ClampCamera` floor (0.5 m) and a cloud-deck fly-through;
aim-with-offset holds a kittenaut's head while it walks; **kitten EVA locomotion re-check** (§11.4);
FOV beyond 15–120; ortho; map/IVA coverage; time channel pause/slow-mo and restore; **the
bubble-relative ego question of §5.2**; no `TimedAlert` text appears in footage.

---

## 10. Docs lockstep (AGENTS.md §9 — MUST, same work item)

| Artifact | What |
|---|---|
| `SPEC_9P_FILESYSTEM.md` | §3.x **two** new families: `/sim/ctl/timed_batch` + `/sim/ctl/schedules/` (grammar, clocks, coalescing, caps, errnos) and `/sim/camera/` (leaves, frames, compositing, track schema); §5.1 one row per new action key; §2.5 the `[camera]` + `[schedule]` gates |
| `docs/KSA_INTEGRATION_MATRIX.md` | its own `##` block — the §6.3 binding table (scheduler adds none) |
| `scope/FULL_SCOPE.md` | §2 feature-inventory rows (camera **and** scheduler); §3 coupling census for `Game/Ksa/Camera/**` |
| `scope/ksa-write-surface.md` | a `{#camera-director}` section; extend the existing camera-focus rows |
| `scope/ksa-read-surface.md` | the camera-state read binding |
| `scope/ksa-runtime-coupling.md` | `###` entries for the `OnAfterFrame` driver, the scheduler tick, **and** the C0.1 hook change |
| `scope/non-ksa-surface.md` | the scheduler (game-free — it belongs here, not in the KSA pages) |
| `CLAUDE.md` | status-table rows; **threading-rules paragraph — two new game-thread work sites**; the `DrawUI` fix |
| `docs/VALIDATION.md` | `## camera` and `## schedules` — **NOT YET RUN** (§9) |
| `docs/MILESTONES.md` | as-built detail |
| `.claude/skills/gatos/` + `docs/TUTORIAL_DATA_REFERENCE.md` | recipes: timed sequences, shot authoring, frames, aim-with-offset |
| `.claude/skills/ksa/camera.md`, `.claude/skills/harmony/SKILL.md` | **correct the stale `___Transform` / `OnFrame(double)` claims** |

---

## 11. Hazards (all verified in the 5168 decomp)

1. **Two `Camera` instances per viewport.** `Viewport.GetCamera()` returns `MapCamera` in Map mode,
   `BaseCamera` otherwise. The game itself sets **both** (`InputEvents.cs:759-760`, with
   `alert: false`). gatOS's existing `camera.focus` sets only one — a real latent bug, fixed in **C1.4**.
2. **FOV unit asymmetry.** `SetFieldOfView` takes **degrees** and does **not** clamp;
   `GetFieldOfView()` returns **radians**; `ChangeFieldOfView` takes a degree delta and clamps
   `[15,120]`. Encode units in the helper names.
3. **Two stale docs will mislead an implementer.** `.claude/skills/ksa/camera.md` documents
   `OnFrame(double)` and a `___Transform` Harmony field injector that **has never existed**;
   `.claude/skills/harmony/SKILL.md:199` repeats it. Fix in **C6.3**. (`Controller.OnFrame` is
   `(Viewport, double)`; the camera is the public field `Controller.Camera`.)
4. **Overriding the main camera changes where kittens walk.** `KittenEva.PrepareWorker` feeds
   `Program.GetMainCamera().GetForwardEcl()/GetRightEcl()/GetUpEcl()` into EVA locomotion
   (`KittenEva.cs:108-116`). Document loudly; consider auto-releasing EVA control while the director
   owns the camera.
5. **`Camera.ClampCamera()`** pushes the camera to *surface + 0.5 m* below `MINIMUM_ALTITUDE`, using
   the **previous** frame's `NearbyCelestial`/altitude. It is the ocean-skimming floor — a feature —
   but altitude requests below 0.5 m are silently corrected. `NearbyCelestial` also nulls beyond
   80 000 km of surface, which disables the planet renderer's per-frame LOD update.
6. **`TimedAlert` pollution.** `FixedController.OnSwitchOn` ("Fixed Camera"), `Camera.AlertFollowing`
   ("Following X") and `SetSimulationSpeed`'s speed alert all draw on screen. Use direct `Mode`
   assignment and `alert: false` everywhere.
7. **`Unfollow()`'s default `changeControl: true`** nulls `Program.ControlledVehicle` — always pass
   `false`.
8. **`SetFollow` teleports** the camera to `target + 2.5 × MeanRadius × forward`. Any follow change
   must be followed by a pose re-assert in the same frame.
9. **`Viewport.OnFrame` runs on all 4 viewports**, including the offscreen thumbnail one
   (`Program.ViewportCount = 4`), before the `Visible` check — which is why a Harmony prefix on
   `Controller.OnFrame` fires up to 4×/frame. Bind the main viewport explicitly; never touch index 1.
   unscience's "first controller wins" guard freezes the other viewports — do not port that bug.
10. **`MapController.OnSwitchOn/Off` toggles `Camera.NoRotation`**, which changes the meaning of
    `PositionCce` and `LocalPosition`. Mode transitions involving Map must go through
    `SetCameraMode` or clear the flag explicitly.
11. **`_fovRadians`, `_vp`, `_vpInv`, `_following` are private.** Do not follow `glass` into
    reflection — `SetFieldOfView` is public and sufficient.
12. `RenderCore.Input.Controllers.{FlyController,OrbitController}` are **decoys** from the Planet
    Demo tool, float-precision and unrelated. Bind by fully-qualified `KSA.*` types.
13. **`dtPlayer` is clamped** (`min(realDelta, 1/MinTargetFrameRate)`, `KSA/App.cs`), so the default
    `render` clock **lags true wall time after any hitch and never catches up**. Correct for
    footage, wrong for syncing to a host recorder — hence the explicit `wall` clock base.
14. **Scheduler budget.** Due entries share `max_commands_per_frame` (default 64) with the ordinary
    queue drain. Archetype coalescing bounds the realistic count by *distinct leaves*, not entries,
    but the interaction must be measured, and `dropped` must never be silent.

---

## 12. Open questions

1. **Multi-viewport.** v1 binds the main viewport only. Is a "B camera" (picture-in-picture, second
   monitor) ever wanted? Cheap to add later; the director is already viewport-parameterised.
2. **Authoring loop — hot reload?** Tracks and schedules are in-memory like audio clips, so the
   iteration loop is *edit → reload → play*. Host-side editing already works with **zero new code**,
   because `/mnt/<host-folder>` passthrough exists: point a mount at a real Windows folder, edit
   `flyby.json` in your editor, then
   `cp /mnt/shots/flyby.json /sim/camera/track/flyby && echo "flyby" > /sim/camera/play`.
   The only thing that buys nothing today is **auto-reload**: gatOS watching the host file and
   re-parsing on save, so the shot updates without touching the terminal (and, while playing,
   re-evaluating at the current `t` — a live-scrub editing loop).
   **Default: ship the `cp` loop, add the watcher only if it proves annoying in practice.**
   Separately, `.tb`/`.json` files under `/mnt/` are already durable — no qcow2 or save-profile
   persistence is needed for them at all.
3. **`SetFollow` during a shot.** Should a track be able to *change* the follow target mid-sequence
   (regaining the exact-ego path) at the cost of a re-assert, or is unfollowed-throughout always
   right? Resolve with the §9 bubble-drift validation.
4. ~~**Should L3 ride S's clock and transport?**~~ **RESOLVED — yes.** One `PlaybackClock`, one
   transport vocabulary, one registry, plus shared-clock groups. Specified in §3.4; sequenced as
   task **S.0** because C3 depends on it.
5. **Shake.** KSA has **no** shake system (repo-wide grep for `shake` returns nothing), so gatOS
   would author it as an aim/roll perturbation channel. In scope, or userland's job via `timed_batch`?
6. **Depth of field / motion blur / exposure.** The FX editors (`/sim/debug/{clouds,terrain,…}`) are
   already filesystems. Is there a post-processing surface worth exposing as "lens" channels?
