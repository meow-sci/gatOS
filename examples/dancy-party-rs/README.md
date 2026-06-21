# dancy-party-rs (Rust TUI example)

🪩 A **party-lights console** for gatOS, built with [ratatui](https://ratatui.rs). Pick some vessels,
build a palette of colors, and turn every light on those vessels into a synchronized, cross-fading,
strobing **dance party** — driven entirely by writing the `/sim` light files.

Where [`gogogo-rs`](../gogogo-rs) is a throttle quadrant and [`simfs-dashboard`](../simfs-dashboard)
is a build-your-own dashboard, this one is pure, gratuitous fun over the same filesystem API.

## What it does

Two screens:

1. **Vessels** — a multi-select list of every vessel that has lights (with its light count). `space`
   arms/disarms a vessel; `a` toggles all; `r` rescans; `Enter` (or `p`) goes to the party.
2. **Party** — build and run the show:
   - **Palette** — an ordered list of colors, each shown as a `████` swatch + hex + RGB. Add a color
     by typing **RGB 0-255** (`255 128 0`) or **HTML hex** (`#ff8000`), or pop the **XKCD fuzzy
     picker** (`f`) — all 949 of KSA's bundled XKCD survey colors, searchable with a live preview
     swatch (space-separated terms are AND-ed, case-insensitive — same search as `simfs-dashboard`).
     Reorder with `[` / `]`, remove with `d`.
   - **Time per color** — how long each color lasts (default **1200 ms**), editable with `[-]`/`[+]`
     or by typing (`e`). Floor is 50 ms.
   - **LETS PARTY! / STOP, MY EYES** — the toggle. On, it animates; off, it resets every light to
     white and stops.

While the party runs, the colors **cross-fade** from one palette entry to the next over each color's
time slot, updated at **`--hz`** (default 60 Hz), and the light **deploy animation** pulses — the
`goal` flips `1`→`0`→`1` on every color step — so the lights physically animate as the colors change.
A live band at the bottom mirrors the current interpolated color so you can see (a calmer copy of)
what your eyes are enduring.

It floats over the game: backgrounds are left unset so purrTTY shows the sim through the text; only
the bars, modal popups, and the live color band paint anything.

## Data interface — the `/sim` filesystem

Everything is driven through the lights under each vessel (see
[`SPEC_9P_FILESYSTEM.md` §3.4.11](../../SPEC_9P_FILESYSTEM.md)):

| what                | path                                        | write              |
| ------------------- | ------------------------------------------- | ------------------ |
| light color         | `vessels/by-id/<id>/lights/<n>/color`       | `r g b`, each 0..1 |
| light animation goal| `vessels/by-id/<id>/lights/<n>/goal`        | `0` / `1`          |
| discovery           | `vessels/by-id/` + each `lights/<n>/`       | — (read once)      |

- The light tree is **walked once at startup and cached** — re-reading a 9p directory tree has a real
  cost, and the wiring doesn't change mid-flight. Press `r` on the vessel screen to rescan (e.g. after
  launching a new craft).
