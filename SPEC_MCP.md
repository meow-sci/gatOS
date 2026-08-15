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

MCP runs as a loopback-only Streamable HTTP server in the gatOS mod process at
`http://127.0.0.1:<bound-port>/mcp`. `mcp_enabled` defaults to `true`; `mcp_preferred_port`
defaults to `4243`, falls back to an ephemeral port when occupied, and accepts
`0` to choose ephemeral directly. The server accepts no bearer token: loopback-only binding plus
exact local `Host`/`Origin` validation are its v1 boundary. It is optional: a failure to start it
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
result inspection limit. Audio and camera-track uploads are deliberately chunkable; upload chunks
should stay well below that framing limit. Existing domain limits still apply: for example,
audio/track store capacity, schedule entry and live-player caps, and the game's command-per-frame
limit are unchanged.

### 1.2 Errors and availability

Expected command failures are returned in the common envelope with a stable `errno`, an `outcome`,
a human-readable `message`, and a `retryable` indication. The errno vocabulary is unchanged:

| errno | Meaning |
|---|---|
| `EINVAL` | Invalid type, shape, arity, range, or semantic combination. |
| `ENOENT` | A requested live entity, module, object, or asset does not exist. |
| `EACCES` | Control/debug access is disabled or the authority gate rejected the target. |
| `EBUSY` | The action cannot run now, such as a full channel table or an already-fired trigger. |
| `EIO` | A KSA operation faulted; its accessor may be degraded for the session. |
| `ETIMEDOUT` | The game thread did not drain the command before `command_timeout_ms`. |
| `EOPNOTSUPP` | The feature is unavailable, disabled by its capability gate, or has degraded. |
| `EFBIG`, `ENOSPC`, `EEXIST`, `EPERM` | The existing audio/track-store upload errors. |

`gatos.get_capabilities` is the required preflight. It reports the live feature gates, configured
domain limits, transport health, control/debug authority status, and degraded accessors. Agents
should call it before a feature-specific tool and should read a target vessel's `controllable` state
before attempting normal flight control.

## 2. Resources

Resources are read-only snapshots for clients that support MCP resources. The equivalent `gatos.get_*`
tools exist for clients that only use tools; they return the same JSON projection.

| URI / template | Content |
|---|---|
| `gatos://world` | Current world summary: time, system, transport/control status, and entity indexes. Use `gatos.get_world(detail:"full")` for the complete snapshot. |
| `gatos://celestials` | Celestial catalog, up to the 1,000-entry list maximum. |
| `gatos://celestials/{id}` | One `BodySnapshot`, addressed by raw celestial id. |
| `gatos://vessels` | Vessel catalog, up to the 1,000-entry list maximum. |
| `gatos://vessels/{id}` | One complete vessel document; its optional sections match `gatos.get_vessel`. |
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
| `gatos.get_capabilities` | — | — | Capability gates, domain limits, health, and transport status. |
| `gatos.wait` | at least one condition | `timeout_ms` | A later snapshot or event satisfying a condition. |

`gatos.get_vessel` and `gatos.get_kitten` accept `include` as an array drawn from
`flight`, `orbit`, `environment`, `propulsion`, `resources`, `power`, `control`, `modules`,
`encounters`, `parts`, and `all`. Omit `include` or include `all` for the complete document. A
sectioned response always includes identity/control fields (`id`, `name`, `situation`,
`parent_body_name`, `controlled`, `controllable`, and `is_kitten`); request `flight` for position,
velocity, attitude, altitude, and navball data.

