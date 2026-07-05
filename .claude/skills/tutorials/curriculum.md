# The tutorial ladder — beginner → advanced

A progressive series for the `guides/` section. Each rung introduces **one** new idea and builds only
on rungs before it, so a reader can climb from "the sim is a filesystem" to a closed-loop autopilot
without a gap. Author tutorials in this order (use `sidebar.order` to place them); when asked to write
"a tutorial about X", find X here first, confirm its prerequisites, and lift its goal / surface /
gotchas.

Data facts for every rung: [`docs/TUTORIAL_DATA_REFERENCE.md`](../../../docs/TUTORIAL_DATA_REFERENCE.md).
Reusable code: [`snippets.md`](snippets.md). Every actuating rung shows **both transports**.

## Concept primers (no code — read-me-first pages)

Some pages teach an *idea* rather than a program; they're the conceptual prerequisites the actuating
rungs lean on. They deliberately skip the dual-transport/code machinery (nothing is actuated).

| Slug (`guides/…`) | Teaches | Feeds | Status |
|---|---|---|---|
| `reference-frames` | KSA reference frames in plain language, focused on **CCI** (the frame gatOS I/O lives in); uses the KSA devs' diagrams (copied into `site/src/assets/frames/`) + one custom SVG for the "aim = −position" trick | the toolkit + every flight rung | **done** |

## Setup — the reusable toolkit (a pre-tutorial every later rung imports)

Before the actuating rungs, one **setup page** builds the shared Python toolkit the whole series
reuses, so later tutorials stay short (read → decide → write, plumbing already done). It's a real
(tiny) program, not a concept primer — it ends by reading a live CCI value and printing it to stderr.

| Slug (`guides/…`) | Builds | Feeds | Status |
|---|---|---|---|
| `gatos-io` (**"Basic gatOS I/O Toolkit"**) | two importable modules in `~/tutorials/`: **`gatos_io.py`** (`read`/`read_scalar`/`read_vec`/`read_quat`/`write`/`write_vec`/`write_nums` over `/sim`, fully type-hinted with `Vec3`/`Quat` aliases) and **`gatos_frames.py`** (`cross`/`dot`/`norm`/`unit`/`neg`/`scale`/`add`/`sub` + the verbatim `from_rows`/`body_to_cci` quaternion) | every flight rung from `point-at-parent` on | **done** |

**The module split is the convention now:** published tutorials `from gatos_io import …` /
`from gatos_frames import …` rather than re-pasting I/O and quaternion code. The two files mirror the
Python half of [`snippets.md`](snippets.md) (still the canonical source of the helper text) — split
into an **I/O** module and a **frames/math** module. When you write a new rung, import the toolkit;
only paste a helper inline if it's genuinely new, then also add it to the relevant module here and in
`snippets.md`. Keep the code lean (short single-line comments, **no docstrings** — these are run in a
terminal/`vi`, not a big IDE); put the real explanation in the page prose.

## The ladder at a glance

