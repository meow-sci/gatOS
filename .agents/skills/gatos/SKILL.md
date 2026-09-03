---
name: gatos
description: >-
  Write scripts and programs against gatOS — the KSA mod that exposes live Kitten Space Agency
  simulation state as a 9P filesystem at /sim (also over HTTP /v1, MQTT, and an AI-oriented MCP server). Use this when asked to
  read game/celestial/vehicle telemetry, control vehicles (throttle, ignite, staging, attitude,
  burns, RCS, lights, docking), use game/debug controls (teleport, impulse kick, refuel, time-warp,
  switch vessel), direct the in-game camera / author cinematic shots and tracks, schedule timed
  command sequences, or write flight-computer / autopilot programs. Covers the full /sim catalog, the
  command model, KSA coordinate frames, and worked Bun/TypeScript + Rust examples.
---

# gatOS `/sim` programming

gatOS exposes **live KSA simulation state as a filesystem**. Programs read game data and control
vehicles by reading and writing files under `/sim` — `cat` a sensor, `echo` a value to a control
file and it actuates the game (returning a real Linux errno on failure). The exact same surface is
served over **HTTP `/v1`** and **MQTT**, so a program can run inside the guest (read `/sim`
directly) or on the host (HTTP). No custom RPC — the files *are* the API.

For an **AI agent**, gatOS also provides a first-class MCP server. It projects the same snapshots
and command pipeline as concise logical JSON resources/tools (world, celestial, vessel, kitten,
runtime, direct controls, and typed same-tick/timed batches), rather than mirroring every `/sim`
leaf. Its public contract is [`SPEC_MCP.md`](../../../SPEC_MCP.md). Do not invent a filesystem-path
tool or use `/sim/display`: that terminal-video stream is deliberately outside MCP v1.

> **The complete, authoritative catalog is [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md)**
> at the repo root — every path, format, unit, read/write semantic, command action key, errno, and
> HTTP route. This skill is the orientation; the SPEC is the reference. **When you change the `/sim`
> surface, update the SPEC in the same change** (it has its own constitution).

## Read these as needed

| File | When |
|---|---|
| [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md) | the full path/format/units/command catalog — your primary reference |
| [`SPEC_MCP.md`](../../../SPEC_MCP.md) | the MCP v1 logical resources/tools, canonical command envelope, batch semantics, capability preflight, and agent-specific caveats |
| [`coordinate-frames.md`](coordinate-frames.md) | KSA reference frames (ECL/CCE/CCF/**CCI**/ENU), surface velocity, the Body→CCI attitude quaternion, orbital math — needed for any flight/orbit work; §8 is the camera's six placement frames |
| [`flight-programs.md`](flight-programs.md) | how to structure a control loop; gating (pause/warp/stale); the `gogogo-rs` and `land-o-matic` case studies |
| [`recipes.md`](recipes.md) | complete runnable Bun/TS programs — connecting, the **teleport** task, throttle/ignite, burns, events, **timed sequences** (§12), **camera shots** (§13) |

In-repo working references: `examples/gogogo-rs` (minimal Rust control panel — throttle + ignite),
`examples/land-o-matic` (full Rust G-FOLD+UPFG landing autopilot), `examples/sdk-ts` (typed
TypeScript/Bun SDK over both transports).

## Mental model in 90 seconds

- **Reads** are SENSOR files: `cat /sim/<path>` → one value + `\n`. Formats: `G9` doubles, `0`/`1`
  flags, space-separated `x y z` vectors and `x y z w` quaternions, verbatim strings, NDJSON streams.
  The atomic per-vessel doc `vessels/<id>/telemetry` is one self-consistent JSON snapshot — prefer it
  for control loops.
- **Writes** actuate the game: `ctl/…` (per-vessel control — incl. `ctl/translate`, bang-bang RCS
  translation by body-axis signs: `1 0 0` = thrust along the nose, latches until `0 0 0`; and its
  sibling `ctl/rotate`, bang-bang RCS torque by body-axis signs: `+x` roll right, `+y` pitch up,
  `+z` yaw right — full authority needs `attitude_mode=manual`), per-module
  files (`engines/<n>/active`,
  `lights/<n>/on`, …), and `debug/…` (cheats: teleport/impulse/refuel/warp/switch). A write is line-buffered
  and actuates on the newline; failures return `EINVAL`/`EACCES`/`EBUSY`/`ETIMEDOUT`/…
- **A write blocks until the game thread executes it — one command per frame.** Sequential writes can
  therefore *never* land in the same tick (a formation teleport smears ~100 m per frame gap at orbital
  speed). To make N writes execute in the **same tick**, write them as one group to `/sim/ctl/batch`:
  one `<path> <value>` line per command, then a `commit` line (SPEC §3.10). Atomic, in order, one
  phase per batch, ≤64 commands. ⚠ Since KSA `2026.8.22.5348`, `teleport`/`impulse` are Solver phase,
  so a batch mixing either with a Frame-phase line (e.g. `ctl/throttle`) now fails `EINVAL`; an
  all-teleport formation batch is still correct. To spread writes over **time** instead of collapsing
  them into one tick, commit the same lines — each prefixed with an **absolute ms offset** — to
  `/sim/ctl/timed_batch`; they become a host-owned player under `/sim/ctl/schedules/<id>/` you can
  pause/scrub/re-rate/loop/stop (SPEC §3.10). Phase mixing *is* allowed there.
