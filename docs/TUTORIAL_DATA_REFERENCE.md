# Tutorial data reference — the `/sim` surface tutorials are built from

This is the **data reference for writing gatOS tutorials** (the progressive `guides/` series in the
Astro/Starlight site under [`site/`](../site/)). It is a curated, tutorial-oriented view of the
`/sim` API: the exact reads, writes, frames, and pacing rules a flight-computer program needs, each
shown in **both** ways a tutorial will present it — the in-guest `/sim` filesystem **and** the host
HTTP `/v1` mirror.

It is not the catalog. The catalog is [`SPEC_9P_FILESYSTEM.md`](../SPEC_9P_FILESYSTEM.md) (every path,
format, unit, errno, action key). This page pulls out the ~20% of that surface tutorials use 80% of
the time, adds the file↔HTTP correspondence so "show both approaches" is a copy job, and links back
to the SPEC and the [`gatos` skill](../.claude/skills/gatos/SKILL.md) for depth.

> **Companion:** the **`tutorials` skill** (`.claude/skills/tutorials/`) is the *authoring* guide —
> house style, Starlight/MDX mechanics, the tutorial ladder, and the reusable code-snippet library.
> This doc is the *data*; that skill is *how to write it up*. Read both before authoring a tutorial.

---

## 1. The one idea every tutorial rests on

**In a gatOS guest, the live simulation is a filesystem.** Reading a file returns the latest value;
writing certain files actuates the game and returns a real Linux errno on failure. There is no RPC,
no client library required — `cat` and `echo` are the whole API. The *same* surface is mirrored over
HTTP `/v1` (and MQTT), so the identical program logic runs two places:

| A program runs… | It reads with | It writes with | Use it in a tutorial when |
|---|---|---|---|
| **inside the guest** | `cat /sim/<path>` (a plain `read()`) | `echo v > /sim/<path>` (a plain `write()`) | teaching the filesystem-as-API idea; shell one-liners; Python/Rust/Bun shipped into Alpine |
| **on the host** | `GET $GATOS_HTTP/fs/<path>` or an aggregate `GET /v1/…` | `POST /v1/command` (JSON) or `POST /v1/fs/<path>` (raw) | dev/iteration from the player's workstation; language SDKs; dashboards |

- **Host base URL:** `http://127.0.0.1:4242/v1` (default `http_preferred_port`).
- **Guest base URL:** the env var `$GATOS_HTTP` (≈ `http://10.0.2.2:4242/v1`) — or just read `/sim`.

