# SPEC: the gatOS MCP API

> **This is the public v1 contract for gatOS's Model Context Protocol (MCP) server.** It is the
> concise, JSON-first interface for an AI agent controlling or observing Kitten Space Agency. It
> groups the same live game model that `/sim`, HTTP, and MQTT expose into useful world, celestial,
> vessel, kitten, runtime, and control operations. It is **not** a path-for-path `/sim` filesystem
> mirror. The filesystem remains the human-exploration interface; MCP is the agent interface.
>
> **CONSTITUTION — keep this in sync.** MCP projects the one `SimSnapshot` read model and sends the
> one `SimCommand` write model. Whenever that underlying public surface changes, update this file in
> the same change. `SPEC_9P_FILESYSTEM.md` remains the detailed authority for `/sim`, units, game
> semantics, action keys, command phase, and errno; this specification is the authority for the MCP
> grouping, resource URIs, tool names, JSON schemas, and MCP-specific transport behavior.

## 1. Purpose and shared model

MCP runs as a Streamable HTTP server in the gatOS mod process. `mcp_bind_host` defaults to
`127.0.0.1`, so the default endpoint is `http://127.0.0.1:<bound-port>/mcp`; it accepts another IP
address or a wildcard such as `0.0.0.0` for intentional network access. `mcp_enabled` defaults to
`true`; `mcp_preferred_port` defaults to `4243`, falls back to an ephemeral port when occupied, and
accepts `0` to choose ephemeral directly. The server accepts no bearer token. It validates an exact
configured Host/Origin for a specific-address bind; a wildcard bind accepts the authority used by
the client. It is optional: a failure to start it
leaves the game, `/sim`, HTTP, MQTT, serial, and the VM usable. It reads only published, immutable
telemetry snapshots on its request threads. Every game mutation is converted to the existing immutable
`SimCommand` and sent through `ICommandSink`/`CommandQueue`; KSA remains game-thread-only.

This gives MCP the same live read-back, authority checks, game-thread phases, command timeout, and
accessor health behavior as the existing transports. It adds no KSA reflection binding and no second
actuator routing table.

MCP uses **raw KSA ids** for celestial and vessel identifiers, as the aggregate HTTP JSON APIs do.
It never asks an agent to use the sanitized names that exist only for filesystem paths.

Every tool result is an envelope with `ok`, `data`, `snapshot_sequence`, `ut`, `outcome`, `errno`,
`message`, and `retryable`. The sequence and UT identify the exact telemetry snapshot an agent
reasoned from. Vectors and quaternions use the
same JSON objects as `SimJson` (`{x,y,z}` and `{x,y,z,w}`); all property names are `snake_case`.

### 1.1 Limits and pagination

`gatos.list_celestials`, `gatos.list_vessels`, and `gatos.list_kittens` accept `limit` and opaque
`cursor` inputs.
Their `data` is `{items, next_cursor?, limit}`, inside the common result envelope. `limit` defaults to **50**, must be a
positive integer, and must not exceed **1,000**. A client follows `next_cursor` until it is absent.

There is **no MCP JSON response/result-size cap or measurement**. In particular,
`gatos.get_world(detail:"full")` and `gatos.get_vessel(include:["all"])` return their complete logical documents rather than silently
truncating them. Request framing is capped at **24 MiB**: this is an HTTP safety limit, not a
result inspection limit. Audio, camera-track, and clutter-texture uploads are deliberately
chunkable; upload chunks should stay well below that framing limit. Binary uploads (`gatos.audio_clip`
and `gatos.paint_texture`) travel as base64, which inflates the payload by **4/3**: budget a chunk
against the 24 MiB framing cap *after* that expansion, not against the decoded byte count. Existing
domain limits still apply: for example, audio/track/texture store capacity, the sticker registry's
`paint_stickers_max_count`, schedule entry and live-player caps, and the game's command-per-frame
limit are unchanged. **Stickers add no upload path of their own**: a sticker draws an image from the
clutter-texture store, so `gatos.paint_texture` remains the only binary paint upload.

### 1.2 Errors and availability

Expected command failures are returned in the common envelope with a stable `errno`, an `outcome`,
a human-readable `message`, and a `retryable` indication. The errno vocabulary is unchanged:

| errno | Meaning |
|---|---|
| `EINVAL` | Invalid type, shape, arity, range, or semantic combination — including a **full sticker registry**, which has no `ENOSPC` spelling in the command envelope and instead reports the cap in `message`. |
| `ENOENT` | A requested live entity, module, object, or asset does not exist. |
| `EACCES` | Control/debug access is disabled or the authority gate rejected the target. |
| `EBUSY` | The action cannot run now, such as a full channel table or an already-fired trigger. |
| `EIO` | A KSA operation faulted; its accessor may be degraded for the session. |
| `ETIMEDOUT` | The game thread did not drain the command before `command_timeout_ms`. |
| `EOPNOTSUPP` | The feature is unavailable, disabled by its capability gate, or has degraded. |
| `EFBIG`, `ENOSPC`, `EEXIST`, `EPERM` | The existing audio-clip, camera-track, and clutter-texture store upload errors. |

`gatos.get_capabilities` is the required preflight. It reports list limits, configured feature
availability, control/debug authority status, and per-action phase, gate, argument-shape, unit, and
safety metadata. Transport health and degraded accessors instead live in the world/status snapshot;
feature-store limits live in `gatos.get_runtime_state`. Agents should call capabilities before a
feature-specific tool and should read a target vessel's `controllable` state before attempting
normal flight control.