- **Two attitude paths:** write a named mode to `ctl/attitude_mode` (`Prograde`, `Retrograde`, …) and
  the onboard autopilot steers (warp-correct, no math) — *or* compute a **Body→CCI quaternion** and
  write `ctl/attitude_target` for a custom direction. Attitude/burn writes are **solver-phase** (take
  effect next solver step; run ~1× warp for closed loops) — and since KSA `2026.8.22.5348` so are
  `debug/…/teleport` and `debug/…/impulse`, which moved Frame → Solver (revs 5331/5339).
- **CCI is the working frame:** `position/cci`, `velocity/cci`, `attitude/quat`, `debug/teleport`,
  `debug/…/impulse` (default frame), and `ctl/burn` Δv are all **Celestial-Centered Inertial about the
  parent body** (Z = north pole, X = vernal point, equatorial X–Y plane). Constants come from
  `bodies/<parent>/{mu,radius,rotation_rate}`.
- **Pace in sim time, not wall time:** block on `time/alarm` (or `GET /v1/time/wait`); gate on
  `time/sim_dt==0` (paused) and `time/warp>1` (warping).

## MCP operating loop for an AI agent

MCP is operation-shaped even where a tool uses the shared `value`/`values`/`token`/`aux` wire slots.
Never fill every optional slot speculatively. Choose the operation, read its parameter descriptions
and capability entry, and send only the slots that operation consumes.

1. Call `gatos.get_capabilities` once per session and again after a feature/configuration change.
2. Call `gatos.get_world(detail:"summary")`, discover raw ids with a list tool, then read the target.
3. Check `controllable`, current parent, module ordinals, feature gates, and degraded health before acting.
4. Use the narrowest logical tool. Use `gatos.command` only as the canonical-action backstop.
5. Use `gatos.execute_batch` for same-tick commands of one derived phase; use
   `gatos.schedule_batch` for absolute millisecond offsets that may mix phases.
6. Save the pre-action `snapshot_sequence`, call `gatos.wait(after_sequence:...)`, and re-read the
   affected state. An accepted command is not immediate read-back.
7. Branch on structured `errno` and `retryable`. Never blindly retry stage, ignite, undock,
   decoupler fire, release, remove, clear, or another one-shot/lifecycle trigger.

The public operation-by-operation calls and recovery playbooks live under `site/src/content/docs/mcp/`;
the versioned contract is `SPEC_MCP.md`.

## Maintaining the MCP projection

gatOS deliberately uses the official C# SDK's low-level primitives behind a custom, stateless,
POST-only Streamable HTTP host. Preserve its Host/Origin validation, lack of sessions/GET/SSE, request
limits, cancellation, and real-SDK client tests. Do not add a second command model: logical tools must
map to `SimCommand` and immutable reads must project `SimSnapshot`/the shared stores.

An MCP surface change updates together: `gatOS.Mcp/` and tests, `CommandCatalog` where the shared
action changes, `SPEC_MCP.md`, `site/src/data/mcp-reference.ts`, the affected `site/.../mcp/` prose,
and this skill when agent behavior changes. Multipurpose tool docs must collocate each operation's
exact required fields, enum/range/unit, full example, phase/gate, and retry hazard.

## Access at a glance

```sh
# In the guest (the /sim mount):
cat   /sim/vessels/active/telemetry            # read the atomic vessel doc
echo 0.5 > /sim/vessels/active/ctl/throttle    # actuate (returns errno on failure)
echo "6578100 0 0 0 7784 0" > /sim/debug/vessels/Hunter/teleport   # CCI state vector
echo "10 0 0 body dv" > /sim/debug/vessels/Hunter/impulse          # +10 m/s kick off the nose

# On the host (HTTP /v1, default 127.0.0.1:4242; guest: $GATOS_HTTP):
curl  http://127.0.0.1:4242/v1/vessels/active/telemetry
curl -X POST http://127.0.0.1:4242/v1/command \
     -H 'content-type: application/json' \
     -d '{"vessel_id":"Hunter","action":"debug.teleport","values":[6578100,0,0,0,7784,0]}'
```

The generic write is `POST /v1/command` with `{vessel_id, action, ordinal?, value?, values?, token?}`
— see the action-key catalog in [SPEC §5](../../../SPEC_9P_FILESYSTEM.md). Its file twin is
`POST /v1/fs/<path>` with the raw value; both mirror the `/sim` files leaf-for-leaf.

## Where things live (quick index of `/sim`)