- During the party the worker thread **broadcasts** each frame: the interpolated color to *every*
  selected `…/color` file and, on each color step, the new goal to *every* `…/goal` file — the
  equivalent of `echo <r g b> > …/color` and `echo 1 > …/goal` done as real file writes, for all
  lights at once. (Colors are written only when the quantized value actually changes, so a static
  one-color palette doesn't spam writes.)
- **In the guest (default):** it reads/writes the real `/sim` mount with `std::fs`. No flags needed.
- **On the host (dev):** pass `--url $GATOS_HTTP` to drive the mod's HTTP `/v1/fs/<path>` mirror — the
  same paths over slirp. (Auto-selected when `/sim` is absent and `$GATOS_HTTP` is set.) HTTP has no
  directory listing, so discovery probes `lights/<n>/…` per vessel until the ordinals run out.

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo run --release                # reads /sim, drives the selected vessels' lights
```

On the host (for dev), point it at the mod's HTTP server, or at a `/sim`-shaped fixture directory:

```sh
cargo run -- --url http://127.0.0.1:4242/v1   # HTTP /v1/fs mode
cargo run -- --root ./some-fixture            # any directory laid out like /sim
cargo run -- --hz 30                          # gentler color update rate (default 60)
```

```
USAGE: dancy-party-rs [--root <dir> | --url <base>] [--hz <n>] [--steps <n>] [--async [--writers <n>]]
```

`--hz` is how often the cross-fade color is rewritten while partying (1..240, default 60). On a busy
rig with many lights, lower it to cut the write rate; the second freshness gate is still the in-game
`sample_rate_hz` (how fast the host samples the game).

### Tuning the write load (perf experiments)

Each color step is broadcast as a real file write to **every** selected light, so with a lot of lights
the fade can visibly lag. The `/sim` 9p bridge has its own refresh cadence and a per-write cost that
isn't known up front — these flags exist to **measure and isolate** that cost. The party screen shows
a live readout: total writes, **per-write latency** (avg / max / last), and, in async mode, the pool
**backlog** and **dropped**-broadcast count.

| flag | what it changes | why it matters |
| ---- | --------------- | -------------- |
| `--steps <n>` | Quantize each fade to `<n>` discrete colors per segment (0 = continuous, the default; `1` = hard cut, no fade). | Caps the number of **distinct** color writes per color step. The biggest lever on write *volume* — a smooth-looking fade may be writing dozens of near-identical colors a second. |
| `--stagger-ms <n>` | Offset each light by `n` ms so the palette **ripples** across the lights instead of all changing at once (0 = lockstep, the default). | A visual effect, but also spreads each color step's writes out over time instead of bursting them. With it on, lights animate independently (per-light dedupe), so the total write volume rises — pair it with `--async`. |
| `--async` | Hand writes to a background thread **pool** instead of blocking the animation loop on each one. | Tests whether the bottleneck is *per-write latency serialized across lights*. If sync stalls but async keeps up (low backlog), the writes are slow but parallelizable. |
| `--writers <n>` | Async pool width (1..64, default 8). | How much write parallelism the 9p server actually benefits from before it saturates. |

Reading the readout while partying:

- **`lat avg/max`** — the real cost of one `echo … > …/color`. This is the number to compare across
  transports (in-guest `/sim` vs host `--url` HTTP) and across `sample_rate_hz` settings.
- **`backlog`** (async only) — writes still queued. Steadily climbing backlog + rising **`dropped`**
  means the writes can't keep up at this `--hz` × light-count; lower `--hz`, lower `--steps`, or raise
  `--writers`.

```sh
cargo run -- --steps 8              # at most 8 colors per fade — far fewer writes, still smooth-ish
cargo run -- --steps 1             # pure color-cycle, no interpolation (minimum writes)
cargo run -- --async               # non-blocking writes, 8-thread pool
cargo run -- --async --writers 24  # wider pool for a rig with many lights
cargo run -- --stagger-ms 80 --async   # rippling wave across the lights, non-blocking writes
cargo run -- --hz 20 --steps 4 --async   # combine to hunt the bottleneck
```

Colors are deduped per light: a light is only rewritten when *its own* quantized color actually
changes, so `--steps` bounds the color-write rate regardless of `--hz`. With `--stagger-ms 0` (the
default) every light shares one color each tick, so this collapses to a single deduped broadcast —
exactly the original lockstep behaviour. A non-zero stagger gives each light its own clock (light `i`
runs `i × stagger-ms` behind the lead), so they ripple — at the cost of more total writes, since they
no longer share a value. (Goal pulses still fire on each light's color step.) When you stop the party,
the async queue is drained before the lights are reset to white, so nothing lands stale.

## Controls

- **Vessels:** `↑`/`↓` (or `j`/`k`) move · `space` arm/disarm · `a` all · `r` rescan ·
  `Enter`/`p` party · `q` quit. Click a row to arm it.
- **Party:** `Tab` cycle focus (palette / time / button) · `Enter` or `P` toggle the party · `b`/`Esc`
  back · `q` quit.
  - *Palette focus:* `↑`/`↓` select · `[`/`]` (or `Shift+↑`/`Shift+↓`) reorder · `a` add RGB/hex ·
    `f` XKCD picker · `d`/`Del` remove. Click the per-row `[↑] [↓] [✕]` buttons too.
  - *Time focus:* `←`/`→` (or `-`/`=`) step ±100 ms · `e` type a value.
- **Add-color modal:** type an RGB triple or hex, `Enter` to add, `Tab` to jump to the XKCD picker.
- **XKCD picker:** type to fuzzy-filter (space = AND), `↑`/`↓`/scroll to browse, `Enter` to add.

## No TUI required

The lights are plain files. To make one vessel's lights flash red then blue by hand:

```sh
for n in /sim/vessels/by-id/Hunter/lights/*/; do
  echo "1 0 0" > "$n/color"; echo 1 > "$n/goal"; sleep 1
  echo "0 0 1" > "$n/color"; echo 0 > "$n/goal"; sleep 1
done
```

…or the HTTP twins, `curl -X POST "$GATOS_HTTP/fs/vessels/by-id/Hunter/lights/0/color" -d "1 0 0"`.
This console is just a friendly, interpolating, multi-vessel face over that surface.

## Regenerating the XKCD palette

`src/xkcd.rs` is generated from KSA's `KSAColor.Xkcd` block by `tools/gen_xkcd.ts` (run with
[Bun](https://bun.sh) from the repo root: `bun examples/dancy-party-rs/tools/gen_xkcd.ts`). The
generated file is committed, so you don't need Bun or the decompiled sources to build the example.
