# Writing flight-control programs against gatOS `/sim`

How to structure programs that *fly* — read telemetry, decide, actuate — over the `/sim` surface.
Pairs with [`coordinate-frames.md`](coordinate-frames.md) (the math) and
[`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md) (every path/format). Two working Rust
references live in the repo:

- **`examples/gogogo-rs`** — a minimal control panel: throttle slider + ignite/shutdown toggle. The
  template for "read a control file, write a control file."
- **`examples/land-o-matic`** — a full G-FOLD + UPFG powered-descent autopilot. The template for a
  closed-loop guidance law (frames, attitude quaternion, re-solve each tick, abort handling).

---

## 1. Where your program runs, and how it connects

| Location | Read | Write | Pick it when |
|---|---|---|---|
| **In the guest** | `cat /sim/<path>` (a plain `read()`) | `echo v > /sim/<path>` (a plain `write()`) | the program ships into the guest (Alpine + `apk add cargo rust`, or `bun`) |
| **On the host** | `GET $GATOS_HTTP/fs/<path>` or aggregate `GET /v1/...` | `POST $GATOS_HTTP/command` (JSON) or `POST /fs/<path>` (raw) | dev/iteration on your workstation against the live mod |

The control files **are** the API: a write actuates on the newline and returns the real errno. There
is no separate RPC. The same paths work both ways; only the access method differs.

**Auto-select the transport** the way the examples do: explicit flag → real `/sim` mount → `$GATOS_HTTP`
env → fallback. In TypeScript the SDK's `defaultTransport()` does exactly this (`new GatosClient()`
uses HTTP when `$GATOS_HTTP` is set, else `/sim`).

---

## 2. The control loop skeleton

```
setup:   discover vessel id; read constants once (parent μ, radius, rotation_rate; engine vac_thrust/isp)
loop:
  1. READ      one atomic telemetry doc  (vessels/<id>/telemetry)  → pos_cci, vel_cci, mass, alt, att_q…
  2. GATE      bail/hold this tick if paused, warping, or telemetry is stale  (§4)
  3. DECIDE    run your guidance/logic in CCI (or local ENU) → desired throttle + thrust direction
  4. ACTUATE   write ctl/throttle, ctl/attitude_target (or a named mode), ctl/ignite/shutdown
  5. PACE      sleep in SIM time, not wall time  (§5)
```

Read the **atomic `telemetry` document** once per tick rather than stitching scalar files — it is one
self-consistent snapshot (same `seq`/`ut` across all fields). Scalar files are best for one-off reads
and shell scripting.

---

## 3. Reading

**Atomic doc (recommended):** `vessels/active/telemetry` (or `vessels/by-id/<id>/telemetry`) →
parse the JSON in [SPEC §4](../../../SPEC_9P_FILESYSTEM.md). Fields you'll use most: `pos_cci`,
`vel_cci`, `mass.t`, `alt.radar`, `att_q`, `orbit.*`, `parent`, `sit`, `warp`, `seq`, `ut`.

**Constants (read once at startup):**
```
bodies/<parent>/mu          bodies/<parent>/radius          bodies/<parent>/rotation_rate
engines/<n>/vac_thrust      engines/<n>/isp                 engines/<n>/min_throttle
```
Aggregate active thrust as `Σ vac_thrust` over active engines and thrust-weighted Isp
`Σ(thrust·isp)/Σthrust`. Re-read after staging (engine set changes).

**Scalar parsing:** every read is a `G9` double (or `0`/`1`, or space-separated `x y z`/`x y z w`),
one value + `\n`. Trim and parse. Vectors split on whitespace.

---

## 4. Gating: never fly blind

Hold control (stop writing, keep last command, show a banner) when any of these is true:

| Condition | Read | Why |
|---|---|---|
| **Paused** | `time/sim_dt == 0` | no physics advancing; integrating a Δt of 0 is meaningless |
| **Time-warp** | `time/warp > 1` (or `telemetry.warp`) | on-rails / fast physics; closed-loop control is only sound near 1× |
| **Stale telemetry** | `telemetry.seq` unchanged since last tick | the host sampler hasn't published a new frame |
| **Non-finite / missing** | `!isFinite(x)` or empty | a closed gate / absent module yields `0`/empty |

Named attitude modes (`ctl/attitude_mode`) and scheduled burns (`ctl/burn`) are *onboard* and remain
correct under warp — it's *closed-loop laws you compute each tick* that must hold under warp.

---

## 5. Pacing in sim time (warp-correct)

Do **not** `sleep(dt)` against the wall clock — the sim runs at a different rate (warp), and pausing
stops it. Pace against sim time:

- **Block until a sim time:** write `time/alarm` with a target `ut`; the read returns when sim time
  reaches it. (`HTTP`: `GET /v1/time/wait?until=<ut>`.) This is warp-correct and pause-safe.
- **"Sleep N sim-seconds":** read `time/ut`, then wait for `ut + N` via the alarm. (SDK:
  `client.sleepSim(N)`.)
- **Tight control loop:** poll the telemetry at your cadence, but key your integration Δt off the
  **change in `ut`** between ticks, not wall time. The effective ceiling is the host `sample_rate_hz`.

---

## 6. Actuating

**Throttle / engines (frame phase — immediate):**
```
ctl/throttle  = clamp(thrust_cmd / thrust_max, 0, 1)   // 0..1 fraction
ctl/ignite    = 1     // one-shot light
ctl/shutdown  = 1     // one-shot cut
ctl/engine    = 1|0   // ignition master as a readable toggle (read = live state)
```
`gogogo-rs` is exactly this: it mirrors `ctl/engine` (the live `EngineOn`) on a button and writes
`ctl/throttle` from a slider — one write per 1% crossed so a held sweep is smooth, not a flood.

**Attitude (solver phase — next solver step):** two choices —
- *Named mode:* `ctl/attitude_mode = Retrograde` (+ `ctl/attitude_frame` if needed). The autopilot
  steers; warp-correct; no math. Best default.
- *Custom direction:* build the Body→CCI quaternion (see
  [coordinate-frames.md §4](coordinate-frames.md)) and write `ctl/attitude_target = "x y z w"`. For
  guidance laws that compute a fresh thrust direction every tick.

**Maneuver node:** `ctl/burn = "ut dvx dvy dvz"` (CCI Δv) lets the onboard computer execute an
impulsive burn at a sim time — useful for transfer/circularization without hand-flying.

Every write returns an errno on failure (see [SPEC §2.4](../../../SPEC_9P_FILESYSTEM.md)): `EACCES`
(control disabled, or not the active vessel when `control_all_vessels=false`), `EINVAL` (bad value),
`EBUSY` (one-shot already fired), `ETIMEDOUT` (game paused/loading past `command_timeout_ms`).

---

## 7. Case study: `gogogo-rs` (simple vehicle control)

The whole interface (from its README) is four files on the **active** vessel:

| widget | reads | writes |
|---|---|---|
| throttle slider | `vessels/active/ctl/throttle` | `vessels/active/ctl/throttle` (`0..1`) |
| ignite/shutdown | `vessels/active/ctl/engine` | `vessels/active/ctl/engine` (`1`=ignite, `0`=shutdown) |
| active-vessel gate | `vessels/active/id` | — |

Patterns to copy: drive everything off `vessels/active/…` (no vessel id needed); mirror the **live**
game state on toggles (read `ctl/engine`, never an internal guess, so staging/other clients stay
consistent); rate-limit writes; grey out when `vessels/active/id` is absent. No-TUI equivalent:
`echo 0.5 > /sim/vessels/active/ctl/throttle`, `echo 1 > /sim/vessels/active/ctl/engine`.

---

## 8. Case study: `land-o-matic` (closed-loop guidance)

A powered-descent autopilot fusing **G-FOLD** (fuel-optimal convex trajectory, re-solved each tick as
an MPC) and **UPFG** (Shuttle terminal steering law). What a guidance program must get right, as
embodied there:

1. **Frames done once, correctly** — CCI for I/O; a local **ENU** frame at the landing site for the
   guidance math; CCF only to place the (co-rotating) target. Surface-relative velocity `v_cci − ω×r`.
   (See [coordinate-frames.md](coordinate-frames.md).)
2. **The attitude-output path** — each tick the solver yields a thrust **direction** in CCI; the
   program turns it into the Body→CCI quaternion (body +X = thrust) using KSA's exact quaternion
   arithmetic and writes `ctl/attitude_target`, plus `ctl/throttle = |u|·mass/thrust_max`, plus
   `ctl/ignite`.
3. **Re-solve every tick (MPC)** — apply only the first command of each fresh solve; re-plan on new
   state. Handle **infeasibility** (hold retrograde, throttle 0) rather than writing garbage.
4. **A real state machine** — Idle → Burn (G-FOLD) → Terminal (UPFG handoff, latched) → Touchdown,
   with **Abort** from anywhere (`throttle 0`, `shutdown 1`, `attitude_mode manual`).
5. **Holds** — suspend guidance with **no control writes** when paused / time-warped / telemetry
   stale, with a visible banner.
6. **Pure, host-testable core** — the guidance math has no `/sim`/game dependency, so it runs against
   a fixture directory (`--root`) or the HTTP mirror (`--url $GATOS_HTTP`) and is unit-tested on the
   host. Structure your guidance the same way: a pure function `state → command`, with I/O at the edges.

Read `examples/land-o-matic/README.md` and `LANDING_PROGRAM_PLAN.md` (§3, the frame contract) before
writing your own guidance law.

---

## 9. Authority & debug gates (so writes don't silently fail)

- `control_enabled=false` → **every** control write is `EACCES`. (Default on.)
- `control_all_vessels=false` → you may only command the **controlled** vessel; others `EACCES`. Use
  `vessels/active/…` or `debug/control_vessel` to take control. (Default: all vessels allowed.)
- `debug_namespace=false` → `/sim/debug/**` is absent and `debug.*` actions `EACCES`. Teleport /
  refuel / warp-set / vessel-switch live here. (Default on.)
- `camera.focus` is view-only and exempt from the authority gate.

---

## 10. Quick decision guide

| You want to… | Do this |
|---|---|
| set throttle / ignite / stage | write `ctl/throttle`, `ctl/ignite`, `ctl/stage` (frame phase, immediate) |
| translate with RCS (EVA/docking) | write `ctl/translate` = body-axis signs (`1 0 0` = along the nose); bang-bang, **latches** until `0 0 0`; composes with an attitude hold |
| rotate with RCS (your own DAP/autopilot) | write `ctl/rotate` = body-axis torque signs (`1 0 0` = roll right, `0 1 0` = pitch up, `0 0 1` = yaw right); bang-bang, **latches** until `0 0 0`; needs `attitude_mode=manual` for full authority (auto strips rotation); pair with `ctl/translate` in one `ctl/batch` |
| hold a standard orientation | write `ctl/attitude_mode` (+ `ctl/attitude_frame`) — let the autopilot steer |
| point at a computed direction | build Body→CCI quaternion → write `ctl/attitude_target` |
| do an impulsive transfer/circularize | write `ctl/burn = "ut dvx dvy dvz"` (CCI Δv) |
| wait for a time / phase angle | `time/alarm` (or `GET /v1/time/wait`); pace in sim time |
| read everything consistently | one read of `vessels/<id>/telemetry` |
| place a vessel exactly | `debug/vessels/<id>/teleport` (CCI state) — see [recipes.md](recipes.md) |
| kick a vessel without burning | `debug/vessels/<id>/impulse` = `x y z [cci\|body] [ns\|dv]` (one-shot Δv cheat; no propellant, no pointing) |
| run a closed loop safely | bracket it at 1× warp; gate on pause/warp/stale; abort path ready |

Concrete end-to-end programs (including the teleport task): [`recipes.md`](recipes.md).
