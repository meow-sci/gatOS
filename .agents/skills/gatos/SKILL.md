---
name: gatos
description: >-
  Write scripts and programs against gatOS — the KSA mod that exposes live Kitten Space Agency
  simulation state as a 9P filesystem at /sim (also over HTTP /v1 and MQTT). Use this when asked to
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

> **The complete, authoritative catalog is [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md)**
> at the repo root — every path, format, unit, read/write semantic, command action key, errno, and
> HTTP route. This skill is the orientation; the SPEC is the reference. **When you change the `/sim`
> surface, update the SPEC in the same change** (it has its own constitution).

## Read these as needed

| File | When |
|---|---|
| [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md) | the full path/format/units/command catalog — your primary reference |
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
  phase per batch, ≤64 commands. To spread writes over **time** instead of collapsing them into one
  tick, commit the same lines — each prefixed with an **absolute ms offset** — to
  `/sim/ctl/timed_batch`; they become a host-owned player under `/sim/ctl/schedules/<id>/` you can
  pause/scrub/re-rate/loop/stop (SPEC §3.10). Phase mixing *is* allowed there.
- **Two attitude paths:** write a named mode to `ctl/attitude_mode` (`Prograde`, `Retrograde`, …) and
  the onboard autopilot steers (warp-correct, no math) — *or* compute a **Body→CCI quaternion** and
  write `ctl/attitude_target` for a custom direction. Attitude/burn writes are **solver-phase** (take
  effect next solver step; run ~1× warp for closed loops).
- **CCI is the working frame:** `position/cci`, `velocity/cci`, `attitude/quat`, `debug/teleport`,
  `debug/…/impulse` (default frame), and `ctl/burn` Δv are all **Celestial-Centered Inertial about the
  parent body** (Z = north pole, X = vernal point, equatorial X–Y plane). Constants come from
  `bodies/<parent>/{mu,radius,rotation_rate}`.
- **Pace in sim time, not wall time:** block on `time/alarm` (or `GET /v1/time/wait`); gate on
  `time/sim_dt==0` (paused) and `time/warp>1` (warping).

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
`pose/{fov,ortho,ortho_height,roll,smoothing}` are the lens.

```sh
echo 1 > /sim/camera/enabled
echo "vessel:apollo11" > /sim/camera/pose/anchor
echo "bodyfixed" > /sim/camera/pose/frame     # +X nose, +Y right, -Z up (so "up" is negative)
echo "-40 0 -6"  > /sim/camera/pose/position
echo "vessel:apollo11 off 0 0 -1.2 up world" > /sim/camera/pose/aim
echo 0.35 > /sim/camera/pose/smoothing        # critically-damped filter, seconds
cat /sim/camera/status                        # one "key value…" line per channel
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
reverts it when it rebuilds the vessel). `vessels/by-id/<id>/always_render` — write `1` to keep that
vessel rendered at **any** distance (bypasses the sub-pixel cull that normally hides far vessels; the
mark survives scene rebuilds and auto-drops when the vessel despawns; EVA kittens are unaffected).
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
  already be in the intended body's SOI. See [SPEC §6](../../../SPEC_9P_FILESYSTEM.md).
- **`debug/…/impulse` defaults to newton-seconds** (Δv = J ÷ live mass), not m/s — append the `dv`
  keyword to apply the vector directly as Δv, and `body` to read it in the vessel frame (+X = nose):
  `echo "10 0 0 body dv" > impulse`. Same CCI-about-current-parent frame as teleport otherwise.
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