The dual presentation is a **deliberate tutorial requirement**: every tutorial that actuates the game
should show the `/sim` file way and the HTTP way, so a reader can pick whichever fits their context.
The [`tutorials` skill's `authoring.md`](../.claude/skills/tutorials/authoring.md) documents the
synced-`<Tabs>` component that renders the two side by side.

---

## 2. THE dual-transport correspondence (the money table)

Every operation a flight tutorial performs, in all three forms. Copy the row for the operation you're
teaching; you now have both approaches.

| Operation | In-guest (`/sim` shell) | Host (HTTP `/v1`) |
|---|---|---|
| **Read a scalar/vector/quat** | `cat /sim/vessels/active/position/cci` | `GET /v1/fs/vessels/active/position/cci` → raw text |
| **Read the atomic vessel doc** | `cat /sim/vessels/active/telemetry` | `GET /v1/vessels/active/telemetry` → JSON (§4) |
| **Read a body constant** | `cat /sim/bodies/Earth/mu` | `GET /v1/fs/bodies/Earth/mu` (or `GET /v1/bodies/Earth`) |
| **Read current time** | `cat /sim/time/ut` | `GET /v1/time` → `{ut,warp,sim_dt,…}` |
| **Write a control (throttle)** | `echo 0.5 > /sim/vessels/active/ctl/throttle` | `POST /v1/fs/vessels/active/ctl/throttle` body `0.5` |
| **Fire a one-shot (ignite)** | `echo 1 > /sim/vessels/active/ctl/ignite` | `POST /v1/fs/vessels/active/ctl/ignite` body `1` |
| **RCS translation (EVA/docking)** | `echo "1 0 0" > /sim/vessels/active/ctl/translate` (body-axis signs; latches — `0 0 0` stops) | `POST /v1/fs/vessels/active/ctl/translate` body `1 0 0` |
| **RCS rotation (own DAP; manual attitude mode)** | `echo "1 0 0" > /sim/vessels/active/ctl/rotate` (torque signs: +x roll right, +y pitch up, +z yaw right; latches — `0 0 0` stops) | `POST /v1/fs/vessels/active/ctl/rotate` body `1 0 0` |
| **Set a named attitude mode** | `echo Prograde > /sim/vessels/active/ctl/attitude_mode` | `POST /v1/fs/vessels/active/ctl/attitude_mode` body `Prograde` |
| **Set a custom attitude quat** | `echo "$x $y $z $w" > …/ctl/attitude_target` | `POST /v1/fs/vessels/active/ctl/attitude_target` body `x y z w` |
| **Schedule a burn** | `echo "$ut $dvx $dvy $dvz" > …/ctl/burn` | `POST /v1/fs/vessels/active/ctl/burn` body `ut dvx dvy dvz` |
| **Generic structured write** | *(write the file above)* | `POST /v1/command` `{"vessel_id","action","value"/"values"/"token"}` (§5) |
| **Wait until a sim time** | `echo <ut> > /sim/time/alarm` then `cat /sim/time/alarm` (parks) | `GET /v1/time/wait?until=<ut>` (long-poll) |
| **Stream events** | `tail -f /sim/events` | `GET /v1/events` (SSE, `data: {…}` lines) |
| **Stream one field on change** | `tail -f`-style loop on `cat` | `GET /v1/fs/<path>?stream=1` (SSE) |
| **Stream a vessel telemetry log** | `tail -f /sim/vessels/active/stream` | `GET /v1/vessels/active/stream` (SSE) |
| **Teleport (set CCI state)** | `echo "$px $py $pz $vx $vy $vz" > /sim/debug/vessels/Hunter/teleport` | `POST /v1/command` `{"vessel_id":"Hunter","action":"debug.teleport","values":[…6…]}` |
| **One-shot impulse kick** | `echo "$x $y $z [cci\|body] [ns\|dv]" > /sim/debug/vessels/Hunter/impulse` | `POST /v1/command` `{"vessel_id":"Hunter","action":"debug.impulse","values":[x,y,z],"token":"body","aux":"dv"}` |
| **Many writes in one tick** | write `<path> <value>` lines + a `commit` line to `/sim/ctl/batch` | `POST /v1/fs/ctl/batch` body = the same multi-line text |

Notes that keep a tutorial honest:

- **`/v1/fs/<path>` is the file mirror** — the path after `/v1/fs/` is *exactly* the `/sim`-relative
  path. So any file in the tree has an HTTP twin by construction. Reads add a trailing `\n`; writes
  take the raw value as the request body.
- **`POST /v1/command` is the structured twin** of a control write — same effect, JSON shape. Prefer
  it for host SDKs; prefer `/v1/fs/…` when a tutorial is literally mirroring a shell `echo`.
- **A failed write carries the errno both ways:** in the guest it's the `write(2)` errno (so a shell
  `echo` prints e.g. `Permission denied` and Python raises `OSError`); over HTTP it's the mapped
  status (`EINVAL`→400, `EACCES`→403, `ENOENT`→404, `EBUSY`→409, `ETIMEDOUT`→504) plus
  `{"errno","message"}`. Full map: [SPEC §2.4](../SPEC_9P_FILESYSTEM.md).
- Aggregate reads (`GET /v1/vessels/{id}`, `/v1/bodies/{id}`) use the **raw** id; the `/v1/fs/` and
  `/sim` paths use the **sanitized** id. Identical for ids that are already `[A-Za-z0-9._-]` (the
  common case — vessel names like `Hunter`). See [SPEC §2.2](../SPEC_9P_FILESYSTEM.md).