```
time/{ut,warp,sim_dt,warp_speeds,auto_warp,alarm}
system/{name,home,sun}
bodies/<id>/{id,class,parent,children,mass,radius,mu,soi,rotation_rate,
             position/ecl, velocity/ecl, orbit/*, atmosphere/*, ocean/*, focus}
vessels/active/…  (alias of the controlled vessel)   vessels/by-id/<id>/
    id name situation parent controlled controllable com scale always_render telemetry stream
    position/{cci,ecl,lat,lon}  velocity/{orbital,surface,inertial,cci}
    attitude/{quat,rates}  altitude/{barometric,radar}  mass/{total,dry,propellant}
    orbit/*  navball/*  environment/*  battery/*  power/*
    engines/<n>/*  tanks/<r>/*  rcs/<n>/*  solar/<n>/*  generators/<n>/*
    lights/<n>/*  docking/<n>/*  decouplers/<n>/*  animations/<n>/*  encounters
    srb/<n>/{engine,part,substance,grain,grain_shape,segment_count,valid,error,active,
             propellant,mass,mass_initial,mass_unburnable,mass_burnable,fraction,
             mass_flow,burn_time,burning_area,chamber_pressure,chamber_temp,
             exit_pressure,exit_temp,area_ratio,
             segments/<m>/{part,substance,grain,mass,mass_initial,mass_unburnable,
                           fraction,radius,length,volume,burn_depth}}
                                        (solid rocket motors — READ-ONLY. Solid propellant is NOT a
                                         tank, so tanks/ never shows a booster; srb/<n>/fraction and
                                         /burn_time are the booster gauges. Ignite via the engine
                                         surface: srb/<n>/engine names the engines/<n> entry. A lit
                                         solid cannot be throttled or shut down.)
    parts/json                          (the whole part/subpart tree as ONE JSON doc — cat + jq it)
    parts/<n>/{instance_id,id,display_name,template,is_root,subpart_count,position,
               subparts/<m>/{instance_id,id,display_name,template,position}}
                                        (parts + subparts; welds anchor picker; telemetry_vessel_parts)
    ctl/{ignite,shutdown,engine,stage,throttle,lights,rcs,translate,rotate,
         attitude_mode,attitude_frame,attitude_target,burn,focus}
events
status/{game_version,sampler,accessors,transports}
ctl/batch                               (atomic same-tick command groups — SPEC §3.10)
ctl/timed_batch                         (the same control surface on a clock; schedule_enabled=true)
ctl/schedules/{count,clear,help,
               <id>/{kind,group,state,t,duration,pending,dropped,clock,last_error,
                     pause,scrub,rate,loop,stop,remove}}      (the live-player registry)
camera/{status,info,target,playback,last_error,enabled,release,mode,follow,tidal,
        map/scope, track/<name>, play,set,stop,
        pose/{position,frame,anchor,geo,orbit/{radius,azimuth,elevation},rotation,
              aim,aim_target,aim_offset,aim_frame,aim_up,roll,fov,ortho,ortho_height,
              smoothing,reset}}         (programmable cinematic camera; camera_enabled=true)
audio/{file/<name>,play,set,stop,status,info}         (userland audio; audio_enabled=true)
debug/                                  (only when debug_namespace=true)
    vessels/<id>/{teleport,impulse,refill_fuel,refill_battery,docking/<n>/pushoff_impulse,
                  weld,weld_here,unweld}
    welds/{clear,count,<source>/{target,part,offset,rotation,lock_rotation,enabled}}
    thug_life/{add,clear,count,<id>/{vessel,part,position,rotation,size,visible,remove,spec}}
    iva/{enabled,run_outside_iva,adopt,adopt_all,clear,count,stats,interior,help,
         <id>/{vessel,part,name,template,position,velocity,angular_velocity,mass,shape,size,
               asleep,nudge,release,spec}}                   (OFF by default — see below)
    always_render_iva   time/warp   focus   control_vessel
    engineplume/{help,templates/<id>/{core,absorption,emission,mach_diamonds,noise,quality}/*,json,reset}
    plumetrail/{help,render/*,json,clear,reset}       (one GLOBAL trail renderer)
    clouds/{help,bodies/<body>/{shared/*,layers/<n>/{*,two_d/*,raymarch/*,types/<m>/*},json,reset}}
    terrain/{help,wireframe,bodies/<body>/{min_height,max_height,slope_roughness_deg,hapke_albedo,
                                           biomes/*,tessellation/*,json,reset}}
```

Each leaf's format, units, archetype (read-only vs control vs trigger), and backing command action
are in [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md). Per-module dirs appear only when the
vessel actually has that module; `bodies`/detail dirs depend on telemetry config gates.

