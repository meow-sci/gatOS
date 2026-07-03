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
| `frames` | KSA reference frames in plain language, focused on **CCI** (the frame gatOS I/O lives in); uses the KSA devs' diagrams (copied into `site/src/assets/frames/`) + one custom SVG for the "aim = −position" trick | rungs 4, 5, 7, 12 | **done** |

## The ladder at a glance

| # | Slug (`guides/…`) | Teaches (one new idea) | Builds on | Status |
|---|---|---|---|---|
| 0 | `hello-sim` | the sim *is* a filesystem; read a value two ways | — | to write |
| 1 | `read-telemetry` | scalar files vs the atomic `telemetry` doc; parse it | 0 | to write |
| 2 | `throttle-and-ignite` | first writes: actuate + read the errno | 1 | to write |
| 3 | `staging-and-modules` | one-shots, master toggles, per-module files | 2 | to write |
| 4 | `attitude-modes` | the onboard flight computer via **named modes** (no math) | 2 | to write |
| 5 | `point-at-parent` | CCI frame + custom **Body→CCI quaternion** | 4 | **WIP page exists** |
| 6 | `wait-in-sim-time` | pace in **sim time**, not wall time | 1 | to write |
| 7 | `hold-a-lock` | the **control loop** + gating (pause/warp/stale) | 5, 6 | to write |
| 8 | `orbital-math` | constants + the vis-viva / circular-orbit cheat-sheet | 1 | to write |
| 9 | `schedule-a-burn` | **maneuver nodes** — `ctl/burn` with a CCI Δv | 4, 8 | to write |
| 10 | `teleport-a-scenario` | set up state with `debug/teleport` (CCI state vector) | 8 | to write |
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

### 5. `point-at-parent` — a custom direction (**WIP page already exists**)
- **Goal:** aim the nose at the parent body with a computed quaternion.
- **Surface:** `position/cci`, `ctl/attitude_target`.
- **New idea:** the **CCI frame** (origin = body center → aim = `-position/cci`); the **Body→CCI
  quaternion** (body +X = thrust axis); why to use KSA's exact arithmetic, not a generic lib.
- **Note:** the page [`vessel-control-point-at-parent.mdx`](../../../site/src/content/docs/guides/vessel-control-point-at-parent.mdx)
  is the model for the whole series — finish/polish it (add the HTTP transport tab) rather than
  rewrite. Contrast with rung 4's shortcut (`RadialIn` does this for free) in a `:::tip`.

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

### 10. `teleport-a-scenario` — set the stage
- **Goal:** place a vessel in a known 120 km circular orbit (and a second one 50 m ahead).
- **Surface:** `debug/vessels/<id>/teleport` (CCI state `px py pz vx vy vz`).
- **New idea:** the CCI **state vector**; teleport is about the **current parent** (must already orbit
  it); equatorial circular `[r,0,0,0,v,0]`; along-track offset via Δθ = d/r.
- **Gotchas:** needs `debug_namespace` (default on); worked in `examples/` and `recipes.md §1`.

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