## 2. Resources

Resources are read-only snapshots for clients that support MCP resources. The equivalent `gatos.get_*`
tools exist for clients that only use tools; they return the same JSON projection.

| URI / template | Content |
|---|---|
| `gatos://world` | Current world summary: time, system, transport/control status, and entity indexes. Use `gatos.get_world(detail:"full")` for the complete snapshot. |
| `gatos://celestials` | Celestial catalog, up to the 1,000-entry list maximum. |
| `gatos://celestials/{id}` | One `BodySnapshot`, addressed by raw celestial id. |
| `gatos://vessels` | Vessel catalog, up to the 1,000-entry list maximum. |
| `gatos://vessels/{id}` | One complete vessel document. Use `gatos.get_vessel` when include filtering is needed. |
| `gatos://kittens` | Kitten-vessel catalog, up to the 1,000-entry list maximum. |
| `gatos://kittens/{id}` | One kitten vessel document. |
| `gatos://runtime/{feature}` | Live state for one runtime feature from the `feature` vocabulary in §3. |
| `gatos://capabilities` | Current capability, gate, limit, and health document. |

Resource reads are snapshot reads, not subscriptions. Use `gatos.wait` to wait for a later snapshot,
a matching event, or a simulation time rather than polling at an arbitrary wall-clock cadence.

## 3. Read tools

All tools below return JSON and preserve the full native precision/shape supplied by `SimJson`.

| Tool | Required input | Optional input | Result |
|---|---|---|---|
| `gatos.get_world` | — | `detail` (`summary` or `full`; defaults to `summary`) | Current world summary or complete snapshot. |
| `gatos.list_celestials` | — | `limit`, `cursor` | Paginated celestial summaries. |
| `gatos.get_celestial` | `id` | — | One full celestial document. |
| `gatos.list_vessels` | — | `limit`, `cursor`, `controlled`, `controllable`, `is_kitten`, `parent_body`, `situation` | Paginated vessel summaries. |
| `gatos.get_vessel` | `id` | `include` | One vessel document, restricted to requested sections. |
| `gatos.list_kittens` | — | `limit`, `cursor` | Paginated kitten summaries. |
| `gatos.get_kitten` | `id` | `include` | One kitten vessel document; a non-kitten id is `EINVAL`. |
| `gatos.get_runtime_state` | `feature` | — | One runtime-state document. |
| `gatos.get_capabilities` | — | — | List limits, feature gates, authority state, and per-action metadata. |
| `gatos.wait` | at least one condition | `timeout_ms` | A later snapshot or event satisfying a condition. |

`gatos.get_vessel` and `gatos.get_kitten` accept `include` as an array drawn from
`flight`, `orbit`, `environment`, `propulsion`, `resources`, `power`, `control`, `modules`,
`encounters`, `parts`, `paint`, and `all`. Omit `include` or include `all` for the complete document. A
sectioned response always includes identity/control fields (`id`, `name`, `situation`,
`parent_body_name`, `controlled`, `controllable`, and `is_kitten`); request `flight` for position,
velocity, attitude, altitude, and navball data.

The `feature` input to `gatos.get_runtime_state` is one of `camera`, `schedules`, `audio`, `paint`,
`paint_textures`, `paint_stickers`, `welds`, `thug_life`, `face_fx`, `iva`, `engine_plume`,
`plume_trail`, `clouds`, or `terrain`. It exposes the corresponding logical snapshot/read-back state, including configuration
and pristine/reset diagnostics where the underlying feature provides them.

`gatos.wait` requires at least one of:

- `after_sequence`: return after the published sequence becomes greater than this value.
- `event_type` with optional `vessel_id`: return the next matching event.
- `until_ut`: return after universal simulation time reaches this value.

When more than one condition is supplied, the wait completes on the first condition that matches.
`timeout_ms` is a bounded wall-clock wait from **1** through **120,000** milliseconds. A timeout
returns an `ETIMEDOUT` envelope with `outcome:"timed_out"` and `retryable:true`; a successful result
carries the later snapshot sequence and the matching event when applicable.

## 4. Command envelope and common semantics

`gatos.command`, `gatos.execute_batch`, and `gatos.schedule_batch` use the canonical command envelope:

```json
{
  "action": "vessel.throttle",
  "vessel_id": "Hunter",
  "ordinal": -1,
  "value": 0.5,
  "values": null,
  "token": null,
  "aux": null
}
```

The fields are exactly `action`, `vessel_id`, `ordinal`, `value`, `values`, `token`, and `aux`; there
are no aliases. Their meanings, accepted action keys, numeric/vector shapes, units, and derived
Frame/Solver phase are the `SimCommand` contract in `SPEC_9P_FILESYSTEM.md` §5. `vessel_id` is the
raw target id for vessel actions and the empty string for global actions. `ordinal` is `-1` when an
action does not address an indexed module or registry entry.

The concise tools in §5 are the preferred agent surface. `gatos.command` is the complete typed
escape hatch for an action that does not merit a special verb; it still accepts only this envelope,
never a `/sim` path or raw file payload.

All mutations retain existing behavior:

- The command phase is derived centrally from its action key. A caller cannot choose Frame versus
  Solver phase.
- Normal vessel control obeys `control_enabled`, `control_all_vessels`, and KSA's own
  `controllable` behavior. Debug actions and the documented authority exemptions keep their current
  behavior.