**Cheats (`/sim/debug`, ported from the sibling `unscience` mod):** `always_render_iva` forces interior
(IVA) meshes to render outside the IVA camera; **welds** rigidly attach one vessel to another vessel's
part (`weld` = explicit pose, `weld_here` = capture the current relative pose, `unweld`; the registry +
`clear`/`enabled` live under `welds/`); **`thug_life`** anchors a flat, world-space textured quad (the
"thug life" sunglasses meme) to a part on a vehicle, tracked each frame (gatOS's first custom GPU render —
`add`/`clear` + per-entry `position`/`rotation`/`size`/`visible`/`remove`). For welds and `thug_life`,
discover the anchor part from the target's `parts/<n>/instance_id` (pass `0` to anchor to the body/CoM
frame). A **weld** anchor may also be a **subpart** — use `parts/<n>/subparts/<m>/instance_id`; an
animated subpart anchor (robotics/landing-leg segment) tracks the animation. (`thug_life` anchors remain
top-level parts only.) Full arg shapes, action keys, and errnos are in [SPEC §3.7](../../../SPEC_9P_FILESYSTEM.md); a
worked `weld_here` example is in [`recipes.md` §9](recipes.md) and a `thug_life` example in
[`recipes.md` §10](recipes.md). The render-side internals (how the quad is drawn into KSA's scene) are
documented in the **ksa skill's `quad.md`**.

**IVA cabin physics (`/sim/debug/iva`) — OFF by default:** free-floating objects inside a vessel's
interior, with real inertial physics — weightless and drifting while coasting, slammed aft when the
engines light, flung around by RCS rotation, and colliding with the *actual* interior surfaces.
`enabled` is a **master switch that starts and ends the whole feature**: while it is `0` (the shipped
default) no simulation exists at all, and writing `0` puts every object back at its exact rest pose and
tears everything down. Turn it on, then cut props loose:

```sh
echo 1 > /sim/debug/iva/enabled
echo "Gemini7 4 Sardine" > /sim/debug/iva/adopt_all   # the 4 smallest props matching "Sardine"
echo "Gemini7 12"        > /sim/debug/iva/adopt_all   # or the 12 smallest loose props, any template
cat /sim/debug/iva/0/position                         # each object is /sim/debug/iva/<id>/
echo "0.3 0 0" > /sim/debug/iva/0/nudge               # flick it across the cabin
echo 1 > /sim/debug/iva/clear                         # everything back where it was
```

Objects drive real shipped IVA prop **SubParts** — so pass a **subpart** `instance_id`
(`parts/<n>/subparts/<m>/instance_id`, or `cat parts/json | jq`), never a top-level part: KSA saves a
top-level part's transform but not a SubPart's, which is exactly why this can never touch your save
file. Objects park (frozen) under time warp, in the VAB, and outside the IVA camera unless
`run_outside_iva=1`. Emits `iva.impact` / `iva.escape` / `iva.release` events — wiring `iva.impact` to
`/sim/audio` gives you clunks. `cat /sim/debug/iva/help` is a full readme; the surface, tuning knobs and
errnos are in [SPEC §3.7](../../../SPEC_9P_FILESYSTEM.md) (**iva**).

**FX editors (`/sim/debug/{engineplume,plumetrail,clouds,terrain}`):** the game's four built-in render
editors as filesystems — **one writable leaf per knob**, each with a fixed inclusive range (out of range
⇒ `EINVAL` before it reaches the game), live read-back, a per-entity `json` document for discovery, and a
per-entity `reset` that restores the values from before gatOS first wrote them. Scopes differ and this is
the thing to get right: `engineplume` is **per template** (an edit repaints *every* nozzle using that
template), `plumetrail` is a **single global** renderer (plus a one-shot `clear` that deletes existing
trails), `clouds` is per body → layer → cloud type, `terrain` is per body (only bodies with a live render
slot) plus a **global** `wireframe`. All writes are Frame-phase, cheap enough to animate at 10–60 Hz —
group simultaneous ones through `/sim/ctl/batch`. Values are stored as 32-bit floats (read-back is
single-precision) and the surface is session-scoped (never persisted; restored at unload). Every field,
range and unit is in [SPEC §3.7](../../../SPEC_9P_FILESYSTEM.md); `cat <family>/help` in-guest.

**Audio playback (`/sim/audio`, gated by `audio_enabled=true` — NOT a debug cheat):** play real
audio (mp3/ogg/wav/flac) through the game's speakers. Upload with `cat clip.mp3 >
/sim/audio/file/clip.mp3` (in-memory, playable once the `cat` finishes; `rm` evicts), then
`echo 'clip.mp3' > /sim/audio/play` — optional `key=value` tokens `start=`/`end=` (ms range),
`vol=` (0..1), `loop=0|1`, `group=sfx|music|ui` (which in-game volume slider governs it),
`id=` (a handle for later `set`/`stop`; auto `#N` otherwise), `pan=`, `pitch=`. Live control:
`echo 'bgm vol=0.15' > /sim/audio/set` (also `pause=1`/`resume=1`/`seek=ms`); stop with
`echo 'bgm' > /sim/audio/stop` (or `all`). `cat /sim/audio/status` lists live channels;
`audio.finished` events land on `/sim/events` (`grep -m1` for completion instead of polling).
Keeps playing at any time-warp (deliberate — alarms must not mute). Host-side binary upload:
`curl -T clip.mp3 http://127.0.0.1:4242/v1/audio/file/clip.mp3`. Full grammar, caps, errnos:
[SPEC §3.9](../../../SPEC_9P_FILESYSTEM.md).

**Timed sequences (`/sim/ctl/timed_batch` + `/sim/ctl/schedules`, gated by `schedule_enabled=true`):**
`ctl/batch` makes N writes land in one tick; a **timed batch** spreads them over a timeline instead.
Same paths, same values, each line prefixed with an **absolute offset in milliseconds** (fractional
allowed — never deltas), optional `@id`/`@clock`/`@rate`/`@loop`/`@group` directives first, then
`commit`:

```sh
cat > /sim/ctl/timed_batch <<'EOF'
@id      launch-seq
@clock   render                          # render | wall | ut
0        vessels/active/ctl/throttle  1
1200     vessels/active/ctl/ignite    1
commit
EOF
cat /sim/ctl/schedules/launch-seq/state   # pending|running|paused|done|failed
echo 0.25 > /sim/ctl/schedules/launch-seq/rate      # also pause/scrub/loop/stop/remove
```

The **host** owns the clock, so timing survives the guest going away and replays identically.
`render` accumulates the game's clamped frame delta (lags after a hitch, never catches up — right for
footage), `wall` is true elapsed time, `ut` is sim time (diverges under warp — right for mission
events). **Phase mixing is allowed** (unlike `ctl/batch`). Commit is **non-blocking and
all-or-nothing** (`ENOENT` bad path, `EINVAL` bad value/directive/cap). On catch-up every *trigger*
fires in order while *state* controls coalesce to the last write per path (counted at
`<id>/dropped`) — which is why a densely generated script is cheap. `schedule.*` events land on
`/sim/events`. Full grammar + semantics: [SPEC §3.10](../../../SPEC_9P_FILESYSTEM.md), or
`cat /sim/ctl/schedules/help` in-guest; worked example in [`recipes.md` §12](recipes.md).

