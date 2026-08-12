# SPEC: the gatOS `/sim` 9P filesystem

> **This file is the single source of truth for the `/sim` API surface.** Every path the guest can
> `cat`/`echo`, every HTTP `/v1` route, every MQTT topic, every command action key, the exact value
> formats, the units, and the read/write semantics are cataloged here. The `gatos` skill
> (`.agents/skills/gatos/SKILL.md`) and any program written against gatOS reference this document.
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
> `gatOS.SimFs/Snapshots/SimSnapshot.cs` (fields + units), `gatOS.SimFs/Commands/**` (write parsing
> + the timed scheduler), `gatOS.SimFs/Camera/**` (camera grammars, compositor, track schema),
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
| **B** | BATCH | usage hint (one line) | `<path> <value>` command lines + a terminating `commit` line — the whole group executes atomically in one game tick (§3.10) |
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

The **audio clip store** (§3.9) and the **camera track store** (§3.11) add four VFS-level errnos on
their upload surfaces (thrown by the write itself, mid-stream — not by a command):

| errno | HTTP | Meaning |
|---|---|---|
| `EFBIG` | 413 | a clip write past `audio_max_clip_bytes`, or a track write past `camera_max_track_bytes` |
| `ENOSPC` | 507 | the store byte cap (`audio_max_total_bytes` / `camera_max_total_bytes`) or the count cap (`audio_max_clips` / `camera_max_tracks`) is full |
| `EEXIST` | 409 | 9P `Tlcreate` of a clip/track name that is already taken |
| `EPERM` | 403 | `mkdir`/`rename` inside `audio/file/` or `camera/track/` (flat files only) |

### 2.5 Config gates (`gatos.default.toml` → live `gatos.toml`)

