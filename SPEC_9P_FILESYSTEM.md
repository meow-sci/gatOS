# SPEC: the gatOS `/sim` 9P filesystem

> **This file is the single source of truth for the `/sim` API surface.** Every path the guest can
> `cat`/`echo`, every HTTP `/v1` route, every MQTT topic, every command action key, the exact value
> formats, the units, and the read/write semantics are cataloged here. The `gatos` skill
> (`.claude/skills/gatos/SKILL.md`) and any program written against gatOS reference this document.
>
> **⚠️ CONSTITUTION — keep this in sync.** The `/sim` tree, the HTTP/MQTT mirrors and the command
> set are a frozen, user-facing API. **Whenever you add, remove, rename, or change the format/units
> of any `/sim` node, control file, debug action, command action key, or HTTP/MQTT endpoint, you
> MUST update this file in the same change** (and `docs/KSA_INTEGRATION_MATRIX.md` when the KSA
> binding changes). The code is authoritative; this file mirrors it — they must never disagree. See
> the maintenance mandate at the end.
>
> **Source of truth in code:**
> `gatOS.SimFs/SimFsTree.cs` (the tree), `gatOS.SimFs/Formats.cs` (value formats),
> `gatOS.SimFs/Snapshots/SimSnapshot.cs` (fields + units), `gatOS.SimFs/Commands/**` (write parsing),
> `gatOS.GameMod/Game/Ksa/KsaCatalog.cs` (action routing), `gatOS.GameMod/Game/Ksa/Actuators/**`
> (write semantics), `gatOS.Http/SimHttpServer.cs` (HTTP routes), `gatOS.GameMod/Configuration/gatos.default.toml`
> (config gates).

---

## 1. What `/sim` is

gatOS exposes live KSA simulation state to programs as a **filesystem**. A C#-implemented 9P2000.L
server publishes an immutable telemetry snapshot ~`sample_rate_hz` times per second; the guest
mounts it at `/sim`. Reading a file returns the latest value; writing certain files (`ctl/…`,
`debug/…`, per-module controls) **actuates the game** synchronously, returning a Linux errno on
failure.

The **same surface** is exposed over three transports (the *transport-parity rule* — they are
projections of one model, never re-implemented):

| Transport | Where | Read a field | Write/actuate |
|---|---|---|---|
| **9P files** | in-guest at `/sim` | `cat /sim/<path>` | `echo <value> > /sim/<path>` |
| **HTTP `/v1`** | host `127.0.0.1:4242` (guest: `$GATOS_HTTP` ≈ `http://10.0.2.2:4242/v1`) | `GET /v1/fs/<path>` or aggregate `GET /v1/...` | `POST /v1/fs/<path>` (raw value) or `POST /v1/command` (JSON) |
| **MQTT** | host/guest `…:1883` (`$GATOS_MQTT`) | retained `gatos/sim/<path>` | publish `gatos/sim/<path>/set` or `gatos/command` |

Programs run **inside the guest** (read `/sim` directly) or **on the host** (use HTTP `/v1`). The
TypeScript SDK (`examples/sdk-ts`) hides the difference behind one typed API and auto-selects the
transport (`HTTP` when `$GATOS_HTTP` is set, else `/sim`).

---

## 2. Conventions

### 2.1 Value formats (`gatOS.SimFs/Formats.cs`)

| Kind | Format | Example |
|---|---|---|
| **Scalar** (double) | `G9` invariant culture (9 significant digits) | `120000.001` |
| **Flag** (bool) | `0` or `1` | `1` |
| **Vector** (3) | space-separated `x y z` | `6.5781e6 0 0` |
| **Quaternion** (4) | space-separated `x y z w` | `0 0 0 1` |
| **String** | verbatim | `Freefall` |
| **List** | newline-separated | `Moon\nISS` |
| **Stream/event line** | one-line JSON (NDJSON, relaxed escaping) | `{"ut":…,"type":…}` |

Every scalar **read** is one value followed by a single `\n`. The `parent`/`children`/`class`
strings are returned verbatim. **Writes** are line-buffered: a control file actuates the moment the
`\n` arrives (so `echo` carries the real errno on the failing `write(2)`); a write with no newline
actuates best-effort on close and cannot report an errno.

### 2.2 ID sanitization (filesystem & `/v1/fs` & MQTT paths)

Directory names derived from KSA ids (vessel ids, body ids, tank resource names) are **sanitized**:
any character outside `[A-Za-z0-9._-]` becomes `_`; duplicate names get `~2`, `~3`, … suffixes in
listing order; empty/`.`/`..` become `_`/`_.`/`_..`. In KSA a vessel's **name *is* its id**
(`Vehicle.SetName` assigns the Id), so the vessel "Hunter" lives at `/sim/vessels/by-id/Hunter`.

> **Note on the HTTP *aggregate* reads:** `GET /v1/vessels/{id}`, `GET /v1/bodies/{id}` and
> `GET /v1/vessels/{id}/telemetry` match the **raw** id (`v.Id`/`b.Id`), not the sanitized one. The
> `/v1/fs/<path>` mirror and the 9P/MQTT trees use the **sanitized** path. For ids that contain only
> `[A-Za-z0-9._-]` (the common case) the two are identical.

### 2.3 Archetypes (read/write semantics)

| Code | Archetype | Read returns | Write accepts |
|---|---|---|---|
| **S** | SENSOR | current value (one line) | — (read-only; writing fails `EACCES`) |
| **St** | STATE | current setpoint | `0`/`1` flag, `0..1` fraction, number, vector, or token (idempotent) |
| **T** | TRIGGER | status (default `0`) | the exact fire token (`1`) — one-shot |
| **Sm** | STREAM | growing-log / blocking-event NDJSON | — |
| **Smb** | BINARY STREAM | continuous raw bytes (binary-safe; blocks for the next item, **never EOF** — `cat` reads it forever) | — |

### 2.4 errno vocabulary (frozen — `gatOS.SimFs/Commands/CommandResult.cs`)

| errno | HTTP | Meaning |
|---|---|---|
| `EINVAL` | 400 | unparseable / out-of-range / wrong arity |
| `ENOENT` | 404 | vessel/module/field vanished or no such path |
| `EACCES` | 403 | control disabled (`control_enabled=false`), debug disabled, authority gate, or writing a SENSOR |
| `EBUSY` | 409 | action can't fire now (e.g. already-fired one-shot) |
| `EIO` | 500 | a KSA call threw (latches the accessor degraded) |
| `ETIMEDOUT` | 504 | game thread didn't drain the command within `command_timeout_ms` (paused/loading) |
| `EOPNOTSUPP` | 501 | accessor latched degraded after a prior fault |

### 2.5 Config gates (`gatos.default.toml` → live `gatos.toml`)