**Programmable camera (`/sim/camera`, gated by `camera_enabled=true` — NOT a debug cheat):** take the
game's main camera and fly it from a shell. `echo 1 > /sim/camera/enabled` captures the live camera,
parks the game's controller and makes gatOS the only writer; everything you then write is an
*override* over that snapshot, and `echo 0 > enabled` (eased) or `echo 1 > camera/release` (hard cut)
puts it all back. A pose is **anchor** (`pose/anchor`: `vessel:`/`body:`/`part:<vessel>/<iid>`/`none`)
+ **frame** (`pose/frame`: `ecl|cce|bodyfixed|enu|lvlh|chase`) + a placement — `pose/position`
(cartesian), `pose/geo` (`lat lon alt [body:<id>]`, altitude above **terrain**), or
`pose/orbit/{radius,azimuth,elevation}` (a non-zero radius wins). Orientation is normally
`pose/aim <target> [off x y z] [frame <f>] [up world|target|velocity|free] [roll <deg>]` — the offset
is measured **on the subject** and re-resolved every frame, which is what glues it to a moving hull.
`pose/{fov,ortho,ortho_height,roll,smoothing}` are the lens. Anchored smoothing filters only the
camera-to-anchor component: the anchor's current-frame translation passes through exactly, and an
`aim` target remains centred because look-at is an exact constraint rather than a smoothed rotation.

```sh
echo 1 > /sim/camera/enabled
echo "vessel:apollo11" > /sim/camera/pose/anchor
echo "bodyfixed" > /sim/camera/pose/frame     # +X nose, +Y right, -Z up (so "up" is negative)
echo "-40 0 -6"  > /sim/camera/pose/position
echo "vessel:apollo11 off 0 0 -1.2 up world" > /sim/camera/pose/aim
echo 0.35 > /sim/camera/pose/smoothing        # critically-damped filter, seconds
cat /sim/camera/status                        # pose channels + applied_position_ecl/applied_rotation
echo 0 > /sim/camera/enabled                  # eased hand-back
```

Three layers composite per channel — **track ?? your override ?? the ownership baseline** — so a
`timed_batch` (or a bare `echo`) can pull focus while a JSON **track** interpolates position. A track
(`cp shot.json /sim/camera/track/shot`; `echo shot > /sim/camera/play`) registers as a player at
`/sim/ctl/schedules/camera/`, so it needs `schedule_enabled` too and answers the same
`pause`/`scrub`/`rate`/`loop`/`stop`. **Every track channel has a `pose/` leaf**, so the JSON is never
the only route. Gotchas: `mode`/`follow`/`tidal` and the player's camera keys are inert while gatOS
owns the camera (use `pose/anchor` + `pose/aim_target`); `pose/ortho_height` is the one change gatOS
cannot undo; the camera is floored at surface + 0.5 m; **an EVA kittenaut walks relative to the shot**
while you hold the camera; and `iva`/`map` cannot be gatOS ownership contexts at all (only `fixed`
is — `camera/map/scope` is the map knob that does ship). Full catalog, errnos and the track JSON
schema: [SPEC §3.11](../../../SPEC_9P_FILESYSTEM.md); frames in
[`coordinate-frames.md` §8](coordinate-frames.md); worked shot in [`recipes.md` §13](recipes.md).

**First-class per-vessel nodes (NOT under `/sim/debug`; also ported from `unscience`):**
`vessels/by-id/<id>/scale` — write any finite value `> 0` to uniformly rescale the whole vessel model
one-shot (`echo 50000 > scale` = planet-sized; `echo 1 >` restores; `0`/negative → `EINVAL`; KSA
reverts it when it rebuilds the vessel). It is **visual/transform-only** — no collider, mass or
performance change — and since KSA `2026.8.22.5348` (rev 5329) that deliberately differs from the
in-game gizmo, whose scaling is now *physical* (`IRescale`: colliders, tank volume, inert mass, nozzle
areas, decoupler force), clamped 0.5×–2× and quantized to 0.25 m steps.
`vessels/by-id/<id>/always_render` — write `1` to keep that vessel rendered at **any** distance
(bypasses the sub-pixel cull that normally hides far vessels; the mark survives scene rebuilds and
auto-drops when the vessel despawns; EVA kittens are unaffected).
Both work on **any vessel by id** even with `control_all_vessels=false` (deliberately
authority-exempt). SPEC §3.4.1 has the full semantics.

## Gotchas that cause silent failures

- A vessel's **name is its id** (`Hunter`, `Polaris` are literal ids). Ids in `/sim` paths are
  sanitized (non-`[A-Za-z0-9._-]` → `_`).
- `control_enabled=false` → all writes `EACCES`. `control_all_vessels=false` → only the controlled
  vessel is commandable (`camera.focus`, `vessel.scale`, `vessel.always_render` and `debug.*` stay
  exempt). `debug_namespace=false` → `/sim/debug/**` is gone. All default on.
- **`/sim/debug/iva/enabled` is off by default** and is a *separate* switch from `debug_namespace` —
  the `iva/` directory is there, but adopting anything before `echo 1 > enabled` answers `EOPNOTSUPP`,
  and nothing is simulated until you do. `adopt` takes a **subpart** `instance_id` (a top-level part is
  `ENOENT` by design) and refuses anything bigger than `iva_max_object_size` (0.5 m) so you cannot cut a
  hull panel loose.
- **`debug.teleport` sets a CCI state about the vessel's *current* parent body** — the vessel must
  already be in the intended body's SOI. See [SPEC §6](../../../SPEC_9P_FILESYSTEM.md). Since KSA
  `2026.8.22.5348` both `teleport` and `impulse` are **Solver** phase (they land on the next solver
  step, and cannot share a `ctl/batch` with Frame-phase actions).
- **`debug/…/impulse` defaults to newton-seconds** (Δv = J ÷ live mass), not m/s — append the `dv`
  keyword to apply the vector directly as Δv, and `body` to read it in the vessel frame (+X = nose):
  `echo "10 0 0 body dv" > impulse`. Same CCI-about-current-parent frame as teleport otherwise.