- State commands are idempotent setpoints; trigger commands are one-shots and may report `EBUSY`.
- Read-backs are published snapshots, so an accepted command can be visible on a later sample or
  solver step rather than in the immediate tool result.

## 5. Control tools

These tools are logical groupings over existing command actions. They do not add a second KSA command
surface. A successful command result has `ok:true`, an accepted-command `data` document, and the
sample at which it was accepted. Failed commands use the error fields in §1.2.

| Tool | Input and intent |
|---|---|
| `gatos.ignite_engines` | `{vessel_id}`. Fires the vessel ignition trigger. |
| `gatos.shutdown_engines` | `{vessel_id}`. Fires the vessel shutdown trigger. |
| `gatos.activate_stage` | `{vessel_id}`. Fires the next-stage trigger. |
| `gatos.vessel_control` | `{operation, vessel_id, ordinal?, value?, values?, token?, aux?}`. Applies one vessel action: engine master, throttle, master lights/RCS, manual RCS translation/rotation, RCS mode, attitude mode/frame/target, burn, scale, or always-render. An unqualified operation derives `vessel.<operation>` except for `focus` and `take_control`; a qualified action remains qualified. Distinct phases need `gatos.execute_batch`/`gatos.schedule_batch` for coordination. |
| `gatos.module_control` | `{operation, vessel_id, ordinal, value?, values?, token?, aux?}`. Controls one indexed engine, RCS controller, light, animation/deployment, docking port, or decoupler. An unqualified operation derives `module.<operation>` except for `undock`, `fire_decoupler`, and `pushoff`; a qualified action remains qualified. |
| `gatos.camera_control` | `{operation, value?, values?, token?, aux?}`. Takes/releases ownership, changes game follow/mode/tidal/map scope, or patches a gatOS camera pose, aim, projection, and smoothing channel. It maps to the existing `camera.*` commands and preserves ownership restrictions. |
| `gatos.camera_track` | `{operation, name?, json?, offset?:0, complete?:true, value?, token?}`. `operation` is `list`, `read`, `upload`, `delete`, `play`, `update`, or `stop`. Upload sends the named JSON track, optionally chunked at a byte `offset`; `complete` commits it. The playback operations map to `camera.play`, `camera.set`, and `camera.stop`. |
| `gatos.audio_control` | `{operation, value?, values?, token?, aux?}`. Plays, adjusts, stops, or reads the status of game audio channels. |
| `gatos.audio_clip` | `{operation, name?, offset?:0, complete?:true, data_base64?}`. `operation` is `list`, `retrieve`, `upload`, or `delete`. Upload data is JSON base64 and can be chunked at a byte `offset`; keep each request below the 24 MiB framing limit while configured clip/store limits remain enforced. Retrieval returns metadata plus an MCP audio content block for known audio extensions, or an embedded binary resource when the container is unknown. |
| `gatos.schedule_control` | `{operation, id?, value?, token?}`. Reads or manages existing schedule players: pause, scrub, rate, loop, stop, remove, or clear. |
| `gatos.debug_control` | `{operation, vessel_id?:"", ordinal?:-1, value?, values?, token?, aux?}`. Performs the logical cheat/debug groups: focus/control-vessel, teleport, impulse, refills, docking pushoff, global IVA rendering, welds, thug-life cosmetics, face FX, and IVA cabin physics. |
| `gatos.render_fx_control` | `{family, operation, entity?, field?, value?, values?, token?}`. Reads, sets, clears where applicable, or restores pristine values for engine plume, plume trail, clouds, and terrain editor state. Its entity scope remains explicit: template, global renderer, body/layer/type, or body/global terrain. |
| `gatos.paint_control` | `{operation, vessel_id?:"", value?:0, color?, target?, file?}`. Controls paint runtime masters, global/template/vessel/part/shared-EVA/individual-EVA rules, and the `texture_bind`/`texture_unbind`/`texture_clear` ground-clutter texture overrides. Flags use `value`; colors use normalized `color:[r,g,b]`; `target` names the blend, template, part instance, semantic EVA material, or — for the texture operations — the stock clutter texture id, which is the asset's **content-relative path** (e.g. `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`); `file` names the uploaded image consumed by `texture_bind`, whose render mode also rides `value` (`0` = `faithful`, the default; `1` = `raw`). |
| `gatos.paint_texture` | `{operation, name?, offset?:0, complete?:true, data_base64?}`. `operation` is `list`, `catalog`, `bindings`, `retrieve`, `upload`, or `delete`. Upload data is JSON base64 and can be chunked at a decoded byte `offset`; `complete` commits it. Keep each request below the 24 MiB framing limit while the configured file-count, per-file, total-byte, and binding limits remain enforced. `catalog` enumerates the overridable stock clutter textures — each `texture_id` is the asset's **content-relative path** (e.g. `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`), install-independent and space-free — including the `used_by` share count; `bindings` returns the desired and applied binding rows. Retrieval returns metadata plus the stored image as an embedded binary resource at `gatos://paint/textures/<name>`. |
| `gatos.paint_sticker` | `{operation, image?, anchor?, vessel_id?, part_iid?:0, position?, normal?, body?, lat?, lon?, heading?, roll?, width?, height?, depth?, alpha?, brightness?, aim?:"camera", range?, id?:-1, value?}`. `operation` is `place`, `spray`, `set`, `remove`, `clear`, `list`, or `debug`. Sticker decals projected from an uploaded image onto vehicles, terrain and ground clutter: `place` takes an explicit `vessel`/`body` anchor, `spray` takes whatever the camera or cursor is pointing at, `set` takes `id` plus exactly one knob, and `list` is a read of the live registry. Images come from `gatos.paint_texture` — there is no second upload surface. |
| `gatos.command` | The canonical command envelope in §4. Complete action coverage without filesystem paths. |
| `gatos.execute_batch` | `{commands:[<canonical command>, ...]}`. Atomically submits one same-tick group. |
| `gatos.schedule_batch` | `{id?, group?, clock, rate, loop, entries:[{at_ms, command:<canonical command>}, ...]}`. Registers a non-blocking timed command player. |

