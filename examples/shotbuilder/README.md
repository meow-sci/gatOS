# shotbuilder

One camera-shot description in, **two runnable files** out:

| Output | Goes to | What it is |
|---|---|---|
| `<name>.tb` | `/sim/ctl/timed_batch` | the move as **plain `/sim` leaf writes**, pre-sampled onto a timeline |
| `<name>.json` | `/sim/camera/track/<name>` | the move as a **typed camera track**, interpolated host-side |

That duality is the whole point of the example. The gatOS camera is not a special API — it is a
set of ordinary files under `/sim/camera`, and *anything* that can write a file can fly it. A
timed batch drives those leaves from a script; a track hands the same curve to the host to
interpolate every rendered frame. Generating both from one description lets you diff them and see
that they really are the same surface.

## Why TypeScript/Bun

The job is "read a small declarative description, emit JSON and emit text". A JSON-native runtime
with zero build step is exactly the right tool, and Bun is already the repo's host-side scripting
language — `examples/nudge` and `examples/sdk-ts` are both Bun/TS. The Rust examples
(`examples/dancy-party-rs`, `kecho`, `starwars`) are all long-running TUIs where a compiled binary
and `ratatui` earn their weight; a code generator is neither. No dependencies, no `bun install`.

## Run the generator

```sh
cd examples/shotbuilder
bun run index.ts shots/flyby.json --out out
```

```
flyby: 2 move(s), 14s
  out/flyby.tb
  out/flyby.json
```

## The shot description

`shots/flyby.json` — a half-orbit sweep around a vessel, then a push-in down its nose, both aimed
1.2 m above its origin:

```json
{
  "name": "flyby",
  "anchor": "vessel:apollo11",
  "frame": "bodyfixed",
  "sampleHz": 20,
  "aim": { "target": "vessel:apollo11", "offset": [0, 0, -1.2], "frame": "bodyfixed", "up": "world" },
  "moves": [
    { "name": "sweep",   "duration": 8, "ease": "in-out",
      "orbit": { "radius": 120, "azimuth": [0, 180], "elevation": -20 }, "fov": 42 },
    { "name": "push-in", "duration": 6, "ease": "out",
      "position": [[-40, 0, -6], [-12, 0, -3]], "fov": [42, 24] }
  ]
}
```

The one rule to learn: **a scalar holds a channel, a `[from, to]` pair animates it.** `"fov": 42`
is a constant; `"fov": [42, 24]` is a 6-second push-in. Same for `[[x,y,z],[x,y,z]]` on `position`.

The numbers are in the vessel's own `bodyfixed` axes — **+X nose, +Y right, −Z up**, the same triad
the RCS translation control uses. So `-40 0 -6` is 40 m behind the nose and 6 m above, and orbit
`elevation` (measured toward **+Z**) is *negative* to put the camera overhead.

`sampleHz` affects **only** the timed batch — the track needs no sampling, because the host
interpolates it. Oversampling is cheap: the scheduler coalesces to the *last* write per leaf per
tick, so a 20 Hz script costs one write per leaf per frame no matter how fine you make it.

## Run the shot in the guest

Host-side folders show up in the guest at `/mnt/<name>` through the existing folder passthrough, so
there is nothing to install and no new gatOS code involved. Point a host mount at `out/`, then:

### Route A — the track (L3)

```sh
cp /mnt/shots/flyby.json /sim/camera/track/flyby   # upload; commit parses + validates it
echo 1 > /sim/camera/enabled                       # take the camera
echo "flyby" > /sim/camera/play                    # roll
```

Once you own the camera, iterating is one line — re-run the generator on the host, then:

```sh
cp /mnt/shots/flyby.json /sim/camera/track/flyby && echo "flyby" > /sim/camera/play
```

Scrub, pause and re-rate it live while it plays:

```sh
echo "t 4 rate 0.25" > /sim/camera/set
echo 1 > /sim/camera/stop
cat /sim/camera/playback          # <state> <t_ms> <duration_ms> <shot> <index> <rate> <loop>
```

> A track **is** a player in the schedule registry, so `camera/play` needs
> `[schedule] schedule_enabled` (the default) — with scheduling off it answers `EOPNOTSUPP` and names
> route B as the alternative. A malformed track is rejected at commit, before `play` ever sees it.

### Route B — the timed batch (L1/L2)

```sh
cat /mnt/shots/flyby.tb > /sim/ctl/timed_batch     # validates and starts on commit
cat /sim/ctl/schedules/flyby/state                 # pending | running | paused | done | failed
```

The generated script is exactly what you would have typed by hand:

```
@id flyby
@clock render
@loop 0

0 camera/enabled 1
0 camera/pose/frame bodyfixed
0 camera/pose/anchor vessel:apollo11
0 camera/pose/aim vessel:apollo11 off 0 0 -1.2 frame bodyfixed up world
0 camera/pose/orbit/radius 120
0 camera/pose/orbit/azimuth 0
…
8000 camera/pose/orbit/azimuth 180
8000 camera/pose/orbit/radius 0
8000 camera/pose/position -40 0 -6 bodyfixed
…
14000 camera/enabled 0
commit
```

Everything in it is a path you can also just `echo` into by hand — which is the point.

## Things the generator gets right so you don't have to

- **Ownership.** `camera/enabled 1` leads, `camera/enabled 0` (an eased hand-back over
  `[camera] camera_release_blend_s`) closes. Distinct paths in one tick keep their authored order,
  so the take lands before the writes that need it. Use `camera/release` instead for a hard cut.
- **Placement precedence is orbit → geodetic → cartesian.** A non-zero `pose/orbit/radius` keeps
  winning over `pose/position`, so a move that switches from an orbit to a dolly gets an explicit
  `camera/pose/orbit/radius 0` first.
- **Easing matches.** `linear`/`in`/`out`/`in-out` are reproduced from `gatOS.SimFs/Camera/Easing.cs`
  (default power 3), so the pre-sampled batch traces the same path the track interpolates.
- **`aim` is constant within a move**, on purpose — the host re-resolves it against the *live*
  target every frame, which is what makes `off 0 1.2 0` stay glued to a moving subject.
- **Only authored channels are emitted.** A move that drives `fov` alone leaves position, roll and
  the orbit channels to whatever else is driving them. Undeclared channels fall through to your
  live leaf writes and then to the baseline the camera had when gatOS took it.

## Reference

The full camera surface — every path, format, action key and errno — is in
[`plans/CAMERA_ASBUILT.md`](../../plans/CAMERA_ASBUILT.md); the timed-batch grammar is in
[`plans/SCHEDULER_ASBUILT.md`](../../plans/SCHEDULER_ASBUILT.md) §5.