- **`ctl/stage` is per-*module* and no longer touches RCS (KSA `2026.8.22.5348`, rev 5329).** It walks
  the staged part's **subtree** and activates only `ISequenced` modules — exactly engines and
  decouplers — whose own sequence matches. `ThrusterController` is not one, so **`rcs/<n>/active` does
  not flip as a side effect any more**: toggle RCS explicitly. Engines/decouplers on **sub-parts** now
  stage where they used to be skipped, and a part with modules in **different sequences** needs one
  `ctl/stage` press per sequence.
- **`engines/<n>/min_throttle` on a multi-engine stack inverted (KSA `2026.8.22.5348`, rev 5317).** The
  write still sets that engine's own floor, but the flight computer now folds the active engines with
  `Max` (seed `0`) instead of `Min` (seed `1`) — the **most** restrictive engine sets the effective
  floor, not the least. One high value now raises the whole stack's floor.
- **`navball/deltav` and `navball/twr` were corrected in KSA `2026.8.22.5348` (rev 5318)** — a part in
  sequence 0 used to zero both. Same path, format and units; re-baseline stored expectations.
- **`ctl/burn` timing moved (KSA `2026.8.22.5348`, rev 5317)** — same interface and CCI Δv, but burn
  duration, throttle profile and the point at which the FC abandons an auto burn all differ from
  earlier builds. Don't hard-code a burn duration.
- **`debug/terrain/.../tessellation/range_m` re-baselined (KSA `2026.8.22.5348`)** — the engine default
  moved 220 → 50 m and the shader's displacement falloff moved from `range×0.1…0.95` to
  `range×0.75…0.975`. Read the live value before writing; old tunings look wrong.
- **`debug/plumetrail/render/trail_color` is gone (KSA `2026.9.7.5402`).** The renderer's global debug
  tint was removed — trail colour/density/lifetime are per plume-trail template asset now. Reading or
  writing it answers `ENOENT`; the other ten `render/*` fields are unchanged.
- **`parts/<n>/display_name` is the authored template name since KSA `2026.9.7.5402`** ("Parachute
  Bay", "Drogue Radial A"…) and therefore **not unique** — key on `instance_id`, never on the name.
- **Crashes can spawn debris/fragment vessels (KSA `2026.9.7.5402`).** Parts now have crash
  tolerances; a hard contact can split a vessel into `<id>_1`, `<id>_2`… fragments plus single-part
  debris, all of which appear under `vessels/` (`controllable`=0, one part, orbit events frozen), and
  control moves to the largest controllable fragment on its own. Programs that enumerate `vessels/`
  should tolerate ids appearing mid-flight; there is no `debris` flag yet.
- **`ctl/stage` also arms parachutes and fires cut modules (KSA `2026.9.7.5402`)** — the new
  `ParachuteDeploy`/`ParachuteCut` modules are `ISequenced`, so a stage press behaves exactly like the
  stage key on a chute bay. `animations/<n>/goal` on a chute-bay door fights the chute state machine.
- **`POST /v1/command` / `gatos/command` require `vessel_id` even for globally addressed actions** —
  the whole `camera.*`, `schedule.*`, `audio.*` families and `debug.warp`/`debug.thug_life_*`/… name
  no vessel, so send `"vessel_id": ""`. Omitting it (or sending `null`) is `400 EINVAL`.
- **`camera/play`/`set`/`stop` need `[schedule] schedule_enabled` as well as `camera_enabled`** — a
  camera track *is* a `/sim/ctl/schedules` player (`kind = camera-track`, id `camera`). With
  scheduling off they answer `EOPNOTSUPP`; every camera *leaf* still works.
- **A malformed camera track fails on `close`, which carries no errno** — the `cp` looks like it
  worked. Read `/sim/camera/last_error` (or upload over HTTP, where the `POST` does carry the EINVAL).
- **Timeline units are mixed on purpose:** `timed_batch` offsets and `ctl/schedules/<id>/t` are
  **milliseconds**; a track's `t`/`duration` and `camera/play at` are **seconds**.
- Mass is **kg**; gravity is **μ/r²** (never 9.8); ground-referenced velocity is `v_cci − ω×r`.
- Don't substitute a generic quaternion library for the Body→CCI attitude math — use KSA's exact
  convention (`transform(+X, q) == thrust_direction`); see [`coordinate-frames.md`](coordinate-frames.md).

## Example: "teleport Hunter to a 120 km Earth orbit, Polaris 50 m ahead"

This is fully worked (a copy-pasteable Bun/TS program) in [`recipes.md` §1](recipes.md). The shape:
read `bodies` for Earth's `mu`/`mean_radius`, compute `r = radius + 120000` and `v = sqrt(mu/r)`,
teleport Hunter to CCI `[r,0,0, 0,v,0]` (equatorial circular), and teleport Polaris to the same orbit
advanced in true anomaly by `Δθ = 50/r`. Everything you need to derive it is in the SPEC + frames doc.
# Paint vehicles and EVA kittens

Paint is session-only and explicitly opt-in. Enable vehicle shader paint with
`echo 1 > /sim/paint/parts/enabled`, then set a normalized sRGB triple and enable the desired rule.
Whole-vessel example: write `0.15 0.5 1` then `1` under
`/sim/vessels/by-id/<id>/paint/parts/{color,enabled}`. An adjacent part/subpart `paint/` directory is
the individual override; it requires the parts telemetry gate. Precedence is instance, live vessel,
template, global.

Enable safe EVA clones with `echo 1 > /sim/paint/kittens/enabled`. Shared rules live under
`/sim/paint/kittens/{shared,materials}`; individual rules under the EVA vessel's
`paint/kitten/{default,materials}`. Material names are discovered live (`body`, `fur`, `helmet`,
`visor`, `mmu`, with numeric suffixes). Disabling either master restores stock but retains rules;
write its `clear` trigger to remove rules. HTTP and MQTT use the normal field mirrors; MCP uses
`gatos.paint_control` or canonical `gatos.command`.

# Draw ground clutter with your own images

`/sim/paint/textures` re-points a stock ground-clutter texture (rocks, trees, grass, shrubs) at an
image you upload. **An ordinary sRGB PNG renders as authored** — the default `faithful` bind mode
corrects for the clutter shader, so no pre-processing and no colour maths are asked of the user.
Session-only, in-memory, restored on unbind. There is **no master switch** — the feature is inert
until something is bound.

The flow is upload → bind → teardown:

```sh
cat /sim/paint/textures/clutter          # texture-id slot w h mips used_by ecotypes
                                         # texture-id is a content-relative asset PATH, e.g.
                                         # Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2
cat rock.png > /sim/paint/textures/file/rock.png    # png|jpeg|bmp|hdr|dds|ktx|ktx2
echo 'Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2 rock.png' > /sim/paint/textures/bind      # 'faithful' by default
echo 'Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2 rock.png raw' > /sim/paint/textures/bind  # …or byte-for-byte, like a stock texture
cat /sim/paint/textures/applied          # <id> <file> pending|applied|failed w h mips vram error
echo 'Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2' > /sim/paint/textures/unbind  # one; 'all' = everything
echo 1 > /sim/paint/textures/clear       # same global teardown as 'unbind all'; uploads survive
rm /sim/paint/textures/file/rock.png     # evict an upload (unbinds it first)
```

Rules a program must follow:

- **A plain sRGB PNG just works.** Bind's optional third token is the render mode and defaults to
  `faithful`, which rewrites the decoded pixels so the image renders at its authored colours, in
  every biome. Nothing has to be pre-corrected, and a user does not have to be told to author against
  mid-grey.
- **Discover, never guess, target ids.** Read `clutter` and use its first column. A `<texture-id>` is
  the asset's **content-relative path** (e.g.
  `Textures/Planets/Earth/GroundClutter/Grass_Diffuse.ktx2`) — install-independent, unique per asset
  and space-free, so it drops straight into the space-separated line. It is not a symbolic name and
  cannot be constructed by hand. Bind takes
  `<texture-id> <file> [faithful|raw]` — the same shape a `bindings` row reads back (mode column
  included), so a listing line can be echoed straight back to re-create a binding.
