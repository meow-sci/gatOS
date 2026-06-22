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
     or by typing (`e`). Floor is 50 ms. (This is one of the knobs in the settings popup, surfaced on
     the screen for convenience.)
   - **Battery + refill** — an aggregate battery meter across the armed vessels, with a **`[ refill ⚡ ]`**
     button next to it (key `g`) that tops every armed vessel's battery back up (`debug.refill_battery`).
   - **Settings (`s`)** — a popup with every display knob, all live (a running party adopts changes at
     once): **frame rate**, **fade steps**, **color time**, **anim time**, **color stagger**,
     **anim stagger**, and the random **brightness** range / time / steps. See [Settings](#settings)
     below.
   - **LETS PARTY! / STOP, MY EYES** — the toggle. On, it animates; off, it resets every light to
     white and stops.
   - **Save profile (`w`)** — prompt for a name and write the palette + every setting to a reusable
     YAML file (the armed vessels are *not* saved). Reload it next run with `--profile <name>`. See
     [Profiles](#profiles).
   - **Hide (`h`)** — collapse the whole UI to a single status bar so a running party doesn't block
     the game. See [Hide mode](#hide-mode).

While the party runs, the colors **cross-fade** from one palette entry to the next on the **color
clock**, and the light **deploy animation** pulses on a separate **animation clock** — the `goal`
flips `1`→`0`→`1` — so the lights physically extend/retract. The two clocks are **independent**: the
in-game deploy stroke is ~2 s, so the animation clock defaults slower (2500 ms) than the color clock
(1200 ms), letting each stroke finish instead of being cut off by a fast color change. A live band at
the bottom mirrors the current interpolated color (and both clocks' segment counters) so you can see a
calmer copy of what your eyes are enduring.

It floats over the game: backgrounds are left unset so purrTTY shows the sim through the text; only
the bars, modal popups, and the live color band paint anything.

## Data interface — the `/sim` filesystem

Everything is driven through the lights under each vessel (see
[`SPEC_9P_FILESYSTEM.md` §3.4.11](../../SPEC_9P_FILESYSTEM.md)):

| what                | path                                        | write              |
| ------------------- | ------------------------------------------- | ------------------ |
| light color         | `vessels/by-id/<id>/lights/<n>/color`       | `r g b`, each 0..1 |
| light animation goal| `vessels/by-id/<id>/lights/<n>/goal`        | `0` / `1`          |
| battery charge      | `vessels/by-id/<id>/battery/charge`         | — (read, 0..1)     |
| battery refill      | `debug/vessels/<id>/refill_battery`         | `1` (needs debug)  |
| discovery           | `vessels/by-id/` + each `lights/<n>/`       | — (read once)      |

- The light tree is **walked once at startup and cached** — re-reading a 9p directory tree has a real
  cost, and the wiring doesn't change mid-flight. Press `r` on the vessel screen to rescan (e.g. after
  launching a new craft).
- During the party the worker **dispatches** each frame's changed writes: the interpolated color to
  every selected `…/color` file whose quantized value changed, and, on each animation step, the new
  goal to every `…/goal` file — the equivalent of `echo <r g b> > …/color` and `echo 1 > …/goal` done
  as real file writes. (Colors are deduped per light, so a static one-color palette doesn't spam
  writes.) Writes are **fire-and-forget**: the worker hands each to the runtime and never waits on the
  result. The gatOS backend batches writes per game tick, so a write "response" is up to a whole frame
  (~16 ms) away — this console doesn't care whether a given write has landed yet, only that the
  animation timing stays crisp, and the concurrent in-flight writes all land in the same tick's batch.
- The battery meter reads `vessels/by-id/<id>/battery/charge` for each armed vessel; the refill button
  writes `1` to `debug/vessels/<id>/refill_battery` (so it needs `/sim/debug` enabled — otherwise the
  write returns `EACCES`, surfaced on the status line).
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
cargo run -- --color-ms 800 --anim-ms 3000    # faster color, slower (completing) deploy
```

```
USAGE: dancy-party-rs [--root <dir> | --url <base>] [--profile <name>] [timing seeds]
```

Load a saved show with `--profile`, then arm vessels for it:

```sh
cargo run -- --profile showtime               # restore palette + all settings (pick vessels in-app)
cargo run -- --profile ./shows/disco.yaml     # a path is used as-is
cargo run -- --profile showtime --color-ms 600  # a flag after --profile overrides that one knob
```

All of the timing flags below are just **seed values** — every one is editable live in the **settings
popup** (`s`) on the party screen, and a running party adopts the change at once (no restart). The
second freshness gate, as always, is the in-game `sample_rate_hz` (how fast the host samples the game).

### Settings

Press `s` on the party screen. `↑`/`↓` pick a row, `←`/`→` adjust (hold **Shift** for a coarse step),
`Esc` closes. Scroll over the popup adjusts the highlighted row. Press **`Enter`** on a row for a
manual-entry popup where you can type an exact whole number (0 or higher) — handy when the stepping is
too coarse or too slow; the value is clamped to that setting's range.

| setting | seed flag | default | what it does |
| ------- | --------- | ------- | ------------ |
| **frame rate** | `--hz <n>` | 30 | Animation frame rate (the worker's dispatch cadence), 1..240 Hz. |
| **fade steps** | `--steps <n>` | 0 (continuous) | Quantize each fade to `<n>` discrete colors per segment (`1` = hard cut, no fade). The biggest lever on write *volume* — a smooth fade can write dozens of near-identical colors a second; this caps the **distinct** writes per color segment. |
| **color time** | `--color-ms <n>` | 1200 | How long each palette color lasts before fading to the next, ms. |
| **anim time** | `--anim-ms <n>` | 2500 | The deploy goal-pulse half-period, ms — the deploy animation runs on its **own** clock, independent of the color. Keep it ≥ the ~2 s in-game stroke so each extend/retract actually completes (the original bug: a fast shared clock cut the deploy off before it finished). |
| **color stagger** | `--color-stagger-ms <n>` | 0 (lockstep) | Offset each light's **color** clock by `n` ms so the palette ripples across the lights. |
| **anim stagger** | `--anim-stagger-ms <n>` | 0 (lockstep) | Offset each light's **animation** clock by `n` ms so the deploy pulse ripples across the lights — independently of the color stagger. |
| **bright min** | `--bright-min <n>` | 10000 (off) | Floor of the random per-light **brightness** range, on a `0..10000` scale (10000 = full; the value is divided by 10000 to get the actual color multiplier). Step 1, coarse (Shift) 20. |
| **bright max** | `--bright-max <n>` | 10000 (off) | Ceiling of the random brightness range, `0..10000`. **Set `min < max` to enable** the effect: each light drifts between independent random brightness targets drawn from `[min, max]`, dimming the palette color so the rig twinkles. `min == max` disables it (constant brightness = that value ÷ 10000). |
| **bright time** | `--bright-ms <n>` | 600 | How long each random brightness target holds before drifting to the next, ms. |
| **bright steps** | `--bright-steps <n>` | 0 (continuous) | Quantize the brightness drift to `<n>` discrete values per segment — same write-volume lever as **fade steps**, but for brightness. |

The two clocks and their two staggers are fully independent: you can ripple the color while every light
deploys in lockstep, fade fast while the deploy stays slow enough to finish, or any combination.

```sh
cargo run -- --steps 8                          # at most 8 colors per fade — far fewer writes
cargo run -- --steps 1                          # pure color-cycle, no interpolation (minimum writes)
cargo run -- --color-ms 600 --anim-ms 3000      # snappy color, deploys that fully extend/retract
cargo run -- --color-stagger-ms 80              # a colour wave rippling across the lights
cargo run -- --anim-stagger-ms 200              # a deploy wave, colour still in lockstep
cargo run -- --bright-min 2000 --bright-max 10000   # random per-light brightness twinkle (0..10000)
cargo run -- --bright-min 3000 --bright-max 10000 --bright-ms 200 --bright-steps 4   # snappier, quantized
```

Colors are deduped per light: a light is only rewritten when *its own* quantized color actually
changes, so **fade steps** bounds the color-write rate regardless of frame rate. With a stagger of `0`
(the default) every light shares one value each tick, so it collapses to a single deduped broadcast; a
non-zero stagger gives each light its own clock (light `i` runs `i × stagger` behind the lead) so they
ripple — at the cost of more total writes. When you stop the party, in-flight writes are briefly
drained before the lights are reset to white, so nothing lands stale.

## Profiles

A **profile** is a reusable snapshot of the *look* of a party — the ordered palette plus every
setting (frame rate, fade steps, both clock durations, both staggers) — serialized to a small,
hand-editable YAML file. The **selected vessels are deliberately not saved**: a profile is the
look-and-feel of the show, and you re-pick which craft it plays on each run.

- **Save:** press `w` on the party screen, type a name, `Enter`. The status line reports the file
  written.
- **Load:** start with `--profile <name>`. It restores the palette and all settings; you then arm
  vessels on the vessel screen and party as usual. Any timing flag *after* `--profile` overrides that
  one knob (the profile is applied first, then the flags).

A bare name resolves to `<profiles_dir>/<name>.yaml`; an argument that looks like a path (has a `/` or
`\`, or ends in `.yaml`/`.yml`) is used verbatim. The profiles directory is `$DANCY_PROFILE_DIR` if
set, else `~/.dancy-party/profiles` (`$HOME`, or `%USERPROFILE%` on Windows), else `./dancy-profiles`.

The file is plain YAML — edit or version-control it by hand:

```yaml
# dancy-party-rs profile
hz: 30
steps: 0
color_ms: 1200
anim_ms: 2500
color_stagger_ms: 0
anim_stagger_ms: 0
bright_min: 10000
bright_max: 10000
bright_ms: 600
bright_steps: 0
colors:
  - "#ff0000"
  - "#00ff00"
```

## Hide mode

Press `h` on the party screen to **hide** the entire UI down to a single status bar (`h` or `Esc`
restores it). The bar shows the party state and the aggregate battery meter, and keeps three live
buttons — **start/stop the party**, **refill battery**, and **show** — so you can run and steer the
show while the rest of the screen stays transparent and the game plays through unobstructed. The
typical flow: set up the palette and timing (or load a `--profile`), arm your vessels, then `h` to get
out of the way once the lights are dancing.

In hide mode `Enter`/`P` toggles the party, `g` refills, `h`/`Esc` shows the full UI again, and `q`
quits — or click the bar's buttons.

## Controls

- **Vessels:** `↑`/`↓` (or `j`/`k`) move · `space` arm/disarm · `a` all · `r` rescan ·
  `Enter`/`p` party · `q` quit. Click a row to arm it.
- **Party:** `Tab` cycle focus (palette / time / button) · `Enter` or `P` toggle the party ·
  `s` settings · `g` refill battery · `w` save profile · `h` hide · `b`/`Esc` back · `q` quit.
  - *Palette focus:* `↑`/`↓` select · `[`/`]` (or `Shift+↑`/`Shift+↓`) reorder · `a` add RGB/hex ·
    `f` XKCD picker · `d`/`Del` remove. Click the per-row `[↑] [↓] [✕]` buttons too.
  - *Time focus:* `←`/`→` (or `-`/`=`) step the color time ±100 ms · `e` type a value.
  - Click the `[ refill ⚡ ]` and `[ settings ]` buttons too.
- **Settings popup:** `↑`/`↓` pick a row · `←`/`→` adjust (Shift = coarse) · scroll to adjust ·
  `Enter` type an exact value · `Esc`/`s` close. Click a row to select it.
- **Setting-input popup:** type a whole number (0 or higher), `Enter` to apply (clamped to the row's
  range), `Esc` to go back.
- **Add-color modal:** type an RGB triple or hex, `Enter` to add, `Tab` to jump to the XKCD picker.
- **XKCD picker:** type to fuzzy-filter (space = AND), `↑`/`↓`/scroll to browse, `Enter` to add.
- **Save-profile modal:** type a name, `Enter` to write, `Esc` to cancel.
- **Hide bar:** `Enter`/`P` toggle party · `g` refill · `h`/`Esc` show · `q` quit (or click).

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
