---
name: gatos
description: >-
  Write scripts and programs against gatOS — the KSA mod that exposes live Kitten Space Agency
  simulation state as a 9P filesystem at /sim (also over HTTP /v1 and MQTT). Use this when asked to
  read game/celestial/vehicle telemetry, control vehicles (throttle, ignite, staging, attitude,
  burns, RCS, lights, docking), use game/debug controls (teleport, refuel, time-warp, switch
  vessel), or write flight-computer / autopilot programs. Covers the full /sim catalog, the command
  model, KSA coordinate frames, and worked Bun/TypeScript + Rust examples.
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
- **Writes** actuate the game: `ctl/…` (per-vessel control), per-module files (`engines/<n>/active`,
  `lights/<n>/on`, …), and `debug/…` (cheats: teleport/refuel/warp/switch). A write is line-buffered
  and actuates on the newline; failures return `EINVAL`/`EACCES`/`EBUSY`/`ETIMEDOUT`/…
- **Two attitude paths:** write a named mode to `ctl/attitude_mode` (`Prograde`, `Retrograde`, …) and
  the onboard autopilot steers (warp-correct, no math) — *or* compute a **Body→CCI quaternion** and
  write `ctl/attitude_target` for a custom direction. Attitude/burn writes are **solver-phase** (take
  effect next solver step; run ~1× warp for closed loops).
- **CCI is the working frame:** `position/cci`, `velocity/cci`, `attitude/quat`, `debug/teleport`, and
  `ctl/burn` Δv are all **Celestial-Centered Inertial about the parent body** (Z = north pole,
  X = vernal point, equatorial X–Y plane). Constants come from `bodies/<parent>/{mu,radius,rotation_rate}`.
- **Pace in sim time, not wall time:** block on `time/alarm` (or `GET /v1/time/wait`); gate on
  `time/sim_dt==0` (paused) and `time/warp>1` (warping).

## Access at a glance

```sh
# In the guest (the /sim mount):
cat   /sim/vessels/active/telemetry            # read the atomic vessel doc
echo 0.5 > /sim/vessels/active/ctl/throttle    # actuate (returns errno on failure)
echo "6578100 0 0 0 7784 0" > /sim/debug/vessels/Hunter/teleport   # CCI state vector

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
    id name situation parent controlled controllable com telemetry stream
    position/{cci,ecl,lat,lon}  velocity/{orbital,surface,inertial,cci}
    attitude/{quat,rates}  altitude/{barometric,radar}  mass/{total,dry,propellant}
    orbit/*  navball/*  environment/*  battery/*  power/*
    engines/<n>/*  tanks/<r>/*  rcs/<n>/*  solar/<n>/*  generators/<n>/*
    lights/<n>/*  docking/<n>/*  decouplers/<n>/*  animations/<n>/*  encounters
    ctl/{ignite,shutdown,engine,stage,throttle,lights,rcs,
         attitude_mode,attitude_frame,attitude_target,burn,focus}
events
status/{game_version,sampler,accessors,transports}
debug/                                  (only when debug_namespace=true)
    vessels/<id>/{teleport,refill_fuel,refill_battery,docking/<n>/pushoff_impulse}
    time/warp   focus   control_vessel
```

Each leaf's format, units, archetype (read-only vs control vs trigger), and backing command action
are in [`SPEC_9P_FILESYSTEM.md`](../../../SPEC_9P_FILESYSTEM.md). Per-module dirs appear only when the
vessel actually has that module; `bodies`/detail dirs depend on telemetry config gates.

## Gotchas that cause silent failures

- A vessel's **name is its id** (`Hunter`, `Polaris` are literal ids). Ids in `/sim` paths are
  sanitized (non-`[A-Za-z0-9._-]` → `_`).
- `control_enabled=false` → all writes `EACCES`. `control_all_vessels=false` → only the controlled
  vessel is commandable. `debug_namespace=false` → `/sim/debug/**` is gone. All default on.
- **`debug.teleport` sets a CCI state about the vessel's *current* parent body** — the vessel must
  already be in the intended body's SOI. See [SPEC §6](../../../SPEC_9P_FILESYSTEM.md).
- Mass is **kg**; gravity is **μ/r²** (never 9.8); ground-referenced velocity is `v_cci − ω×r`.
- Don't substitute a generic quaternion library for the Body→CCI attitude math — use KSA's exact
  convention (`transform(+X, q) == thrust_direction`); see [`coordinate-frames.md`](coordinate-frames.md).

## Example: "teleport Hunter to a 120 km Earth orbit, Polaris 50 m ahead"

This is fully worked (a copy-pasteable Bun/TS program) in [`recipes.md` §1](recipes.md). The shape:
read `bodies` for Earth's `mu`/`mean_radius`, compute `r = radius + 120000` and `v = sqrt(mu/r)`,
teleport Hunter to CCI `[r,0,0, 0,v,0]` (equatorial circular), and teleport Polaris to the same orbit
advanced in true anomaly by `Δθ = 50/r`. Everything you need to derive it is in the SPEC + frames doc.