- **The write's success is not the GPU's.** `bind` only queues a Frame-phase command. Poll `applied`
  (or `status`, which carries `bound applied retiring vram_bytes revision error`) for the real
  outcome; a failed decode leaves the stock texture drawn and puts the reason in the row.
- **A binding replaces a texture *asset*.** `used_by > 1` in the `clutter` listing means every one of
  the listed ecotypes changes together. Check before binding.
- **Errno vocabulary:** `EINVAL` bad name / unparseable line / unrecognised container; `ENOENT`
  unknown texture id or missing upload; `EBUSY` the upload has not committed yet; `ENOSPC` file,
  byte, or binding cap; `EFBIG` per-file cap (mid-write, so the failing `write(2)` carries it).
  Images larger than `paint_texture_max_dimension` are downscaled, not rejected.
- **Uploads over the network are the one non-uniform surface.** HTTP uses
  `PUT /v1/paint/texture/file/<name>[?offset=N&complete=0|1]` and answers **413/EFBIG** above the
  1 MiB request cap, so chunk any real PNG. MCP uses `gatos.paint_texture(operation:"upload")` with
  base64 (which inflates 4/3 against the 24 MiB frame). **MQTT carries no binary upload**, though it
  mirrors every control leaf. Everything else rides `/v1/fs/paint/textures/...` and
  `gatos/sim/paint/textures/...` normally; MCP binds with
  `gatos.paint_control(operation:"texture_bind", target:"<texture-id>", file:"<name>", value:0|1)`
  — `value` is the mode (`0` = `faithful`, `1` = `raw`).
- **`raw` is the like-for-like option.** Clutter diffuse maps are modulation maps, not albedo: the
  shader computes `albedo = 2 * decode(t.rgb, t.a) * mix(meanLum, instanceColor, t.a) / meanLum`, so
  the texel is doubled and **alpha is not opacity** — it selects sRGB (`0`) vs linear (`1`) decoding
  *and* the blend toward the per-instance terrain tint. `faithful` (the default) cancels both: RGB is
  scaled by `2^(-1/2.2)` (white `255` → `186`, round-trip error < 0.2%) and alpha is cleared to `0`.
  `raw` uploads the decoded bytes untouched, so the image is read exactly as one of KSA's own clutter
  textures — mid-grey `0.5` neutral, biome-tinted at `A=255` — which is what you want when replacing
  a stock texture like-for-like. A decode that is not RGBA8 (some ktx/dds/hdr) cannot be corrected:
  the `faithful` binding lands `failed` in `applied`, its error naming `raw` as the fix. Real cutout
  opacity is a separate `opacity` slot either way. Mips are generated automatically and are mandatory
  — without them the texture aliases badly at range.

# Spray your own images onto the world (stickers)

`/sim/paint/stickers` projects an uploaded image onto a vehicle part, onto terrain, or onto the
ground clutter standing on it. **There is no sticker upload surface** — a sticker's image *is* an
entry of `/sim/paint/textures/file/`, so upload once and reuse it for as many stickers as you like.
Session-only, no master switch: with nothing placed the feature is one branch per frame.