| Key | Default | Effect |
|---|---|---|
| `telemetry_enabled` | `true` | master read feed; `false` freezes `/sim` data |
| `telemetry_vessel_detail` | `true` | per-vessel detail (navball/environment/per-module, orbit extras); off ⇒ core only |
| `telemetry_vessel_parts` | `true` | per-vessel `parts/` list (the welds anchor picker; cached); off ⇒ the subtree vanishes |
| `telemetry_bodies` | `true` | `/sim/bodies` + `/sim/system` |
| `telemetry_events` | `true` | `/sim/events` diffs |
| `control_enabled` | `true` | master write switch; `false` ⇒ every control write `EACCES` |
| `control_all_vessels` | `true` | `false` ⇒ only the **controlled** vessel is commandable (`EACCES` otherwise); `camera.focus` and the `debug.*` namespace are exempt |
| `debug_namespace` | `true` | exposes `/sim/debug/**` and the `debug.*` actions; `false` ⇒ those vanish / `EACCES` |
| `sample_rate_hz` | `10` | master cadence (1..120) |
| `http_enabled` / `http_preferred_port` | `true` / `4242` | HTTP `/v1` server (falls back to ephemeral on clash) |
| `http_field_endpoints` | `true` | the `/v1/fs/<path>` mirror (off ⇒ those routes `ENOENT`) |
| `mqtt_enabled` / `mqtt_preferred_port` | `true` / `1883` | embedded MQTT broker |
| `command_timeout_ms` | `2000` | how long a write waits for the game thread before `ETIMEDOUT` |
| `display_enabled` | `false` | boot seed for `/sim/display/enabled` — the screen stream (§3.8); **off by default** |
| `display_fps` / `display_width` / `display_height` | `15` / `320` / `180` | boot seeds for the stream cadence + downscale size (runtime control is the `/sim/display/*` files) |
| `display_encoding` | `rgba-zlib` | boot seed for the frame encoding (`rgba-zlib` \| `rgba`; zlib needs purrTTY's 2026-07-02+ native — §3.8) |

---

## 3. The tree

Legend: **A** = archetype (§2.3). Paths are relative to `/sim`. `<id>` = sanitized id. Files under a
vessel appear at both `vessels/by-id/<id>/…` and the alias `vessels/active/…` (the controlled
vessel). Directories marked *(detail)* require `telemetry_vessel_detail=true`; *(bodies)* require
`telemetry_bodies=true`. Per-module dirs only appear when the vessel actually has that module.

### 3.1 `/time`

| Path | A | Format | Meaning |
|---|---|---|---|
| `time/ut` | S | scalar | Universal sim time, seconds. |
| `time/warp` | S | scalar | Current time-warp factor (1 = realtime). |
| `time/sim_dt` | S | scalar | Sim seconds advanced by the last tick; `0` ⇒ effectively paused. |
| `time/warp_speeds` | S | `f f f …` | The discrete warp factors the game offers. |
| `time/auto_warp` | S | `0` or `1 <ut>` | Auto-warp-to-time active flag + target ut. |
| `time/alarm` | St | scalar | **Blocking**: write a target `ut`; the read parks until sim time reaches it, then returns the reached ut. The warp-correct "sleep until". |

### 3.2 `/system` *(bodies)*

| Path | A | Format | Meaning |
|---|---|---|---|
| `system/name` | S | string | System name (named after its star). |
| `system/home` | S | string | Home body id. |
| `system/sun` | S | string | Primary star id. |

### 3.3 `/bodies/<id>` *(bodies)* — celestial catalog

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `bodies/<id>/id` | S | string | Body id. |
| `bodies/<id>/class` | S | string | `Planet`, `Moon`, `Star`, … |
| `bodies/<id>/parent` | S | string | Parent body id (empty for the root star). |
| `bodies/<id>/children` | S | list | Ids of orbiting bodies (newline-separated). |
| `bodies/<id>/mass` | S | scalar | Mass, kg. |
| `bodies/<id>/radius` | S | scalar | Mean radius, meters. |
| `bodies/<id>/mu` | S | scalar | Standard gravitational parameter **μ = GM**, m³/s². |
| `bodies/<id>/soi` | S | scalar | Sphere-of-influence radius, meters. |
| `bodies/<id>/rotation_rate` | S | scalar | Sidereal rotation rate about +Z (CCF/CCI north), rad/s. |
| `bodies/<id>/position/ecl` | S | vector | Position in the system **ECL** (ecliptic) frame, meters. |
| `bodies/<id>/velocity/ecl` | S | vector | Velocity in **ECL**, m/s. |
| `bodies/<id>/orbit/apoapsis` | S | scalar | Apoapsis **altitude** above the parent surface, meters. |
| `bodies/<id>/orbit/periapsis` | S | scalar | Periapsis altitude, meters. |
| `bodies/<id>/orbit/ecc` | S | scalar | Eccentricity. |
| `bodies/<id>/orbit/inc` | S | scalar | Inclination, **degrees**. |
| `bodies/<id>/orbit/lan` | S | scalar | Longitude of ascending node, degrees. |
| `bodies/<id>/orbit/argpe` | S | scalar | Argument of periapsis, degrees. |
| `bodies/<id>/orbit/sma` | S | scalar | Semi-major axis, meters. |
| `bodies/<id>/orbit/period` | S | scalar | Orbital period, seconds. |
| `bodies/<id>/atmosphere/present` | S | `1` | Present only when the body has atmosphere. |
| `bodies/<id>/atmosphere/height` | S | scalar | Atmosphere top above surface, meters. |
| `bodies/<id>/atmosphere/scale_height` | S | scalar | Scale height, meters. |
| `bodies/<id>/atmosphere/sea_level_pressure` | S | scalar | Sea-level pressure, Pa. |
| `bodies/<id>/atmosphere/sea_level_density` | S | scalar | Sea-level density, kg/m³. |
| `bodies/<id>/ocean/present` | S | `1` | Present only when the body has an ocean. |
| `bodies/<id>/ocean/density` | S | scalar | Ocean density, kg/m³. |
| `bodies/<id>/focus` | T | write `1` | Move the main camera to this celestial (view-only; exempt from the authority gate). Action `camera.focus`. |

The `orbit/` and `atmosphere/` dirs are absent for the root star / airless bodies.

### 3.4 `/vessels`

`vessels/active/…` is a live **alias** of the controlled vessel (same qids as `by-id/<activeId>`);
`vessels/active/id` reads the active vessel id, or `ENOENT` when nothing is controlled.
`vessels/by-id` lists all vessels.

#### 3.4.1 Core vessel scalars — `vessels/by-id/<id>/…`

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `id` | S | string | Stable vehicle id (== name). |
| `name` | S | string | Display name (KSA: equals the id). |
| `situation` | S | string | `Prelaunch`, `Landed`, `Freefall`, `Flying`, … A `[Flags]` enum → values can be **composite** (comma-separated, e.g. `Landed, …`); parse accordingly. |
| `parent` | S | string | **Parent body id** the CCI frame is centered on. |
| `controlled` | S | flag | `1` when this is the player-controlled vessel. |
| `controllable` | S | flag | `1` when KSA will accept flight-control + flight-computer commands (`Vehicle.IsControllable`: the vessel has a Control Module). A vessel reading `0` here **silently ignores** throttle/stage/attitude/burn/RCS/ignite — gatOS does not gate, it relies on KSA's own lockout, so pre-check this. The controlled vessel is always `1`. (KSA 2026.6.9.4750.) |
| `com` | S | vector | Center of mass in the assembly frame, meters. |
| `telemetry` | S | JSON | The **atomic** per-vessel document (see §4). One `read()` = one self-consistent snapshot. |
| `position/cci` | S | vector | Position in **CCI** (Celestial-Centered Inertial about `parent`), meters. |
| `position/ecl` | S | vector | Position in the parent's ecliptic frame, meters. |
| `position/lat` | S | scalar | Geodetic latitude, degrees. |
| `position/lon` | S | scalar | Geodetic longitude, degrees. |
| `velocity/orbital` | S | scalar | Orbital (inertial) speed, m/s. |
| `velocity/surface` | S | scalar | Surface-relative speed, m/s. |
| `velocity/inertial` | S | scalar | Inertial speed, m/s. |
| `velocity/cci` | S | vector | Velocity in **CCI**, m/s (the vector behind `orbital`). |
| `attitude/quat` | S | quat | **Body→CCI** attitude quaternion `x y z w`. |
| `attitude/rates` | S | vector | Body rotation rates, rad/s. |
| `altitude/barometric` | S | scalar | Altitude above mean radius, meters. |
| `altitude/radar` | S | scalar | Altitude above terrain/ocean, meters (use for ground clearance). |
| `mass/total` | S | scalar | Total (wet) mass, kg. |
| `mass/dry` | S | scalar | Dry mass, kg. |
| `mass/propellant` | S | scalar | Propellant mass, kg. |
| `stream` | Sm | NDJSON | Growing-log of `{seq,ut,sit,alt,vel,att,mass}` per sample (`tail -f`). |

#### 3.4.2 Orbit *(detail; present while in orbit)* — `…/orbit/`

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `orbit/apoapsis` | S | scalar | Apoapsis **altitude** above parent surface, meters. |
| `orbit/periapsis` | S | scalar | Periapsis altitude, meters. |
| `orbit/ecc` | S | scalar | Eccentricity. |
| `orbit/inc` | S | scalar | Inclination, degrees. |
| `orbit/lan` | S | scalar | Longitude of ascending node, degrees. |
| `orbit/argpe` | S | scalar | Argument of periapsis, degrees. |
| `orbit/sma` | S | scalar | Semi-major axis, meters. |
| `orbit/period` | S | scalar | Orbital period, seconds. |
| `orbit/true_anomaly` | S | scalar | True anomaly, degrees. |
| `orbit/time_to_ap` | S | scalar | Seconds to next apoapsis. |
| `orbit/time_to_pe` | S | scalar | Seconds to next periapsis. |
| `orbit/next_patch` | S | scalar | Sim time of the next patch transition (SOI change/escape); `0` when none. |

#### 3.4.3 Navball *(detail)* — `…/navball/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `navball/pitch` | S | int | Pitch, degrees. |
| `navball/yaw` | S | int | Yaw/heading, degrees. |
| `navball/roll` | S | int | Roll, degrees. |
| `navball/twr` | S | scalar | Thrust-to-weight ratio. |
| `navball/deltav` | S | scalar | Remaining vacuum Δv, m/s. |
| `navball/frame` | S | string | Navball reference frame (`EclBody`, `Lvlh`, …). |
| `navball/speed` | S | scalar | Navball speed readout, m/s. |

#### 3.4.4 Environment *(detail)* — `…/environment/`

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `environment/pressure` | S | scalar | Static pressure, Pa. |
| `environment/density` | S | scalar | Atmospheric density, kg/m³. |
| `environment/dynamic_pressure` | S | scalar | Dynamic pressure q, Pa. |
| `environment/ocean_density` | S | scalar | Ocean density, kg/m³ (0 outside ocean). |
| `environment/terrain_radius` | S | scalar | Terrain radius below the vessel, meters. |
| `environment/accel` | S | vector | Linear acceleration in body frame, m/s². |
| `environment/angular_accel` | S | vector | Angular acceleration in body frame, rad/s². |
| `environment/g_force` | S | scalar | Acceleration magnitude in g (|accel| / g₀). |

#### 3.4.5 Power & battery — `…/power/`, `…/battery/` *(battery present only with a battery)*

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `power/produced` | S | scalar | Total instantaneous electrical power produced, W. |
| `power/consumed` | S | scalar | Total instantaneous electrical power consumed, W. |
| `battery/charge` | S | scalar | Battery charge fraction 0..1. |
| `battery/fraction` | S | scalar | Same as `charge` (alias). |
| `battery/capacity` | S | scalar | Battery capacity, joules. |

#### 3.4.6 Engines — `…/engines/<n>/` (n = engine index)

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `engines/<n>/active` | **St** | flag | Read = active; **write `0`/`1`** to enable/disable (action `engine.active`). |
| `engines/<n>/vac_thrust` | S | scalar | Vacuum thrust, N. |
| `engines/<n>/isp` | S | scalar | Specific impulse, s. |
| `engines/<n>/throttle` | S | scalar | Commanded throttle 0..1. |
| `engines/<n>/propellant` | S | flag | Propellant available. |
| `engines/<n>/min_throttle` | **St** | fraction | Read = deep-throttle floor; **write `0..1`** (action `engine.min_throttle`). |

#### 3.4.7 Tanks — `…/tanks/<resource>/` (resource = sanitized resource name)

| Path | A | Format | Meaning |
|---|---|---|---|
| `tanks/<r>/amount` | S | scalar | Current amount. |
| `tanks/<r>/capacity` | S | scalar | Capacity. |
| `tanks/<r>/fraction` | S | scalar | Fill fraction 0..1. |

#### 3.4.8 RCS *(present when fitted)* — `…/rcs/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `rcs/<n>/active` | **St** | flag | Read = active; **write `0`/`1`** (action `rcs.active`). |
| `rcs/<n>/propellant` | S | flag | Propellant available. |
| `rcs/<n>/map` | S | string | Active control-axis flags (e.g. `Pitch|Yaw`). |

#### 3.4.9 Solar panels *(present when fitted)* — `…/solar/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `solar/<n>/produced` | S | scalar | Instantaneous power produced, W. |
| `solar/<n>/occluded` | S | flag | Occluded from the sun. |
| `solar/<n>/sun_aoa` | S | scalar | Sun angle of attack, degrees. |
| `solar/<n>/efficiency` | S | scalar | Sun efficiency 0..1. |
| `solar/<n>/tracker_angle` | S | scalar | Tracker angle, degrees (only when a tracker is fitted). |
| `solar/<n>/goal` | **St** | fraction | Deploy setpoint 0..1 (only when the panel has a deploy animation; action `animation.goal`). |
| `solar/<n>/current` | S | scalar | Actual deploy fraction 0..1. |
| `solar/<n>/state` | S | string | `Deployed`/`Retracted`/`Deploying`/`Retracting`/`Broken`. |

#### 3.4.10 Generators *(present when fitted)* — `…/generators/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `generators/<n>/active` | S | flag | Producing. |
| `generators/<n>/produced` | S | scalar | Instantaneous power produced, W. |

#### 3.4.11 Lights *(present when fitted)* — `…/lights/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `lights/<n>/on` | **St** | flag | Read = on; **write `0`/`1`** (action `light.on`). |
| `lights/<n>/brightness` | **St** | number | **Write** intensity (action `light.brightness`). |
| `lights/<n>/color` | **St** | `r g b` | **Write** RGB, each 0..1 (action `light.color`). |
| `lights/<n>/outer_angle` | **St** | number | Spotlight cone **outer** half-angle in **degrees** — the hard beam edge (action `light.outer_angle`). Larger ⇒ wider beam; stock default 45°. Clamped to ~0..89.94°. Writing it also pulls `inner_angle` down to stay ≤ outer, so narrowing it actually narrows the cone (KSA swaps the two if inner > outer). Only affects spotlights (point lights carry but ignore it). |
| `lights/<n>/inner_angle` | **St** | number | Spotlight cone **inner** half-angle in **degrees** — the full-brightness core (action `light.inner_angle`). Clamped to `[0, outer]`. Equal to outer ⇒ hard edge; smaller ⇒ softer falloff. Bring it down with `outer_angle` for a narrow pinpoint/laser. Only affects spotlights. |
| `lights/<n>/goal` | **St** | fraction | Actuate/deploy setpoint 0..1 (action `animation.goal`). **Only present when the light part has an animation.** |
| `lights/<n>/current` | S | scalar | Actual deploy fraction 0..1 (only with an animation). |
| `lights/<n>/state` | S | string | Animation deployment state — `Deployed`/`Retracted`/`Deploying`/`Retracting`/`Broken` (only with an animation). |

> The `goal`/`current`/`state` trio is **co-located** here for convenience: it is the *same*
> vessel-level keyframe animation also reachable under `animations/<n>/` (§3.4.14). Both views write
> the one `animation.goal` action by the animation's vessel-level ordinal, so writing either path
> drives the same actuator — they are not independent. A light part with no animation omits the three
> files entirely.

#### 3.4.12 Docking ports *(present when fitted)* — `…/docking/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `docking/<n>/docked` | S | flag | Docked. |
| `docking/<n>/docked_to` | S | string | Part id docked to, or empty. |
| `docking/<n>/pushoff_impulse` | S | scalar | Separation impulse applied on undock, N·s (newton-seconds). |
| `docking/<n>/undock` | T | write `1` | Undock this port (action `docking.undock`; `EBUSY` if not docked). |

#### 3.4.13 Decouplers *(present when fitted)* — `…/decouplers/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `decouplers/<n>/fired` | S | flag | Has fired (irreversible). |
| `decouplers/<n>/fire` | T | write `1` | Fire (action `decoupler.fire`; re-fire ⇒ `EBUSY`). |

#### 3.4.14 Animations *(present when fitted)* — `…/animations/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `animations/<n>/goal` | **St** | fraction | Deploy setpoint 0..1 (action `animation.goal`). |
| `animations/<n>/current` | S | scalar | Actual deploy fraction 0..1. |
| `animations/<n>/state` | S | string | Deployment state. |

#### 3.4.15 Encounters *(detail; present when any)* — `…/encounters`

NDJSON, one line per predicted closest approach: `{"body":<id>,"ut":<t>,"distance":<m>}`.

#### 3.4.16 Parts *(present when `telemetry_vessel_parts` is on)* — `…/parts/<n>/` (n = part index)

Top-level parts only (subparts are not surfaced); the **welds** anchor picker (§3.7). The list is
cached per vehicle and rebuilt on part-count change or every 10 s. `<n>` is a 0-based index (friendly
to enumerate) but **not** stable across vehicle edits — `instance_id` is the stable handle a weld uses.

| Path | A | Type | Meaning |
|---|---|---|---|
| `parts/<n>/instance_id` | S | uint | Stable part id (`Part.InstanceId`) — pass as `<part_iid>` to a weld. |
| `parts/<n>/id` | S | string | `Part.Id` (can collide across instances of one template). |
| `parts/<n>/display_name` | S | string | Human-readable name. |
| `parts/<n>/template` | S | string | Part template id (`Part.Template.Id`). |
| `parts/<n>/is_root` | S | flag | Whether this is the root part. |
| `parts/<n>/subpart_count` | S | int | Number of subparts (informational). |
| `parts/<n>/position` | S | `x y z` | Part position in the vehicle assembly frame, m. |

#### 3.4.17 Control surface — `…/ctl/` *(present only when a command sink is wired)*

| Path | A | Write | Action key | Phase | Meaning |
|---|---|---|---|---|---|
| `ctl/ignite` | T | `1` | `vessel.ignite` | Frame | Ignite the active engines. |
| `ctl/shutdown` | T | `1` | `vessel.shutdown` | Frame | Shut down the active engines. |
| `ctl/engine` | **St** | `0`/`1` | `vessel.engine` | Frame | Ignition master: read = live `EngineOn`, write `1`=ignite/`0`=shutdown. |
| `ctl/stage` | T | `1` | `vessel.stage` | Frame | Activate the next stage. |
| `ctl/throttle` | **St** | `0..1` | `vessel.throttle` | Frame | Manual throttle fraction; read = current setpoint. |
| `ctl/lights` | **St** | `0`/`1` | `vessel.lights` | Frame | Master lights. |
| `ctl/rcs` | **St** | `0`/`1` | `vessel.rcs` | Frame | Master RCS. |
| `ctl/attitude_mode` | **St** | token | `vessel.attitude_mode` | **Solver** | `manual`, or an auto track-target (see §3.4.18). |
| `ctl/attitude_frame` | **St** | token | `vessel.attitude_frame` | **Solver** | Reference frame for the named modes (see §3.4.18). |
| `ctl/attitude_target` | **St** | `x y z w` | `vessel.attitude_target` | **Solver** | Custom **Body→CCI** quaternion; the autopilot points body **+X** along it. |
| `ctl/burn` | **St** | `ut dvx dvy dvz` | `vessel.burn` | **Solver** | Schedule an impulsive burn at `ut` with a CCI Δv vector. |
| `ctl/focus` | T | `1` | `camera.focus` | Frame | Move the camera to this vessel (view-only; no control change). |

> **Solver phase matters.** `attitude_mode`/`attitude_frame`/`attitude_target`/`burn` write
> `FlightComputer` fields that KSA's async solver snapshots-and-restores each step. The mod drains
> them inside the solver prefix so they stick; a naive frame-phase write would flash on then revert.
> All transports get the right phase automatically (derived from the action key). As an author you
> just write the file — but expect these to take effect on the **next solver step** (~10 Hz), not
> instantly.

#### 3.4.18 Attitude tokens (accepted values)

`ctl/attitude_mode` (case-insensitive): `manual`, `Prograde`, `Retrograde`, `Normal`, `AntiNormal`,
`RadialOut`, `RadialIn`, `Toward`, `Away`, `Antivel`, `Align`, `Forward`, `Backward`, `Up`, `Down`,
`Ahead`, `Behind`, `Outward`, `Inward`, `PositiveDv`, `NegativeDv`, `Custom`, `None`.

`ctl/attitude_frame` (case-insensitive): `EclBody`, `EnuBody`, `Lvlh`, `VlfBody`, `BurnBody`, `Dock`.

### 3.5 `/events`

`Sm` — NDJSON of discrete events diffed from snapshots, one line per event:
`{"ut":…,"type":…,"vessel":<id?>,"detail":…}` (`vessel` omitted for global events). Types include:
`situation-change`, `engine-state`, `flameout`, `docked`, `undocked`, `decoupled`,
`animation-complete`, `battery-depleted`, `battery-charged`, vessel appeared/vanished.

### 3.6 `/status` *(present whenever the command sink is wired)*

| Path | A | Format | Meaning |
|---|---|---|---|
| `status/game_version` | S | string | KSA version string (`unknown` until sampled). |
| `status/sampler` | S | `ok <hz>` / `idle` | Sampler cadence. |
| `status/accessors` | S | NDJSON | One line per **degraded** integration accessor: `{"name":…,"since_ut":…,"error":…}`. Empty when healthy. |
| `status/transports` | S | string | Bound transport summary (ports, control on/off). |

### 3.7 `/debug/**` *(present only when `debug_namespace=true`)*

The cheat surface. Exempt from the `control_all_vessels` authority gate (it is its own opt-in).

| Path | A | Write | Action key | Phase | Meaning |
|---|---|---|---|---|---|
| `debug/vessels/<id>/teleport` | **St** | `px py pz vx vy vz` | `debug.teleport` | Frame | Set the vessel's **CCI state vector** (position m, velocity m/s) about its **current parent body**. See §6. |
| `debug/vessels/<id>/refill_fuel` | T | `1` | `debug.refill_fuel` | **Solver** | Refill all consumables. |
| `debug/vessels/<id>/refill_battery` | T | `1` | `debug.refill_battery` | **Solver** | Refill all batteries. |
| `debug/vessels/<id>/docking/<n>/pushoff_impulse` | **St** | number (N·s ≥0) | `debug.docking_pushoff` | Frame | Override a docking port's undock separation impulse (`DockingPort.PushoffImpulse`). |
| `debug/time/warp` | **St** | factor | `debug.warp` | Frame | Set the time-warp factor directly (`Universe.SetSimulationSpeed`). |
| `debug/focus` | **St** | vehicle/body id | `camera.focus` | Frame | Move the camera to any astronomical by id (view-only). |
| `debug/control_vessel` | **St** | vehicle id | `debug.control_vessel` | Frame | Focus **and** take control of a vehicle by id. (4750: KSA may refuse a target that isn't `controllable` — pre-check the `controllable` read; outcome to be confirmed in a live flight.) |
| `debug/always_render_iva` | **St** | `0`/`1` | `debug.always_render_iva` | Frame | Global render cheat: force interior (IVA) part meshes to render outside the IVA camera. Vessel-agnostic. |
| `debug/vessels/<id>/weld` | **St** | `<target> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <lock>` | `debug.weld_create` | Frame | Weld this vessel (source) to a target part with an explicit pose. See **welds** below. Read = the current spec for this source, or empty. |
| `debug/vessels/<id>/weld_here` | **St** | `<target> <part_iid> [<lock>]` | `debug.weld_here` | Frame | Weld at the **current** relative pose (captured now). `lock` defaults to `1`. |
| `debug/vessels/<id>/unweld` | T | `1` | `debug.weld_remove` | Frame | Remove this source's weld. |
| `debug/welds/clear` | T | `1` | `debug.weld_clear` | Frame | Remove **all** welds. Vessel-agnostic. |
| `debug/welds/count` | S | int | — | — | Number of active welds. |
| `debug/welds/<source>/target` | S | string | — | — | The anchor vessel id. |
| `debug/welds/<source>/part` | S | uint | — | — | Anchor part `instance_id` (`0` = target body frame). |
| `debug/welds/<source>/offset` | S | `x y z` | — | — | Position offset in the anchor frame (m). |
| `debug/welds/<source>/rotation` | S | `pitch yaw roll` | — | — | Orientation offset (deg; display — the weld is driven by an exact quaternion). |
| `debug/welds/<source>/lock_rotation` | S | `0`/`1` | — | — | Whether orientation is locked to the anchor. |
| `debug/welds/<source>/enabled` | **St** | `0`/`1` | `debug.weld_enable` | Frame | Suspend/resume this weld (keeps the entry). |
| `debug/thug_life/help` | S | text | — | — | Console-friendly usage readme + worked examples (EVA Kittens `Hunter`/`Polaris`/`Banjo`). `cat` it. |
| `debug/thug_life/add` | **St** | `<vessel> <part_iid>` or `<vessel> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <w> <h>` | `debug.thug_life_add` | Frame | Anchor a "thug life" sunglasses quad to a part. 2-token form defaults the transform; 10-token form is explicit. Read = empty. See **thug-life** below. |
| `debug/thug_life/clear` | T | `1` | `debug.thug_life_clear` | Frame | Remove **all** sunglasses. Vessel-agnostic. |
| `debug/thug_life/count` | S | int | — | — | Number of active sunglasses entries. |
| `debug/thug_life/<id>/vessel` | S | string | — | — | The anchor vessel id. |
| `debug/thug_life/<id>/part` | S | uint | — | — | Anchor part `instance_id` (`0` = vehicle body frame). |
| `debug/thug_life/<id>/position` | **St** | `x y z` | `debug.thug_life_position` | Frame | Offset in the part's local frame (m). |
| `debug/thug_life/<id>/rotation` | **St** | `pitch yaw roll` | `debug.thug_life_rotation` | Frame | Orientation offset in the part's local frame (deg). |
| `debug/thug_life/<id>/size` | **St** | `width height` | `debug.thug_life_size` | Frame | Quad size (m). |
| `debug/thug_life/<id>/visible` | **St** | `0`/`1` | `debug.thug_life_visible` | Frame | Show/hide (keeps the entry). |
| `debug/thug_life/<id>/remove` | T | `1` | `debug.thug_life_remove` | Frame | Remove this entry. |
| `debug/thug_life/<id>/spec` | S | spec line | — | — | The write-compatible 10-token spec (echo to `add` to recreate as a new id). |

**welds** (the "weld one vessel rigidly to another, anchored to a part" cheat — a game hack):
- Discover anchor parts under `vessels/by-id/<target>/parts/<n>/` (§3.4); each part's `instance_id` is the
  stable handle you pass as `<part_iid>` (`0` ⇒ anchor to the target's body/CoM frame). A vessel may be the
  **source** of at most one weld (re-writing `weld` replaces it); many sources may anchor to one target.
- `weld` takes an explicit pose; `weld_here` captures the current source↔anchor pose so the source stays put
  and then tracks rigidly — the practical path (computing offsets by hand is hard).
- The source is repositioned every frame on the game thread (after the vehicle solvers). Errnos: `EBUSY`
  (source==target, or the two orbit different bodies), `ENOENT` (target/part gone), `EINVAL` (bad arity/values).
- Welds are **runtime-only** (never persisted) and cleared on mod unload.

**thug-life** (the "anchor a flat sunglasses-meme quad to a part" cosmetic cheat — a pure visual hack):
- Each `add` creates a new entry with an integer **id** — the **smallest free slot** (reused after
  `remove`/`clear`, so the numbering tracks the live set rather than growing unbounded) — that appears as
  `debug/thug_life/<id>/`. Many entries may share a vessel/part. Discover anchor parts under
  `vessels/by-id/<vessel>/parts/<n>/` (§3.4); pass a part's `instance_id` (`0` ⇒ the vehicle body frame).
- The quad is a procedurally-generated `26×5` sunglasses texture drawn each frame in world space, tracking
  the anchor part. `position`/`rotation` are in the part's local frame; `width`/`height` size it (defaults
  `0.975`/`0.1875` m keep the texture aspect). `visible 0` keeps the entry but skips drawing.
- The render hook + GPU resources are installed **lazily on the first entry** and torn down when the last
  entry is removed (and at unload) — zero cost when unused. Entries are **runtime-only** (never persisted).
  Errnos: `ENOENT` (vessel/part/id gone), `EINVAL` (bad arity/values), `EIO` (renderer unavailable).

### 3.8 `/display` *(the screen stream — STREAM_PLAN.md)*

A downscaled, frame-rate-limited render of the KSA viewport (the public offscreen scene target — no
UI), encoded as the **Kitty terminal graphics protocol**. A guest program `cat`s the stream to its SSH
stdout and any kitty-capable terminal renders it (in-game purrTTY tabs *or* external emulators). The
controls are plain files, so any SSH client tunes the feed; they mutate the host-side capture directly
(not a `SimCommand`), so they actuate immediately with no game-thread round-trip. **Default off** — the
capture costs nothing until a client writes `1` to `enabled` *and* opens `stream`.

| Path | A | Format / Write | Meaning |
|---|---|---|---|
| `display/enabled` | **St** | `0`/`1` (also `on`/`off`, `true`/`false`) | Master gate. Capture runs only while set **and** ≥1 reader has `stream` open. |
| `display/fps` | **St** | integer (clamped 1..60) | Stream cadence, decoupled from the game frame rate. |
| `display/width` | **St** | pixels (clamped 16..1920) | Downscale target width; the terminal renders the image at this pixel size. |
| `display/height` | **St** | pixels (clamped 16..1920) | Downscale target height. |
| `display/encoding` | **St** | `rgba-zlib` (default) \| `rgba` | Frame wire format (zlib-deflated RGBA — 3–10× smaller on the wire, the default — or raw RGBA). Unknown ⇒ `EINVAL`. `rgba-zlib` requires purrTTY's **2026-07-02+ native**: earlier pins memory-corrupted on compressible `o=z` payloads (a zig 0.15.2 std flate bug — purrtty gotcha 34, fixed by the `purrtty/vt-video-fixes` native patch). `rgba` remains the pixel-exact zero-inflate fallback. |
| `display/format` | S | `WxH@fps enc` | Read-only discovery of the live parameters. |
| `display/stream` | **Smb** | — (read) | The binary Kitty frame feed. A **continuous** stream: a single `cat /sim/display/stream` blocks for each next frame and renders it forever (never EOF; Ctrl-C to stop). Each frame is a complete, self-contained, LF-free Kitty unit; a slow reader skips to the latest (drop-old); multiple readers fan out. Frames come in two kitty forms: a **keyframe** (`a=T`, transmit+display — emitted for the first frame, when a new reader opens the stream, on a size/encoding change, and at least once per second) and steady-state **replace** units (`a=t`, transmit-only) that swap the fixed image id's bytes in place under the placement the last keyframe created — a consumer attaching mid-stream sees video within ≤1 s, and steady state causes no per-frame placement churn in the terminal. **Delivery granularity:** a guest `read()` completes only once its full buffer fills (kernel 9p semantics — no partial-read wakeups), so consumer latency = read-buffer ÷ data-rate. `cat` is fine at video rates; to consume a *low-rate* feed use small reads (`dd if=/sim/display/stream bs=64`). |

Out-of-range writes to the numeric controls **clamp** (and succeed), matching the config's clamp-don't-reject rule.

> **Debug harness (dormant):** `DisplaySurface.PngDumpDirectory` (settable only in code — see the
> comment at the construction site in `Mod.cs`) switches `stream` from Kitty bytes to a host-side
> dump of one `screencap-<ISO 8601 UTC>.{png,kitty}` pair per second plus a plain-text progress line
> per pair on the feed. It is the tier-1/2 validation harness from STREAM_PLAN.md §11 (used to
> corner the 2026-07 purrTTY libghostty `o=z` corruption); normal builds leave it unset.

---

## 4. The atomic `telemetry` document

`vessels/<id>/telemetry` (and `GET /v1/vessels/{id}/telemetry`, MQTT `gatos/sim/vessels/<id>/telemetry`)
return one JSON object — the frozen compact shape (`Formats.VesselTelemetry`). This is the
recommended single read for a control loop (self-consistent, no stitching). Fields:

```jsonc
{
  "seq": 1234,            // snapshot sequence (monotonic; use to detect new data)
  "ut": 56789.123,        // universal sim time, s
  "warp": 1,              // time-warp factor
  "id": "Hunter",
  "sit": "Freefall",      // situation
  "controlled": true,
  "controllable": true,   // KSA accepts control commands (has a Control Module)
  "parent": "Earth",      // parent body id (the CCI center)
  "pos_cci": [x, y, z],   // position in CCI, m
  "pos_ecl": [x, y, z],   // position in parent ecliptic, m
  "vel_cci": [x, y, z],   // velocity in CCI, m/s
  "vel":  { "orb": .., "surf": .., "inr": .. },   // speeds, m/s
  "alt":  { "baro": .., "radar": .. },            // altitudes, m
  "mass": { "t": .., "d": .., "p": .. },          // total / dry / propellant, kg
  "att_q": [x, y, z, w],  // Body→CCI quaternion
  "orbit": {              // present only while in orbit
    "ap": .., "pe": ..,   // apoapsis/periapsis altitude, m
    "ecc": .., "inc": .., // eccentricity, inclination(deg)
    "sma": .., "period": ..,
    "ta": .., "t_ap": .., "t_pe": ..   // true anomaly(deg), time-to-ap/pe(s)
  },
  "power": { "prod": .., "cons": .., "battery": .. }  // W, W, charge 0..1 (battery omitted if none)
}
```

---

## 5. The command model

Every write — over any transport — becomes one immutable `SimCommand` routed by action key
(`gatOS.GameMod/Game/Ksa/KsaCatalog.cs`). The fields:

| Field | Type | Use |
|---|---|---|
| `vessel_id` | string | target vehicle id (the stable `Vehicle.Id`; for `camera.focus`/`control_vessel` the id rides in `token`). |
| `action` | string | the action key (table below). |
| `ordinal` | int | module index (engine/rcs/light/animation/decoupler/docking); `-1` for vessel-level. |
| `value` | number | scalar arg: `0`/`1` flag, `0..1` fraction, or number. |
| `values` | number[] | vector arg: quaternion (4), burn `ut dvx dvy dvz` (4), color `r g b` (3), teleport `px py pz vx vy vz` (6). |
| `token` | string | symbolic arg: attitude mode/frame token, or a target id for focus/control. |

### 5.1 Action key catalog (the complete write surface)

| Action | ordinal | arg | Phase | `/sim` file it backs | Notes |
|---|---|---|---|---|---|
| `vessel.ignite` | — | value `1` | Frame | `ctl/ignite` | one-shot |
| `vessel.shutdown` | — | value `1` | Frame | `ctl/shutdown` | one-shot |
| `vessel.engine` | — | value `0`/`1` | Frame | `ctl/engine` | ignition master |
| `vessel.stage` | — | value `1` | Frame | `ctl/stage` | one-shot |
| `vessel.throttle` | — | value `0..1` | Frame | `ctl/throttle` | |
| `vessel.lights` | — | value `0`/`1` | Frame | `ctl/lights` | |
| `vessel.rcs` | — | value `0`/`1` | Frame | `ctl/rcs` | |
| `vessel.attitude_mode` | — | token | **Solver** | `ctl/attitude_mode` | §3.4.18 |
| `vessel.attitude_frame` | — | token | **Solver** | `ctl/attitude_frame` | §3.4.18 |
| `vessel.attitude_target` | — | values `[x,y,z,w]` | **Solver** | `ctl/attitude_target` | Body→CCI quaternion |
| `vessel.burn` | — | values `[ut,dvx,dvy,dvz]` | **Solver** | `ctl/burn` | CCI Δv |
| `engine.active` | engine n | value `0`/`1` | Frame | `engines/<n>/active` | |
| `engine.min_throttle` | engine n | value `0..1` | Frame | `engines/<n>/min_throttle` | |
| `rcs.active` | rcs n | value `0`/`1` | Frame | `rcs/<n>/active` | |
| `light.on` | light n | value `0`/`1` | Frame | `lights/<n>/on` | |
| `light.brightness` | light n | value number | Frame | `lights/<n>/brightness` | |
| `light.color` | light n | values `[r,g,b]` | Frame | `lights/<n>/color` | |
| `light.outer_angle` | light n | value number (deg) | Frame | `lights/<n>/outer_angle` | outer cone half-angle; clamped ~0..89.94°; also lowers inner to ≤ outer |
| `light.inner_angle` | light n | value number (deg) | Frame | `lights/<n>/inner_angle` | inner cone half-angle; clamped `[0, outer]` |
| `animation.goal` | anim n | value `0..1` | Frame | `animations/<n>/goal`, `solar/<n>/goal`, `lights/<n>/goal` | one ordinal, three views |
| `decoupler.fire` | decoupler n | value `1` | Frame | `decouplers/<n>/fire` | one-shot |
| `docking.undock` | docking n | value `1` | Frame | `docking/<n>/undock` | one-shot |
| `camera.focus` | — | token = id | Frame | `ctl/focus`, `bodies/<id>/focus`, `debug/focus` | view-only; no authority gate |
| `debug.control_vessel` | — | token = id | Frame | `debug/control_vessel` | grants control |
| `debug.teleport` | — | values `[px,py,pz,vx,vy,vz]` | Frame | `debug/vessels/<id>/teleport` | CCI about current parent |
| `debug.refill_fuel` | — | value `1` | **Solver** | `debug/vessels/<id>/refill_fuel` | |
| `debug.refill_battery` | — | value `1` | **Solver** | `debug/vessels/<id>/refill_battery` | |
| `debug.docking_pushoff` | docking n | value N·s | Frame | `debug/vessels/<id>/docking/<n>/pushoff_impulse` | |
| `debug.warp` | — | value factor | Frame | `debug/time/warp` | vessel-agnostic (`vessel_id` ignored) |
| `debug.always_render_iva` | — | value `0`/`1` | Frame | `debug/always_render_iva` | vessel-agnostic render cheat |
| `debug.weld_create` | — | token = target id; values `[part_iid,x,y,z,pitch,yaw,roll,lock]` | Frame | `debug/vessels/<id>/weld` | `vessel_id` = source; explicit pose |
| `debug.weld_here` | — | token = target id; values `[part_iid,lock]` | Frame | `debug/vessels/<id>/weld_here` | `vessel_id` = source; captures current pose |
| `debug.weld_remove` | — | value `1` | Frame | `debug/vessels/<id>/unweld` | `vessel_id` = source |
| `debug.weld_enable` | — | value `0`/`1` | Frame | `debug/welds/<source>/enabled` | suspend/resume |
| `debug.weld_clear` | — | — | Frame | `debug/welds/clear` | vessel-agnostic; removes all welds |
| `debug.thug_life_add` | — | token = vessel id; values `[part_iid]` (transform defaulted) or `[part_iid,x,y,z,pitch,yaw,roll,w,h]` | Frame | `debug/thug_life/add` | vessel-agnostic; creates a new sunglasses entry (lowest free id) |
| `debug.thug_life_remove` | entry id | value `1` | Frame | `debug/thug_life/<id>/remove` | vessel-agnostic; id in `ordinal` |
| `debug.thug_life_clear` | — | — | Frame | `debug/thug_life/clear` | vessel-agnostic; removes all |
| `debug.thug_life_position` | entry id | values `[x,y,z]` | Frame | `debug/thug_life/<id>/position` | id in `ordinal` |
| `debug.thug_life_rotation` | entry id | values `[pitch,yaw,roll]` | Frame | `debug/thug_life/<id>/rotation` | id in `ordinal` |
| `debug.thug_life_size` | entry id | values `[width,height]` | Frame | `debug/thug_life/<id>/size` | id in `ordinal` |
| `debug.thug_life_visible` | entry id | value `0`/`1` | Frame | `debug/thug_life/<id>/visible` | id in `ordinal` |

### 5.2 Writing over each transport

**9P / `/sim` file** — write the value text into the file:
```sh
echo 0.5 > /sim/vessels/active/ctl/throttle
echo "6578100 0 0 0 7784 0" > /sim/debug/vessels/Hunter/teleport
```

**HTTP `POST /v1/command`** — JSON body (the canonical generic write):
```json
{ "vessel_id": "Hunter", "action": "debug.teleport", "values": [6578100,0,0,0,7784,0] }
```
Response `200 {"outcome":"ok"}`, or `{ "errno": "...", "message": "..." }` with the mapped status.

**HTTP `POST /v1/fs/<path>`** — raw value body (the file twin), e.g.
`POST /v1/fs/vessels/active/ctl/throttle` body `0.5` → `200 {"outcome":"ok"}`.

**MQTT** — publish `gatos/command` (the JSON) or `gatos/sim/<path>/set` (the raw value).

---

## 6. Teleport semantics (read carefully)

`debug.teleport` takes a **6-component CCI state vector** `px py pz vx vy vz` (position meters,
velocity m/s) and applies it **about the vessel's *current* parent body** via
`Orbit.CreateFromStateCci(parent, …)` + `Vehicle.Teleport`. Consequences:

- The frame is **CCI** of the current parent (`vessels/<id>/parent`). Z = parent spin axis (north),
  X = vernal point (fixed in the equatorial plane), Y completes the right-handed set. An orbit lying
  in the X–Y plane is **equatorial** (inclination 0).
- To place a vessel in an orbit *around Earth*, the vessel must **already be parented to Earth**
  (inside Earth's SOI). The teleport does not change which body it orbits — it sets the state about
  whatever parent it currently has.
- A **circular** orbit of radius `r = body.radius + altitude` needs speed `v = sqrt(mu / r)` with the
  velocity perpendicular to the position. Example equatorial circular state: `[r,0,0, 0,v,0]`.
- "Ahead/behind on the same orbit by `d` meters" = advance/retard the **true anomaly** by
  `Δθ = d / r` (rotate both position and velocity by `Δθ` about the orbit normal). For small `d`
  this is ≈ offsetting position along the velocity unit vector by `d`.

`mu` and `radius` come from `/sim/bodies/<parent>/{mu,radius}` (or SDK `bodies()` → `mu`,
`mean_radius`). See `.claude/skills/gatos/recipes.md` for a complete teleport program.

---

## 7. HTTP `/v1` endpoint reference (`gatOS.Http/SimHttpServer.cs`)

Base URL: `$GATOS_HTTP` (guest) or `http://127.0.0.1:<http_preferred_port>/v1` (host, default `4242`).
Loopback only, no auth. Aggregate reads serialize the snapshot via `SimJson`.

| Method | Route | Returns |
|---|---|---|
| GET | `/v1/snapshot` | the whole `SimSnapshot` (atomic). |
| GET | `/v1/openapi.json` | OpenAPI 3.1 document. |
| GET | `/v1/time` | `{ut,warp,sim_dt,warp_speeds,auto_warp_active,auto_warp_target_ut}`. |
| GET | `/v1/status` | integration health + transports. |
| GET | `/v1/system` | `SystemSnapshot`. |
| GET | `/v1/bodies` | `BodySnapshot[]`. |
| GET | `/v1/bodies/{id}` | one body (raw id; `404` if gone). |
| GET | `/v1/vessels` | `string[]` of vessel ids. |
| GET | `/v1/vessels/{id}` | one `VesselSnapshot` (raw id). |
| GET | `/v1/vessels/{id}/telemetry` | the compact telemetry doc (§4). |
| GET | `/v1/fs/<path>` | raw field value `text/plain` + trailing `\n` (requires `http_field_endpoints`). |
| GET | `/v1/fs/<path>?stream=1` | SSE; one `data: <value>` per change (multi-line split per line). |
| POST | `/v1/fs/<path>` | write raw value to a control/debug field → `{"outcome":"ok"}`. |

The `/display` control leaves mirror leaf-by-leaf through `/v1/fs/display/*` (e.g. `POST /v1/fs/display/enabled`
with body `1`, `GET /v1/fs/display/format`) and MQTT `gatos/sim/display/*` — by construction, since they
are ordinary scalar control files. The binary `display/stream` feed is `IsStreaming` and so is **excluded**
from the field mirror (a dedicated HTTP media route is deferred — STREAM_PLAN.md S8); consume it from the
guest over 9p.
| POST | `/v1/command` | the generic JSON command (§5) → `{"outcome":"ok"}`. |
| GET | `/v1/events` | SSE of `{ut,type,vessel?,detail}`. |
| GET | `/v1/vessels/{id}/stream` | SSE of the per-vessel telemetry stream line. |
| GET | `/v1/time/wait?until=<ut>` | long-poll; blocks until sim time ≥ `until`, returns `{"reached_ut":…}`. |

---

## 8. Units quick reference

| Quantity | Unit |
|---|---|
| length / position / altitude / radius / SMA | meters |
| velocity / speed | m/s |
| mass | **kg** (KSA native — no tonnes) |
| μ (gravitational parameter) | m³/s² |
| thrust | N |
| Isp | s (× g₀ = 9.80665 for exhaust velocity) |
| time / ut / period | s |
| angles in `/sim` files (lat, lon, inc, lan, argpe, true_anomaly, navball, sun_aoa) | **degrees** |
| body rotation rate, body rates, angular accel | **rad/s**, rad/s, rad/s² |
| power | W; energy/capacity J |
| pressure | Pa; density kg/m³ |
| attitude quaternion | unit `x y z w`, Body→CCI |

---

## 9. Maintenance mandate (MUST)

This document is part of the build contract. **Any change to the `/sim` surface must update this
file in the same change.** Concretely, you MUST edit `SPEC_9P_FILESYSTEM.md` whenever you:

1. add/remove/rename a `/sim` node, a `ctl/` control, a `debug/` action, or a per-module file;
2. change a value **format** or **units** (`Formats.cs`, snapshot field semantics);
3. add/change a command **action key**, its `ordinal`/`value`/`values`/`token` shape, or its
   **phase** (`KsaCatalog.cs`, `SimCommand.SolverActions`);
4. add/change an HTTP `/v1` route or MQTT topic, or a config gate that affects availability;
5. change the errno mapping or the archetype of a file.

Also update `docs/KSA_INTEGRATION_MATRIX.md` (the KSA binding view) and the `gatos` skill if the
change affects how programs are written. Keep the "Source of truth in code" pointers at the top
accurate. The code wins; this file must mirror it exactly.