| # | Slug (`guides/…`) | Teaches (one new idea) | Builds on | Status |
|---|---|---|---|---|
| 0 | `hello-sim` | the sim *is* a filesystem; read a value two ways | — | to write |
| 1 | `read-telemetry` | scalar files vs the atomic `telemetry` doc; parse it | 0 | to write |
| 2 | `throttle-and-ignite` | first writes: actuate + read the errno | 1 | to write |
| 3 | `staging-and-modules` | one-shots, master toggles, per-module files | 2 | to write |
| 4 | `attitude-modes` | the onboard flight computer via **named modes** (no math) | 2 | to write |
| 5 | `vessel-control-point-at-parent` | CCI frame + custom **Body→CCI quaternion**, via the toolkit | `gatos-io`, `reference-frames` | **done** |
| 6 | `wait-in-sim-time` | pace in **sim time**, not wall time | 1 | to write |
| 7 | `hold-a-lock` | the **control loop** + gating (pause/warp/stale) | 5, 6 | to write |
| 8 | `orbital-math` | constants + the vis-viva / circular-orbit cheat-sheet | 1 | to write |
| 9 | `schedule-a-burn` | **maneuver nodes** — `ctl/burn` with a CCI Δv | 4, 8 | to write |
| 10 | `teleport-into-orbit` | set up state with `debug/teleport` (CCI state vector) — an argparse program placing an N-vessel formation into a circular/eccentric orbit | `gatos-io`, `reference-frames` | **done** |
| 10b | `searchlight-track-a-vessel` | the **continuous control loop** (read → gate → aim → pace, runs until Ctrl-C): track a target vessel with a computed attitude + a spotlight aimed by a **calibrated** animation goal | 5, 10 (staging); teaches pacing/gating inline until 6/7 exist | **done** |
| 10c | `eva-taxi-to-a-part` | **part→world geometry** (`part_world = pos + transform(seat − com, att_q)`) + **RCS translation flight** (`ctl/translate` bang-bang latching jets; velocity-matching `dv = v_des − v_rel` law → emergent flip-and-burn) | 10b (the loop), `gatos-io` (`transform` addition) | **done** |
| 10d | `punch-it` | the **impulse cheat** (`debug/vessels/<id>/impulse`) + the **body-frame `body` keyword** doing the rotation for you: map a human word (front/back/left/right/top/bottom) → a body-axis unit push; N·s (Δv=J/m) vs `dv` m/s | `gatos-io` (`write`); body-frame idea from 5/10c | **done** |
| 11 | `react-to-events` | event-driven control (`/sim/events`, `grep -m1`, SSE) | 2 | to write |
| 12 | `closed-loop-guidance` | full autopilot architecture (pure core, ENU, abort) | 7, 9 | capstone |

---

## Per-rung detail

### 0. `hello-sim` — the simulation is a filesystem
- **Goal:** the reader `cat`s a live value and `GET`s the same value over HTTP, and *gets* that there's
  no API to learn beyond files.
- **Surface:** `time/ut`, `system/name`, `vessels/active/id`, `bodies/Earth/radius`. `ls /sim`.
- **New idea:** the mental model + the two transports (in-guest `cat` vs host `GET /v1/fs/…`).
- **Both transports:** yes — this is where you establish the synced-tabs convention.
- **Gotchas:** a value is text + `\n`; `vessels/active/id` is `ENOENT` when nothing is controlled.

### 1. `read-telemetry` — read everything at once
- **Goal:** print a one-line orbit summary of the active vessel.
- **Surface:** `vessels/active/telemetry` (atomic doc, §4) vs the scalar files `position/cci`,
  `mass/total`, `orbit/*`.
- **New idea:** scalar files (clear, one-off) vs the atomic doc (self-consistent — the right read for a
  loop); JSON parsing; `sit`/`parent`/`controllable`.
- **Gotchas:** guard non-finite/absent fields; `controllable == 0` matters later.

### 2. `throttle-and-ignite` — your first writes
- **Goal:** throttle up and light the engine, then shut down.
- **Surface:** `ctl/throttle`, `ctl/ignite`, `ctl/shutdown`, `ctl/engine`. The `vessels/active/` alias.
- **New idea:** a write **actuates** and returns a real errno; setpoint (`throttle`, read back) vs
  one-shot (`ignite`); mirror live state with `ctl/engine`. Frame-phase = immediate.
- **Both transports:** `echo … > …` vs `POST /v1/fs/…` / `POST /v1/command`.
- **Gotchas:** `EACCES` if `control_enabled=false` or not the active vessel; `controllable==0` silently
  ignores. Mirrors `examples/gogogo-rs`.

### 3. `staging-and-modules` — stage, lights, and per-module control
- **Goal:** stage, flip master lights/RCS, then toggle a single engine/light by index.
- **Surface:** `ctl/stage`, `ctl/lights`, `ctl/rcs`; `engines/<n>/active`, `lights/<n>/on`.
- **New idea:** modules appear only when fitted; index addressing; the fan-out concept (many writes in
  one tick — `examples/kecho`).
- **Gotchas:** one-shots return `EBUSY` if re-fired; a glob `echo >` is an ambiguous redirect.