```sh
cat meow.png > /sim/paint/textures/file/meow.png    # the shared image store (png|jpeg|bmp|...)
echo 'meow.png w=2 h=2' > /sim/paint/stickers/spray  # aim=camera by default; aim=cursor also works
cat /sim/paint/stickers/last        # 0 vessel Kitten-1 part 41 hit 8.42m   (or: no hit within 2000m)
cat /sim/paint/stickers/status      # <id> <image> vessel|body <target> live=0|1 texture=ready|missing|...
cat /sim/paint/stickers/info        # enabled= stickers= stickers_max= live= images= vram_bytes= patch= renderer= max_view_distance_m=
echo 0.4 > /sim/paint/stickers/0/alpha        # size "w h" | depth | rotation | alpha | brightness | visible | image
cat /sim/paint/stickers/0/spec      # meow.png vessel Kitten-1 41 0.1 0.5 -1.4 0 1 0 roll=15 w=2 h=2 d=0.3 alpha=0.4 brightness=1
echo 1 > /sim/paint/stickers/0/remove         # one; 'echo 1 > clear' for all
```

Place by coordinates instead of aiming, when you want it exact and scriptable:

```sh
iid=$(cat /sim/vessels/by-id/Kitten-1/parts/0/instance_id)      # needs telemetry_vessel_parts=1
echo "meow.png vessel Kitten-1 $iid 0 0.5 -1.4 0 1 0 roll=15 w=0.6 h=0.3" > /sim/paint/stickers/place
echo 'meow.png body Mun 12.03 -41.88 heading=90 w=5 h=5'                   > /sim/paint/stickers/place
```

Rules a program must follow:

- **Two grammars, one shared tail.** `spray` takes `<image> [aim=camera|cursor] [range=] [roll=] [w=]
  [h=] [d=] [alpha=] [brightness=]`; `place` takes `<image> vessel <vessel_id> <part_iid> x y z nx ny
  nz [roll=…]` or `<image> body <body_id> <lat> <lon> [heading=…]`. `w`/`h` ∈ `(0,1000]` m default
  `1`; `d` ∈ `(0,100]` m default **`0.3` on a vessel, `1` on a body**; `alpha` ∈ `[0,1]` default `1`;
  `brightness` ∈ `(0,8]` default `1`; `range` ∈ `(0,1e6]` m default `2000`; `roll`/`heading` any
  finite degrees, default `0`. The rotation key is anchor-specific — `roll` for a vessel, `heading`
  for a body — so neither spelling can be used against the wrong anchor. Duplicate, unknown or
  out-of-range keys are **EINVAL on the `write(2)`**, before anything reaches the game.
- **`spray` can miss.** No hit within `range` ⇒ **ENOENT**, and `last` reads `no hit within <range>m`.
  It hits vehicle parts first and the terrain behind them; **ground clutter cannot be aimed at** (it
  exists only on the GPU) — but the decal box then projects onto the rock anyway, which is the point.
  On `spray`, `roll=` *adds to* the orientation that reads upright from where you are, and omitting
  `d=` is not the same as passing a number: the default is chosen after the ray says what it hit.
- **Ids are the smallest free slot and are reused** after `remove`/`clear`. Never assume the id of a
  create — read `last`, or wait for the `paint.sticker_placed` event on `/sim/events` (its detail is
  the same line, and `vessel_id` is set only for a vessel anchor).
- **`<id>/spec` is exactly a `place` line.** Echo it back to clone the sticker under a new id. That is
  also the save game: `for d in /sim/paint/stickers/[0-9]*; do cat "$d/spec"; done > ~/specs`, plus a
  copy of the images, replayed at boot. Nothing is persisted host-side.
- **Dormant, not deleted.** A despawned vessel, a staged-away part or an evicted image gives
  `live=0` (`texture=missing`) and the entry survives with its settings and `spec` intact; it draws
  again when the anchor or the image comes back. Re-uploading an image under the same name
  **hot-swaps** it under every sticker using it.
- **Sub-parts are valid anchors.** `part_iid` may come from `parts/<n>/subparts/<m>/instance_id`, and
  `spray` naturally anchors to the sub-part it hit — which is why a decal on a gimballing bell or a
  robotics segment tracks it.
- **What the renderer will and will not do.** Main viewport only (not crew portraits or extra
  windows). Beyond `paint_stickers_max_view_distance_m` (5000 m) a sticker is not drawn at all. The
  decal fades out at grazing angles (normal cutoff) and its lighting is an approximation of the
  surface it sits on — use `brightness` ∈ `(0,8]` to compensate rather than expecting exact scene
  lighting. `echo 1 > /sim/paint/stickers/debug` draws every sticker as a magenta checker of its
  projection box: use it first whenever a sticker is missing or misplaced.
- **Health, not exit status.** A create only queues a Frame-phase command. `info` carries
  `patch=0|1 renderer=idle|active|degraded` and `last_error` carries any renderer/texture fault; the
  latches are `paint.sticker_renderer` and `paint.sticker_texture` in `/sim/status/accessors`.
- **Caps and gates:** `paint_stickers_max_count` (256) — a full registry is **EINVAL** naming the
  limit, not ENOSPC. The subtree exists only when both `paint_stickers_enabled` and
  `paint_textures_enabled` are on. Gate string `control_enabled + paint stickers`.
- **Transports.** Everything above mirrors to `/v1/fs/paint/stickers/...` and
  `gatos/sim/paint/stickers/...` normally. MCP uses one tool,
  `gatos.paint_sticker(operation:"place"|"spray"|"set"|"remove"|"clear"|"list"|"debug")` — `set`
  takes `id` plus exactly **one** knob (`width`+`height`, `depth`, `roll`/`heading`, `alpha`,
  `brightness`, `image`, or `value` for visibility) — plus the `paint_stickers` runtime feature
  document. Images still upload through `gatos.paint_texture`.