---

## 3. Reading telemetry

### 3.1 One atomic doc vs many scalar files

Two read shapes, both on every transport — pick per tutorial:

- **The atomic doc** `vessels/<id>/telemetry` — one JSON object, one self-consistent snapshot (same
  `seq`/`ut` across every field). **This is the right read for a control loop** (no stitching, no
  torn state). Shape in [SPEC §4](../SPEC_9P_FILESYSTEM.md); the fields a flight program touches most:
  `pos_cci`, `vel_cci`, `mass.t`, `alt.radar`, `att_q`, `orbit.*`, `parent`, `sit`, `warp`, `seq`,
  `ut`, `controllable`.
- **Scalar files** `vessels/<id>/position/cci`, `…/mass/total`, `…/attitude/quat`, … — one value per
  file. Best for **shell one-liners and teaching**, where reading a single named file is the clearest
  possible demonstration. `vessels/active/…` is a live alias of the controlled vessel — use it so a
  tutorial needs no vessel id.

A good progression: early tutorials read individual scalar files (concrete, obvious); once a reader is
running a loop, switch them to the atomic `telemetry` doc and explain *why* (consistency).

### 3.2 The vessel fields tutorials use

| What | Scalar file (`vessels/<id>/…`) | In the atomic doc | Frame / unit |
|---|---|---|---|
| position | `position/cci` | `pos_cci` | **CCI**, m |
| velocity (inertial) | `velocity/cci` | `vel_cci` | **CCI**, m/s |
| speeds | `velocity/{orbital,surface,inertial}` | `vel.{orb,surf,inr}` | m/s |
| attitude | `attitude/quat` | `att_q` | **Body→CCI** quat `x y z w` |
| body rates | `attitude/rates` | — | rad/s |
| altitude | `altitude/{barometric,radar}` | `alt.{baro,radar}` | m (use `radar` for ground clearance) |
| mass | `mass/{total,dry,propellant}` | `mass.{t,d,p}` | **kg** |
| orbit | `orbit/{apoapsis,periapsis,ecc,inc,sma,period,true_anomaly,time_to_ap,time_to_pe}` | `orbit.{ap,pe,ecc,inc,sma,period,ta,t_ap,t_pe}` | m / deg / s |
| parent body | `parent` | `parent` | the id the CCI frame centers on |
| situation | `situation` | `sit` | string (`Prelaunch`/`Landed`/`Freefall`/`Flying`, `[Flags]` → may be composite) |
| can we command it? | `controllable` | `controllable` | `1` iff KSA accepts control (has a Control Module) — **pre-check this**; a `0` vessel silently ignores throttle/attitude/etc. |

Full per-module surface (engines, tanks, rcs, solar, lights, docking, decouplers, animations, parts,
power/battery, navball, environment) is in [SPEC §3.4](../SPEC_9P_FILESYSTEM.md). Per-module dirs
appear only when the vessel actually has that module.

---

## 4. Flight-computer controls (the heart of the tutorial series)

The `ctl/` files are **onboard setpoints** — the sim integrates them itself, so they stay correct at
any time-warp. Your program is mission control; the flight computer flies. Everything below is on
`vessels/<id>/ctl/…` (or the `vessels/active/…` alias). Full table: [SPEC §3.4.17](../SPEC_9P_FILESYSTEM.md).

### 4.1 Engines & throttle (Frame phase — takes effect immediately)

| File | Write | Meaning |
|---|---|---|
| `ctl/throttle` | `0..1` | Manual throttle fraction (read = current setpoint). |
| `ctl/ignite` | `1` | Light the active engines (one-shot). |
| `ctl/shutdown` | `1` | Cut the active engines (one-shot). |
| `ctl/engine` | `0`/`1` | Ignition **master** you can read back: read = live `EngineOn`, write `1`=ignite/`0`=shutdown. Prefer this for a UI toggle so it mirrors true game state. |
| `ctl/stage` | `1` | Activate the next stage (one-shot). |
| `ctl/lights`, `ctl/rcs` | `0`/`1` | Master lights / master RCS. |

