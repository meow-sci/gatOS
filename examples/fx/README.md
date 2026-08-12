# fx / celebrate — face-anchored particle effects

Two Rust CLIs over the gatOS `/sim/debug/fx/` surface, which bursts one-shot particle effects
from KSA's own particle pool, anchored — by default — right in front of an EVA kitten's face.

## fx

```bash
fx party                       # confetti on every kitten
fx danger Hunter               # fire flash on Hunter
fx death Hunter --scale 2      # a big grey puff
fx sparkle --bursts 5 -i 0.25  # a five-volley glitter salvo
fx list                        # profiles + live emitter count
fx clear                       # stop everything now
```

Profiles (authored in the mod, `SimpleColor` renderer, zero asset dependencies):

| Profile | Look | For |
|---|---|---|
| `party` | tumbling confetti chips, wide hue spread, gentle fall | celebrations |
| `sparkle` | tiny HDR gold glitter that catches bloom | small wins |
| `danger` | hot red-orange flash that grows then shrinks, licks upward | trouble |
| `death` | slow grey puff that swells and drifts up | the end |

Options: `--scale <x>` (size/velocity multiplier), `--offset x,y,z` (anchor override, metres,
vessel assembly frame — kittens default to their face at `(0.25, 0, -0.85)`), `--bursts <n>` +
`--interval <s>` (repeat volleys), `--sim <path>` / `--url <base>` (the `/sim` mount, or the
HTTP `/v1/fs` mirror; env `GATOS_SIM` / `GATOS_HTTP`).

With no vessel ids, targets every vessel whose `is_kitten` leaf reads `1`.

## celebrate

The one-word party button — alternating `party`/`sparkle` volleys on every kitten:

```bash
celebrate
celebrate Hunter --volleys 10 --interval 0.25 --scale 1.5
```

## How it works

Each burst writes one line to `/sim/debug/fx/spawn`:

```
<vessel> <profile> [scale <s>] [offset <x> <y> <z>]
```

The mod acquires one emitter from the game's shared particle pool, applies the profile's
hand-built template, anchors it to the vessel (`Context.Vehicle` + assembly-frame
`LocalOffset`, so the effect rides the kitten), and lets it self-retire — every spawn is a
`Burst`, so nothing can leak the pool. `echo 1 > /sim/debug/fx/clear` cuts everything short.

## Requirements

- gatOS with `[control] debug_namespace = true`.
- The game's graphics **Particles** setting on (spawns are refused, not queued, while it's off).
- A mounted `/sim` or the HTTP API.

## Build

```bash
cargo build --release   # target/release/{fx,celebrate}
```
