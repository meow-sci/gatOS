---
name: gatos
description: >-
  Write scripts and programs against gatOS — the KSA mod that exposes live Kitten Space Agency
  simulation state as a 9P filesystem at /sim (also over HTTP /v1 and MQTT). Use this when asked to
  read game/celestial/vehicle telemetry, control vehicles (throttle, ignite, staging, attitude,
  burns, RCS, lights, docking), use game/debug controls (teleport, impulse kick, refuel, time-warp,
  switch vessel), or write flight-computer / autopilot programs. Covers the full /sim catalog, the
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
| [`coordinate-frames.md`](coordinate-frames.md) | KSA reference frames (ECL/CCE/CCF/**CCI**/ENU), surface velocity, the Body→CCI attitude quaternion, orbital math — needed for any flight/orbit work |
| [`flight-programs.md`](flight-programs.md) | how to structure a control loop; gating (pause/warp/stale); the `gogogo-rs` and `land-o-matic` case studies |
| [`recipes.md`](recipes.md) | complete runnable Bun/TS programs — connecting, the **teleport** task, throttle/ignite, burns, events |

In-repo working references: `examples/gogogo-rs` (minimal Rust control panel — throttle + ignite),
`examples/land-o-matic` (full Rust G-FOLD+UPFG landing autopilot), `examples/sdk-ts` (typed
TypeScript/Bun SDK over both transports).

## Mental model in 90 seconds

- **Reads** are SENSOR files: `cat /sim/<path>` → one value + `\n`. Formats: `G9` doubles, `0`/`1`
  flags, space-separated `x y z` vectors and `x y z w` quaternions, verbatim strings, NDJSON streams.
  The atomic per-vessel doc `vessels/<id>/telemetry` is one self-consistent JSON snapshot — prefer it
  for control loops.
- **Writes** actuate the game: `ctl/…` (per-vessel control — incl. `ctl/translate`, bang-bang RCS
  translation by body-axis signs: `1 0 0` = thrust along the nose, latches until `0 0 0`), per-module
  files (`engines/<n>/active`,
  `lights/<n>/on`, …), and `debug/…` (cheats: teleport/impulse/refuel/warp/switch). A write is line-buffered
  and actuates on the newline; failures return `EINVAL`/`EACCES`/`EBUSY`/`ETIMEDOUT`/…
- **A write blocks until the game thread executes it — one command per frame.** Sequential writes can
  therefore *never* land in the same tick (a formation teleport smears ~100 m per frame gap at orbital
  speed). To make N writes execute in the **same tick**, write them as one group to `/sim/ctl/batch`:
  one `<path> <value>` line per command, then a `commit` line (SPEC §3.10). Atomic, in order, one
  phase per batch, ≤64 commands.
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
    parts/<n>/{instance_id,id,display_name,template,is_root,subpart_count,position}
                                        (top-level parts; welds anchor picker; telemetry_vessel_parts)
    ctl/{ignite,shutdown,engine,stage,throttle,lights,rcs,translate,
         attitude_mode,attitude_frame,attitude_target,burn,focus}
events
status/{game_version,sampler,accessors,transports}
ctl/batch                               (atomic same-tick command groups — SPEC §3.10)
audio/{file/<name>,play,set,stop,status,info}         (userland audio; audio_enabled=true)
debug/                                  (only when debug_namespace=true)
    vessels/<id>/{teleport,impulse,refill_fuel,refill_battery,docking/<n>/pushoff_impulse,
                  weld,weld_here,unweld}
    welds/{clear,count,<source>/{target,part,offset,rotation,lock_rotation,enabled}}
    thug_life/{add,clear,count,<id>/{vessel,part,position,rotation,size,visible,remove,spec}}
    always_render_iva   time/warp   focus   control_vessel
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
frame). Full arg shapes, action keys, and errnos are in [SPEC §3.7](../../../SPEC_9P_FILESYSTEM.md); a
worked `weld_here` example is in [`recipes.md` §9](recipes.md) and a `thug_life` example in
[`recipes.md` §10](recipes.md). The render-side internals (how the quad is drawn into KSA's scene) are
documented in the **ksa skill's `quad.md`**.

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
- **`debug.teleport` sets a CCI state about the vessel's *current* parent body** — the vessel must
  already be in the intended body's SOI. See [SPEC §6](../../../SPEC_9P_FILESYSTEM.md).
- **`debug/…/impulse` defaults to newton-seconds** (Δv = J ÷ live mass), not m/s — append the `dv`
  keyword to apply the vector directly as Δv, and `body` to read it in the vessel frame (+X = nose):
  `echo "10 0 0 body dv" > impulse`. Same CCI-about-current-parent frame as teleport otherwise.
- Mass is **kg**; gravity is **μ/r²** (never 9.8); ground-referenced velocity is `v_cci − ω×r`.
- Don't substitute a generic quaternion library for the Body→CCI attitude math — use KSA's exact
  convention (`transform(+X, q) == thrust_direction`); see [`coordinate-frames.md`](coordinate-frames.md).

## Example: "teleport Hunter to a 120 km Earth orbit, Polaris 50 m ahead"

This is fully worked (a copy-pasteable Bun/TS program) in [`recipes.md` §1](recipes.md). The shape:
read `bodies` for Earth's `mu`/`mean_radius`, compute `r = radius + 120000` and `v = sqrt(mu/r)`,
teleport Hunter to CCI `[r,0,0, 0,v,0]` (equatorial circular), and teleport Polaris to the same orbit
advanced in true anomaly by `Δθ = 50/r`. Everything you need to derive it is in the SPEC + frames doc.