The fields above define the outer v1 wire envelopes; the discriminator branches in §5.1 define the
legal operation-specific payloads. There is no untyped `control` patch object or `/sim` path payload.
The action catalog validates field arity, range, unit, gate, and phase. CCI vectors/quaternions retain
the `values` conventions in the underlying command contract, and FX field validation remains driven
by the existing FX catalog.

### 5.1 Operation-shaped calls

The generic-looking payload slots are a wire-compatibility envelope, not a bag of interchangeable
options. **Choose an operation first, then send only the slots on that row.** Omitted numeric slots
default to zero, which is not a substitute for a required value. The server advertises these rules
in tool and parameter descriptions; `gatos.get_capabilities` remains the live source for gates,
phase, safety, and canonical action availability.

#### Vessel and module control

| Tool operation | Complete operation payload | Meaning / accepted values |
|---|---|---|
| `vessel_control` `ignite`, `shutdown`, `stage` | `{operation,vessel_id}` | One-shot triggers; inspect live state before retrying. |
| `engine_master`, `lights`, `rcs`, `always_render` | `{operation,vessel_id,value:0\|1}` | Vessel-wide flags. |
| `throttle` | `{operation:"throttle",vessel_id,value:0..1}` | Manual throttle fraction. |
| `translate`, `rotate` | `{operation,vessel_id,values:[x,y,z]}` | Body-axis signs; magnitudes ignored; `[0,0,0]` stops. |
| `rcs_mode` | `{operation:"rcs_mode",vessel_id,token:"Enabled"\|"Disabled"}` | Flight-computer RCS master, distinct from `rcs`. |
| `attitude_mode` | `{operation:"attitude_mode",vessel_id,token:<mode>}` | `manual`, `StabilityAssist`, `Prograde`, `Retrograde`, `Normal`, `AntiNormal`, `RadialIn`, `RadialOut`, `Target`, `AntiTarget`, or `Maneuver`; case-insensitive. |
| `attitude_frame` | `{operation:"attitude_frame",vessel_id,token:<frame>}` | `Inertial`, `Orbital`, `Surface`, or `Target`; case-insensitive. |
| `attitude_target` | `{operation:"attitude_target",vessel_id,values:[x,y,z,w]}` | Body-to-CCI quaternion. |
| `burn` | `{operation:"burn",vessel_id,values:[ut,dvx,dvy,dvz]}` | Absolute UT seconds and parent-body CCI delta-v in m/s. |
| `scale` | `{operation:"scale",vessel_id,value:>0}` | Finite render scale; `1` restores ordinary scale. |
| `focus`, `take_control` | `{operation,vessel_id}` | View-only focus or debug control transfer. |
| `module_control` `engine_active`, `rcs_active`, `light_on` | `{operation,vessel_id,ordinal,value:0\|1}` | Indexed flags. |
| `engine_minimum_throttle`, `animation_goal`, `solar_deployment` | `{operation,vessel_id,ordinal,value:0..1}` | Indexed normalized setpoints. |
| `light_brightness`, `light_outer_angle`, `light_inner_angle` | `{operation,vessel_id,ordinal,value}` | Brightness or degrees, subject to the live module's bounds. |
| `light_color` | `{operation:"light_color",vessel_id,ordinal,values:[r,g,b]}` | Normalized RGB. |
| `undock`, `fire_decoupler` | `{operation,vessel_id,ordinal}` | Irreversible indexed triggers. |
| `pushoff` | `{operation:"pushoff",vessel_id,ordinal,value:<N*s>}` | Debug docking separation impulse. |

#### Camera, audio, and schedules