### 4.2 Attitude — two paths (Solver phase — next solver step, ~10 Hz)

The single most important choice in a flight tutorial:

1. **Named mode (no math)** — write a token to `ctl/attitude_mode` and the onboard autopilot steers
   there, tracking as you orbit, warp-correct:
   ```sh
   echo Prograde > /sim/vessels/active/ctl/attitude_mode
   ```
   Modes: `manual`, `Prograde`, `Retrograde`, `Normal`, `AntiNormal`, `RadialOut`, `RadialIn`,
   `Toward`, `Away`, `Antivel`, `Align`, `Forward`, `Backward`, `Up`, `Down`, `Ahead`, `Behind`,
   `Outward`, `Inward`, `PositiveDv`, `NegativeDv`, `Custom`, `None`. Optionally set the frame the mode
   resolves in via `ctl/attitude_frame` (`EclBody`, `EnuBody`, `Lvlh`, `VlfBody`, `BurnBody`, `Dock`).
   **This is the best default** — reach for it whenever the direction has a name.

2. **Custom quaternion** — for a direction the named modes don't cover, compute a **Body→CCI**
   quaternion and write `ctl/attitude_target`:
   ```sh
   echo "0 0 0 1" > /sim/vessels/active/ctl/attitude_target   # x y z w
   ```
   The autopilot points body **+X** (the nose/thrust axis) along `transform(+X, q)` in CCI. Building
   `q` correctly is the crux of the intermediate tutorials — see §5 and the verbatim helper in the
   [`tutorials` skill's `snippets.md`](../.claude/skills/tutorials/snippets.md).

### 4.3 Burns (Solver phase)

| File | Write | Meaning |
|---|---|---|
| `ctl/burn` | `ut dvx dvy dvz` | Schedule an impulsive maneuver at sim time `ut` with a **CCI** Δv vector; the autopilot orients and executes it. Great for transfer/circularization without hand-flying. |

### 4.4 The phase gotcha every attitude/burn tutorial must state

`attitude_mode` / `attitude_frame` / `attitude_target` / `burn` are **solver-phase**: they take effect
on the **next solver step (~10 Hz), not the instant the write returns**. (KSA's async solver
snapshots the flight computer each step, so a naive frame-phase write would flash on then revert; gatOS
drains these in the solver prefix so they stick — you just write the file.) Consequences to teach:

- Expect a tick of latency; a closed loop that recomputes a quaternion every tick should run **near
  1× warp**. Named modes and scheduled burns are onboard and stay correct under warp — it's your
  *own* per-tick math that must hold at 1×.
- The other `ctl/` writes (throttle/ignite/stage/lights/rcs) are **frame-phase** — effectively
  immediate.

---

## 5. Reference frames — the facts flight tutorials keep needing

Full treatment: [`gatos/coordinate-frames.md`](../.claude/skills/gatos/coordinate-frames.md) (the
working reference) and [`docs/KSA_CELESTIAL_COORDINATE_FRAMES.md`](KSA_CELESTIAL_COORDINATE_FRAMES.md)
(the KSA model). The distilled facts a tutorial cites:

- **All KSA frames are right-handed and orthonormal.** No other convention exists.
- **CCI is the working frame** (Celestial-Centered Inertial about the vessel's `parent`). Everything
  you read and write in flight is CCI: `position/cci`, `velocity/cci`, `attitude/quat`,
  `debug/teleport`, `ctl/burn` Δv.
  - **origin** = the parent body's center → `position/cci` is literally the arrow *from the body to
    the vessel* (so "toward the body" = `-position/cci`, no trig).
  - **+Z** = the body's north/spin axis; **+X** = the vernal point (a fixed direction in inertial
    space); **Y** = Z×X. The **X–Y plane is the equatorial plane** → an orbit lying in it has
    inclination 0. Position `(r,0,0)` with velocity `(0,v,0)` is an equatorial, prograde, circular
    orbit — the canonical teleport example.
  - CCI is inertial → Newtonian orbital mechanics work directly in it.
- **Body→CCI attitude quaternion** (`attitude/quat`, `ctl/attitude_target`), stored `x y z w`:
  applying it to a body axis gives that axis in CCI. The vessel's **thrust/nose axis is body +X**, so
  `transform(UnitX, q) == desired_direction_cci` by construction. **Use KSA's exact quaternion
  arithmetic** (Shepperd's method, Hamilton product) — a generic library with a different sign/handedness
  convention *looks* close and steers wrong. The verbatim port is in
  [`snippets.md`](../.claude/skills/tutorials/snippets.md) and the worked WIP tutorial
  [`vessel-control-point-at-parent.mdx`](../site/src/content/docs/guides/vessel-control-point-at-parent.mdx).
- **Surface-relative velocity** (for landing, ground speed, drag): `v_surface = v_cci − ω × r`, with
  `ω = [0,0,rotation_rate]` in CCI. The scalar `velocity/surface` is provided; compute the vector
  yourself. Equal to inertial on a non-rotating body.
- **Local ENU** (East-North-Up) at a CCI position, for surface guidance: `Û = r̂`, `Ê = normalize(ẑ×Û)`,
  `N̂ = Û×Ê` (guard the poles). Full recipe in `coordinate-frames.md §3`.

### 5.1 Orbital math cheat-sheet (CCI, SI)

```
μ = bodies/<parent>/mu            (m³/s²; G·M — never assume 9.8)
R = bodies/<parent>/radius        (m, mean radius)
r = R + altitude                  (orbital radius from center)
g(r) = μ / r²                     (local gravity magnitude)
v_circular(r) = sqrt(μ / r)       v_escape(r) = sqrt(2μ / r)
vis-viva: v² = μ(2/r − 1/a)       period: T = 2π sqrt(a³/μ)
Tsiolkovsky: Δv = Isp·g₀·ln(m_wet/m_dry)   (g₀ = 9.80665, only for Isp→exhaust velocity)
TWR = thrust / (mass · g(r))
```

`/sim` orbit `apoapsis`/`periapsis` are **altitudes above the surface** (add `radius` for geocentric
radius). Angles (`inc`, `lan`, `argpe`, `true_anomaly`, lat/lon, navball) are in **degrees** — convert
to radians for trig.

---

## 6. Constants & the body catalog

Read once at startup; they don't change during flight.

| What | File | HTTP |
|---|---|---|
| gravitational parameter μ | `bodies/<id>/mu` | `GET /v1/fs/bodies/<id>/mu` |
| mean radius | `bodies/<id>/radius` | `GET /v1/fs/bodies/<id>/radius` |
| rotation rate ω | `bodies/<id>/rotation_rate` | `GET /v1/fs/bodies/<id>/rotation_rate` |
| SOI radius | `bodies/<id>/soi` | `GET /v1/fs/bodies/<id>/soi` |
| whole body record | — | `GET /v1/bodies/<id>` → `{mu, mean_radius, soi_meters, …}` |
| the star / home ids | `system/{sun,home,name}` | `GET /v1/system` |

Engine constants (`engines/<n>/vac_thrust`, `isp`, `min_throttle`) are read the same way; re-read them
after staging, since the active engine set changes. Aggregate active thrust as `Σ vac_thrust` over
active engines and thrust-weighted Isp as `Σ(thrust·isp)/Σthrust`.

Body catalog + constants require `telemetry_bodies=true` (default on). See [SPEC §3.3](../SPEC_9P_FILESYSTEM.md).

---

## 7. Pacing & gating — the difference between a demo and a program

Two rules that separate a toy from a flight program; teach them the moment a tutorial introduces a loop.

### 7.1 Pace in **sim time**, never wall time

The sim runs at a different rate than your clock (time-warp) and stops when paused. Don't `sleep(dt)`.

| Intent | In-guest | Host |
|---|---|---|
| block until sim time ≥ `ut` | `echo <ut> > /sim/time/alarm` then `cat /sim/time/alarm` (parks, returns reached ut) | `GET /v1/time/wait?until=<ut>` (blocks, returns `{"reached_ut":…}`) |
| "sleep N sim-seconds" | read `time/ut`, wait for `ut+N` via the alarm | read `/v1/time`, wait `ut+N` |
| tight loop Δt | key integration off the **change in `ut`** between ticks (ceiling = `sample_rate_hz`, default 10) | same |

### 7.2 Gate: never fly blind

Hold control (stop writing, keep the last command, show a banner) when any of these is true:

| Condition | Read | Why |
|---|---|---|
| **paused** | `time/sim_dt == 0` | no physics advancing |
| **time-warp** | `time/warp > 1` (or `telemetry.warp`) | closed-loop control is only sound near 1× |
| **stale telemetry** | `telemetry.seq` unchanged since last tick | the host sampler hasn't published a new frame |
| **non-finite / missing** | `!isFinite(x)` or empty | a closed gate / absent module yields `0`/empty — sanitize |

Named modes and scheduled burns survive warp; it's per-tick computed control that must hold at 1×.
Detail: [`gatos/flight-programs.md §4–§5`](../.claude/skills/gatos/flight-programs.md).

---

## 8. Events & streams

- **Discrete events** — one JSON line per event (`situation-change`, `engine-state`, `flameout`,
  `docked`/`undocked`, `decoupled`, `animation-complete`, battery events, vessel appeared/vanished,
  `audio.finished`). In-guest `tail -f /sim/events`; host `GET /v1/events` (SSE). Great for
  "wait until X happens" without polling — e.g. `grep -m1` a completion.
- **Per-vessel telemetry log** — a growing NDJSON of `{seq,ut,sit,alt,vel,att,mass}` per sample:
  `tail -f /sim/vessels/active/stream` / `GET /v1/vessels/active/stream`.
- **One field on change** — `GET /v1/fs/<path>?stream=1` (SSE, one `data:` per change).

See [SPEC §3.5 / §7](../SPEC_9P_FILESYSTEM.md).

---

## 9. Setting up scenarios (debug/cheats a tutorial uses)

Tutorials often need to *place* a vessel before demonstrating control. `debug/**` (gated by
`debug_namespace=true`, default on; exempt from the authority gate) is how:

- **Teleport** — set a **CCI state vector** about the vessel's **current parent body**:
  ```sh
  echo "6578100 0 0 0 7784 0" > /sim/debug/vessels/Hunter/teleport   # equatorial 120 km circular
  ```
  Host: `POST /v1/command {"vessel_id":"Hunter","action":"debug.teleport","values":[6578100,0,0,0,7784,0]}`.
  **The vessel must already orbit the intended body** — teleport doesn't change which body it orbits.
  A circular orbit needs `r = radius + altitude`, `v = sqrt(μ/r)`, velocity ⟂ position; `[r,0,0,0,v,0]`
  is equatorial. Full semantics: [SPEC §6](../SPEC_9P_FILESYSTEM.md); worked program:
  [`gatos/recipes.md §1`](../.claude/skills/gatos/recipes.md).
- **One-shot impulse** — kick a vessel without propellant or pointing: `x y z [cci|body] [ns|dv]`
  to `/sim/debug/vessels/<id>/impulse`. Defaults: parent-CCI frame, newton-seconds (Δv = J ÷ live
  mass — KSA's own separation-impulse math). `body` reads the vector in the vessel frame (+X = nose);
  `dv` applies it directly as Δv m/s:
  ```sh
  echo "10 0 0 body dv" > /sim/debug/vessels/Hunter/impulse   # +10 m/s straight off the nose
  ```
  Host: `POST /v1/command {"vessel_id":"Hunter","action":"debug.impulse","values":[10,0,0],"token":"body","aux":"dv"}`.
  Full semantics: [SPEC §6](../SPEC_9P_FILESYSTEM.md).
- **Refuel / batteries** — `echo 1 > /sim/debug/vessels/<id>/refill_fuel` (and `refill_battery`).
- **Set time-warp** — `echo 100 > /sim/debug/time/warp`.
- **Switch controlled vessel** — `echo Polaris > /sim/debug/control_vessel` (focuses + takes control).

Other `debug/**` surface (welds, thug_life, always_render_iva) is cosmetic/advanced and cataloged in
[SPEC §3.7](../SPEC_9P_FILESYSTEM.md) — reserve it for late/bonus tutorials.

---

## 10. Units & the gotchas that silently ruin a tutorial program

| Trap | The rule |
|---|---|
| mass | **kg** (KSA native — no tonnes). |
| gravity | `μ/r²`, never a hardcoded 9.8. |
| ground-referenced velocity | surface-relative `v_cci − ω×r`, not raw `vel_cci`. |
| altitude choice | `radar` for terrain clearance, `barometric` for above-mean-radius. |
| attitude quaternion | KSA's exact Body→CCI arithmetic; never a foreign quaternion lib. |
| attitude/burn latency | solver-phase (a tick late); run closed loops near 1× warp. |
| a vessel's id | its **name** (`Hunter`, `Polaris` are literal ids); path ids are sanitized. |
| non-finite reads | a closed gate/absent module yields `0`/empty — guard before using. |
| `controllable == 0` | KSA silently ignores commands; pre-check it. |

Full units table: [SPEC §8](../SPEC_9P_FILESYSTEM.md). Full gotcha list:
[`coordinate-frames.md §7`](../.claude/skills/gatos/coordinate-frames.md).

---

## 11. Reference index — for a tutorial about X, read these

| Writing a tutorial about… | Primary sources |
|---|---|
| reading telemetry | this §3; [SPEC §3.4 / §4](../SPEC_9P_FILESYSTEM.md) |
| throttle / ignite / staging | this §4.1; [SPEC §3.4.17 / §5.1](../SPEC_9P_FILESYSTEM.md); example `examples/gogogo-rs` |
| named attitude modes | this §4.2; [SPEC §3.4.18](../SPEC_9P_FILESYSTEM.md); [`coordinate-frames.md §5`](../.claude/skills/gatos/coordinate-frames.md) |
| custom attitude / pointing | this §4.2 + §5; [`coordinate-frames.md §4`](../.claude/skills/gatos/coordinate-frames.md); WIP `vessel-control-point-at-parent.mdx`; [`snippets.md`](../.claude/skills/tutorials/snippets.md) |
| orbital math / burns | this §4.3 + §5.1; [SPEC §6](../SPEC_9P_FILESYSTEM.md); [`recipes.md §4`](../.claude/skills/gatos/recipes.md) |
| reference frames | [`coordinate-frames.md`](../.claude/skills/gatos/coordinate-frames.md); [`KSA_CELESTIAL_COORDINATE_FRAMES.md`](KSA_CELESTIAL_COORDINATE_FRAMES.md) |
| control loops / gating / pacing | this §7; [`flight-programs.md`](../.claude/skills/gatos/flight-programs.md) |
| events & waiting | this §8; [SPEC §3.5 / §7](../SPEC_9P_FILESYSTEM.md) |
| teleport / scenario setup | this §9; [SPEC §6](../SPEC_9P_FILESYSTEM.md); [`recipes.md §1`](../.claude/skills/gatos/recipes.md) |
| the HTTP API in general | [SPEC §7](../SPEC_9P_FILESYSTEM.md); `examples/sdk-ts` |
| a closed-loop autopilot (advanced) | [`flight-programs.md §8`](../.claude/skills/gatos/flight-programs.md); `examples/land-o-matic` |

Keep this doc in lockstep with the SPEC: when the `/sim` surface changes, the SPEC is updated in the
same change (its constitution) — refresh the affected rows here too so tutorials never teach a stale API.