The `feature` input to `gatos.get_runtime_state` is one of `camera`, `schedules`, `audio`, `welds`, `thug_life`,
`face_fx`, `iva`, `engine_plume`, `plume_trail`, `clouds`, or `terrain`. It exposes the corresponding
logical snapshot/read-back state, including configuration and pristine/reset diagnostics where the
underlying feature provides them.

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
| `gatos.camera_track` | `{operation, name?, json?, offset?:0, complete?:true}`. `operation` is `list`, `read`, `upload`, or `delete`. Upload sends the named JSON track, optionally chunked at a byte `offset`; `complete` commits the upload. Track playback remains under `gatos.camera_control`/`gatos.schedule_control`. |
| `gatos.audio_control` | `{operation, value?, values?, token?, aux?}`. Plays, adjusts, stops, or reads the status of game audio channels. |
| `gatos.audio_clip` | `{operation, name?, offset?:0, complete?:true, data_base64?}`. `operation` is `list`, `retrieve`, `upload`, or `delete`. Upload data is JSON base64 and can be chunked at a byte `offset`; keep each request below the 24 MiB framing limit while configured clip/store limits remain enforced. Retrieval returns metadata plus an MCP audio content block for known audio extensions, or an embedded binary resource when the container is unknown. |
| `gatos.schedule_control` | `{operation, id?, value?, token?}`. Reads or manages existing schedule players: pause, scrub, rate, loop, stop, remove, or clear. |
| `gatos.debug_control` | `{operation, vessel_id?:"", ordinal?:-1, value?, values?, token?, aux?}`. Performs the logical cheat/debug groups: focus/control-vessel, teleport, impulse, refills, docking pushoff, global IVA rendering, welds, thug-life cosmetics, face FX, and IVA cabin physics. |
| `gatos.render_fx_control` | `{family, operation, entity?, field?, value?, values?, token?}`. Reads, sets, clears where applicable, or restores pristine values for engine plume, plume trail, clouds, and terrain editor state. Its entity scope remains explicit: template, global renderer, body/layer/type, or body/global terrain. |
| `gatos.command` | The canonical command envelope in §4. Complete action coverage without filesystem paths. |
| `gatos.execute_batch` | `{commands:[<canonical command>, ...]}`. Atomically submits one same-tick group. |
| `gatos.schedule_batch` | `{id?, group?, clock, rate, loop, entries:[{at_ms, command:<canonical command>}, ...]}`. Registers a non-blocking timed command player. |

The operation and shared command fields above are the complete v1 JSON schemas; there is no
untyped `control` patch object or `/sim` path payload. The action catalog validates field arity,
range, unit, gate, and phase. CCI vectors/quaternions retain the `values` conventions in the
underlying command contract, and FX field validation remains driven by the existing FX catalog.

### 5.1 Batches

`gatos.execute_batch` is the JSON counterpart of `/sim/ctl/batch`, not a serialized batch-file upload.
It validates every command before submission, preserves declared order, allows at most **64** commands,
and rejects a mixture of Frame and Solver commands with `EINVAL`. It executes as one game-tick drain.
If an individual command fails at game time, later commands still run; the result reports the first
failure just as the existing batch behavior does.

### 5.2 Timed batches

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
- Audio, camera tracks, and FX retain their current configured store caps, session lifetime, and
  pristine-reset semantics.

`/sim/display` is intentionally **not exposed by MCP v1**. Its infinite binary Kitty stream is a
terminal-video transport with no logical JSON resource/tool equivalent. This omission is deliberate,
documented, and must remain explicit in future coverage reviews.

## 6.1 Paint

`gatos.paint_control` is the first-class logical wrapper for every `paint.*` catalog action. Input:
`operation` is the action suffix (for example `parts_enabled`, `global_color`, `vessel_color`,
`part_color`, `kittens_enabled`, or `kitten_material_color`); `vessel_id` addresses vessel/EVA
rules; `target` carries a template id, uint part `instance_id`, or semantic EVA material name;
`value` carries flags; `color` is a normalized three-number sRGB array. `gatos.command` remains the
complete canonical-envelope backstop.

`gatos.get_runtime_state(feature:"paint")` and `gatos://runtime/paint` return the complete immutable
paint snapshot: desired rules, runtime masters/status, shader compile diagnostics, discovered EVA
material names, active bindings, clone usage/cap, raytraced capability, and per-subsystem errors.
`gatos.get_vessel(include:["paint"])` includes that vessel's whole-vessel, per-part, and (for EVA)
individual material rules. `gatos.get_capabilities` advertises paint actions with logical tool
`gatos.paint_control` and gate `control_enabled + paint runtime master`.

## 7. Coverage and maintenance mandate

MCP v1 must be complete for all current **logical** gatOS reads and commands, without duplicating the
filesystem's per-leaf UX. The implementation owns a declarative MCP capability registry that maps:

1. every `SimJson`/snapshot projection needed by a logical resource or read tool;
2. every public `SimCommand.Action` to either one logical MCP control tool or `gatos.command`;
3. every special store capability (audio clip, camera track, scheduler, and FX); and
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
