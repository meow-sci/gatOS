# astroterm-args-ts

Point [`astroterm`](https://github.com/da-luce/astroterm) — a terminal star map — at **the same
patch of sky the KSA player is looking at in-game**, by reading live state from gatOS `/sim`.

`astroterm` is a ground-observer planetarium: give it a **latitude, longitude, and UTC datetime** and
it draws the hemisphere centered on that observer's local zenith at that time. It has no "look
direction." So this tool turns the in-game look direction into the geographic point on the home body
whose zenith points that way, and feeds astroterm that observer plus `datetime = game_epoch + ut`.

> Full derivation and feasibility analysis: **`../../ASTROTERM_PLAN.md`**.

## How it works

1. Read the look direction from `/sim` — by default the vessel's local **"up"** (`position/cci`),
   or its **nose** (`--view nose`, the attitude quaternion applied to body +X).
2. Express it as the home body's **geographic latitude/longitude** (the ground point whose zenith
   points that way). For an Earth-parented vessel looking up that's just its own ground point
   (`position/lat`/`position/lon`); for Luna or the nose it's a *virtual* Earth ground point, found by
   rotating the direction through the published body orientations
   (`bodies/<id>/orientation/cci_to_ecl` and `ccf_to_ecl`) — pure quaternion algebra, no integration.
3. Emit `astroterm -a <lat> -o <lon> -d <game_epoch + ut> …`. astroterm's own GMST rotates the sky as
   game time advances.

### `--game-epoch` — the alignment knob

The in-game star field sits at a fixed rotation relative to astroterm's J2000 catalog, and that
rotation **depends on which star data is loaded**. You calibrate it with one number: the UTC instant
that game time 0 maps to. Shifting `--game-epoch` rotates astroterm's whole sky in right ascension
(15° per hour), so you tune it once per star-data set until the constellations line up.

- Default: `2025-11-30T00:00:00` (JD 2461009.5 — KSA's documented default epoch).
- J2000-aligned replacement star data: pass `--game-epoch 2000-01-01T11:58:55.816` (J2000 is
  2000-01-01T12:00 **TT**, which is `11:58:55.816` **UTC**). Fractional seconds are accepted.

## Prerequisites

- **Bun** (or Node) to run this tool.
- **`astroterm`** on `PATH` where you want the map drawn (inside the purrTTY guest). It's a single
  self-contained binary needing only `ncurses`. (If absent, this tool prints the command instead.)
- gatOS running. In-guest it reads `/sim` directly; on the host set `$GATOS_HTTP` (host mode uses the
  `/v1/fs` field mirror, which needs `http_field_endpoints=true` — the default).

## Usage

```sh
# In the guest, controlled vessel — runs astroterm for the sky overhead right now:
bun run src/index.ts

# J2000-aligned star data, just print the command (e.g. on the host):
bun run src/index.ts --game-epoch 2000-01-01T11:58:55.816 --print

# Center on the vessel's nose instead of straight up:
bun run src/index.ts --view nose

# Keep it tracking game time / motion, relaunching every 10 s:
bun run src/index.ts --watch 10

bun run src/index.ts --help     # all options
```

Diagnostics (transport, the resolved lat/lon + datetime) go to **stderr**; in `--print` mode the
**command line** is the only thing on stdout, so `eval "$(bun run src/index.ts --print)"` works.

## Where to observe from

- **Near-Earth orbit** — `position/cci` over Earth; the sub-satellite point is the observer. Best on
  the night side / pointing away from the Sun so the skybox is visible.
- **Luna's surface** — the nicest: no atmosphere, stars always crisp. Uses the virtual-ground-point
  conversion.
- **Earth surface, night** — works, but daylight/atmosphere hides stars.

## Limitations

- **Stars & constellations match; the Sun/Moon/planets do not** — astroterm places those from real
  ephemerides at the datetime, unrelated to KSA's sim. Use `--constellations` (on by default).
- **Hemisphere only**, **North up**, **no roll/pan** — astroterm always centers the zenith.
- The alignment is only as good as `--game-epoch` is calibrated for your star data (see above).
- Datetime is rounded to whole seconds (astroterm's `-d` granularity); the ~0.5 s ⇒ ~0.002° error is
  far below ASCII-map resolution.

## Tests

```sh
bun test        # game-free: frame round-trips, epoch+ut datetime, Earth/Luna/nose paths
bun run typecheck
```