### 4. `attitude-modes` — the flight computer, no math (**the "basic flight computer" rung**)
- **Goal:** hold prograde, then retrograde, then radial-in, and watch the vessel steer itself.
- **Surface:** `ctl/attitude_mode` (tokens, §3.4.18), `ctl/attitude_frame`.
- **New idea:** the setpoint is **onboard** — the autopilot steers and tracks, warp-correct, no
  quaternion. Reach for a named mode whenever the direction has a name.
- **Both transports:** `echo Prograde > …/ctl/attitude_mode` vs `POST /v1/fs/…`.
- **Gotchas:** **solver-phase latency** (~10 Hz, a tick late) — introduce it here; `manual` releases.

### 5. `vessel-control-point-at-parent` — a custom direction (**done**)
- **Goal:** aim the nose at the parent body with a computed quaternion.
- **Surface:** `position/cci`, `ctl/attitude_target`.
- **New idea:** the **CCI frame** (origin = body center → aim = `-position/cci`); the **Body→CCI
  quaternion** (body +X = thrust axis); why to use KSA's exact arithmetic, not a generic lib.
- **Note:** the page [`vessel-control-point-at-parent.mdx`](../../../site/src/content/docs/guides/vessel-control-point-at-parent.mdx)
  is the **model for the whole series' house style**. It now **imports the `gatos-io` toolkit**
  (`from gatos_io import read_vec, write_vec` / `from gatos_frames import neg, body_to_cci`), so the
  program is ~4 meaningful lines and the explanation lives in the prose. It opens with a
  prerequisites `<Steps>` linking `reference-frames` + `gatos-io`. Contrast with rung 4's shortcut
  (`RadialIn` does this for free) in the closing `:::tip`. (A future polish could add the HTTP
  transport tab; today it's in-guest Python to match the toolkit.)

### 6. `wait-in-sim-time` — pace correctly
- **Goal:** do something, wait 300 sim-seconds, do the next thing — correctly under warp/pause.
- **Surface:** `time/alarm` (write target, read parks) / `GET /v1/time/wait?until=`.
- **New idea:** sim time ≠ wall time; never `sleep(dt)`; key Δt off the change in `ut`.
- **Gotchas:** the ceiling is `sample_rate_hz` (default 10).

### 7. `hold-a-lock` — the control loop + gating
- **Goal:** turn rung 5's one-shot into a loop that re-reads and re-points every tick, safely.
- **Surface:** loop of `telemetry` read → compute → `ctl/attitude_target` write, paced by rung 6.
- **New idea:** the loop skeleton (read → gate → decide → actuate → pace); **gate** on paused
  (`sim_dt==0`), warping (`warp>1`), stale (`seq` unchanged); hold with no writes + a banner.
- **Gotchas:** run closed loops near 1× warp; named modes survive warp, your per-tick math doesn't.

### 8. `orbital-math` — the numbers
- **Goal:** compute circular speed and read the current orbit's apoapsis/periapsis/period.
- **Surface:** `bodies/<parent>/{mu,radius}`, `orbit/*`.
- **New idea:** μ/r² gravity (never 9.8); `v_circular=sqrt(μ/r)`; vis-viva; altitudes are above-surface
  (add radius); angles in degrees.
- **Gotchas:** mass is kg; read constants once.

### 9. `schedule-a-burn` — maneuver nodes
- **Goal:** circularize at apoapsis by scheduling a prograde burn.
- **Surface:** `ctl/burn = "ut dvx dvy dvz"`; `orbit/time_to_ap`, `time/ut`.
- **New idea:** an impulsive maneuver at a future sim time with a **CCI Δv**; onboard execution (no
  hand-flying); compute Δv from vis-viva.
- **Both transports:** `echo "$ut …" > ctl/burn` vs `POST /v1/command {action:"vessel.burn",values:[…]}`.
- **Gotchas:** solver-phase; Δv is a CCI vector, not a scalar.

### 10. `teleport-into-orbit` — set the stage (**done**)
- **Goal:** an `argparse` program that drops one *or a formation of* vessels into a circular **or
  eccentric** orbit of any body: `--parent`, repeatable `--vessel`, `--altitude` (periapsis),
  `--eccentricity`, `--true-anomaly`, `--spread` (along-track meters).
- **Surface:** `bodies/<id>/{mu,radius}`, `vessels/by-id/<id>/parent`, `debug/vessels/<id>/teleport`
  (CCI state `px py pz vx vy vz`).
- **New idea:** an orbit **is** a state vector (a point + a push); the perifocal→CCI equatorial state
  `r = p/(1+e·cosθ)`, `pos = (r cosθ, r sinθ, 0)`, `vel = (−k sinθ, k(e+cosθ), 0)`, `k=√(μ/p)`,
  `p = r_pe(1+e)` — which **collapses to the circular case at e=0** (one code path); `--altitude`
  anchors to **periapsis** (never buries the low point); along-track spacing via angle = meters ÷
  radius; teleport is about the **current parent** (validate `parent` first). Also teaches
  **compute-then-actuate**: split into a *plan* phase (all reads + math) and a bare *write* phase, so
  the N teleports fire back-to-back and land in one physics tick — interleaving a read between writes
  lets the sim tick and smears the formation at orbital speed.
- **Self-contained:** teaches the orbit math inline, so it does **not** depend on the (unwritten) rung
  8 `orbital-math`. Adds `write_nums` + `scale`/`add`/`sub` to the toolkit; page is
  [`teleport-into-orbit.mdx`](../../../site/src/content/docs/guides/teleport-into-orbit.mdx).
- **Gotchas:** needs `debug_namespace` (default on); keep apoapsis inside the parent's `soi`; also
  worked (host/TS, two-vessel circular) in `recipes.md §1`.

### 10b. `searchlight-track-a-vessel` — the continuous tracker (**done**)
- **Goal:** a program that *runs forever*: keep a source vessel's nose (and its spotlight) locked on
  a target vessel — recompute the aim every half sim-second, hold under pause/warp, clean up on
  Ctrl-C (attitude → `manual`, light off).
- **Surface:** `vessels/by-id/<id>/position/cci` ×2, `parent` (same-parent guard),
  `ctl/attitude_target`, `ctl/attitude_mode` (release), `lights/<n>/{on,outer_angle,goal,state}`,
  `time/{ut,alarm,sim_dt,warp}`.
- **New idea:** the **control loop** — read → gate → aim → pace — plus aim-between-vessels
  (`aim = sub(target_pos, source_pos)`; CCI positions share a frame only under the same parent) and
  the **calibration constant**: `lights/<n>/goal` sweeps the part's aim animation 0..1, but where in
  that sweep the beam parallels body +X is a fact about the part's model — a `--aim-goal` flag tuned
  by eye (poke `goal` from a shell, watch, bake the number in).
- **Self-contained:** rungs 6/7 are unwritten, so it teaches `sleep_sim` (alarm-file pacing) and the
  paused/warp gate inline (wall-clock nap while held, sim-time pacing while flying) — fold back into
  those rungs when they're authored. Page:
  [`searchlight-track-a-vessel.mdx`](../../../site/src/content/docs/guides/searchlight-track-a-vessel.mdx).
- **Gotchas:** attitude writes are solver-phase (~10 Hz — hold under warp rather than chase);
  `lights/<n>/goal` exists only when the light part carries an animation (catch `OSError` and carry
  on); light setup writes are frame-phase (instant); print telemetry to **stderr** so stdout stays
  pipeable.

### 10c. `eva-taxi-to-a-part` — the EVA part taxi (**done**)
- **Goal:** fly an EVA kitten (no main engine — backpack RCS only) to a standoff point beside a
  *named part* of a target vessel; hop the hull by re-running with another part; station-keep on
  arrival; hard cleanup on every exit path.
- **Surface:** `parts/<n>/{position,display_name}` (+ `--list` via `os.listdir`), `com`,
  `attitude/quat`, `position/cci`/`velocity/cci` ×2, **`ctl/translate`** (the RCS translation
  control this rung motivated — body-axis signs, bang-bang, **latches** until `0 0 0`),
  `ctl/attitude_target`, `ctl/rcs`, `ctl/attitude_mode` (release).
- **New ideas:** (1) **part→world**: `part_world = pos_cci + transform(seat − com, att_q)` — the
  assembly frame is the ship's "blueprint", `com` re-origins it, the live attitude quaternion
  carries it into CCI (the weld-engine math, and the reason `transform` joined `gatos_frames.py`);
  (2) **velocity-matching guidance** on bang-bang jets: `v_des = ê·min(v_max, k·d)`,
  `dv = v_des − v_rel`, point the nose along `dv` (gated on the *live* nose via
  `transform((1,0,0), att)`), thrust `1 0 0` when aligned — flip-and-burn emerges from the
  subtraction.
- **Gotchas:** `ctl/translate` **latches** — every exit path must write `0 0 0` (`try/finally`);
  thrust must be gated on the live attitude, not the setpoint; straight-line pathing can clip the
  hull (raise `--standoff`, or via-point around — left as the exercise); part indices are unstable
  (name-fragment matching; `telemetry_vessel_parts` gate); same-parent CCI guard. Page:
  [`eva-taxi-to-a-part.mdx`](../../../site/src/content/docs/guides/eva-taxi-to-a-part.mdx).

### 10d. `punch-it` — the impulse cheat, in the vessel's own frame (**done**)
- **Goal:** a tiny `argparse` program that shoves any vessel in a human direction —
  `front`/`back`/`left`/`right`/`top`/`bottom` — with a one-shot impulse of a given magnitude; no
  engine, no propellant, no aiming.
- **Surface:** `debug/vessels/<id>/impulse` (`x y z [cci|body] [ns|dv]`), written once via `write`.
- **New ideas:** (1) the **impulse cheat** — a one-shot velocity bump riding the teleport machinery
  (instant, non-physical, works on any vessel controlled-or-not, gate = `debug_namespace`); (2) the
  **`body` frame keyword does the rotation** — read the vector in the vessel body triad (aircraft-style
  **+X nose, +Y right, +Z down**) and the game carries it through the live attitude, so "left" is always
  the ship's left with *no* quaternion math (contrast eva-taxi 10c, which hand-rolls `transform`); (3)
  units: default **N·s** (Δv = J/m, same currency as docking pushoff) vs `dv` for direct m/s.
- **Self-contained:** teaches the body-triad convention inline (the one picture); only imports `write`.
  Adds no toolkit helpers. Page: [`punch-it.mdx`](../../../site/src/content/docs/guides/punch-it.mdx).
- **Gotchas:** `+Z` is **down** (up = −Z); frame-phase = one command/tick (batch via `/sim/ctl/batch`
  to kick a formation together, like the teleport batch); errnos `EACCES` (debug off) / `ENOENT` (gone)
  / `EINVAL` (bad vector/keyword). The direction=which-way-it-goes convention is a choice — negate to
  make it "punched *away from*" instead.

### 11. `react-to-events` — event-driven control
- **Goal:** wait for launch/flameout/SOI-change and act, without polling.
- **Surface:** `/sim/events` (`tail -f`, `grep -m1`) / `GET /v1/events` (SSE); per-vessel `stream`.
- **New idea:** discrete events as a coordination primitive; block on the next event cheaply.
- **Gotchas:** events honor `telemetry_events`; parse one JSON object per line.

### 12. `closed-loop-guidance` — capstone
- **Goal:** the architecture of a real autopilot (not a full G-FOLD — the *shape*).
- **Surface:** everything above, plus a local **ENU** frame and surface-relative velocity.
- **New idea:** a **pure `state → command` core** (host-testable, no `/sim` dependency); re-solve each
  tick (MPC); a state machine with an **abort** from anywhere; holds under warp/pause/stale.
- **Gotchas:** frames done once, correctly; KSA's exact quaternion; point at `examples/land-o-matic`
  and [`flight-programs.md §8`](../gatos/flight-programs.md) for the full treatment.

---

## Extending the ladder

New rungs slot **after their last prerequisite** — keep the one-new-idea rule. Candidate bonus rungs
once the core exists: RCS translation/docking, solar/power management, a live terminal dashboard
(reading `stream`), the `/sim/display` screen stream, and the cosmetic cheats (welds, `thug_life`).
When you add or reorder a rung, update the table above and re-check every "builds on" so a reader is
never sent an idea they haven't met.
