# nudge

Shift a vessel's **CCI** position by a fixed delta while keeping its velocity, by
reading and writing the gatOS `/sim` filesystem.

It reads the vessel's current CCI position and velocity, adds the `(px, py, pz)` delta
to the position, and teleports it to that new state vector. Because the velocity is
passed through unchanged, the vessel keeps moving exactly as it was — it just gets
"nudged" over by the delta. Handy for sliding an **EVA kitten** along the surface a few
meters at a time.

## Usage

Run it inside the guest (where `/sim` is mounted):

```sh
bun index.ts --vehicle Kitten_1 --px 1.5 --py 0 --pz -2
```

- `--vehicle <id>` — the vessel id (a vessel's name *is* its id; non-`[A-Za-z0-9._-]`
  chars are sanitized to `_`). **Required.**
- `--px --py --pz <double>` — the position delta in meters, in CCI about the vessel's
  current parent body. Any omitted axis defaults to `0`.
- `--sim-root <path>` — the `/sim` mount root (default `/sim`, or `$GATOS_SIM`).

## How it works

It reads two files and writes one (see [`SPEC_9P_FILESYSTEM.md`](../../SPEC_9P_FILESYSTEM.md)):

| File | Direction | Format |
|---|---|---|
| `/sim/vessels/by-id/<id>/position/cci` | read | `x y z` (meters) |
| `/sim/vessels/by-id/<id>/velocity/cci` | read | `x y z` (m/s) |
| `/sim/debug/vessels/<id>/teleport` | write | `px py pz vx vy vz` (CCI) |

The two reads are issued concurrently and the teleport is committed immediately after,
keeping the window between sampling the position and applying the nudge as small as
possible — orbital velocities are large (km/s), so any delay means the vessel has
already moved.

> **CCI** = Celestial-Centered Inertial about the vessel's *current* parent body.
> `teleport` sets the state about whatever body the vessel is already orbiting; it does
> **not** change the parent. Requires `debug_namespace=true` (default) — otherwise the
> write returns `EACCES`. See SPEC §6 and the `gatos` skill.