| Tool operation | Complete operation payload | Meaning / accepted values |
|---|---|---|
| `camera_control` `ownership`/`take` | `{operation,value:0\|1}` | Take ownership or eased release. |
| `release`, `reset`, `stop` | `{operation}` | Hard release, clear pose overrides, or stop player. |
| `mode` | `{operation:"mode",token:"orbit"\|"free"\|"map"\|"iva"\|"fixed"}` | Game camera mode while not owned. |
| `follow`, `anchor`, `aim_target` | `{operation,token:<target-ref>}` | `vessel:<id>`, `body:<id>`, `part:<vessel>/<iid>` where supported, or `none`. |
| `tidal`, `ortho` | `{operation,value:0\|1}` | Boolean channels. |
| `map_scope`, `orbit_radius`, `orbit_azimuth`, `orbit_elevation`, `roll`, `fov`, `ortho_height`, `smoothing` | `{operation,value}` | Metres, degrees, or seconds according to the operation; live configured bounds apply. |
| `position`, `aim_offset` | `{operation,values:[x,y,z],token?:<frame>}` | Three-vector; only position consumes the optional frame token. |
| `frame`, `aim_frame` | `{operation,token:"ecl"\|"cce"\|"bodyfixed"\|"enu"\|"lvlh"\|"chase"}` | Placement or aim-offset frame. |
| `geodetic` | `{operation:"geodetic",values:[lat,lon,alt],token?:"body:<id>"}` | Degrees, degrees, terrain altitude metres. |
| `rotation` | `{operation:"rotation",values:[x,y,z,w]}` | Explicit quaternion, norm 0.5..2. |
| `aim` | `{operation:"aim",token:<target-ref>,values:[offX,offY,offZ,frameOrdinal,upOrdinal,roll,rollPresent]}` | Complete aim constraint; frame 0..5 and up 0..3 are defined in `SPEC_9P_FILESYSTEM.md` §5. |
| `aim_up` | `{operation:"aim_up",token:"world"\|"target"\|"velocity"\|"free"}` | Aim up reference. |
| `play` | `{operation:"play",token:<track>,aux?:<group>,values?:[atS,rate,loop,atPresent,ratePresent,loopPresent]}` | Start a track; timeline is seconds. |
| `set` | `{operation:"set",values:[key,value,...]}` | Camera player keys: `0=t seconds`, `1=rate`, `2=loop`, `3=paused`. |
| `audio_control` `play` | `{operation:"play",token:<clip>,aux?:<channel>,values?:[startMs,endMs,vol,loop,pan,pitch,group]}` | Group `0=sfx`, `1=music`, `2=ui`; `endMs=0` means full clip. |
| `update`, `pause`, `resume`, `seek` | `{operation,token:<channel-or-clip>,values:[key,value,...]}` | Keys: `0=vol`, `1=pan`, `2=pitch`, `3=paused`, `4=seek_ms`. |
| `stop` | `{operation:"stop",token:"all"\|<channel>\|<clip>}` | Stop matching channels. |
| `schedule_control` `list` | `{operation:"list"}` | All players. |
| `get`, `stop`, `remove` | `{operation,id}` | Read, stop, or remove one player. |
| `pause`, `loop` | `{operation,id,value:0\|1}` | `resume` is pause with value `0`. |
| `scrub`, `rate` | `{operation,id,value}` | Position in ms or rate 0..100. |
| `clear` | `{operation:"clear"}` | Remove every player. |

#### Debug, render FX, and paint

The debug family is intentionally explicit and potentially destructive. The common shapes are:
`teleport` uses `{vessel_id,values:[px,py,pz,vx,vy,vz]}`; `impulse` uses
`{vessel_id,values:[x,y,z],token?:"cci"|"body",aux?:"ns"|"dv"}`; refills use only `vessel_id`;
weld creation uses `vessel_id` as source, `token` as target, and the documented pose in `values`;
indexed thug-life and IVA mutations use `ordinal`; IVA adoption uses `token` as vessel id and
`values` beginning with the SubPart instance id. Exact worked shapes are published on the
`gatos.debug_control` public reference page and the canonical actions remain in
`SPEC_9P_FILESYSTEM.md` §5.1.

`gatos.render_fx_control` always uses `{family,operation,entity?,field?,value?,values?}`. `family` is
`engine_plume`, `plume_trail`, `clouds`, or `terrain`; `operation` is `set`, `reset`, or the
plume-trail-only `clear`. Entity is an engine template, a body, or omitted for the global trail
renderer and terrain wireframe. Field arity/range comes from the live FX catalog.

`gatos.paint_control` uses `value:0|1` for enabled/clear operations, `color:[r,g,b]` for color
operations, and `target` for `blend`, template ids, part instance ids, and semantic EVA material
names. `vessel_id` is required only for vessel/part/individual-EVA operations. §6.1 gives the
precedence and runtime-master behavior.

The three **ground-clutter texture** operations are global and take neither `vessel_id` nor `color`;
they are gated by `control_enabled + paint textures store`, not by the paint runtime master. As of
KSA `2026.8.22.5348` a **stock clutter texture id is the asset's content-relative path** — e.g.
`Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2` — install-independent, unique per asset and
space-free; it is never a symbolic name. Always take it from `catalog` rather than constructing it:

| Tool operation | Complete operation payload | Meaning / accepted values |
|---|---|---|
| `paint_control` `texture_bind` | `{operation:"texture_bind",target:<stock-texture-id>,file:<uploaded-name>,value?:0\|1}` | Draw one stock clutter texture with a committed upload. `target` is an id from `gatos.paint_texture(operation:"catalog")` — a **content-relative asset path** such as `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`, never a symbolic name; `file` is a name from `operation:"list"`. `value` is the render mode — `0` = `faithful` (**the default**: gatOS rewrites the decoded pixels so an ordinary sRGB PNG renders as authored, in every biome), `1` = `raw` (bytes untouched, interpreted exactly as one of KSA's own clutter textures). Re-binding the same pair in the *other* mode is a real change and re-uploads. |
| `texture_unbind` | `{operation:"texture_unbind",target:<stock-texture-id>}` | Restore that one stock texture (`target` is the same content-relative path form as `texture_bind`); a `target` that is not currently bound is `ENOENT`. `target:"all"` is accepted here too and performs the same global teardown as `texture_clear` — the shorthand means the same thing on every transport. |
| `texture_clear` | `{operation:"texture_clear",value:1}` | Global teardown: restore every stock texture. Uploads are kept, so a later `texture_bind` needs no re-upload. |
| `paint_texture` `list`, `catalog`, `bindings` | `{operation}` | Uploads (`name`, `bytes`, `kind`, `version`, `ready`); overridable stock textures (`texture_id` — the content-relative asset path — slot, size, mips, `used_by`, ecotypes); desired binding rows (with their `faithful`/`raw` mode) plus applied rows. |
| `retrieve` | `{operation:"retrieve",name}` | Metadata plus the stored bytes as an embedded resource at `gatos://paint/textures/<name>`. |
| `upload` | `{operation:"upload",name,data_base64,offset?:0,complete?:true}` | Base64 image bytes written at a **decoded** byte offset; `complete:false` leaves the file uncommitted (a bind of it is `EBUSY`), `complete:true` commits and sniffs the container. |
| `delete` | `{operation:"delete",name}` | Evict an upload; a binding that names it is torn down first. |

A complete two-chunk upload, bind, and teardown:

```json
{"tool":"gatos.paint_texture","arguments":{"operation":"catalog"}}
{"tool":"gatos.paint_texture","arguments":{"operation":"upload","name":"mossy-rock.png","offset":0,"complete":false,"data_base64":"<first chunk>"}}
{"tool":"gatos.paint_texture","arguments":{"operation":"upload","name":"mossy-rock.png","offset":1048576,"complete":true,"data_base64":"<final chunk>"}}
{"tool":"gatos.paint_control","arguments":{"operation":"texture_bind","target":"Textures/Planets/Luna/GroundClutter/LunaRocksMaterial_Diffuse.ktx2","file":"mossy-rock.png","value":0}}
{"tool":"gatos.get_runtime_state","arguments":{"feature":"paint_textures"}}
{"tool":"gatos.paint_control","arguments":{"operation":"texture_unbind","target":"Textures/Planets/Luna/GroundClutter/LunaRocksMaterial_Diffuse.ktx2"}}
{"tool":"gatos.paint_control","arguments":{"operation":"texture_clear","value":1}}
```

`texture_bind` validates the target id and the named upload when the command runs on the game thread
— an unknown stock texture id is `ENOENT`, an uncommitted upload is `EBUSY`, an unrecognised
container is `EINVAL` — but the decode and GPU upload happen on the next reconcile. Whether the image
actually reached the GPU is therefore read back from the `applied` rows in
`gatos.get_runtime_state(feature:"paint_textures")`, where each row carries `pending`, `applied`, or
`failed` plus the decoded size, mip count, VRAM bytes, and any error text. Store errnos are the
filesystem's: `EINVAL` for a bad name or an unrecognised image container, `ENOENT` for an unknown
stock texture id or missing upload, `EBUSY` for an upload that has not committed, `ENOSPC` for the
file-count/total-byte/binding caps, and `EFBIG` for the per-file byte cap.

`gatos.paint_sticker` is likewise operation-shaped. Every mutating operation authors exactly one
canonical `paint.sticker_*` command — the same one the `/sim/paint/stickers` line grammars build —
and every argument is validated against the shared sticker rules *before* submission, so a bad call
returns `EINVAL` without reaching the game thread. All of it is gated by
`control_enabled + paint stickers`:

| Tool operation | Complete operation payload | Meaning / accepted values |
|---|---|---|
| `place` (vessel anchor) | `{operation:"place",image,anchor?:"vessel",vessel_id,part_iid,position:[x,y,z],normal:[nx,ny,nz],roll?,width?,height?,depth?,alpha?,brightness?}` | Exact placement in a part's local frame. `part_iid` is a part **or sub-part** `instance_id` from `gatos.get_vessel(include:["parts"])`; `position` is part-local metres and `normal` the outward surface normal (finite, non-zero). `anchor` may be omitted — it is inferred from whichever of `vessel_id`/`body` is filled. |
| `place` (body anchor) | `{operation:"place",image,anchor?:"body",body,lat,lon,heading?,width?,height?,depth?,alpha?,brightness?}` | Geodetic placement that rides the planet's rotation. `lat` ∈ `[-90, 90]`, `lon` ∈ `[-360, 360]`, `heading` in degrees from north. |
| `spray` | `{operation:"spray",image,aim?:"camera"\|"cursor",range?,roll?,width?,height?,depth?,alpha?,brightness?}` | Place where the main camera (headless-friendly, and steerable with `gatos.camera_control`) or the mouse cursor is pointing. `range` defaults to `2000` m; `roll` **adds to** the "reads upright from here" rotation the picker computes; an omitted `depth` takes the hit anchor kind's default. Nothing hit is `ENOENT`. |
| `set` | `{operation:"set",id, …exactly one of…}` | One knob per call, mirroring the one-file-one-action filesystem shape: `width`+`height` (both, `paint.sticker_size`), `depth`, `roll` or `heading` (`paint.sticker_rotation`), `alpha`, `brightness`, `image` (hot-swap), or `value` `0\|1` (visibility). Zero or two or more knobs is `EINVAL`. |
| `remove` | `{operation:"remove",id}` | Remove one sticker and free its id. An unknown id is `ENOENT`. |
| `clear` | `{operation:"clear"}` | Remove every sticker. The uploaded images are kept. |
| `list` | `{operation:"list"}` | A **read**, not a command: `{stickers, runtime, last}` straight from the published registry. |
| `debug` | `{operation:"debug",value:0\|1}` | Global development aid — draw every sticker as a magenta checker of its projection box instead of its image, proving the box, the depth reconstruction and the anchor matrices without any art. |

Defaults and ranges are the filesystem's: `width`/`height` ∈ `(0, 1000]` m (default `1`), `depth` ∈
`(0, 100]` m (default `0.3` on a vessel anchor and `1` on a body anchor), `alpha` ∈ `[0, 1]`
(default `1`), `brightness` ∈ `(0, 8]` (default `1`), `range` ∈ `(0, 1e6]` m (default `2000`), and
rotation is any finite degree value (default `0`). A complete upload → spray → tune → teardown:

```json
{"tool":"gatos.paint_texture","arguments":{"operation":"upload","name":"meow.png","data_base64":"<png bytes>","complete":true}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"spray","image":"meow.png","aim":"camera","width":2,"height":2}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"list"}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"place","image":"meow.png","anchor":"vessel","vessel_id":"Kitten-1","part_iid":41,"position":[0,0.5,-1.4],"normal":[0,1,0],"roll":15,"width":0.6,"height":0.3}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"place","image":"meow.png","anchor":"body","body":"Mun","lat":12.03,"lon":-41.88,"heading":90,"width":5,"height":5}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"set","id":0,"alpha":0.4}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"debug","value":1}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"remove","id":0}}
{"tool":"gatos.paint_sticker","arguments":{"operation":"clear"}}
```

A successful `place`/`spray` emits a `paint.sticker_placed` event, which is how a `spray` reports
what it actually hit to an agent that is not re-reading the registry: wait for it with
`gatos.wait(event_type:"paint.sticker_placed")`.

### 5.2 Batches

`gatos.execute_batch` is the JSON counterpart of `/sim/ctl/batch`, not a serialized batch-file upload.
It validates every command before submission, preserves declared order, allows at most **64** commands,
and rejects a mixture of Frame and Solver commands with `EINVAL`. It executes as one game-tick drain.
If an individual command fails at game time, later commands still run; the result reports the first
failure just as the existing batch behavior does.

### 5.3 Timed batches

`gatos.schedule_batch` is the JSON counterpart of `/sim/ctl/timed_batch`, not a text script. `at_ms` is a
non-negative, absolute timeline offset in milliseconds. `clock` is `render`, `wall`, or `ut`; `rate`
and `loop` are explicit. It validates all entries before it reserves/registers an id, accepts mixed
command phases, and returns immediately with the assigned id.

Schedule progression, catch-up, groups, coalescing, dropped-state count, failed-entry behavior,
player persistence, and live-player/entry limits are exactly the existing scheduler contract. Use
`gatos.schedule_control` or `gatos.get_runtime_state(feature:"schedules")` for outcome/read-back. This tool does
not reinterpret `at_ms` as ticks or relative delays.

## 6. Agent-critical behavior

The tool descriptions repeat high-risk caveats, but an agent must reason from these existing game
semantics:

- CCI position, velocity, burns, teleport, and default impulse vectors are about the vessel's
  current parent body. Teleport does not change the parent SOI. An impulse defaults to N·s; use
  direct `dv` only when a m/s Δv is intended.
- `translate`/`rotate` are bang-bang body-axis commands. `rcs_mode=Disabled` makes both inert;
  manual latches may be cleared by game focus/UI/camera changes or warp above 30×.
- Solver commands, including attitude, burn, refills, and RCS mode, take effect on the next solver
  step. Same-tick batches cannot mix them with Frame actions.
- Solid rocket motors are visible in vessel propulsion state but cannot be throttled or shut down
  after ignition. Disabled or already-fired decouplers report their existing errors.
- Camera ownership excludes the game map/IVA contexts; camera-track playback also requires schedule
  support; orthographic height remains a non-restorable game-side change.
- IVA physics is independently master-gated; disabling it returns every adopted object to rest.
  Adoption accepts interior subparts, not top-level parts.
- Audio, camera tracks, clutter textures, and FX retain their current configured store caps, session
  lifetime, and pristine-reset semantics.
- **Clutter diffuse textures are modulation maps, not albedo — and `texture_bind` corrects for that
  by default.** The shader's effective colour is
  `albedo = 2 * decode(t.rgb, t.a) * mix(meanLum, instanceColor, t.a) / meanLum`: the texel is
  doubled, and **alpha is not opacity** — it selects sRGB (`0`) versus linear (`1`) decoding *and*
  how far the per-instance terrain tint applies. `value:0`, the **default `faithful` mode**, rewrites
  the decoded pixels before upload — RGB scaled by `2^(-1/2.2)` to cancel the doubling (white `255`
  stores as `186`; round-trip error under 0.2%, all 8-bit quantization) and alpha cleared to `0`,
  which selects the sRGB path *and* collapses the terrain tint to exactly `1` — so an ordinary sRGB
  PNG renders as authored and identically in every biome, with no hand-correction to instruct a user
  in. `value:1`, `raw`, uploads the bytes untouched for a like-for-like stock replacement: there
  mid-grey `0.5` is neutral and `A=255` opts into full terrain tinting. A decode that is not RGBA8
  (some ktx/dds/hdr) cannot be corrected — the `faithful` binding reports `failed` in the applied
  rows with an error naming `raw` as the fix. Real cutout opacity is a separate `opacity` texture
  slot in either mode. Mip chains are generated automatically and are mandatory — a single-mip
  replacement aliases badly at range.
- **Binding replaces a texture asset, not a material.** Every clutter material that shares the stock
  asset changes at once. Read the `used_by` count and the ecotype list in the `paint_textures`
  catalog before binding; `used_by` greater than one means the override is shared. Oversize uploads
  are downscaled to the configured maximum dimension rather than rejected.

`/sim/display` is intentionally **not exposed by MCP v1**. Its infinite binary Kitty stream is a
terminal-video transport with no logical JSON resource/tool equivalent. This omission is deliberate,
documented, and must remain explicit in future coverage reviews.

## 6.1 Paint

`gatos.paint_control` is the first-class logical wrapper for every `paint.*` catalog action. Input:
`operation` is the action suffix (for example `parts_enabled`, `global_color`, `vessel_color`,
`part_color`, `kittens_enabled`, `kitten_material_color`, `texture_bind`, `texture_unbind`, or
`texture_clear`); `vessel_id` addresses vessel/EVA rules; `target` carries a template id, uint part
`instance_id`, semantic EVA material name, or stock clutter texture id (a content-relative asset path); `file` carries the uploaded
image name consumed by `texture_bind`; `value` carries flags, and for `texture_bind` the render mode
(`0` = `faithful`, the default; `1` = `raw`); `color` is a normalized three-number sRGB array. `gatos.command` remains the complete canonical-envelope backstop.

`gatos.get_runtime_state(feature:"paint")` and `gatos://runtime/paint` return the complete immutable
paint snapshot: desired part/template/vessel/global and shared/individual EVA rules, both runtime
masters and their status, the blend mode, shader compile diagnostics, discovered EVA material names,
active EVA bindings, clone usage/peak/cap, raytraced capability, and per-subsystem errors.
`gatos.get_vessel(include:["paint"])` includes that vessel's whole-vessel, per-part, and (for EVA)
individual material rules. `gatos.get_capabilities` advertises paint actions with logical tool
`gatos.paint_control` and gate `control_enabled + paint runtime master`.

Custom **ground-clutter textures** are a separate feature document, not part of the paint snapshot.
`gatos.get_runtime_state(feature:"paint_textures")` and `gatos://runtime/paint_textures` return
`{runtime, bindings, applied, clutter, files, revision, limits}`: the game-side runtime status
(availability, applied count, images awaiting deferred destruction, VRAM bytes, error), the desired
bindings, the applied rows with their per-binding state/size/mips/VRAM/error, the overridable stock
texture catalog with each entry's slot, dimensions, mip count, `used_by` share count and ecotypes,
the committed uploads, the desired-state revision, and the configured caps. `gatos.paint_texture`
owns the store operations. `gatos.get_capabilities` reports `features.paint_textures` (whether the
store is wired at all) and advertises the three `paint.texture_*` actions with logical tool
`gatos.paint_control` and gate **`control_enabled + paint textures store`** — a distinct gate from
paint's, because the texture overrides have no runtime master to enable.

**Stickers** are a third paint feature document with a third gate.
`gatos.get_runtime_state(feature:"paint_stickers")` and `gatos://runtime/paint_stickers` return
`{runtime, stickers, last, debug, limits}`: the subsystem health line (availability, sticker count,
how many currently resolve their anchor and draw, distinct GPU images, VRAM bytes, whether the lazy
render patch is installed, `renderer` ∈ `idle`/`active`/`degraded`, and the last fault text), the
published sticker array (id, image, anchor kind, target id, part `instance_id`, position, normal,
rotation, size, depth, alpha, brightness, `visible`, `live`, and the image's GPU state ∈
`ready`/`missing`/`uploading`/`failed`), the result line of the last `place`/`spray`, the global
box-checker flag, and the configured `max_count` / `max_view_distance_m`. `gatos.paint_sticker` owns
every operation. `gatos.get_capabilities` reports `features.paint_stickers` (whether the registry is
wired at all — it requires the texture store too, since a sticker draws an uploaded image) and
advertises all twelve `paint.sticker_*` actions with logical tool `gatos.paint_sticker` and gate
**`control_enabled + paint stickers`**. A sticker whose vessel despawns, whose part stages away or
whose image is deleted goes **dormant** (`live:false`) rather than being removed, so an agent may
find an entry that is listed but not drawing; that is the state to re-check before re-placing.

## 7. Coverage and maintenance mandate

MCP v1 must be complete for all current **logical** gatOS reads and commands, without duplicating the
filesystem's per-leaf UX. The implementation owns a declarative MCP capability registry that maps:

1. every `SimJson`/snapshot projection needed by a logical resource or read tool;
2. every public `SimCommand.Action` to either one logical MCP control tool or `gatos.command`;
3. every special store capability (audio clip, camera track, clutter-texture store, sticker registry, scheduler, and FX); and
4. the intentional `/sim/display` exclusion.

Tests must fail if a public action or covered logical projection is absent from this registry. They
also pin tool JSON schemas, raw-id addressing, list pagination limits, gates, errno forwarding,
derived phase, same-tick batch behavior, timed scheduler lifecycle, the 24 MiB request framing
limit, and the lack of an MCP response-size cap. Snapshot fixtures must prove that MCP reads match the shared `SimJson` model rather than a
transport-specific reserialization.

When changing gatOS, update in the same work item:

- this specification for MCP resources, tools, schemas, response shape, limits, or exclusions;
- `SPEC_9P_FILESYSTEM.md` for the underlying `/sim`/HTTP/MQTT surface, units, action keys, phases,
  errno, or config gate;
- `AGENTS.md`, `docs/ARCHITECTURE.md`, `docs/KSA_INTEGRATION_MATRIX.md`, `README.md`, the site
  reference, and the `gatos` skill when the public transport model or agent authoring behavior changes;
- the KSA integration matrix and `scope/` only when a game binding changes (MCP alone adds none).

The code remains authoritative. The four transport projections and both specifications must never
disagree.