| Key | Default | Effect |
|---|---|---|
| `telemetry_enabled` | `true` | master read feed; `false` freezes `/sim` data |
| `telemetry_vessel_detail` | `true` | per-vessel detail (navball/environment/per-module, orbit extras); off ⇒ core only |
| `telemetry_vessel_parts` | `true` | per-vessel `parts/` list (the welds anchor picker; cached); off ⇒ the subtree vanishes |
| `telemetry_bodies` | `true` | `/sim/bodies` + `/sim/system` |
| `telemetry_events` | `true` | `/sim/events` diffs |
| `control_enabled` | `true` | master write switch; `false` ⇒ every control write `EACCES` |
| `control_all_vessels` | `true` | `false` ⇒ only the **controlled** vessel is commandable (`EACCES` otherwise); `camera.focus`, `vessel.scale`, `vessel.always_render` and the `debug.*` namespace are exempt |
| `debug_namespace` | `true` | exposes `/sim/debug/**` and the `debug.*` actions; `false` ⇒ those vanish / `EACCES` |
| `sample_rate_hz` | `10` | master cadence (1..120) |
| `telemetry_bodies_rate_hz` | `0` | bodies resample cadence (0 = every tick); lower ⇒ `/sim/bodies` + `system` update at that cadence (values between are the previous sample) |
| `http_enabled` / `http_preferred_port` | `true` / `4242` | HTTP `/v1` server (falls back to ephemeral on clash) |
| `http_field_endpoints` | `true` | the `/v1/fs/<path>` mirror (off ⇒ those routes `ENOENT`) |
| `mqtt_enabled` / `mqtt_preferred_port` | `true` / `1883` | embedded MQTT broker |
| `mqtt_publish_hz` | `0` | cap on the MQTT world-topic cadence (0 = every snapshot; below the sample rate the broker coalesces to the newest snapshot). MQTT topics are also **subscription-gated**: a topic no live filter matches is not published (a new subscription forces its retained baseline within a cycle), and a vanished vessel's retained topics are cleared with an empty payload |
| `command_timeout_ms` | `2000` | how long a write waits for the game thread before `ETIMEDOUT` |
| `display_enabled` | `false` | boot seed for `/sim/display/enabled` — the screen stream (§3.8); **off by default** |
| `display_fps` / `display_width` / `display_height` | `15` / `320` / `180` | boot seeds for the stream cadence + downscale size (runtime control is the `/sim/display/*` files) |
| `display_encoding` | `rgba-zlib` | boot seed for the frame encoding (`rgba-zlib` \| `rgba`; zlib needs purrTTY's 2026-07-02+ native — §3.8) |
| `audio_enabled` | `true` | serve `/sim/audio` (§3.9) — userland audio playback; `false` removes the surface (and the `/v1/audio` routes) entirely |
| `audio_max_clip_bytes` | `16777216` | per-clip upload cap (`EFBIG` past it; clamped 4 KiB..256 MiB) |
| `audio_max_total_bytes` | `67108864` | store-wide byte cap (`ENOSPC`; clamped ≥ clip cap..1 GiB) |
| `audio_max_clips` | `64` | clip-count cap (`ENOSPC`; clamped 1..1024) |
| `audio_max_channels` | `16` | concurrent playback channels (`EBUSY` past it; clamped 1..64) |
| `iva_physics_enabled` | `false` | boot seed for `/sim/debug/iva/enabled` — the IVA cabin-physics master switch (§3.7); **off by default, and off means no simulation exists at all** |
| `iva_run_outside_iva` | `false` | boot seed for `/sim/debug/iva/run_outside_iva`; off ⇒ leaving the IVA camera parks the objects |
| `iva_max_objects` | `16` | floating objects per vessel (`EBUSY` past it; clamped 1..64) |
| `iva_max_object_size` | `0.5` | largest bounding-box extent (m) a SubPart may have and still be adoptable (`EINVAL` past it; clamped 0.01..5) |
| `iva_density_kg_m3` | `300` | density used to derive object mass from proxy volume (clamped 1..20000) |
| `iva_max_speed` | `15` | velocity clamp in m/s — the anti-tunnelling guard (clamped 0.1..200) |
| `iva_friction` / `iva_restitution` | `0.6` / `1.0` | contact friction, and bounciness as the maximum recovery velocity in m/s |
| `iva_substep_hz` / `iva_max_substeps_per_frame` | `120` / `8` | fixed integration rate and the post-hitch catch-up bound |
| `iva_double_sided_interior` | `true` | emit interior triangles in both windings so art winding cannot let objects fall through walls |
| `iva_impact_speed` | `0.4` | speed change (m/s) in one substep that fires an `iva.impact` event |
| `schedule_enabled` | `true` | serve `/sim/ctl/timed_batch` + the `/sim/ctl/schedules/` registry (§3.10); `false` removes both nodes and answers every `schedule.*` action `EOPNOTSUPP` — **and `camera/play`\|`set`\|`stop` too**, because a camera track is a registry entry (§3.11) |
| `schedule_max_live` | `16` | concurrent live players — schedules **and** the camera track (`EINVAL` on the commit past it; a finished player is evicted oldest-first to make room, emitting `schedule.evicted`); clamped 1..256 |
| `schedule_max_entries` | `8192` | timed entries per schedule (`EINVAL` past it; clamped 1..262144) |
| `schedule_max_bytes` | `1048576` | buffered bytes per open `timed_batch` handle (`EINVAL` past it; clamped 1 KiB..16 MiB) |
| `schedule_default_clock` | `"render"` | clock base for a schedule with no `@clock`: `render` \| `wall` \| `ut` (§3.10); anything else warns and falls back to `render` |
| `camera_enabled` | `true` | serve `/sim/camera` (§3.11) — the programmable cinematic camera; `false` removes the whole subtree and answers every `camera.*` action `EOPNOTSUPP` **except `camera.focus`**, which predates it and keeps working |
| `camera_max_tracks` | `32` | uploaded track count cap (`ENOSPC` past it; clamped 1..512) |
| `camera_max_track_bytes` | `1048576` | per-track JSON byte cap (`EFBIG` past it; clamped 4 KiB..64 MiB) |
| `camera_max_total_bytes` | `8388608` | store-wide byte cap across all tracks (`ENOSPC`; clamped ≥ the per-track cap..256 MiB) |
| `camera_max_keys` | `4096` | keyframes per animated track channel (`EINVAL` past it, at parse time; clamped 2..65536) |
| `camera_fov_min` / `camera_fov_max` | `1` / `179` | the `camera/pose/fov` range in degrees (`EINVAL` outside it) — deliberately wider than the game's own 15–120, since `SetFieldOfView` does not clamp; clamped 0.1..179, and `fov_max` is additionally floored at `fov_min` |
| `camera_release_blend_s` | `0.6` | eased hand-back duration in seconds when `camera/enabled 0` releases the camera (`0` = an immediate cut); clamped 0..10. **The TOML key is snake_case; the C# property is `CameraReleaseBlendS`** |
| `camera_allow_time_channel` | `true` | let a camera track's `time` channel drive simulation speed (§3.11); **additionally requires `debug_namespace`** — with either gate off the channel is ignored with a one-shot host warning and the shot plays at 1× (a warning, never an error) |

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
| `scale` | **St** | scalar | Uniform model scale factor; read = current (best-effort; `1` = unscaled/unknown), **write any finite value > 0** to rescale the whole vessel model (action `vessel.scale`, Frame, one-shot). Default `1.0`; no upper clamp; `0`/negative/non-finite → `EINVAL`. Exempt from the active-vessel authority gate — works on **any** vessel by id. The game reverts the scale when it rebuilds the vessel (scene reload / staging / undock). First per-vessel control intentionally placed here rather than under `/sim/debug/`. |
| `always_render` | **St** | flag | Render-distance override; read = current mark, **write `1`** to keep this vessel rendered at any distance (bypasses KSA's sub-pixel cull — normally a vehicle whose projected diameter falls under 1 px is not drawn), **`0`** to restore the stock cull (action `vessel.always_render`, Frame). Off by default; exempt from the active-vessel authority gate — works on **any** vessel by id, like `scale`. The mark keys on the vessel **id**, so it survives scene rebuilds, and is dropped automatically when the vessel despawns. The render patches behind it exist only while ≥ 1 vessel is marked. Note: EVA kittens render through their own path and are **not** affected. |
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
| `mass/propellant` | S | scalar | Propellant mass, kg. **Includes solid-rocket-motor grain mass** (KSA 5018+), which is itemized under `srb/` (§3.4.8), *not* `tanks/` — so `mass/propellant` − Σ `tanks/` = Σ `srb/<n>/mass`. |
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
| `orbit/time_to_ap` | S | scalar | Seconds to next apoapsis; `0` when the orbit has none (e.g. hyperbolic). |
| `orbit/time_to_pe` | S | scalar | Seconds to next periapsis; `0` when the orbit has none. |
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
| `tanks/<r>/amount` | S | scalar | Current amount, kg. |
| `tanks/<r>/capacity` | S | scalar | Capacity, kg. |
| `tanks/<r>/fraction` | S | scalar | Fill fraction 0..1. |

> **Liquids only — solid rocket motors live under `srb/` (§3.4.8), not here (KSA `2026.7.9.5018`+).**
> `tanks/` enumerates KSA `Tank` modules. Solid propellant introduced with SRBs lives on a separate
> `SolidGrainSegment` module, so a booster's grain contributes **no** `tanks/<r>/` entry, while
> `mass/propellant` (§3.4.1) *does* count it — on a vessel carrying SRBs
> **`mass/propellant` > Σ `tanks/<r>/amount`**, and the difference is Σ `srb/<n>/mass`.

#### 3.4.8 Solid rocket motors *(present when fitted)* — `…/srb/<n>/` (n = SRB index)

One entry per solid rocket motor. **Read-only** — a solid has no throttle and cannot be shut down
once lit; it is ignited through the ordinary engine surface (`ctl/ignite`, `engines/<n>/active`, or
staging) and then burns its grain to depletion. `srb/<n>/engine` names the `engines/<n>` entry that
lights this motor — an SRB appears in **both** trees (`engines/` for thrust/Isp and ignition,
`srb/` for the propellant and burn state that `tanks/` cannot show).

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `srb/<n>/engine` | S | scalar | Index of the `engines/<n>` entry that ignites this motor; `-1` if unresolved. |
| `srb/<n>/part` | S | scalar | `instance_id` of the motor's part (the `parts/` handle, §3.4.17). |
| `srb/<n>/substance` | S | text | Solid propellant substance name. |
| `srb/<n>/grain` | S | text | Grain geometry id — the cast core shape that sets the thrust profile. |
| `srb/<n>/grain_shape` | S | text | The grain geometry's shape name. |
| `srb/<n>/segment_count` | S | scalar | Number of grain segments in the stack. |
| `srb/<n>/valid` | S | flag | `1` when the segment stack resolved; `0` means the motor cannot fire. |
| `srb/<n>/error` | S | text | Why the stack is invalid (empty when `valid` is `1`). |
| `srb/<n>/active` | S | flag | `1` while burning. |
| `srb/<n>/propellant` | S | flag | `1` when burnable grain remains and pressure can be sustained. |
| `srb/<n>/mass` | S | scalar | Current grain mass across the stack, kg. |
| `srb/<n>/mass_initial` | S | scalar | Full-stack grain mass when new, kg. |
| `srb/<n>/mass_unburnable` | S | scalar | Sliver/slag that can never burn (pressure quenches first), kg. |
| `srb/<n>/mass_burnable` | S | scalar | `max(mass − mass_unburnable, 0)`, kg — the usable propellant left. |
| `srb/<n>/fraction` | S | scalar | `mass_burnable` ÷ usable grain when new, 0..1. **The pre-ignition "how much booster is left" read.** |
| `srb/<n>/mass_flow` | S | scalar | Instantaneous propellant flow, kg/s (0 when not burning). |
| `srb/<n>/burn_time` | S | scalar | Seconds of thrust left at the current flow. **0 while not burning** — use `fraction` before ignition. |
| `srb/<n>/burning_area` | S | scalar | Current burning surface area, m² — what drives a solid's thrust curve as the grain regresses. |
| `srb/<n>/chamber_pressure` | S | scalar | Chamber pressure, Pa (0 when not burning). |
| `srb/<n>/chamber_temp` | S | scalar | Chamber temperature, K (0 when not burning). |
| `srb/<n>/exit_pressure` | S | scalar | Nozzle exit pressure, Pa (0 when not burning). |
| `srb/<n>/exit_temp` | S | scalar | Nozzle exit temperature, K (0 when not burning). |
| `srb/<n>/area_ratio` | S | scalar | Nozzle expansion (exit/throat) area ratio as sized for this stack. |

**Grain segments — `srb/<n>/segments/<m>/`.** The stacked casing sections holding the cast
propellant (stacking them is how total impulse is sized in the editor); `<m>` is 0-based.

| Path | A | Format | Meaning / units |
|---|---|---|---|
| `segments/<m>/part` | S | scalar | `instance_id` of the segment's part. |
| `segments/<m>/substance` | S | text | Solid propellant substance name (empty when unconfigured). |
| `segments/<m>/grain` | S | text | Grain geometry id cast into this segment. |
| `segments/<m>/mass` | S | scalar | Current grain mass in this segment, kg. |
| `segments/<m>/mass_initial` | S | scalar | This segment's grain mass when new, kg. |
| `segments/<m>/mass_unburnable` | S | scalar | This segment's share of the unburnable sliver, kg. |
| `segments/<m>/fraction` | S | scalar | Usable grain left in this segment, 0..1. |
| `segments/<m>/radius` | S | scalar | Casing inner radius, m. |
| `segments/<m>/length` | S | scalar | Segment length, m. |
| `segments/<m>/volume` | S | scalar | Grain volume when new, m³. |
| `segments/<m>/burn_depth` | S | scalar | How far the burning surface has regressed from the initial port wall, m. |

> **No controls, by design.** KSA forces a solid's throttle to 0 or 1, so there is nothing to
> actuate here: ignite via `ctl/ignite` / `engines/<n>/active` / `ctl/stage`, and expect the motor to
> run to depletion. `debug/vessels/<id>/refill_fuel` (§3.7) **does** refill solid grains along with
> liquid tanks. Grain geometry and nozzle area ratio are editor-time design choices and are exposed
> read-only.

#### 3.4.9 RCS *(present when fitted)* — `…/rcs/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `rcs/<n>/active` | **St** | flag | Read = active; **write `0`/`1`** (action `rcs.active`). |
| `rcs/<n>/propellant` | S | flag | Propellant available. |
| `rcs/<n>/map` | S | string | Active control-axis flags (e.g. `Pitch|Yaw`). |

#### 3.4.10 Solar panels *(present when fitted)* — `…/solar/<n>/`

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

#### 3.4.11 Generators *(present when fitted)* — `…/generators/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `generators/<n>/active` | S | flag | Producing. |
| `generators/<n>/produced` | S | scalar | Instantaneous power produced, W. |

#### 3.4.12 Lights *(present when fitted)* — `…/lights/<n>/`

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
> vessel-level keyframe animation also reachable under `animations/<n>/` (§3.4.15). Both views write
> the one `animation.goal` action by the animation's vessel-level ordinal, so writing either path
> drives the same actuator — they are not independent. A light part with no animation omits the three
> files entirely.

#### 3.4.13 Docking ports *(present when fitted)* — `…/docking/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `docking/<n>/docked` | S | flag | Docked. |
| `docking/<n>/docked_to` | S | string | Part id docked to, or empty. |
| `docking/<n>/pushoff_impulse` | S | scalar | Separation impulse applied on undock, N·s (newton-seconds). |
| `docking/<n>/undock` | T | write `1` | Undock this port (action `docking.undock`; `EBUSY` if not docked). |

#### 3.4.14 Decouplers *(present when fitted)* — `…/decouplers/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `decouplers/<n>/fired` | S | flag | Has fired (irreversible). |
| `decouplers/<n>/enabled` | S | flag | Whether the decoupler module is enabled. KSA ≥ 2026.8.5.5168 lets players **disable** a part's decoupler module (turning e.g. an adapter into a static fairing); a disabled decoupler **cannot fire**. `1` for every decoupler the player has not explicitly disabled. |
| `decouplers/<n>/fire` | T | write `1` | Fire (action `decoupler.fire`; re-fire ⇒ `EBUSY`; **disabled ⇒ `EOPNOTSUPP`**). |

#### 3.4.15 Animations *(present when fitted)* — `…/animations/<n>/`

| Path | A | Format | Meaning |
|---|---|---|---|
| `animations/<n>/goal` | **St** | fraction | Deploy setpoint 0..1 (action `animation.goal`). |
| `animations/<n>/current` | S | scalar | Actual deploy fraction 0..1. |
| `animations/<n>/state` | S | string | Deployment state. |

#### 3.4.16 Encounters *(detail; present when any)* — `…/encounters`

NDJSON, one line per predicted closest approach: `{"body":<id>,"ut":<t>,"distance":<m>}`.

> **Coverage widened in KSA `2026.7.9.5018`.** Earlier builds skipped any sibling body whose sphere of
> influence was below a flat 10 000 km cutoff; KSA now decides candidacy from orbital geometry (radius-band
> overlap plus an approximate MOID at the mutual nodes). Small moons — Phobos, Deimos — therefore produce
> encounter lines that previous builds silently omitted. The line format is unchanged; programs that treated
> "no encounter entry" as "no approach possible" for small bodies should be re-checked.

#### 3.4.17 Parts *(present when `telemetry_vessel_parts` is on)* — `…/parts/<n>/` (n = part index)

Top-level parts, each with its subparts nested under `subparts/<m>/`; the **welds** anchor picker
(§3.7). The list is cached per vehicle and rebuilt on part-count change or every 10 s. `<n>`/`<m>` are
0-based indexes (friendly to enumerate) but **not** stable across vehicle edits — `instance_id` is the
stable handle a weld uses. A subpart is a full game part with its own runtime-unique `instance_id`,
equally valid as a weld `<part_iid>` (an animated subpart anchor — e.g. a robotics segment — tracks
its live pose).

| Path | A | Type | Meaning |
|---|---|---|---|
| `parts/json` | S | JSON | The **whole part/subpart tree as one JSON document**: an array of part objects (snake_case, the shared record projection) each `{index, instance_id, id, display_name, template, is_root, subpart_count, position_vehicle_asmb:{x,y,z}, subparts:[{index, instance_id, id, display_name, template, position_vehicle_asmb}]}`. The one-`cat` discovery path — e.g. `cat parts/json \| jq -r '.[] \| .. \| .instance_id? // empty'` or find-by-name with `jq '.[] \| select(.display_name=="Solar Array")'`. Serialized only when the parts list actually rebuilds (count change / 10 s); reads in between are served from cache. |
| `parts/<n>/instance_id` | S | uint | Stable part id (`Part.InstanceId`) — pass as `<part_iid>` to a weld. |
| `parts/<n>/id` | S | string | `Part.Id` (can collide across instances of one template). |
| `parts/<n>/display_name` | S | string | Human-readable name. |
| `parts/<n>/template` | S | string | Part template id (`Part.Template.Id`). |
| `parts/<n>/is_root` | S | flag | Whether this is the root part. |
| `parts/<n>/subpart_count` | S | int | Number of subparts (= entries under `subparts/`). |
| `parts/<n>/position` | S | `x y z` | Part position in the vehicle assembly frame, m. |
| `parts/<n>/subparts/<m>/instance_id` | S | uint | Stable subpart id — pass as `<part_iid>` to a weld, same as a part's. |
| `parts/<n>/subparts/<m>/id` | S | string | Subpart `Part.Id`. |
| `parts/<n>/subparts/<m>/display_name` | S | string | Human-readable name. |
| `parts/<n>/subparts/<m>/template` | S | string | Subpart template id. |
| `parts/<n>/subparts/<m>/position` | S | `x y z` | Subpart position in the vehicle assembly frame, m (sampled; the weld tracks the live pose). |

#### 3.4.18 Control surface — `…/ctl/` *(present only when a command sink is wired)*

| Path | A | Write | Action key | Phase | Meaning |
|---|---|---|---|---|---|
| `ctl/ignite` | T | `1` | `vessel.ignite` | Frame | Ignite the active engines. |
| `ctl/shutdown` | T | `1` | `vessel.shutdown` | Frame | Shut down the active engines. |
| `ctl/engine` | **St** | `0`/`1` | `vessel.engine` | Frame | Ignition master: read = live `EngineOn`, write `1`=ignite/`0`=shutdown. |
| `ctl/stage` | T | `1` | `vessel.stage` | Frame | Activate the next stage. |
| `ctl/throttle` | **St** | `0..1` | `vessel.throttle` | Frame | Manual throttle fraction; read = current setpoint. |
| `ctl/lights` | **St** | `0`/`1` | `vessel.lights` | Frame | Master lights. |
| `ctl/rcs` | **St** | `0`/`1` | `vessel.rcs` | Frame | Master RCS (the per-thruster `ThrusterController` active flags). ⚠ This is **not** the flight computer's RCS toggle (keybind **R**) — that is a separate switch, exposed as **`ctl/rcs_mode`** below, and it overrides this one. |
| `ctl/translate` | **St** | `x y z` | `vessel.translate` | Frame | **Manual RCS translation** — the file twin of the player's translate keys. The **signs** command bang-bang thrust along the **body axes** (+x = forward/nose, −x = backward; +y = right, −y = left; +z = down, −z = up; `0` = that axis off — KSA's body frame is X-nose/Y-right/Z-down). Fires every RCS thruster whose `ControlMap` matches, at full thrust, each solver step (`ManualThrustMode.Direct`); magnitudes are ignored. **Latches like a held key** until overwritten — write `0 0 0` to stop. Composes with an active flight-computer attitude hold (auto-attitude strips only the rotation bits). Read = the latched command as signs. Needs translation-mapped RCS thrusters (e.g. the EVA kitten backpack); vessels without them accept the write but nothing fires. |
| `ctl/rotate` | **St** | `x y z` | `vessel.rotate` | Frame | **Manual RCS rotation** — the file twin of the player's rotation keys and the symmetric sibling of `ctl/translate` (W1, plans/AGC_PLAN.md §7.4). The **signs** command bang-bang torque about the **body axes** (+x = roll right, −x = roll left; +y = pitch up, −y = pitch down; +z = yaw right, −z = yaw left — KSA's own torque-command convention on the X-nose/Y-right/Z-down body frame). Fires every RCS thruster whose `ControlMap` matches, at full thrust, each solver step (`ManualThrustMode.Direct`), and drives engine gimbals through the same bits (`ComputeTvcControl`); magnitudes are ignored. **Latches like a held key** until overwritten — write `0 0 0` to stop. **Full authority only with `ctl/attitude_mode = manual`**: an active auto-attitude hold strips the rotation bits (`WithNoRotation()`), the inverse of translate's compose behavior — under a hold, a manual rotation bit only biases the held axis's target rate. Composes with `ctl/translate` (each preserves the other's bits). Read = the latched command as signs. Vessels without rotation-mapped RCS/gimbals accept the write but nothing fires. |
| `ctl/attitude_mode` | **St** | token | `vessel.attitude_mode` | **Solver** | `manual`, or an auto track-target (see §3.4.19). ⚠ KSA ≥ 2026.7.8.4980: the game's **RCS toggle** (`FlightComputer.RCSMode`, default keybind **R**) gates the autopilot's RCS authority — with it off, an auto hold on a vessel whose only attitude authority is RCS **silently does not actuate** (the read-back still reports the mode you set; only engine-gimbal TVC keeps working, and only while burning). That toggle is exposed as **`ctl/rcs_mode`** — read it if a hold seems dead. |
| `ctl/attitude_frame` | **St** | token | `vessel.attitude_frame` | **Solver** | Reference frame for the named modes (see §3.4.19). |
| `ctl/rcs_mode` | **St** | token | `vessel.rcs_mode` | **Solver** | **The flight computer's RCS master switch** — `Enabled` or `Disabled` (case-insensitive), the file twin of the in-game **R** keybind. Distinct from `ctl/rcs`, which toggles the per-thruster `ThrusterController` active flags. ⚠ **Since KSA 2026.8.5.5168 this is a hard cut-off for *manual* RCS too:** while `Disabled`, the game zeroes the manual thruster command flags outright, so **`ctl/translate` and `ctl/rotate` do nothing at all**, and auto attitude holds lose RCS torque authority (only engine-gimbal TVC survives, and only while burning). Read this first when an RCS command appears to be ignored — the `ctl/translate`/`ctl/rotate` read-backs report the *commanded* signs and cannot reveal the condition. |
| `ctl/attitude_target` | **St** | `x y z w` | `vessel.attitude_target` | **Solver** | Custom **Body→CCI** quaternion; the autopilot points body **+X** along it. ⚠ KSA ≥ 2026.7.8.4980: the flight computer's roll mode defaults to **decoupled** ("ANY"), so on a fresh vessel the quaternion's **roll component is not held** — pointing converges, roll floats free unless the player sets a roll mode in-game (loaded saves keep their saved mode). |
| `ctl/burn` | **St** | `ut dvx dvy dvz` | `vessel.burn` | **Solver** | Schedule an impulsive burn at `ut` with a CCI Δv vector. |
| `ctl/focus` | T | `1` | `camera.focus` | Frame | Move the camera to this vessel (view-only; no control change). |

> **Solver phase matters.** `attitude_mode`/`attitude_frame`/`attitude_target`/`burn`/`rcs_mode` write
> `FlightComputer` fields that KSA's async solver snapshots-and-restores each step. The mod drains
> them inside the solver prefix so they stick; a naive frame-phase write would flash on then revert.
> All transports get the right phase automatically (derived from the action key). As an author you
> just write the file — but expect these to take effect on the **next solver step** (~10 Hz), not
> instantly.

#### 3.4.19 Attitude tokens (accepted values)

`ctl/attitude_mode` (case-insensitive): `manual`, `Prograde`, `Retrograde`, `Normal`, `AntiNormal`,
`RadialOut`, `RadialIn`, `Toward`, `Away`, `Antivel`, `Align`, `Forward`, `Backward`, `Up`, `Down`,
`Ahead`, `Behind`, `Outward`, `Inward`, `PositiveDv`, `NegativeDv`, `Custom`, `None`.

`ctl/attitude_frame` (case-insensitive): `EclBody`, `EnuBody`, `Lvlh`, `VlfBody`, `BurnBody`, `Dock`.

`ctl/rcs_mode` (case-insensitive): `Enabled`, `Disabled`.

> **⚠ `ctl/translate` / `ctl/rotate` no longer latch unconditionally (KSA ≥ 2026.8.5.5168).** Both
> still latch until you rewrite them, but the **game** now also clears the latched flags on its own via
> `Vehicle.ClearHeldPlayerInput()`: when the controlled vessel changes, when the **game window loses
> focus**, on a camera-mode switch, and — re-applied *every update* — while the game is not accepting
> vehicle input, while **ImGui has keyboard focus** (i.e. the player is typing into any in-game text
> field), or while **time warp exceeds 30×**. A flight program that holds a translation across a warp
> step or while the player types must **re-assert** it rather than assume the latch survived. This does
> **not** affect `ctl/throttle` or `ctl/ignite`, which are held in different fields.

### 3.5 `/events`

`Sm` — NDJSON of discrete events diffed from snapshots, one line per event:
`{"ut":…,"type":…,"vessel":<id?>,"detail":…}` (`vessel` omitted for global events). Types include:
`situation-change`, `engine-state`, `flameout`, `docked`, `undocked`, `decoupled`,
`animation-complete`, `battery-depleted`, `battery-charged`, vessel appeared/vanished.

Playback completion emits **`audio.finished`** (global; `detail` = `<id> <clip> <reason>` with
reason ∈ `ended` (played out or hit `end=`) | `stopped` (explicit `stop`) | `replaced` (its `id=`
was reused)) — so a program can `grep -m1` for completion instead of polling `audio/status` (§3.9).
Audio events ride the next telemetry sample, so they honor the `telemetry_events` gate.

The **schedule registry** (§3.10) emits five global types, all with `vessel` omitted:

| Type | `detail` |
|---|---|
| `schedule.started` | `<id> kind=schedule entries=<n> duration_ms=<ms.0>` — a committed schedule became live |
| `schedule.finished` | `<id> kind=schedule dropped=<n>` — it ran off its end |
| `schedule.failed` | `<id> entry=<n> <ERRNO>` — the **first** failing entry (the schedule keeps running) |
| `schedule.dropped` | `<id> dropped=<n> total=<n>` — catch-up coalescing discarded entries; **throttled to ≤ 1 per player per second** |
| `schedule.evicted` | `<id> kind=<schedule\|camera-track> reason=max_live` — a *finished* player was reclaimed to free a slot at `schedule_max_live` |

The **camera** (§3.11) emits two global types: **`camera.shot`** (`detail` =
`<id> track=<track> shot=<index> name=<shot-name>`, one per shot boundary, re-armed on every loop
wrap) and **`camera.finished`** (`detail` = `<id> track=<track> kind=camera-track
reason=complete|stopped|replaced`, where `complete` fires exactly once). `<id>` is the player's
registry id — normally the literal `camera`.

The IVA cabin physics (§3.7) emits three per-vessel types: **`iva.impact`** (an object's speed changed
by more than `iva_impact_speed` in one substep — the "clunk" signal to wire to `/sim/audio`; `detail` =
`object <id> (<name>) hit at <speed> m/s`), **`iva.escape`** (an object left the interior bounding box and
was recentred) and **`iva.release`** (an object's SubPart was staged away, so it auto-released). These ride
the telemetry sample too, and so honor `telemetry_events`.

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
| `debug/vessels/<id>/impulse` | **St** | `x y z [cci\|body] [ns\|dv]` | `debug.impulse` | Frame | One-shot impulsive kick: a 3-vector **impulse in N·s** (default; Δv = J ÷ live vessel mass, the `Vehicle.Split` separation-impulse math) or a direct **Δv in m/s** (`dv`), in the parent-**CCI** frame (default) or the **vessel body frame** (`body`; +X = nose/thrust axis). The two keywords may follow the numbers in any order. No propellant is spent; the orbit is rebuilt at the current CCI position with the bumped velocity (the teleport pattern), so it works on-rails and in the physics bubble alike. Zero vector = no-op success. Read = `0 0 0` (no read-back). See §6. |
| `debug/vessels/<id>/refill_fuel` | T | `1` | `debug.refill_fuel` | **Solver** | Refill all consumables. |
| `debug/vessels/<id>/refill_battery` | T | `1` | `debug.refill_battery` | **Solver** | Refill all batteries. |
| `debug/vessels/<id>/docking/<n>/pushoff_impulse` | **St** | number (N·s ≥0) | `debug.docking_pushoff` | Frame | Override a docking port's undock separation impulse (`DockingPort.PushoffImpulse`). |
| `debug/time/warp` | **St** | factor | `debug.warp` | Frame | Set the time-warp factor directly (`Universe.SetSimulationSpeed`). |
| `debug/focus` | **St** | vehicle/body id | `camera.focus` | Frame | Move the camera to any astronomical by id (view-only). |
| `debug/control_vessel` | **St** | vehicle id | `debug.control_vessel` | Frame | Focus **and** take control of a vehicle by id. (4750: KSA may refuse a target that isn't `controllable` — pre-check the `controllable` read; outcome to be confirmed in a live flight.) |
| `debug/always_render_iva` | **St** | `0`/`1` | `debug.always_render_iva` | Frame | Global render cheat: force interior (IVA) part meshes to render outside the IVA camera. Vessel-agnostic. |
| `debug/vessels/<id>/weld` | **St** | `<target> <part_iid> <x> <y> <z> <pitch> <yaw> <roll> <lock>` | `debug.weld_create` | Frame | Weld this vessel (source) to a target part **or subpart** with an explicit pose. See **welds** below. Read = the current spec for this source, or empty. |
| `debug/vessels/<id>/weld_here` | **St** | `<target> <part_iid> [<lock>]` | `debug.weld_here` | Frame | Weld at the **current** relative pose (captured now). `lock` defaults to `1`. |
| `debug/vessels/<id>/unweld` | T | `1` | `debug.weld_remove` | Frame | Remove this source's weld. |
| `debug/welds/clear` | T | `1` | `debug.weld_clear` | Frame | Remove **all** welds. Vessel-agnostic. |
| `debug/welds/count` | S | int | — | — | Number of active welds. |
| `debug/welds/<source>/target` | S | string | — | — | The anchor vessel id. |
| `debug/welds/<source>/part` | S | uint | — | — | Anchor part/subpart `instance_id` (`0` = target body frame). |
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
| `debug/iva/enabled` | **St** | `0`/`1` | `debug.iva_physics` | Frame | **The master switch for the whole IVA cabin-physics feature.** Off by default. `0` releases every object at its exact rest pose and disposes every cabin simulation, so nothing runs at all. Vessel-agnostic. See **iva** below. |
| `debug/iva/run_outside_iva` | **St** | `0`/`1` | `debug.iva_run_outside_iva` | Frame | Keep simulating when no viewport is in the IVA camera. Off ⇒ leaving IVA parks the objects. Vessel-agnostic. |
| `debug/iva/help` | S | text | — | — | Console-friendly usage readme + worked examples (stock Gemini 7 props). `cat` it. |
| `debug/iva/adopt` | **St** | `<vessel> <subpart_iid>` or `<vessel> <subpart_iid> <vx> <vy> <vz>` | `debug.iva_adopt` | Frame | Cut one **SubPart** loose as a floating object (optionally with a starting velocity in the vessel assembly frame, m/s). Read = empty. |
| `debug/iva/adopt_all` | **St** | `<vessel> [max] [template_substring]` | `debug.iva_adopt_all` | Frame | Cut loose the **smallest** eligible interior props first, up to `max` (`0`/absent = the per-vessel cap), optionally filtered by a case-insensitive template substring. Read = empty. |
| `debug/iva/clear` | T | `1` | `debug.iva_clear` | Frame | Release **all** objects (the sim stays enabled). Vessel-agnostic. |
| `debug/iva/count` | S | int | — | — | Number of floating objects across all vessels. |
| `debug/iva/stats` | S | `vessels objects sleeping substeps avg_ms max_ms parked reason` | — | — | Driver counters. `parked` is `0`/`1`; `reason` is `warp`, `paused`, `editor`, `not-iva`, `unknown`, or `-` while running. |
| `debug/iva/interior` | S | `vessel triangles source_parts min_x min_y min_z max_x max_y max_z fallback` (0+ lines, LF-terminated) | — | — | One row per vessel with built interior geometry. `fallback` = `1` when no interior meshes were found and a synthetic box room was used. |
| `debug/iva/<id>/vessel` | S | string | — | — | The vessel whose cabin this object floats in. |
| `debug/iva/<id>/part` | S | uint | — | — | The driven SubPart's `instance_id`. |
| `debug/iva/<id>/name` | S | string | — | — | The driven SubPart's display name. |
| `debug/iva/<id>/template` | S | string | — | — | The driven SubPart's template id (e.g. `CoreIVAPropA_Subpart_DeanSardineA`). |
| `debug/iva/<id>/position` | S | `x y z` | — | — | Position in the **vessel assembly frame** (m). |
| `debug/iva/<id>/velocity` | S | `vx vy vz` | — | — | Velocity **relative to the cabin** (m/s). |
| `debug/iva/<id>/angular_velocity` | S | `wx wy wz` | — | — | Angular velocity relative to the cabin (rad/s). |
| `debug/iva/<id>/mass` | S | number | — | — | Body mass (kg): `iva_density_kg_m3` × collision-proxy volume. |
| `debug/iva/<id>/shape` | S | string | — | — | Collision-proxy kind. `box` in this build. |
| `debug/iva/<id>/size` | S | `x y z` | — | — | Collision-proxy full extents (m). |
| `debug/iva/<id>/asleep` | S | `0`/`1` | — | — | `1` = settled and sleeping (costs nothing to simulate). |
| `debug/iva/<id>/nudge` | **St** | `vx vy vz` | `debug.iva_nudge` | Frame | One-shot velocity kick in the vessel assembly frame (m/s), added to the current velocity; wakes the body. Read = `0 0 0` (no read-back). |
| `debug/iva/<id>/release` | T | `1` | `debug.iva_release` | Frame | Un-adopt: restore the SubPart's exact rest pose and drop the body. |
| `debug/iva/<id>/spec` | S | spec line | — | — | The write-compatible 2-token spec (echo to `adopt` to re-adopt this SubPart). |
| `debug/engineplume/help` | S | — | — | — | Console-friendly readme for the engine-plume family (scope, usage, field groups). `cat` it. |
| `debug/engineplume/templates/<id>/core/{radius_weight,nozzle_pressure_weight,jet_expansion_weight,exit_mach_weight}` | **St** | number (each `0.0001..100`) | `debug.engineplume_set` | Frame | The four term weights of the plume-length model. |
| `debug/engineplume/templates/<id>/absorption/{density,scattering_brightness,phase_eccentricity,refraction_intensity}` | **St** | number (`0.0001..100`, `0..100`, `-1..1`, `0..10`) | `debug.engineplume_set` | Frame | Medium absorption density, scattered-light brightness, scattering phase eccentricity (−1 back-scatter … +1 forward-scatter), refraction/heat-haze strength. |
| `debug/engineplume/templates/<id>/absorption/fake_clean_burn` | **St** | `0`/`1` | `debug.engineplume_set` | Frame | Fake a soot-free burn while inside an atmosphere. |
| `debug/engineplume/templates/<id>/emission/brightness` | **St** | number (`0..200`) | `debug.engineplume_set` | Frame | Overall emissive brightness of the plume. |
| `debug/engineplume/templates/<id>/emission/color{0,1,2,3}` | **St** | `r g b` (each `0..1`) | `debug.engineplume_set` | Frame | The 4-stop emission gradient — `color0` at the nozzle exit, `color3` at the plume tip. |
| `debug/engineplume/templates/<id>/mach_diamonds/{lead_in,lead_out,middle_radius}` | **St** | number (`0..1`) | `debug.engineplume_set` | Frame | Where the shock-diamond pattern fades in/out along the plume, and a diamond's bright-core radius (fraction of plume radius). |
| `debug/engineplume/templates/<id>/noise/{density_strength,radial_strength,shape_strength}` | **St** | number (`0..2`) | `debug.engineplume_set` | Frame | Intensity of the density / radial-shape / overall-shape noise. |
| `debug/engineplume/templates/<id>/noise/radial_barrel_shock` | **St** | number (`0..4`) | `debug.engineplume_set` | Frame | Extra radial-noise intensity applied at the barrel shock. |
| `debug/engineplume/templates/<id>/noise/{density_size,radial_size,radial_speed,shape_size}` | **St** | number (`0..100`) | `debug.engineplume_set` | Frame | Feature sizes of the three noise fields + the radial noise's scroll speed. |
| `debug/engineplume/templates/<id>/quality/{samples,self_shadow_samples}` | **St** | number (`1..100` / `0..10`) | `debug.engineplume_set` | Frame | Raymarch and self-shadow sample counts — **rounded to an integer** on apply (`0` self-shadow = off). |
| `debug/engineplume/templates/<id>/quality/vessel_shadows` | **St** | `0`/`1` | `debug.engineplume_set` | Frame | Whether the plume casts volumetric shadows onto the vessel. |
| `debug/engineplume/templates/<id>/json` | S | JSON object | — | — | Every field of this template in one line: `{"<field path>": value \| [components], …}`. Discovery / profile capture — writes always go to the individual leaves. |
| `debug/engineplume/templates/<id>/reset` | T | `1` | `debug.engineplume_reset` | Frame | Restore this template's pristine (pre-gatOS) values. |
| `debug/plumetrail/help` | S | — | — | — | Console-friendly readme for the trail family. `cat` it. |
| `debug/plumetrail/render/{max_distance,voxel_first_slice,min_step_size}` | **St** | number, meters (`0.01..1e7`, `0.001..1e5`, `0.001..1e5`) | `debug.plumetrail_set` | Frame | Maximum render distance, first voxel-slice depth, minimum raymarch step. |
| `debug/plumetrail/render/{step_size_distance_scale,expansion_time,erosion_max_depth,erosion_edge_sharpness}` | **St** | number (`0..10`, `0.001..10000` s, `0..1`, `0..0.999`) | `debug.plumetrail_set` | Frame | Step growth with camera distance, segment expansion time, noise-erosion depth and edge sharpness. |
| `debug/plumetrail/render/self_shadow_steps` | **St** | number (`0..64`) | `debug.plumetrail_set` | Frame | Self-shadow raymarch step count, **rounded to an integer** (`0` = off). |
| `debug/plumetrail/render/{light_brightness,sky_ambient_brightness}` | **St** | number (`0..1000`) | `debug.plumetrail_set` | Frame | Direct-light and sky-ambient brightness on the trail. |
| `debug/plumetrail/render/trail_color` | **St** | `r g b a` (each `0..1`) | `debug.plumetrail_set` | Frame | Debug tint applied to every trail. |
| `debug/plumetrail/json` | S | JSON object | — | — | Every trail field in one line (same shape as the plume `json`). |
| `debug/plumetrail/clear` | T | `1` | `debug.plumetrail_clear` | Frame | Drop the trail geometry currently in the world (a one-shot — **not** a settings change). |
| `debug/plumetrail/reset` | T | `1` | `debug.plumetrail_reset` | Frame | Restore the trail renderer's pristine values. |
| `debug/clouds/help` | S | — | — | — | Console-friendly readme for the clouds family. `cat` it. |
| `debug/clouds/bodies/<id>/shared/{transition_start_km,transition_end_km,max_shadows_altitude_km}` | **St** | number, km (`≥0`) | `debug.clouds_set` | Frame | Where the ground→orbit cloud representation starts/finishes blending, and the altitude above which cloud shadows stop. |
| `debug/clouds/bodies/<id>/layers/<n>/rotation_speed` | **St** | `x y z` (a plain 3-vector, unbounded) | `debug.clouds_set` | Frame | Layer rotation rate about each body axis. |
| `debug/clouds/bodies/<id>/layers/<n>/detail_tile_km` | **St** | number, km (`≥0`) | `debug.clouds_set` | Frame | Tiling size of the layer's detail texture. |
| `debug/clouds/bodies/<id>/layers/<n>/color` | **St** | `r g b` (each `0..1`) | `debug.clouds_set` | Frame | Volumetric cloud colour of the layer. |
| `debug/clouds/bodies/<id>/layers/<n>/scroll_speed` | **St** | number, m/s (`0..1e6`) | `debug.clouds_set` | Frame | Scroll speed of the layer's noise. |
| `debug/clouds/bodies/<id>/layers/<n>/two_d/{lambertian,color}` | **St** | number (`0..1`) / `r g b` (each `0..1`) | `debug.clouds_set` | Frame | Lambertian shading weight and colour of the flat (distant) cloud representation. |
| `debug/clouds/bodies/<id>/layers/<n>/raymarch/{step_size,max_step,light_distance}` | **St** | number, meters (`0..1e6`) | `debug.clouds_set` | Frame | Base raymarch step, its upper clamp, and the distance marched toward the light when sampling in-scattering. |
| `debug/clouds/bodies/<id>/layers/<n>/raymarch/step_scale` | **St** | number (`0..1`) | `debug.clouds_set` | Frame | How fast the raymarch step grows with distance. |
| `debug/clouds/bodies/<id>/layers/<n>/raymarch/light_samples` | **St** | number (`0..20`) | `debug.clouds_set` | Frame | Light samples per raymarch step, **rounded to an integer**. |
| `debug/clouds/bodies/<id>/layers/<n>/types/<m>/{start_altitude,height}` | **St** | number, meters (`-1e6..1e6` / `0..1e6`) | `debug.clouds_set` | Frame | Bottom altitude and vertical thickness of one cloud type inside the layer. |
| `debug/clouds/bodies/<id>/layers/<n>/types/<m>/{density,edge_sharpness,multi_scatter}` | **St** | number (`0..100`, `0..1`, `0..1`) | `debug.clouds_set` | Frame | Optical density, edge-falloff sharpness and multiple-scattering brightness of that cloud type. |
| `debug/clouds/bodies/<id>/layers/<n>/types/<m>/interpolate` | **St** | `0`/`1` | `debug.clouds_set` | Frame | Whether that cloud type's shapes are interpolated. |
| `debug/clouds/bodies/<id>/json` | S | JSON object | — | — | Every cloud field of the body in one line (indexed keys included). |
| `debug/clouds/bodies/<id>/reset` | T | `1` | `debug.clouds_reset` | Frame | Restore that body's pristine cloud values. |
| `debug/terrain/help` | S | — | — | — | Console-friendly readme for the terrain family. `cat` it. |
| `debug/terrain/wireframe` | **St** | `0`/`1` | `debug.terrain_set` | Frame | **Global** (not per body): draw all planet terrain as wireframe. Addressed with an empty entity token. |
| `debug/terrain/bodies/<id>/{min_height,max_height}` | **St** | number, meters (`-20000..0` / `0..20000`) | `debug.terrain_set` | Frame | The height range the body's height field maps to. |
| `debug/terrain/bodies/<id>/slope_roughness_deg` | **St** | number, degrees (`0..90`) | `debug.terrain_set` | Frame | Mean micro-slope roughness used by the surface BRDF (stored internally in radians). |
| `debug/terrain/bodies/<id>/hapke_albedo` | **St** | number (`0.0001..0.99999`) | `debug.terrain_set` | Frame | Mean single-scattering albedo of the Hapke surface model. |
| `debug/terrain/bodies/<id>/biomes/blend_strength` | **St** | number (`1..10`) | `debug.terrain_set` | Frame | Sharpness of the blend between neighbouring biome materials. |
| `debug/terrain/bodies/<id>/biomes/{detail_fade_start_km,detail_fade_end_km}` | **St** | number, km (`≥0`) | `debug.terrain_set` | Frame | Altitudes where biome detail textures start / finish fading in. |
| `debug/terrain/bodies/<id>/tessellation/{edge_length_px,factor,range_m}` | **St** | number (`0.1..20` px, `0..1`, `1..20000` m) | `debug.terrain_set` | Frame | Target screen-space edge length, the global tessellation-factor scale, and the camera distance over which tessellation falls off. |
| `debug/terrain/bodies/<id>/json` | S | JSON object | — | — | Every terrain field of the body in one line. |
| `debug/terrain/bodies/<id>/reset` | T | `1` | `debug.terrain_reset` | Frame | Restore that body's pristine terrain values. |

**welds** (the "weld one vessel rigidly to another, anchored to a part" cheat — a game hack):
- Discover anchors under `vessels/by-id/<target>/parts/<n>/` — top-level parts **and** their
  `subparts/<m>/` (§3.4.17), or grab the whole tree in one read from `parts/json` (`cat parts/json | jq`);
  either level's `instance_id` is the stable handle you pass as `<part_iid>`
  (`0` ⇒ anchor to the target's body/CoM frame). A subpart anchor tracks its live pose, so welding to an
  animated subpart (robotics/landing-leg segment) follows the animation. A vessel may be the
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

**iva** (free-floating cabin objects with real inertial physics — plans/IVA_MOVEMENTS.md):
- **`enabled` is the master switch and it is off by default.** While it is off nothing exists: no physics
  simulation, no interior collision mesh, no buffer pool, no per-frame work. Writing `0` releases every
  object at its exact rest pose and disposes every cabin simulation, returning to that state; so does mod
  unload. `iva_physics_enabled` in `gatos.toml` is only the boot seed for this flag.
- Objects are gatOS-owned rigid bodies **driving real shipped IVA prop SubParts**, simulated in a
  gatOS-owned physics world in the **vessel assembly frame** — never in KSA's own solver, which models a
  vehicle as one dynamic body wrapped in coarse exterior colliders (a cabin object would be ejected through
  the hull *and* would shove the spacecraft) and does not step at all for a coasting vessel. Coupling is
  therefore strictly one-way: objects feel the vessel, the vessel never feels them.
- The forcing field is `a = −a_p − α×r_b − 2ω×v − ω×(ω×r_b)` over the vessel's own accelerometer readings
  (`environment/accel`, `attitude/rates`, `environment/angular_accel`, `com`), so one formula covers pad
  (objects rest on the floor at 1 g), coast (weightless drift), burn (slammed aft), spin (flung outward and
  lagging) and touchdown. Gravity never appears — it is absent from proper acceleration by construction.
- Collision geometry is derived automatically from the vessel's own **interior meshes** (every part whose
  model template is interior-only, minus the adopted objects themselves), in the assembly frame. Read
  `interior` to confirm it built. No hand-authored volumes; modded interiors work for free.
- **SubParts only, and this is binding.** KSA serializes a transform for top-level parts but not for
  SubParts, so a displaced object physically cannot leak into a save file. `adopt` on a top-level part
  is `ENOENT` by design. Discover candidates under `vessels/by-id/<vessel>/parts/<n>/subparts/<m>/`
  (§3.4.17) or in one read from `parts/json`; pass a subpart's `instance_id`.
- Object ids are integers — the **smallest free slot**, reused after `release`/`clear` — appearing as
  `debug/iva/<id>/`. Ids are global across vessels; the per-vessel object cap is `iva_max_objects`.
- **Parking.** Objects freeze (velocities zeroed, poses held) while the game is paused, under time warp,
  in the vehicle editor, and whenever no viewport is in the IVA camera unless `run_outside_iva` is `1`.
  `stats` reports which. Un-parking always resumes from rest, never from a stale velocity.
- **Interior geometry is rebuilt on a part-count change or an adopt/release**, not on a timer — building
  the collision tree is the one expensive operation here, and a periodic hitch would be worse than the
  geometry going stale after a rare count-preserving interior edit. Toggle `enabled` off/on to force it.
- **Rails.** Speed is clamped to `iva_max_speed`; an object that escapes the interior bounding box is
  teleported back to the cabin centroid (`iva.escape`); adopting anything whose bounding box exceeds
  `iva_max_object_size` is refused so hull panels and seats cannot be cut loose; a staged-away part
  auto-releases (`iva.release`).
- **Events** (§3.5): `iva.impact` (speed change past `iva_impact_speed` in one substep — wire it to
  `/sim/audio` for clunks), `iva.escape`, `iva.release`.
- Everything is **runtime-only** — never persisted, released at unload. Errnos: `EOPNOTSUPP` (the master
  switch is off, or the subpart has no CPU mesh to size a proxy from), `ENOENT` (vessel/subpart/object id
  gone), `EBUSY` (per-vessel cap reached, or that subpart already floats), `EINVAL` (bad arity/values, or
  the subpart is larger than `iva_max_object_size`), `EIO` (the cabin simulation is unavailable).

**FX editors** (`engineplume` / `plumetrail` / `clouds` / `terrain` — the game's four built-in imgui
render editors exposed as filesystems). Shared rules for all four:
- **One leaf per knob.** Every field is its own file with its own inclusive range; a wrong component
  count, a non-finite number, or a value outside the range fails **`EINVAL` before reaching the game**
  (and is re-checked game-side, so a hand-written `POST /v1/command` cannot bypass it). Each entity also
  publishes a whole-entity `json` document for **discovery and profile capture** — reads only; writes
  always go to the leaves.
- **Read-back is live**, sampled from the same accessor the write goes through, so a value round-trips.
  The game stores most of these as **32-bit floats**, so a read-back is single-precision (`0.1` reads
  back as `0.100000001`); integer-valued counts are **rounded** on apply and read back rounded.
- **Cadence.** The FX surface is re-read when a `/sim` write lands (immediately) or every **2 s**
  otherwise — the 2 s beat is what catches edits made in the game's own imgui editors. Between rebuilds
  the previous values are republished by reference (an idle FX subtree costs nothing).
- **`reset` restores the pristine value**: the first time gatOS writes a field it records what was there
  before; `reset` replays every recorded field of that entity through the normal write path and drops the
  record (a reset with nothing recorded is a successful no-op). Everything is **session-scoped** — never
  persisted, and every touched field is restored at mod unload.
- **Animating.** All FX writes are **Frame**-phase, so one command lands per frame drain; group
  simultaneous changes through `/sim/ctl/batch` (§3.10) to land them in one tick. Writes are cheap enough
  to drive from a 10–60 Hz light-show loop — write only the leaves that changed.
- **Errnos:** `EINVAL` unknown field path / wrong arity / out of range / non-finite; `ENOENT` unknown or
  vanished template/body (also on a read of a leaf whose entity went away, or an out-of-range
  layer/cloud-type index); `EOPNOTSUPP` the family's game-side accessor is latched degraded (it also
  shows in `status/accessors`); `EIO` a KSA call threw.
- The whole surface is gated by `debug_namespace` like the rest of `/sim/debug`, is mirrored leaf-by-leaf
  over HTTP `/v1/fs/debug/…` and MQTT `gatos/sim/debug/…`, and rides `GET /v1/snapshot` as `fxEditors`.

**engineplume** — **per TEMPLATE, shared, not per engine.** `templates/<id>/` edits the one
`VolumetricExhaustTemplate` with that id, so an edit repaints **every nozzle in the universe** that
references it, immediately. Discovery: `ls templates/` (the loaded template ids; if the game's template
registry cannot be read, the roster degrades to the templates actually referenced by live nozzles).
After each write gatOS runs the game editor's own propagation pass over every live nozzle instance
(settings-changed → modifier refresh → gas-visibility recompute), so a burning engine repaints within the
frame. Startup/shutdown transient curves, the test grid and the wireframe debug view are **not** exposed.

**plumetrail** — **one global renderer**, not per vessel: the fields sit directly in the family dir. The
renderer re-reads them every frame, so a write takes effect with no apply call. `clear` is a **one-shot**
that deletes the trail geometry currently in the world (`reset` only restores settings, it does not clear
trails). The trail simulation/LOD/wind parameters are **not** exposed (they live behind private
segment-manager state), nor are the two toggles that would force a GPU frame-resource rebuild.

**clouds** — **per body → per layer → per cloud type.** Only bodies that actually define clouds appear
under `bodies/`; the `layers/<n>/` and `types/<m>/` indices that exist are exactly the ones that body
defines. Discovery: `ls bodies/`, then `cat bodies/<id>/json`. After each write gatOS re-derives the
affected layer's GPU render data and repopulates the cloud-shadow atlas — exactly the sequence the
in-game editor runs, and one that never rebuilds a Vulkan pipeline. If that render-side handle is
unavailable the **data write still stands and the write still succeeds** (`Ok`) — the change simply
appears on the renderer's next natural repopulate. The layer noise scale is **deliberately not exposed**
(changing it would force a pipeline rebuild); shape/density splines and texture slots are deferred.

**terrain** — a deliberately small first slice, in two tiers. `wireframe` is **family-global** (one
toggle for all planet terrain, addressed with an empty entity token) and has no `json`/`reset` of its
own. Everything under `bodies/<id>/` is **per body and only for bodies that currently hold a terrain
render slot** — a body with no slot is simply absent. A per-body write is a **paired write**: the body's
reference object (where the field has one) *and* the GPU-mapped uniform struct at the body's slot,
mirrored into every frame-in-flight copy so the change does not flicker; values are read back out of that
same uniform struct, so read-back is what the GPU samples. If the uniform-buffer handles cannot be
resolved, the per-body roster is empty (`bodies/` lists nothing) while `wireframe` stays live. Per-biome
materials, procedural modifiers, ground clutter/ecotypes, the BVH debug views and the exporters are
**not** exposed.

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
| `display/stream` | **Smb** | — (read) | The binary Kitty frame feed. A **continuous** stream: a single `cat /sim/display/stream` blocks for each next frame and renders it forever (never EOF; Ctrl-C to stop). Each frame is a complete, self-contained, LF-free Kitty unit; a slow reader skips to the latest (drop-old); multiple readers fan out. Frames come in two kitty forms: a **keyframe** (`a=T`, transmit+display — emitted for the first frame, when a new reader opens the stream, on a size/encoding change, and at least once per second) and steady-state **replace** units (`a=t`, transmit-only) that swap the fixed image id's bytes in place under the placement the last keyframe created — a consumer attaching mid-stream sees video within ≤1 s, and steady state causes no per-frame placement churn in the terminal. **Identical consecutive frames are coalesced** (a still scene publishes nothing between keyframes — the terminal keeps showing the stored image; the ≤1 s keyframe remains the heartbeat). **Delivery granularity:** a guest `read()` completes only once its full buffer fills (kernel 9p semantics — no partial-read wakeups), so consumer latency = read-buffer ÷ data-rate. `cat` is fine at video rates; to consume a *low-rate* feed use small reads (`dd if=/sim/display/stream bs=64`). |

Out-of-range writes to the numeric controls **clamp** (and succeed), matching the config's clamp-don't-reject rule.

> **Debug harness (dormant):** `DisplaySurface.PngDumpDirectory` (settable only in code — see the
> comment at the construction site in `Mod.cs`) switches `stream` from Kitty bytes to a host-side
> dump of one `screencap-<ISO 8601 UTC>.{png,kitty}` pair per second plus a plain-text progress line
> per pair on the feed. It is the tier-1/2 validation harness from STREAM_PLAN.md §11 (used to
> corner the 2026-07 purrTTY libghostty `o=z` corruption); normal builds leave it unset.

### 3.9 `/audio` *(userland audio playback — GATOS_CUSTOM_AUDIO_PLAN; present when `audio_enabled=true`)*

Play **real audio** (mp3 / ogg / wav / flac) through the game's speakers with nothing more exotic
than file writes: upload a clip's bytes into the writable `file/` directory, then `echo` a play
line. Clips are held **in-memory on the mod side** (never touch disk); FMOD sniffs the container
header, so the filename extension is for humans only. Playback routes into the game's own mixer
groups, so the matching in-game volume slider (Sfx / Music / UI) governs it. Deliberately, gatOS
audio **keeps playing at any time-warp** (the game mutes its own SFX above 10× — a master alarm
that mutes at warp defeats the purpose).

```sh
cat alarm.mp3 > /sim/audio/file/alarm.mp3        # upload (bytes live in mod memory)
echo 'alarm.mp3' > /sim/audio/play               # play the whole clip
echo 'music.ogg id=bgm loop=1 vol=0.4 group=music' > /sim/audio/play
echo 'bgm vol=0.15' > /sim/audio/set             # live-adjust
echo 'bgm' > /sim/audio/stop                     # stop one; `all` stops everything
cat /sim/audio/status                            # live channels
rm /sim/audio/file/alarm.mp3                     # evict
```

| Path | A | Format / Write | Meaning |
|---|---|---|---|
| `audio/file/` | dir (writable) | — | The clip store: `Tlcreate` + chunked writes accumulate an upload that becomes playable **on close (clunk)** — partial uploads are invisible to `play` (`EBUSY` if raced). Re-upload (`cat >`, O_TRUNC) truncates and replaces, bumping the clip's version on close; append (`>>`) extends. `mkdir`/`mv` inside ⇒ `EPERM`. |
| `audio/file/<name>` | — | binary | One uploaded clip. Name: single component, ≤ 64 chars, `[A-Za-z0-9._-]` (`EINVAL` otherwise). Reads return the stored bytes (`md5sum` both sides matches). `rm` evicts immediately (playing channels finish naturally — FMOD holds its own copy). Caps: per-clip `EFBIG`, total bytes / clip count `ENOSPC` — enforced **mid-write** so the failing `write(2)` carries the errno. |
| `audio/play` | **St** | `<name> [start=<ms>] [end=<ms>] [vol=<0..1>] [loop=0\|1] [group=sfx\|music\|ui] [id=<token>] [pan=<-1..1>] [pitch=<mult>]` | Start playback on a new channel (action `audio.play`). Defaults: whole clip, `vol=1`, no loop, `group=sfx`, auto id `#1`,`#2`,… `start`/`end` play a range in ms (`end` past the clip clamps; `end<=start` ⇒ `EINVAL`); `loop=1` loops forever (a range loops that range). `id=` names the channel for `set`/`stop`; reusing a live id **replaces** it (old channel stopped first); the `#` prefix is reserved for auto ids. `pitch` is a speed multiplier (0 < pitch ≤ 100). Read = empty. Errnos: `ENOENT` unknown clip, `EBUSY` uploading / channel table full, `EINVAL` bad grammar/values, `EIO` FMOD refused the bytes (corrupt/unsupported), `EOPNOTSUPP` audio disabled. |
| `audio/set` | **St** | `<id-or-name> [vol=] [pan=] [pitch=] [pause=0\|1] [resume=1] [seek=<ms>]` | Live-adjust a playing channel (action `audio.set`). Target resolution: exact channel id first, else **every** channel playing the clip of that name. `pause=1` pauses, `pause=0`/`resume=1` resumes. At least one adjustment required. `ENOENT` when nothing matches (an already-finished channel is `ENOENT` — by design, not worth scripting around). |
| `audio/stop` | **St** | `all` \| `<id-or-name>` | Stop matching channel(s) (action `audio.stop`). `all` is idempotent (never fails); a named target with no match ⇒ `ENOENT`. |
| `audio/status` | S | one line per live channel | Columns (stable order): `id name state pos_ms len_ms vol loop group`, state ∈ `playing`\|`paused`, `pos_ms` quantized to ~100 ms (keeps the changed-only MQTT mirror calm). Backed by a snapshot the game thread publishes once per frame — reads never touch game state. |
| `audio/info` | S | `enabled=1 clips=… clips_max=… bytes=… bytes_max=… clip_bytes_max=… channels=… channels_max=…` | Store usage + caps + live channel count, one line. |

**Engine notes (what the actuator does):** clips ≤ 1 MiB are fully decoded at first play
(`CreateSample` — instant, tiny); larger clips decode during mix (`CreateCompressedSample` —
create stays cheap, memory ≈ file size, concurrent plays fine). FMOD **copies** the buffer at
create, and each committed upload is a fresh immutable byte array, so re-upload/eviction never
disturbs a playing channel; the FMOD copy is released only once its clip version is evicted *and*
its channels finished (never mid-playback). `end=` is enforced on the per-frame tick (~16 ms
precision, correct under `pitch=`). Memory worst case ≈ store bytes + one FMOD copy per cached
clip version (≈ file size; ≤ 1 MiB clips decode to PCM) — bounded by the caps.

### 3.10 `/ctl` *(present whenever the command sink is wired)*

The global (non-per-vessel) control surface: the two batch writers, and the live-player registry the
timed one feeds. Everything from `ctl/timed_batch` down additionally needs
`[schedule] schedule_enabled=true` — with it off those nodes do not exist and `ctl/batch` is
unaffected.

| Path | A | Write | Meaning |
|---|---|---|---|
| `ctl/batch` | **B** | command lines + `commit` | Execute up to **64** control writes **atomically in one game tick**. Read = a one-line usage hint. |
| `ctl/timed_batch` | **B** | `@`-directives + `<offsetMs> <path> <value>` lines + `commit` | Register a **schedule**: the same control surface, on a clock. Non-blocking — the write validates and returns an id; the schedule then plays host-side for as long as it takes. Read = a one-line usage hint. |
| `ctl/schedules/` | D | — | The live-player registry (schedules **and** the camera track). |
| `ctl/schedules/count` | S | — | Integer: how many players are live. |
| `ctl/schedules/help` | S | — | The whole grammar + semantics as a multi-line console reference. |
| `ctl/schedules/clear` | T | `1` | Stop and remove **every** player, including committed-but-not-yet-activated ones (`schedule.clear`). |
| `ctl/schedules/<id>/` | D | — | One directory per live player. |
| `…/<id>/kind` | S | — | `schedule`, or **`camera-track`** for a playing camera track (§3.11). |
| `…/<id>/group` | S | — | The `@group` name, or `-` when ungrouped. |
| `…/<id>/state` | S | — | `pending` \| `running` \| `paused` \| `done` \| `failed` (lower-case). |
| `…/<id>/t` | S | — | Current timeline offset, **ms** with one decimal (`250.0`). |
| `…/<id>/duration` | S | — | Total length, ms with one decimal. |
| `…/<id>/pending` | S | — | Integer: entries not yet fired (always `0` for a camera track). |
| `…/<id>/dropped` | S | — | Integer: entries discarded by catch-up coalescing so far. |
| `…/<id>/clock` | S | — | `render` \| `wall` \| `ut` — the clock base, fixed for the player's life. |
| `…/<id>/last_error` | S | — | `entry <n>: <ERRNO> (<message>)` for the **first** failing entry, or `-`. |
| `…/<id>/pause` | **St** | `0`/`1` | Freeze/resume the clock (`schedule.pause`). |
| `…/<id>/scrub` | **St** | ms ≥ 0 | Seek. Read-back is the current position (`schedule.scrub`). |
| `…/<id>/rate` | **St** | `0..100` | Playback multiplier; `0` is a legal frozen state (`schedule.rate`). |
| `…/<id>/loop` | **St** | `0`/`1` | Wrap at the end instead of stopping (`schedule.loop`). |
| `…/<id>/stop` | T | `1` | Stop playback; the player **stays listed** with its final state (`schedule.stop`). |
| `…/<id>/remove` | T | `1` | Drop the player from the registry (`schedule.remove`). |

Every status leaf is **live** (formatted per access, never snapshot-memoized): `t` advances every
rendered frame, far faster than the telemetry publish cadence.

**Why `ctl/batch` exists:** every ordinary control write blocks until the game thread has executed it, so
back-to-back writes necessarily land in *consecutive* frames — one command per frame, no matter how
fast the writer. At orbital speed that smears a multi-vessel setup (e.g. formation teleports) by
~50–150 m per frame gap. A batch is one write → one command group → one drain: zero sim time passes
between its commands.

**Grammar** — one command per line: `<path> <value>`, where `<path>` is the path of any control
file (bare `debug/vessels/x/teleport`, `/`-rooted, or `/sim/`-rooted all work) and `<value>` is
exactly what a direct write to that file would take. The path ends at the **first space or tab**;
any further run of whitespace before the value is skipped (so columns may be padded for alignment
— paths themselves never contain whitespace, by ID sanitization §2.2). Leading indentation, blank
lines, and `#` comment lines are ignored. A line that is exactly `commit` ends the batch and
fires it.

```sh
cat > /sim/ctl/batch <<'EOF'
debug/vessels/Hunter/teleport  -6551000   0 0 0      -7800.382 0
debug/vessels/Polaris/teleport -6551000 -10 0 0.0119 -7800.382 0
debug/vessels/Banjo/teleport   -6551000 -20 0 0.0238 -7800.382 0
commit
EOF
```

**Semantics:**

- **All-or-nothing validation.** Every line is resolved and parsed *before* anything is submitted:
  an unresolvable path fails the write `ENOENT`; a non-control target, unparseable value, empty
  batch, or over-limit batch fails `EINVAL` — and nothing fires.
- **Atomic execution.** The group rides the command queue as ONE unit — the game thread executes it
  in order inside a single drain, never split across ticks. At execution the commands are
  independent: a failure does not stop the rest; the write returns the **first** failure's errno
  (the host log names the failing line).
- **One phase per batch.** All lines must share the action phase (§5.1) — Frame **or** Solver, not
  both (`EINVAL`): the two phases drain at different points, so "same tick" is unsatisfiable.
- **Limits:** ≤ **64** commands and ≤ **64 KiB** per batch (`EINVAL`).
- **Abort for free.** Closing the file without a `commit` line discards the batch. (An
  *unterminated* trailing `commit` — no final newline — fires best-effort on close, like any
  control file's unterminated write; prefer a newline-terminated `commit` so the failing
  `write(2)` carries the real errno.)
- Debug paths are batchable exactly when `/sim/debug` is being served (the lines resolve against
  the same tree); per-command authority gating applies unchanged at execution.

**Transports:** `POST /v1/fs/ctl/batch` with the same multi-line text (including the `commit`
line) as the body, or MQTT publish to `gatos/sim/ctl/batch/set` — the field mirror delivers the
whole body as one write, so the batch fires exactly like the 9p file.

**`ctl/timed_batch` + `ctl/schedules/` — the same surface, on a clock.**

**Why it exists:** `ctl/batch` collapses N writes into one *instant*; a schedule spreads them over
*time* without a guest program having to stay awake. The host owns the clock, so the timing does not
depend on the SSH session surviving, does not drift with the guest's own scheduling, and replays
identically every run. And because path resolution is `ctl/batch`'s — any `/sim`-relative path to a
control file — a timed batch inherits the **entire** control surface for free: staging sequences,
light shows, FX sweeps, audio cues, warp changes, camera moves.

**Grammar** — optional `@`-directives (all of which must precede every entry), then one
`<offsetMs> <path> <value…>` line per command, then a line that is exactly `commit`. Blank lines and
`#` comment lines are ignored; whitespace between the three fields is collapsed, so column-aligned
scripts parse. Path spellings are `ctl/batch`'s (bare, `/`-rooted, `/sim/`-rooted).

```sh
cat > /sim/ctl/timed_batch <<'EOF'
@id      launch-seq     # optional; auto "#N" otherwise. [A-Za-z0-9_.-]{1,64}
@clock   render         # render | wall | ut   (case-insensitive; default schedule_default_clock)
@rate    1.0            # 0..100; 0 freezes the timeline
@loop    0              # 0 | 1
@group   take-3         # optional; joins a shared-clock group, same charset as @id

0        vessels/by-id/Hunter/ctl/throttle  1
1200     vessels/by-id/Hunter/ctl/ignite    1
3400     debug/time/warp                    1
commit
EOF

cat /sim/ctl/schedules/launch-seq/state     # pending|running|paused|done|failed
echo 0.25 > /sim/ctl/schedules/launch-seq/rate
```

**Semantics:**

- **Offsets are absolute milliseconds from the schedule's start, never deltas.** Fractional values
  are legal (`16.67`), must be finite and ≥ 0. Absolute is the deliberate choice: a delta encoding
  accumulates rounding over a long sequence, and re-timing one line would shift every line after it.
- **Three clock bases, and they are genuinely different.** `render` accumulates the game's *clamped*
  per-frame `dtPlayer`, so after a hitch it lags true wall time and **never catches up** — which is
  exactly right for footage (a shot stays smooth) and exactly wrong for syncing to a host-side
  recorder. `wall` is true elapsed time, so a stall is followed by a catch-up burst. `ut` is sim
  time, which diverges wildly from both under warp and is the right choice for mission events.
- **Phase mixing IS allowed here** — a deliberate relaxation of `ctl/batch`'s one-phase rule (§5.1),
  not an oversight. A batch forbids it because "the same tick" is unsatisfiable across the Frame and
  Solver drain points; a schedule spans many ticks, so each entry simply routes to its own phase
  queue when it comes due.
- **Catch-up policy is derived from the archetype, not declared.** When many entries come due in one
  tick (a hitch, a scrub, a high `@rate`), every **TRIGGER** entry fires in order, while **STATE**
  entries coalesce to the **last write per path** — an intermediate setpoint nobody could observe is
  dropped. **Cross-path order is preserved.** Discards are counted at `<id>/dropped` and reported as
  `schedule.dropped`, never silently. This bounds a burst by *distinct leaves* rather than entries.
- **All-or-nothing validation, non-blocking commit.** Everything is resolved and parsed before
  anything registers: unresolvable path ⇒ `ENOENT`; non-control target, unparseable value, bad or
  duplicated directive, zero entries, or a cap breach ⇒ `EINVAL`; `control_enabled=false` ⇒
  `EACCES`. The id is reserved **last**, so a rejected commit never burns the name. `commit` then
  validates, registers and returns — the schedule outlives the `write(2)` that created it, and its
  runtime outcomes surface at `ctl/schedules/<id>/` and in `/sim/events`.
- **A committed schedule appears at the next game tick.** `count` and `ls` reflect *activated*
  players; the id is reserved immediately, so a duplicate-id commit in the gap is still rejected.
- **Scrub fires nothing.** A seek re-seats the cursor by binary search — it is navigation, not
  playback. Scrubbing backwards makes the passed entries replay on the following ticks.
- **Loop drains the tail first.** On a wrap the finished cycle's remaining entries fire, in order,
  before the new cycle's already-due head, and the clock keeps the remainder — so a loop boundary is
  indistinguishable from any other busy tick and a long loop does not drift.
- **A failed entry does not stop the schedule.** The rest still runs; only the **first** failure is
  recorded (the cause, not the last symptom) at `<id>/last_error`, and `state` latches `failed`.
- **`@group` members share ONE clock instance**, so `pause`/`scrub`/`rate`/`loop` on *any* member
  moves them all — that is what makes several schedules one take. The group clock's base, rate and
  loop come from the **first** member to create it; a later joiner's `@clock`/`@rate`/`@loop` are
  ignored for the shared clock, and it starts at the group's *current* position (so its already-past
  entries fire on its first tick — it joins a take in progress). Its duration is the max over
  members; the clock is dropped with its last member.
- **Finished players persist so a script can come back and read the outcome** — until the registry
  hits `schedule_max_live`, at which point activation reclaims *finished* players **oldest first**,
  one `schedule.evicted` event per slot, stopping the moment the count is back under the cap. Below
  the cap nothing is ever reclaimed. "Finished" means `done`, or `failed` **and** out of entries,
  not looping, and past its duration — a looping or still-running player is never evicted.
- **Limits:** `schedule_max_entries` entries, `schedule_max_bytes` buffered bytes per open handle,
  `schedule_max_live` live players, a duplicate `@id` — all `EINVAL` (§2.5).
- **Abort for free.** Closing the handle without a `commit` line discards the schedule silently. An
  *unterminated* trailing `commit` fires best-effort on clunk and can carry **no** errno — prefer a
  newline-terminated `commit`.

**Transports:** every leaf above is an ordinary VFS node, so the field mirror carries it with no new
code: `POST /v1/fs/ctl/timed_batch` with the whole script as the body (or MQTT
`gatos/sim/ctl/timed_batch/set`), `GET /v1/fs/ctl/schedules/<id>/state`,
`POST /v1/fs/ctl/schedules/<id>/rate`, and `POST /v1/command` / `gatos/command` for the seven
`schedule.*` actions (§5.1).

### 3.11 `/camera` *(programmable cinematic camera — CAMERA_CONTROLS_PLAN; present when `camera_enabled=true`)*

Borrow the game's main camera and fly it from a shell. There is no camera API to learn: the camera is
a directory of control files, and `echo`ing a number into one moves the view on the next rendered
frame. The surface is deliberately **three layers over one channel set** — write a leaf directly
(L1), drive those same leaves along a timeline through `/sim/ctl/timed_batch` (L2), or upload a JSON
**track** and let gatOS interpolate it with easing and splines at render rate (L3). **Every track
channel has a corresponding `pose/` leaf**, so the JSON is an *option* and never the only route to
anything; the two are the same surface, which is what lets a hand-written `echo` pull focus in the
middle of a running shot. Ownership is a **mode park plus a same-frame viewport hook**:
`camera/enabled 1` captures the
live camera's state, switches the viewport to `Fixed` and unfollows, which makes the game's own
camera controller write nothing — so gatOS is the sole transform writer. A guarded Harmony prefix on
the **main** `Viewport.OnFrame` applies the pose after simulation advances and immediately before KSA
rebuilds that viewport's camera matrices; its postfix publishes the final clamped transform.
Everything you write is an *override* layered on the captured baseline, so a channel you never touch
falls through to how the game had it, and release puts it all back. The pose is composed after the
shared schedule clock advances and before the current frame's matrix build, so a moving anchor, camera
track and timed sidecar all describe the **same rendered frame**.

```sh
echo 1 > /sim/camera/enabled                        # take it (parks the game's camera controller)
echo "vessel:apollo11" > /sim/camera/pose/anchor    # measure everything about this ship
echo "bodyfixed"       > /sim/camera/pose/frame     # in the ship's own axes (+X nose, +Y right, -Z up)
echo "-40 0 -6"        > /sim/camera/pose/position  # 40 m aft, 6 m above
echo "vessel:apollo11 off 0 0 -1.2 up world" > /sim/camera/pose/aim
echo 24   > /sim/camera/pose/fov                    # degrees
echo 0.35 > /sim/camera/pose/smoothing              # critically-damped filter, seconds
cat /sim/camera/status                              # one "key value…" line per channel
cp /mnt/shots/flyby.json /sim/camera/track/flyby    # upload a track (parsed + validated on close)
echo "flyby at 2 rate 0.5" > /sim/camera/play       # play it; it appears at ctl/schedules/camera/
echo 0 > /sim/camera/enabled                        # eased hand-back (camera_release_blend_s)
```

| Path | A | Format / Write | Meaning |
|---|---|---|---|
| `camera/status` | S | multi-line `key value…` | The whole camera state in one read: `owned mode follow tidal map_scope anchor frame position geo rotation applied_position_ecl applied_rotation aim fov ortho smoothing orbit time_scale`. `position`/`rotation` are the composed writable pose channels; `applied_position_ecl`/`applied_rotation` are KSA's final render-time transform after placement, smoothing and its camera clamp. `geo`'s fourth field is `1` when the geodetic spelling is live; `ortho` is `<enabled> <half-height m>`; `orbit` is `radius azimuth elevation`. |
| `camera/info` | S | one line `k=v …` | `enabled owned tracks tracks_max bytes bytes_max track_bytes_max keys_max fov_min fov_max`, plus the `frames=`/`modes=`/`up=` token vocabularies this build accepts. |
| `camera/target` | S | string | The follow target's **bare id**, or `-`. |
| `camera/playback` | S | `<state> <t_ms> <duration_ms> <shot> <index> <rate> <loop>` | The live track player. Times are ms with one decimal, matching `ctl/schedules/<id>/t`; `state` is the §3.10 vocabulary; `<shot>` is `-` before the first shot edge and `<index>` is `-1`. |
| `camera/last_error` | S | `<track>: <message>`, or `-` | Why the last track upload or `play` was rejected. Exists because a 9p **clunk** (which is what commits an upload) cannot carry an errno — this is that diagnosis, readable from inside the guest. Cleared only by a `play` that actually started, or at teardown. |
| `camera/enabled` | **St** | `0`/`1` → `camera.enabled` | Take (`1`) or **eased-release** (`0`) the camera. Read = whether gatOS owns it. `EINVAL` if not 0/1. |
| `camera/release` | **T** | `1` → `camera.release` | **Hard cut** back to game control — the "give it back *now*" verb, no blend. `EINVAL` on any other token. |
| `camera/mode` | **St** | `orbit`\|`free`\|`map`\|`iva`\|`fixed` → `camera.mode` | Switch the **game's** camera mode. `EINVAL` on an unknown token; `EOPNOTSUPP` while gatOS owns the camera (ownership *is* a park in `fixed`). **This is not a gatOS park target** — see the ownership notes below. |
| `camera/follow` | **St** | target ref → `camera.follow` | Point the game's camera at a `vessel:<id>` / `body:<id>`, or `none` to unfollow — set on **both** the viewport's base and map cameras. Read = the canonical target ref (`camera/target` is the same thing as a bare id). `EINVAL` if unparseable; `ENOENT` if the target is not live; `EOPNOTSUPP` for a `part:` ref (the game can only follow a whole object — aim at a part instead) or while gatOS owns the camera. |
| `camera/tidal` | **St** | `0`/`1` → `camera.tidal` | Tidal-lock the existing follow. `EINVAL` if not 0/1; `ENOENT` if nothing is being followed; `EOPNOTSUPP` while owned. |
| `camera/map/scope` | **St** | metres ≥ 0 → `camera.map_scope` | The map view's zoom: the radius the map camera orbits its focus at. **Not** ownership-gated — it configures the game's map controller, not the composed pose. `EINVAL` non-finite or `< 0`. Three inherited behaviours: the game clamps it **up** to the focus's mean radius every map frame; a focus change re-derives it wholesale on the next map entry; and it has no visible effect outside `map` mode. |
| `camera/track/` | dir (writable) | — | The track store: `Tlcreate` + chunked writes accumulate an upload that becomes playable **on close (clunk)**. `mkdir`/`mv` inside ⇒ `EPERM`. |
| `camera/track/<name>` | — | JSON text | One uploaded track. Name: single component, ≤ 64 chars, `[A-Za-z0-9._-]` (`EINVAL` otherwise). Reads return the stored bytes; `rm` evicts. Caps enforced **mid-write** so the failing `write(2)` carries the errno: `EFBIG` per-track, `ENOSPC` store bytes / track count. A malformed track fails the **close**, which cannot carry an errno — read `camera/last_error`. Unlike audio clips, tracks **are** in the field mirror (small JSON, useful over every transport). |
| `camera/play` | **St** | `<track> [at <sec>] [rate <x>] [loop 0\|1] [group <token>]` → `camera.play` | Start a take. Read = the loaded track name, or `-`. Defaults: `at 0`, `rate 1`, `loop` from the track's own `"loop"`. Replaces any running take (emitting `camera.finished reason=replaced`). `ENOENT` unknown track, `EBUSY` still uploading, `EINVAL` bad name / parse failure (the parse message is returned) / `schedule_max_live` full, `EOPNOTSUPP` when `schedule_enabled=false`. |
| `camera/set` | **St** | `[t <sec>] [rate <x>] [loop 0\|1] [paused 0\|1]` → `camera.set` | Live-adjust the running take — the *same* `PlaybackClock` the `ctl/schedules/<id>/` leaves drive. At least one adjustment required. `ENOENT` nothing playing, `EINVAL` bad pair or range, `EOPNOTSUPP` when `schedule_enabled=false`. |
| `camera/stop` | **T** | `1` → `camera.stop` | Stop the take and hand its channels back to the overrides and the baseline. **Idempotent** (Ok with nothing playing). `EOPNOTSUPP` when `schedule_enabled=false`. |
| `camera/pose/position` | **St** | `<x> <y> <z> [<frame>]` → `camera.position` | Cartesian placement in the pose frame, metres. The optional tail also sets `pose/frame`; omitting it means "keep the current frame". Read = `x y z <frame>`. `EINVAL` on non-finite components or an unknown frame token. |
| `camera/pose/frame` | **St** | frame token → `camera.frame` | Which way the placement axes point: `ecl`\|`cce`\|`bodyfixed`\|`enu`\|`lvlh`\|`chase`. `EINVAL` otherwise. |
| `camera/pose/anchor` | **St** | target ref → `camera.anchor` | What the placement is measured **about**: `vessel:<id>` \| `body:<id>` \| `part:<vessel-id>/<instance-id>` \| `none`. `EINVAL` unparseable, `ENOENT` not live. |
| `camera/pose/geo` | **St** | `<lat> <lon> <alt> [body:<id>]` → `camera.geo` | Geodetic placement: degrees and **metres above terrain** (degrading to above the mean sphere on a body with no heightmap). Longitude accepts both conventions (`-80.649` and `279.351`) and normalizes to `[-180, 180)`. The optional tail also sets the anchor; without it the current anchor must **already** be a body (`EOPNOTSUPP` otherwise). `EINVAL` out of range, `ENOENT` no such body. |
| `camera/pose/orbit/radius` | **St** | metres ≥ 0 → `camera.orbit_radius` | Hang the camera on a sphere about the anchor. **Non-zero wins over `position`/`geo`**; write `0` to hand placement back. `EINVAL` non-finite or `< 0`. |
| `camera/pose/orbit/azimuth` | **St** | degrees → `camera.orbit_azimuth` | Sweep in the frame's XY plane, from +X toward +Y. Any finite value (a track animating `0 → 360` closes exactly). `EINVAL` non-finite. |
| `camera/pose/orbit/elevation` | **St** | degrees `[-90, 90]` → `camera.orbit_elevation` | Rise above that plane toward +Z. `EINVAL` outside the range. |
| `camera/pose/rotation` | **St** | `<x> <y> <z> <w>` → `camera.rotation` | An explicit orientation quaternion — a vector(4) control with **one extra constraint**: the norm must be in `[0.5, 2]`, so an all-zero quaternion (which names no rotation) is `EINVAL` rather than being silently normalized to identity. Superseded by `aim` whenever an aim target is set. |
| `camera/pose/aim` | **St** | `<target> [off <x> <y> <z>] [frame <frame>] [up <up>] [roll <deg>]` → `camera.aim` | Four channels at once. Defaults for omitted keywords: offset `0 0 0`, frame `bodyfixed`, up `world`. **Roll is the exception** — a line without `roll` leaves the roll channel alone, because roll is animatable on its own. Keywords are order-independent and may appear at most once. `EINVAL` bad grammar/values, `ENOENT` target not live. |
| `camera/pose/aim_target` | **St** | target ref → `camera.aim_target` | What to look at — same vocabulary as `anchor`, including `part:`. Re-resolved **in the current simulation frame** and applied as an exact look-at constraint, so smoothing never lets a moving subject drift off-centre. `EINVAL`/`ENOENT`. |
| `camera/pose/aim_offset` | **St** | vector(3) → `camera.aim_offset` | Offset from the aim target, metres, measured in the **aim frame** (i.e. on the subject). `EINVAL` non-finite. |
| `camera/pose/aim_frame` | **St** | frame token → `camera.aim_frame` | The frame the aim offset is measured in. Defaults to `bodyfixed`, deliberately *not* the pose frame. |
| `camera/pose/aim_up` | **St** | `world`\|`target`\|`velocity`\|`free` → `camera.aim_up` | Which way is up while aiming (below). `EINVAL` otherwise. |
| `camera/pose/roll` | **St** | degrees → `camera.roll` | Roll about the view axis, applied **after** aim (an explicit `rotation` already names a complete orientation). `EINVAL` non-finite. |
| `camera/pose/fov` | **St** | degrees `[camera_fov_min, camera_fov_max]` → `camera.fov` | Vertical field of view. The default `1..179` is far wider than the game's own 15–120 — `SetFieldOfView` does not clamp, so fisheye and extreme telephoto really are available. `EINVAL` outside the configured range. |
| `camera/pose/ortho` | **St** | `0`/`1` → `camera.ortho` | Orthographic projection instead of perspective. `EINVAL` if not 0/1. |
| `camera/pose/ortho_height` | **St** | metres `> 0` → `camera.ortho_height` | Orthographic half-height. `EINVAL` on `≤ 0` or non-finite. ⚠ **The one camera change gatOS cannot undo** — KSA exposes a setter but no getter for it (5168), so there is nothing to capture at ownership and nothing to restore at release. It is therefore written only while the channel is explicitly claimed by an override or a track. |
| `camera/pose/smoothing` | **St** | seconds `[0, 10]` → `camera.smoothing` | Critically-damped filter on authored camera motion — the cheapest way to turn a coarse `timed_batch` ladder into a glide. For anchored placement it smooths the camera-to-anchor component while passing the anchor's translation through exactly; aim remains an exact constraint. Explicit (non-aim) rotation is smoothed. `0` is raw. `EINVAL` outside the range. |
| `camera/pose/reset` | **T** | `1` → `camera.pose_reset` | Drop **your overrides only**. An active track keeps driving; a reset is about your writes, not about stopping playback. Use `camera/release` to clear everything including the baseline. |

**The six frames.** A camera position is meaningless until you say what it is measured *about* (the
anchor) and which way the axes point (the frame). `pose/frame` and `pose/aim_frame` take the same six
tokens, resolved against the anchor every frame:

| Token | Vessel / part anchor | Body anchor | Unresolvable when |
|---|---|---|---|
| `ecl` | identity rotation, origin **(0,0,0)** — the vector *is* the absolute ecliptic point; no anchor needed | same | never |
| `cce` | identity rotation (CCE shares the ecliptic's axes), origin = the anchor's position | same | no anchor resolves |
| `bodyfixed` | the vessel's own body axes (**+X nose, +Y right, −Z up**); a `part:` anchor composes the part's assembly rotation onto them | the body's CCF (Z = north pole, X = prime meridian ∩ equator) | no anchor |
| `enu` | the vessel's East-North-Up | built at the pose's **own** geodetic lat/lon about that body | the vessel is not orbiting a body, is at its parent's centre, or the lat/lon is on the rotation axis |
| `lvlh` | the vessel's Local-Vertical-Local-Horizontal | the body's own, about its parent | position and velocity are near-zero or collinear (no orbital motion to derive it from) |
| `chase` | the vessel body frame (KSA's vehicle body frame *is* the chase convention; part-aware) | — | there is no vessel or part anchor |

**Nothing ever silently falls back to a different frame.** At *write* time an unresolvable frame is
`EOPNOTSUPP` on the failing `write(2)`. **Per frame**, the director instead **holds the last good
pose** and logs the reason once — a despawned anchor cannot write 60 log lines a second — and a
non-finite result is treated as unresolvable, so no NaN can reach the view matrix. Placement
precedence is **orbit → geodetic → cartesian**.

**Aim** resolves the same way, every frame: `aim point = target position + offset` rotated into the
aim frame. `pose/aim_up` picks the up reference: `world` = ecliptic +Z; `target` = the aim target's
own up (a celestial's rotation axis, or a vessel/part's body **−Z**); `velocity` = the **anchor's**
velocity, falling back to the aim target's when there is no anchor; `free` = the camera's current up
carried forward (parallel transport, so tracking never snaps the horizon back to level). A degenerate
up falls back to world +Z and then to ecliptic +X — a wrong roll is recoverable, a NaN view matrix is
not. A camera sitting exactly on its subject holds the previous rotation rather than producing one.

**Compositing precedence — `Track ?? Override ?? Baseline`, per channel.** *Baseline* is captured
from the live game camera at ownership take and never changes while owned, so an owned-but-unwritten
camera sits exactly where the game left it. *Override* is every leaf write above. *Track* is only the
channels the **active shot actually declares** — an undeclared channel falls through, which is what
lets a `timed_batch` (or a bare `echo`) pull focus while a track interpolates position. **Writing a
channel a shot is driving is accepted and superseded on the next frame** — no error, no lock: the
override is recorded and reappears the moment the shot stops declaring that channel. `pose/reset`
clears the overrides and leaves both the track and the baseline; `camera/release` clears everything,
baseline included, and hands the camera back.

**Ownership notes (read before shooting).**

- **While gatOS owns the camera the player's camera keys do nothing**, and `mode`/`follow`/`tidal`
  answer `EOPNOTSUPP`. That is the point — two writers fighting over one transform looks terrible.
  `pose/anchor` + `pose/aim_target` do everything a follow does and more. Always keep a way to run
  `echo 1 > /sim/camera/release`.
- **Only `fixed` is an ownership context, and that is a property of the game, not a choice.**
  gatOS's per-frame write survives because the parked controller writes nothing at the top of the
  next frame — true of `FixedController` alone. `IVAController` re-pins the seat position
  unconditionally (and, unfollowed, immediately cycles the mode away), and `MapController` assigns
  both position and rotation from its own solution (and, unfollowed, switches to `Free`). So
  **`camera/mode` is not a park target**: it accepts all five tokens `orbit|free|map|iva|fixed`
  because it drives the *game's* mode while gatOS is idle, and gatOS cannot own the camera in IVA or
  Map at all without a Harmony patch it deliberately does not install. `camera/map/scope` is the part
  of the map surface that *does* ship, and it is not ownership-gated.
- **`mode`, `follow`, `tidal`, `target` and `status` report idle values while gatOS does not own the
  camera.** An idle director publishes once and then does nothing, which is what keeps the feature
  genuinely free when off; the cost is that those leaves do not mirror the *player's* camera.
  `camera/map/scope` is the exception — a write to it publishes its own read-back.
- **Taking the camera out of `map` mode prints one three-second "Fixed Camera" alert.** Leaving Map
  must go through the game's own mode switch, because `MapController.OnSwitchOff` is the only thing
  that restores the player's controlled vessel — bypassing it would strand them with an
  uncontrollable ship. That transition also drops any latched `ctl/translate`/`ctl/rotate` flags
  (§3.4.19). Every other entry mode is a silent direct assignment.
- ⚠ **Owning the main camera changes which way an EVA kittenaut walks.** KSA feeds the main camera's
  basis into EVA locomotion, so while gatOS holds the camera, "forward" for a kitten on EVA is
  wherever the *shot* is facing. Documented rather than worked around: silently taking EVA control
  away would be the more surprising side effect.
- **The camera cannot go below surface + 0.5 m.** `Camera.ClampCamera()` runs at the top of every
  camera frame and pushes the camera up to that floor. It is the ocean-skimming feature, not a bug —
  but a `pose/geo` altitude below 0.5 m is silently corrected, and the altitude it tests is the
  frame viewport's, not necessarily the camera being written.
- **Releasing stops the take.** Once the director is idle nothing samples the player, so `Restore()`
  ends with the `camera/stop` verb (emitting `camera.finished reason=stopped`). The eased blend
  recomputes its destination **every frame of the blend**, so handing back onto a *moving* follow
  target lands on the target rather than on where it used to be.

**The track JSON schema** (`camera/track/<name>`; comments and trailing commas are accepted — a shot
list is exactly the kind of file people annotate). **Any unknown key at any level is `EINVAL`**, which
is also what makes adding a channel later non-breaking:

```jsonc
{
  "loop": false,                    // bool, default false — the authored loop default
  "defaults": {                     // object, optional
    "frame":  "cce",                // frame token — default for a shot's position block
    "anchor": "vessel:apollo11",    // target ref — default shot anchor
    "ease":   "in-out",             // ease token OR a 4-element bezier [x1,y1,x2,y2]
    "ease_power": 3                 // number [0.01,16]; requires "ease"
  },
  "shots": [                        // REQUIRED, non-empty, <= 256, ordered by t, non-overlapping
    {
      "name":     "pad-rise",       // string,  default "shot-<index>"
      "t":        0.0,              // seconds >= 0, default 0 — absolute on the track timeline
      "duration": 8.0,              // seconds > 0, REQUIRED
      "anchor":   "body:earth",     // target ref, default defaults.anchor, else none
      "blend_in": 0.5,              // seconds >= 0, default 0 — cross-fade from the previous shot

      // position — pick ONE mode; the mode is inferred from what you author, and an explicit
      // "mode" that disagrees with it is EINVAL.
      "position": { "mode": "cartesian", "curve": "catmull-rom", "frame": "bodyfixed",
                    "keys": [ { "t": 0, "v": [x,y,z], "ease": "out", "ease_power": 3,
                                "handle_out": [x,y,z], "handle_in": [x,y,z] } ] },
      // "position": { "mode": "orbit", "frame": "bodyfixed",
      //               "radius":    { "keys": [ {"t":0,"v":120} ] },        // metres >= 0
      //               "azimuth":   { "keys": [ {"t":0,"v":0}, {"t":8,"v":360} ] },  // degrees
      //               "elevation": { "keys": [ {"t":0,"v":15} ] } },       // degrees [-90,90]
      // "position": { "mode": "attach", "frame": "chase", "offset": [0, 3, -12] },

      "aim":      { "target": "vessel:apollo11",   // REQUIRED here, and must not be "none"
                    "offset": [0, 1.2, 0],         // default [0,0,0] — CONSTANT for the shot
                    "frame":  "bodyfixed",         // default bodyfixed, NOT defaults.frame
                    "up":     "world",             // default world
                    "roll":   { "keys": [ … ] } }, // optional animated roll, degrees
      "rotation": { "curve": "catmull-rom", "keys": [ {"t":0,"v":[x,y,z,w]} ] },
      "roll":     { "keys": [ … ] },  // degrees   (mutually exclusive with aim.roll)
      "fov":      { "keys": [ … ] },  // degrees, each key inside [camera_fov_min, camera_fov_max]
      "time":     { "keys": [ … ] }   // simulation-speed factor >= 0 (0 = paused) — see below
    }
  ]
}
```

- **Channel shape.** Every channel is `{ "curve": …, "keys": [ … ] }`, or the bare-array shorthand
  `"fov": [ … ]`. `position` is the one exception: its `curve` sits beside `keys` on the block.
  `curve` ∈ `step | linear | catmull-rom | bezier`, default **`linear`** — except a **rotation**
  channel, whose default is **`catmull-rom`** (squad with ≥ 3 keys, slerp otherwise); `bezier` on a
  rotation channel is `EINVAL`.
- **Keys.** Non-empty, at most `camera_max_keys`, times **strictly increasing** and inside
  `[0, duration]`; one key is legal (a constant). A key is `t` (required), `v` (required — number,
  `[x,y,z]`, or `[x,y,z,w]`), plus optional `ease`, `ease_power`, `handle_in`, `handle_out`. A
  quaternion key's norm must be in `[0.5, 2]`. `bezier` needs **both** handles on every segment;
  a handle on a non-bezier curve is `EINVAL`, as is `ease_power` without `ease` or alongside handles.
- **The ease-resolution rule:** a segment's ease comes from its **start** key; failing that, from the
  **end** key; failing that, `defaults.ease`; failing that, linear. Both spellings appear in real
  shot files, and either single rule would silently make half of one inert. It is folded into every
  key at parse time, so the evaluator never looks sideways.
- **Absolute, never incremental.** A full turn is the key pair `0 → 360`; there is no `+= ω·dt`
  anywhere, so the same `t` always yields a bit-identical sample (asserted), and an eased full orbit
  closes bit-exactly on its start point.
- **A shot declares only the channels it authors**, and only those are taken from the track — a
  `fov`-only shot in a track with `defaults.anchor` set claims `fov` alone. Rejected: a shot with no
  channels at all, out-of-order or overlapping shots, `roll` declared both at shot level and inside
  `aim`. **Warned, not rejected:** a shot declaring both `aim` and `rotation` — `aim` wins and the
  rotation channel is dropped.
- **Outside a shot the evaluator holds, it does not release:** before the first shot, in a gap
  between shots, and past the last shot it returns the nearest shot's terminal sample. A non-looping
  track that runs off its end reports `done` and **keeps returning its final sample**, so the shot
  does not snap away the instant it lands; `camera/stop` is what releases the channels. Ending
  playback is the player's decision, never the evaluator's.
- **Leaves with no track channel** (deliberate): `geo`, `ortho`, `ortho_height`, `smoothing`, and
  `frame`/`anchor`/`aim_*` as standalone channels. They stay L1/L2-only.
- **The `time` channel has no leaf at all.** `debug/time/warp` already covers the discrete case and
  is already schedulable, so C4 added only the *interpolated* form. It is **double-gated** on
  `debug_namespace` **and** `camera_allow_time_channel`, and a closed gate is a **warning, not an
  error** — the shot plays at 1×, with one host log line per ownership session. `0` pauses, `0.15` is
  slow-mo, `> 1` warps. The simulation speed is captured **lazily**, the first frame the channel is
  actually driven, and restored on release only if it was captured — a director that never touches
  time leaves the player's warp exactly as found. It **fights the game's auto-warp** rather than
  yielding to it (KSA itself picks no winner); stop an auto-warp before rolling a shot that drives
  time.

**Engine notes.** The director runs in a main-viewport-identity prefix after simulation advance and
before that viewport's controller/matrix build, and self-gates while unowned. It writes only
`PositionEcl`, `LocalRotation` and projection; KSA's immediately following `Camera.OnFrame` derives
every matrix, and the hook's postfix publishes the final applied transform. gatOS never touches a
matrix. A
playing track is a real entry in the `/sim/ctl/schedules` registry with `kind = camera-track` and the
predictable id **`camera`**, so `pause`/`scrub`/`rate`/`loop`/`stop` work on it through *either*
surface and `camera/playback` and `ctl/schedules/camera/t` can never disagree — it is the same
`PlaybackClock`. A committed track's bytes are **immutable**: a re-upload installs a fresh array under
a bumped version, so a shot that started on v1 keeps playing v1. Both the leaf `pose/orbit/*` path and
a track's `"mode":"orbit"` resolve through the *same* spherical placement, so `echo 90 >
pose/orbit/azimuth` and a track's azimuth curve put the camera in the same place. The whole authoring
loop is `cp /mnt/shots/flyby.json /sim/camera/track/flyby` — host-side editing already works through
the existing `/mnt` passthrough; there is no watcher and no persistence layer.

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
| `values` | number[] | vector arg: quaternion (4), burn `ut dvx dvy dvz` (4), color `r g b` (3), teleport `px py pz vx vy vz` (6), impulse `x y z` (3), translate `x y z` (3, signs), rotate `x y z` (3, signs), the audio play slots / set pairs (§3.9 notes below). |
| `token` | string | symbolic arg: attitude mode/frame token, a target id for focus/control, the audio clip name (`audio.play`) or channel target (`audio.set`/`audio.stop`), the impulse frame keyword (`cci`/`body`; omit ⇒ `cci`), the FX-editor entity (template id / body id; absent for `plumetrail`, `""` for the terrain globals). |
| `aux` | string | secondary symbolic arg — `audio.play` uses it for the caller-chosen channel `id=` (omit ⇒ auto `#N`); `debug.impulse` for the unit keyword (`ns`/`dv`; omit ⇒ `ns`); the FX-editor `*_set` actions for the concrete field path. |

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
| `vessel.translate` | — | values `[x,y,z]` | Frame | `ctl/translate` | body-axis signs, bang-bang; latches until rewritten (`0 0 0` stops) — **but the game also clears the latch** (§3.4.19); dead while `rcs_mode=Disabled` |
| `vessel.rotate` | — | values `[x,y,z]` | Frame | `ctl/rotate` | body-axis torque signs (+x roll right, +y pitch up, +z yaw right), bang-bang; latches until rewritten (`0 0 0` stops) — **but the game also clears the latch** (§3.4.19); full authority needs `attitude_mode=manual`; dead while `rcs_mode=Disabled` |
| `vessel.attitude_mode` | — | token | **Solver** | `ctl/attitude_mode` | §3.4.19 |
| `vessel.attitude_frame` | — | token | **Solver** | `ctl/attitude_frame` | §3.4.19 |
| `vessel.attitude_target` | — | values `[x,y,z,w]` | **Solver** | `ctl/attitude_target` | Body→CCI quaternion |
| `vessel.burn` | — | values `[ut,dvx,dvy,dvz]` | **Solver** | `ctl/burn` | CCI Δv |
| `vessel.rcs_mode` | — | token | **Solver** | `ctl/rcs_mode` | `Enabled`/`Disabled`; the flight computer's RCS master switch — `Disabled` kills `ctl/translate`+`ctl/rotate` outright |
| `vessel.scale` | — | value > 0 | Frame | `vessels/by-id/<id>/scale` | positive-only (`EINVAL` if ≤ 0 / non-finite); one-shot; exempt from the active-vessel authority gate |
| `vessel.always_render` | — | value `0`/`1` | Frame | `vessels/by-id/<id>/always_render` | render-distance override (bypass the sub-pixel cull); exempt from the active-vessel authority gate |
| `engine.active` | engine n | value `0`/`1` | Frame | `engines/<n>/active` | |
| `engine.min_throttle` | engine n | value `0..1` | Frame | `engines/<n>/min_throttle` | |
| `rcs.active` | rcs n | value `0`/`1` | Frame | `rcs/<n>/active` | |
| `light.on` | light n | value `0`/`1` | Frame | `lights/<n>/on` | |
| `light.brightness` | light n | value number | Frame | `lights/<n>/brightness` | |
| `light.color` | light n | values `[r,g,b]` | Frame | `lights/<n>/color` | |
| `light.outer_angle` | light n | value number (deg) | Frame | `lights/<n>/outer_angle` | outer cone half-angle; clamped ~0..89.94°; also lowers inner to ≤ outer |
| `light.inner_angle` | light n | value number (deg) | Frame | `lights/<n>/inner_angle` | inner cone half-angle; clamped `[0, outer]` |
| `animation.goal` | anim n | value `0..1` | Frame | `animations/<n>/goal`, `solar/<n>/goal`, `lights/<n>/goal` | one ordinal, three views |
| `decoupler.fire` | decoupler n | value `1` | Frame | `decouplers/<n>/fire` | one-shot; `EBUSY` if already fired, `EOPNOTSUPP` if the module is disabled (`decouplers/<n>/enabled` = `0`) |
| `docking.undock` | docking n | value `1` | Frame | `docking/<n>/undock` | one-shot |
| `camera.focus` | — | token = id | Frame | `ctl/focus`, `bodies/<id>/focus`, `debug/focus` | view-only; no authority gate; sets the follow target on **both** the viewport's base and map cameras (so the map view can no longer lag the base view), `alert:false`; works even with `camera_enabled=false` |
| `debug.control_vessel` | — | token = id | Frame | `debug/control_vessel` | grants control |
| `debug.teleport` | — | values `[px,py,pz,vx,vy,vz]` | Frame | `debug/vessels/<id>/teleport` | CCI about current parent |
| `debug.impulse` | — | values `[x,y,z]`; token = frame (`cci`\|`body`, omit ⇒ `cci`); aux = unit (`ns`\|`dv`, omit ⇒ `ns`) | Frame | `debug/vessels/<id>/impulse` | one-shot kick; N·s ⇒ Δv = J ÷ live mass; `dv` ⇒ Δv m/s as-is |
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
| `debug.iva_physics` | — | value `0`/`1` | Frame | `debug/iva/enabled` | vessel-agnostic; **the master switch** — `0` releases every object and disposes every cabin simulation |
| `debug.iva_run_outside_iva` | — | value `0`/`1` | Frame | `debug/iva/run_outside_iva` | vessel-agnostic |
| `debug.iva_adopt` | — | token = vessel id; values `[subpart_iid]` or `[subpart_iid,vx,vy,vz]` | Frame | `debug/iva/adopt` | vessel-agnostic; **SubParts only** (a top-level part is `ENOENT` by design) |
| `debug.iva_adopt_all` | — | token = vessel id; value = max (`0` = the cap); `aux` = template substring | Frame | `debug/iva/adopt_all` | vessel-agnostic; smallest props first |
| `debug.iva_release` | object id | value `1` | Frame | `debug/iva/<id>/release` | vessel-agnostic; id in `ordinal`; restores the exact rest pose |
| `debug.iva_clear` | — | — | Frame | `debug/iva/clear` | vessel-agnostic; releases all (the sim stays enabled) |
| `debug.iva_nudge` | object id | values `[vx,vy,vz]` | Frame | `debug/iva/<id>/nudge` | id in `ordinal`; assembly-frame velocity kick (m/s) |
| `debug.engineplume_set` | — | token = template id; aux = field path; `values` per the field's arity (a scalar is additionally mirrored in `value`) | Frame | `debug/engineplume/templates/<id>/<field>` | vessel-agnostic; edits the **shared** template (every nozzle using it) |
| `debug.engineplume_reset` | — | token = template id | Frame | `debug/engineplume/templates/<id>/reset` | vessel-agnostic; restores the pristine (pre-gatOS) values |
| `debug.plumetrail_set` | — | token **absent**; aux = field path; `values` per arity (scalar also in `value`) | Frame | `debug/plumetrail/render/<field>` | vessel-agnostic; one global renderer, so no entity id |
| `debug.plumetrail_reset` | — | — | Frame | `debug/plumetrail/reset` | vessel-agnostic; restores the pristine render settings |
| `debug.plumetrail_clear` | — | — | Frame | `debug/plumetrail/clear` | vessel-agnostic; drops the live trail geometry (one-shot, not a settings change) |
| `debug.clouds_set` | — | token = body id; aux = field path (carries the layer/cloud-type indices); `values` per arity (scalar also in `value`) | Frame | `debug/clouds/bodies/<id>/<field>` | vessel-agnostic; re-uploads the affected layer |
| `debug.clouds_reset` | — | token = body id | Frame | `debug/clouds/bodies/<id>/reset` | vessel-agnostic; restores that body's pristine cloud values |
| `debug.terrain_set` | — | token = body id (**`""`** for the global `wireframe`); aux = field path; `values` per arity (scalar also in `value`) | Frame | `debug/terrain/wireframe`, `debug/terrain/bodies/<id>/<field>` | vessel-agnostic; per-body fields write the reference object **and** the GPU uniform slot |
| `debug.terrain_reset` | — | token = body id | Frame | `debug/terrain/bodies/<id>/reset` | vessel-agnostic; restores that body's pristine terrain values |
| `audio.play` | — | token = clip name; aux = channel id (optional); values `[start_ms, end_ms, vol, loop, pan, pitch, group]` (defaults `[0,0,1,0,0,1,0]`; `end_ms` 0 = whole clip; group `0`=sfx `1`=music `2`=ui) | Frame | `audio/play` | vessel-agnostic; not a `debug.*` action but gated by `audio_enabled` (`EOPNOTSUPP` when off) |
| `audio.set` | — | token = channel id or clip name; values = flat `[key, value, …]` pairs (keys: `0`=vol `1`=pan `2`=pitch `3`=paused(0/1) `4`=seek_ms) | Frame | `audio/set` | vessel-agnostic |
| `audio.stop` | — | token = `all` \| channel id \| clip name | Frame | `audio/stop` | vessel-agnostic |
| `schedule.pause` | — | value `0`/`1`; token = player id | Frame | `ctl/schedules/<id>/pause` | vessel-agnostic; gated by `schedule_enabled` (`EOPNOTSUPP` when off). `EINVAL` if not 0/1 or the id token is missing, `ENOENT` no such player |
| `schedule.scrub` | — | value = ms ≥ 0 finite; token = player id | Frame | `ctl/schedules/<id>/scrub` | vessel-agnostic; seek only — fires no entries |
| `schedule.rate` | — | value `0..100`; token = player id | Frame | `ctl/schedules/<id>/rate` | vessel-agnostic; `0` is a legal frozen state |
| `schedule.loop` | — | value `0`/`1`; token = player id | Frame | `ctl/schedules/<id>/loop` | vessel-agnostic |
| `schedule.stop` | — | value `1`; token = player id | Frame | `ctl/schedules/<id>/stop` | vessel-agnostic; the player stays listed |
| `schedule.remove` | — | value `1`; token = player id | Frame | `ctl/schedules/<id>/remove` | vessel-agnostic; drops it from the registry |
| `schedule.clear` | — | value `1`; **no** token | Frame | `ctl/schedules/clear` | vessel-agnostic; always Ok; also discards committed-but-unactivated schedules |
| `camera.enabled` | — | value `0`/`1` | Frame | `camera/enabled` | vessel-agnostic; `1` takes the camera, `0` releases it over `camera_release_blend_s`. Every `camera.*` below is gated by `camera_enabled` (`EOPNOTSUPP` when off) and bypasses the authority gate |
| `camera.release` | — | value `1` | Frame | `camera/release` | vessel-agnostic; hard cut back to game control |
| `camera.mode` | — | token ∈ `orbit`\|`free`\|`map`\|`iva`\|`fixed` | Frame | `camera/mode` | vessel-agnostic; drives the **game's** mode; `EOPNOTSUPP` while gatOS owns the camera |
| `camera.follow` | — | token = target ref (`vessel:`/`body:`/`none`) | Frame | `camera/follow` | vessel-agnostic; both viewport cameras; `EOPNOTSUPP` for `part:` or while owned, `ENOENT` if gone |
| `camera.tidal` | — | value `0`/`1` | Frame | `camera/tidal` | vessel-agnostic; `ENOENT` when nothing is followed, `EOPNOTSUPP` while owned |
| `camera.map_scope` | — | value = metres ≥ 0 | Frame | `camera/map/scope` | vessel-agnostic; **not** ownership-gated; the game re-clamps it (§3.11) |
| `camera.position` | — | values `[x,y,z]`; token = frame token or `""` (keep the current frame) | Frame | `camera/pose/position` | vessel-agnostic |
| `camera.frame` | — | token = frame token | Frame | `camera/pose/frame` | vessel-agnostic |
| `camera.anchor` | — | token = target ref (incl. `part:<vessel>/<iid>`) | Frame | `camera/pose/anchor` | vessel-agnostic; `ENOENT` if not live |
| `camera.geo` | — | values `[lat,lon,alt]` (lon normalized to `[-180,180)`); token = `body:<id>` or `""` | Frame | `camera/pose/geo` | vessel-agnostic; `EOPNOTSUPP` when the token is empty and the current anchor is not a body |
| `camera.orbit_radius` | — | value = metres ≥ 0 finite | Frame | `camera/pose/orbit/radius` | vessel-agnostic; non-zero wins over `position`/`geo` |
| `camera.orbit_azimuth` | — | value = degrees (any finite) | Frame | `camera/pose/orbit/azimuth` | vessel-agnostic |
| `camera.orbit_elevation` | — | value = degrees `[-90,90]` | Frame | `camera/pose/orbit/elevation` | vessel-agnostic |
| `camera.rotation` | — | values `[x,y,z,w]`, norm ∈ `[0.5,2]` | Frame | `camera/pose/rotation` | vessel-agnostic; a zero quaternion is `EINVAL`, never normalized to identity |
| `camera.aim` | — | token = target ref; values = the **7 slots** `[offX, offY, offZ, aimFrameOrdinal, aimUpOrdinal, roll, rollPresent]`, defaults `[0,0,0,2,0,0,0]` | Frame | `camera/pose/aim` | vessel-agnostic; frame ordinals `0`=ecl `1`=cce `2`=bodyfixed `3`=enu `4`=lvlh `5`=chase; up ordinals `0`=world `1`=target `2`=velocity `3`=free; **slot 6 = 0 leaves the roll channel alone** |
| `camera.aim_target` | — | token = target ref | Frame | `camera/pose/aim_target` | vessel-agnostic; `ENOENT` if not live |
| `camera.aim_offset` | — | values `[x,y,z]` (metres, in the aim frame) | Frame | `camera/pose/aim_offset` | vessel-agnostic |
| `camera.aim_frame` | — | token = frame token | Frame | `camera/pose/aim_frame` | vessel-agnostic |
| `camera.aim_up` | — | token ∈ `world`\|`target`\|`velocity`\|`free` | Frame | `camera/pose/aim_up` | vessel-agnostic |
| `camera.roll` | — | value = degrees (finite) | Frame | `camera/pose/roll` | vessel-agnostic; applied after aim |
| `camera.fov` | — | value = degrees `[camera_fov_min, camera_fov_max]` | Frame | `camera/pose/fov` | vessel-agnostic |
| `camera.ortho` | — | value `0`/`1` | Frame | `camera/pose/ortho` | vessel-agnostic |
| `camera.ortho_height` | — | value = metres `> 0` finite | Frame | `camera/pose/ortho_height` | vessel-agnostic; **not restorable** (§3.11) |
| `camera.smoothing` | — | value = seconds `[0,10]` | Frame | `camera/pose/smoothing` | vessel-agnostic |
| `camera.pose_reset` | — | value `1` | Frame | `camera/pose/reset` | vessel-agnostic; clears overrides only |
| `camera.play` | — | token = track name; aux = `@group` name (optional); values = the **6 slots** `[atSeconds, rate, loop, atPresent, ratePresent, loopPresent]`, defaults `[0,1,0,0,0,0]` | Frame | `camera/play` | vessel-agnostic; **also needs `schedule_enabled`** (`EOPNOTSUPP` otherwise) — a track is a `ctl/schedules` player. A grouped player ignores `at`/`rate`/`loop` |
| `camera.set` | — | values = flat `[key, value, …]` pairs (keys: `0`=t seconds `1`=rate `2`=loop `3`=paused); **no** token | Frame | `camera/set` | vessel-agnostic; needs `schedule_enabled`; `ENOENT` when nothing is playing |
| `camera.stop` | — | value `1` | Frame | `camera/stop` | vessel-agnostic; needs `schedule_enabled`; idempotent |

### 5.2 Writing over each transport

**9P / `/sim` file** — write the value text into the file:
```sh
echo 0.5 > /sim/vessels/active/ctl/throttle
echo "6578100 0 0 0 7784 0" > /sim/debug/vessels/Hunter/teleport
```

**Atomic multi-write** — every write above blocks until the game thread executes it (one command
per frame); to make N writes land in the **same tick**, group them through `/sim/ctl/batch` (§3.10):
```sh
printf '%s\n' \
  "vessels/by-id/Hunter/ctl/throttle 1" \
  "vessels/by-id/Polaris/ctl/throttle 1" \
  commit > /sim/ctl/batch
```

**Timed multi-write** — to spread writes over a *timeline* instead, commit them to
`/sim/ctl/timed_batch` (§3.10); the same paths, each with an absolute-ms offset:
```sh
printf '%s\n' \
  "0    vessels/by-id/Hunter/ctl/throttle 1" \
  "1200 vessels/by-id/Hunter/ctl/ignite   1" \
  commit > /sim/ctl/timed_batch
```

**HTTP `POST /v1/command`** — JSON body (the canonical generic write):
```json
{ "vessel_id": "Hunter", "action": "debug.teleport", "values": [6578100,0,0,0,7784,0] }
```
Response `200 {"outcome":"ok"}`, or `{ "errno": "...", "message": "..." }` with the mapped status.

> **`vessel_id` is mandatory — including for globally addressed actions.** The JSON parser (shared by
> `POST /v1/command` and MQTT `gatos/command`) requires `vessel_id` (or its alias `vessel`) to be
> present **and to be a JSON string**; omitting it, or sending `null`, is `400 EINVAL`
> `missing 'vessel_id'`. Actions that do not address a vehicle at all — the whole `camera.*`,
> `schedule.*` and `audio.*` families, plus `debug.warp`, `debug.weld_clear`, `debug.thug_life_*`,
> `debug.iva_*` and the `debug.*` FX-editor actions — take the **empty string**, which is what the
> `/sim` control files themselves author and what `examples/sdk-ts` sends:
> ```json
> { "vessel_id": "", "action": "camera.fov", "value": 24 }
> { "vessel_id": "", "action": "schedule.rate", "token": "launch-seq", "value": 0.5 }
> ```
> Any other string is simply ignored by those actions (they route before vehicle resolution), but
> `""` is the canonical spelling — it says "this command names no vessel".

**HTTP `POST /v1/fs/<path>`** — raw value body (the file twin), e.g.
`POST /v1/fs/vessels/active/ctl/throttle` body `0.5` → `200 {"outcome":"ok"}`.

**MQTT** — publish `gatos/command` (the JSON) or `gatos/sim/<path>/set` (the raw value).

---

## 6. Teleport & impulse semantics (read carefully)

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
- **Formations: teleport the whole fleet through `/sim/ctl/batch` (§3.10).** Sequential teleport
  writes each wait a frame, and one frame at ~7.8 km/s is ~100 m of drift — far more than a
  few-meter spacing. Batched, all the teleports execute in the same tick and the spacing you
  computed is the spacing you get.

`mu` and `radius` come from `/sim/bodies/<parent>/{mu,radius}` (or SDK `bodies()` → `mu`,
`mean_radius`). See `.agents/skills/gatos/recipes.md` for a complete teleport program.

**Impulse** (`debug.impulse`) rides the same machinery — it reads the current CCI state, bumps only
the **velocity**, and re-teleports at the same position — so everything above about the frame applies:

- The default vector frame is the **current parent's CCI**; `+Z` kicks toward the parent's north pole,
  and a kick along the velocity unit vector is prograde. The `body` keyword instead reads the vector in
  the **vessel body frame** (+X = nose/thrust axis, the same convention as `attitude/quat`) and rotates
  it through the live Body→CCI attitude at application time — `100000 0 0 body` kicks straight
  "forward" wherever the vessel points.
- Units: the default is an **impulse in newton-seconds** — Δv = J ÷ the vessel's live total mass, the
  same math as KSA's own docking-separation impulse (cf. `pushoff_impulse`, stock 5000–7000 N·s). The
  `dv` keyword skips the mass division and applies the vector **directly as Δv in m/s** — handy when
  you've done the orbital math and just want the velocity change (`ctl/burn` semantics, minus the
  autopilot, the propellant, and the waiting).
- It is **instantaneous and non-physical**: no propellant is spent, no engine needs to point anywhere,
  and the change lands in one tick regardless of magnitude. A landed vessel gets kicked exactly like an
  orbiting one (the resulting "orbit" may immediately intersect the ground — that's your problem, it's
  a cheat). Zero vectors succeed as a no-op. Errnos: `EINVAL` (bad arity/keyword/non-finite),
  `EBUSY` (no parent body, or mass unavailable for an N·s kick), `ENOENT` (vessel gone).

---

## 7. HTTP `/v1` endpoint reference (`gatOS.Http/SimHttpServer.cs`)

Base URL: `$GATOS_HTTP` (guest) or `http://127.0.0.1:<http_preferred_port>/v1` (host, default `4242`).
Loopback only, no auth. Aggregate reads serialize the snapshot via `SimJson`. Connections are
HTTP/1.1 **keep-alive** (idle timeout ~30 s; `Connection: close` honored; SSE responses stream until
the client disconnects) — polling clients can and should reuse one connection.

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
| GET | `/v1/audio/files` | JSON clip list `[{name,bytes,version,ready}]` (requires `audio_enabled`). |
| PUT/POST | `/v1/audio/file/{name}[?offset=N][&complete=0\|1]` | **binary** clip upload: the raw body lands at `offset` (default `0` = start a fresh, truncated upload). `complete` defaults to `1` (single-shot commits immediately); chunked uploads send `complete=0` on every chunk but the last, each chunk's `offset` = bytes sent so far (out-of-order ⇒ `EINVAL` 400). Bodies ≤ 1 MiB (the server's request cap) — chunk anything larger. → `{"outcome":"ok","name":…,"bytes":…,"ready":…}`. |
| DELETE | `/v1/audio/file/{name}` | evict a clip → `{"outcome":"ok"}` (404 when absent). |

The audio **control** surface needs no dedicated routes: `POST /v1/fs/audio/play` (body = the same
line the 9P file takes), likewise `set`/`stop`; `GET /v1/fs/audio/status|info` (+ `?stream=1` SSE);
`POST /v1/command` with the `audio.*` actions. MQTT mirrors the same leaves (`gatos/sim/audio/*` +
`gatos/sim/audio/play/set` etc.) and accepts `audio.*` on `gatos/command`. The dedicated binary
upload routes above are the **one deliberate transport-parity exception** (the field-write path is
UTF-8 text with a 1 MiB body cap); MQTT gets **no** upload at all (text payloads + retained-topic
memory make it a bad fit) — like the display stream, documented rather than mirrored. Example:

```sh
curl -T alarm.mp3 "http://127.0.0.1:4242/v1/audio/file/alarm.mp3"       # upload (≤ 1 MiB)
curl -X POST --data 'alarm.mp3 vol=0.8' "http://127.0.0.1:4242/v1/fs/audio/play"
```

The **schedule** (§3.10) and **camera** (§3.11) surfaces add **no dedicated routes at all** — every
one of their leaves is an ordinary VFS node, so the `/v1/fs/<path>` mirror and `POST /v1/command`
already reach all of it. Note the path spelling: the segment after `/v1/fs/` is the `/sim`-relative
path with **no `sim/` prefix** (`/v1/fs/camera/pose/fov`, not `/v1/fs/sim/camera/...`); only the MQTT
topics carry a `sim/` component (`gatos/sim/camera/pose/fov`). Camera **tracks are deliberately in
the field mirror** (unlike audio clips, which opt out): a track is small JSON text, so
`POST /v1/fs/camera/track/<name>` with the document as the body is the whole upload — there is **no**
`/v1/camera` binary route, and none is needed while `camera_max_track_bytes` (1 MiB default) sits at
the server's 1 MiB request cap. A malformed track fails that `POST` directly with its parse `EINVAL`
(the HTTP path can carry the errno the 9p clunk cannot). Examples:

```sh
H=http://127.0.0.1:4242/v1
curl -X POST --data-binary @flyby.json "$H/fs/camera/track/flyby"       # upload a track
curl -X POST --data 'flyby at 2 rate 0.5' "$H/fs/camera/play"           # play it
curl -s "$H/fs/camera/status"                                           # the whole camera state
curl -X POST --data-binary @launch.tb "$H/fs/ctl/timed_batch"           # commit a schedule
curl -s "$H/fs/ctl/schedules/launch-seq/state"                          # …and watch it
curl -X POST -H 'content-type: application/json' \
     -d '{"vessel_id":"","action":"camera.fov","value":24}' "$H/command"
```

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
| schedule / playback timeline offsets (`ctl/timed_batch` offsets, `ctl/schedules/<id>/{t,duration,scrub}`, `camera/playback` times) | **milliseconds** — the one place in `/sim` that is not seconds |
| track-authoring times (a shot's `t`/`duration`/`blend_in`, a key's `t`, `camera/play at`, `camera/set t`) | **seconds** |
| playback rate (`ctl/schedules/<id>/rate`, `camera/set rate`) | dimensionless multiplier, `0..100` (`0` = frozen) |
| camera angles (`pose/{roll,fov}`, `pose/orbit/{azimuth,elevation}`, `pose/geo` lat/lon, a track's `roll`/`fov`) | **degrees** |
| camera lengths (`pose/position`, `pose/aim_offset`, `pose/geo` altitude, `pose/orbit/radius`, `pose/ortho_height`, `camera/map/scope`) | **meters** (altitude is above **terrain**, degrading to above the mean sphere) |
| camera durations (`pose/smoothing`, `camera_release_blend_s`) | **seconds** |
| camera `time` channel / `time_scale` | dimensionless simulation-speed factor (`0` = paused, `1` = realtime) |
| `/sim/debug` FX-editor fields (§3.7) | **per field** — the unit is in the leaf's name or its §3.7 row (`…_km` km, `…_deg` degrees, `…_px` pixels, `…_m`/raymarch/height meters, `s`, `m/s`, `r g b`/`r g b a` each `0..1`); these do **not** follow the "everything is meters" rule |

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
