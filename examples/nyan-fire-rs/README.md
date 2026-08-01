# nyan-fire-rs (Rust TUI example)

🌈 An **engine-plume colour console** for gatOS, built with [ratatui](https://ratatui.rs). Pick some
volumetric-exhaust templates, build a palette, and drive the plume's four-stop emission gradient as a
scrolling rainbow — with a `--nyan` preset that puts the nyan-cat trail behind your rocket.

Where [`dancy-party-rs`](../dancy-party-rs) makes the *lights* dance, this one repaints the *fire*. It
is a deliberate near-fork of that console — same worker/UI split, same palette editor and XKCD picker,
same profiles, same hide mode — pointed at `/sim/debug/engineplume` instead of the light files. (Every
file diffs 1:1 against its sibling, on purpose.)

## What it does

Two screens:

1. **Templates** — a multi-select list of every loaded volumetric-exhaust template, with how many of
   its four gradient stops resolved and the brightness the game reports. `space` arms/disarms a
   template; `a` toggles all; `c` **captures** that template's four current stops into your palette;
   `r` rescans; `Enter` (or `p`) goes to the show.
2. **Show** — build and run it:
   - **Stripe palette** — an ordered list of colours, each shown as a `████` swatch + hex + its `/sim`
     wire form. Add one by typing **RGB 0-255** (`255 128 0`) or **HTML hex** (`#ff8000`), pop the
     **XKCD fuzzy picker** (`f`) — all 949 of KSA's bundled survey colours, searchable with a live
     preview swatch (space-separated terms are AND-ed) — or capture a template's own gradient with `c`
     on the templates screen. Reorder with `[` / `]`, remove with `d`.
   - **Time per stripe** — how long one palette entry occupies a gradient slot (default **1200 ms**),
     editable with `[-]`/`[+]` or by typing (`e`). Floor is 50 ms.
   - **Read-back + reset** — the four stops (and the brightness) the **game** currently reports for the
     first armed template, with a **`[ reset ⟲ ]`** button next to it (key `g`) that restores every
     armed template to its pristine, pre-gatOS values.
   - **Settings (`s`)** — a popup with every knob, all live (a running show adopts changes at once):
     **frame rate**, **fade steps**, **stripe time**, **slot stagger**, **slot step**, **tpl stagger**,
     and the whole **brightness** block. See [Settings](#settings) below.
   - **LIGHT THE RAINBOW! / CUT THE FIRE** — the toggle. On, it scrolls; off, it resets the armed
     templates and stops.
   - **Save profile (`w`)** — prompt for a name and write the palette + every setting to a reusable
     YAML file (the armed templates are *not* saved). Reload it next run with `--profile <name>`.
   - **Hide (`h`)** — collapse the whole UI to a single status bar so a running show doesn't block the
     game. See [Hide mode](#hide-mode).

The plume's emission gradient has **four spatial stops** — `color0` at the nozzle exit through
`color3` at the plume tip — which the game already blends between *along* the exhaust. So this console
treats your palette as a **cyclic stripe sequence** and shows a moving window over it: at stripe
segment `k`, slot `i` gets `palette[(k + i × slot_step) % len]`. The whole rainbow therefore sits along
the plume at once and **scrolls nozzle-ward** one entry per **stripe time**. An `n`-entry palette
completes a full scroll in `n × stripe time`.

**fade steps `1`** keeps the bands crisp (each slot holds one flat colour for a whole segment, then
snaps); **`0`** cross-fades them smoothly. **slot step `0`** collapses the window so all four stops
share one colour and the plume simply cycles — and, a curiosity: a **slot stagger** of exactly one
stripe time cancels the window offset and does the same thing.

It floats over the game: backgrounds are left unset so purrTTY shows the sim through the text; only the
bars, modal popups, and the live gradient band paint anything. That band is drawn as four equal
quarters — slot 0 leftmost — so it is literally a picture of the plume.

## ⚠️ Templates are SHARED

> A template is **not** one engine. `templates/<id>` is the one `VolumetricExhaustTemplate` with that
> id, so editing it repaints **every nozzle in the universe that uses it** — your booster, the AI
> traffic, the vessel two hundred kilometres away. That is the game's own FX editor's behaviour, not a
> gatOS quirk.
>
> Stopping the show (or quitting) writes `1` to each armed template's `reset`, which restores the
> **pristine, pre-gatOS** values gatOS captured on its first write — this console never hand-restores
> colours, because the only thing that knows the originals is the mod. A reset with nothing recorded is
> a successful no-op. Everything is session-scoped and restored at mod unload too, so a crash can't
> leave your plumes purple forever.

## Data interface — the `/sim` filesystem

Everything is driven through the engine-plume FX editor (see
[`SPEC_9P_FILESYSTEM.md` §3.7](../../SPEC_9P_FILESYSTEM.md)):

| what | path | write |
| ---- | ---- | ----- |
| gradient stop 0 (nozzle exit) | `debug/engineplume/templates/<id>/emission/color0` | `r g b`, each 0..1 |
| gradient stops 1–3 (→ plume tip) | `…/emission/color1` · `color2` · `color3` | `r g b`, each 0..1 |
| overall emissive brightness | `…/emission/brightness` | number 0..200 |
| restore pristine values | `…/reset` | `1` |
| every field of one template | `…/json` | — (read) |
| family readme | `debug/engineplume/help` | — (read) |
| discovery (fs) | `ls debug/engineplume/templates/` | — |
| discovery (HTTP) | `GET /v1/snapshot` → `fx_editors.plume_templates[].id` | — |
| atomic same-tick group | `ctl/batch` | `<path> <value>` lines + `commit` |

- The whole surface needs **`[control] debug_namespace = true`** in `gatos.toml`. When it is off the
  entire `/sim/debug` subtree is **absent**, so writes fail **`ENOENT`** (HTTP 404), not `EACCES` — the
  status line says which config key to flip, and the header carries a `dbg:` badge. `EACCES` (403)
  means `[control] enabled = false` instead: different key, different fix.
- Each frame writes **only the stops whose quantized colour changed** (per-leaf dedupe, so a static
  palette re-broadcasts nothing), and when two or more leaves change they go out as **one
  `/sim/ctl/batch` group** — one write, one command group, one game-tick drain — so all four stops move
  in the *same* tick and the gradient never renders torn. `--no-batch` reverts to one write per leaf.
  The SPEC blesses this shape: *"Writes are cheap enough to drive from a 10–60 Hz light-show loop —
  write only the leaves that changed."*
- Writes are **fire-and-forget**: the worker hands each to the runtime and never waits. gatOS batches
  writes per game tick, so a "response" is a whole frame away and this console only cares that the
  timing stays crisp. On stop, in-flight writes are briefly drained *before* the reset, so nothing
  stale lands after it.
- The read-back row shows what the **game** reports. gatOS re-samples the FX surface on a write or
  every **2 s** otherwise, and the values are 32-bit floats game-side — so `0.6` reads back as
  `0.60000002` and an idle row can be up to 2 s behind. Both are expected; never compare a read-back to
  what you wrote.
- **In the guest (default):** it reads/writes the real `/sim` mount with `std::fs`. No flags needed.
- **On the host (dev):** pass `--url $GATOS_HTTP` to drive the mod's HTTP `/v1/fs/<path>` mirror.
  `/v1/fs` has **no directory listing** (a path that resolves to a directory is a 404), so the template
  roster comes from `GET /v1/snapshot` → `fx_editors.plume_templates[]`; those ids are the **raw** ones,
  so the console sanitizes each into its `/sim` path segment the same way the server names the
  directory. (Which is also why this console never uses `POST /v1/command` — that route would want the
  *raw* id in its token. Address by path and the server does the mapping for you.)

## The `--nyan` preset

```sh
nyan-fire-rs --nyan
```

Six flat stripes — `#ff0000` `#ff9900` `#ffff00` `#33ff00` `#0099ff` `#6633ff` — at **220 ms** each
with **fade steps 1** (hard cut), so the rainbow scrolls a full cycle every 1.3 s with crisp bands and
no muddy in-between hues. Two reasons the hard cut is right: the gradient already blends *spatially*,
so a temporal fade on top blends red→orange→yellow through each other into browns and greys; and a hard
cut caps each template at four colour writes per 220 ms segment instead of a stream bounded only by
`--hz`.

It does **not** touch `emission/brightness`: the authored per-template value is whatever the game ships
(and differs per engine), and `200` — the ceiling — is a bloom blowout, not "full". So the stripes burn
at the template's own brightness. Add a throb with:

```sh
nyan-fire-rs --nyan --bright-min 20 --bright-max 60 --bright-ms 150
```

`--nyan` is applied **at its argument position**, like `--profile` — so `--nyan --color-ms 400` slows it
down, while `--color-ms 400 --nyan` does not.

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo run --release -- --nyan      # reads /sim, repaints the armed templates
```

On the host (for dev), point it at the mod's HTTP server, or at a `/sim`-shaped fixture directory:

```sh
cargo run -- --url http://127.0.0.1:4242/v1   # HTTP /v1 mode
cargo run -- --root ./some-fixture            # any directory laid out like /sim
cargo run -- --steps 1 --color-ms 300         # crisp stripes, a bit slower than --nyan
```

```
USAGE: nyan-fire-rs [--nyan] [--root <dir> | --url <base>] [--profile <name>] [timing seeds]
```

Load a saved show with `--profile`, then arm templates for it:

```sh
cargo run -- --profile rainbow                 # restore palette + all settings (pick templates in-app)
cargo run -- --profile ./shows/nyan.yaml       # a path is used as-is
cargo run -- --profile rainbow --color-ms 600  # a flag after --profile overrides that one knob
```

All of the timing flags below are just **seed values** — every one is editable live in the **settings
popup** (`s`) on the show screen, and a running show adopts the change at once (no restart, and the
stripe clock doesn't jump).

### Settings

Press `s` on the show screen. `↑`/`↓` pick a row, `←`/`→` adjust (hold **Shift** for a coarse step),
`Esc` closes. Scroll over the popup adjusts the highlighted row. Press **`Enter`** on a row for a
manual-entry popup where you can type an exact value, clamped to that row's range. Most rows take a
whole number; the two **bright** rows take a `0..200` decimal (e.g. `42.5`).

| setting | seed flag | default | what it does |
| ------- | --------- | ------- | ------------ |
| **frame rate** | `--hz <n>` | 30 | Animation frame rate (the worker's dispatch cadence), 1..240 Hz. |
| **fade steps** | `--steps <n>` | 0 (continuous) | Quantize each stripe fade to `<n>` discrete values per segment. **`1` = hard cut** — flat bands with crisp edges, the nyan look, and the minimum write volume. The biggest lever on write *rate*: it caps the **distinct** writes per stripe segment regardless of frame rate. |
| **stripe time** | `--color-ms <n>` | 1200 | How long one palette entry occupies a gradient slot, ms. An `n`-entry palette scrolls a full cycle in `n ×` this. |
| **slot stagger** | `--slot-stagger-ms <n>` | 0 (lockstep) | Offset each gradient slot's clock by `n` ms so the scroll ripples across the four stops. Independent of the *window* offset below — set it to exactly the stripe time and it cancels the window (all four stops land on the same colour). |
| **slot step** | `--slot-step <n>` | 1 (rainbow window) | How many palette entries apart adjacent gradient slots sit. `1` gives the 4-wide moving window (the rainbow). `0` collapses it — all four stops share one colour, so the plume is solid and cycles. `2`+ spreads a coarser rainbow across the plume. |
| **tpl stagger** | `--tpl-stagger-ms <n>` | 0 (lockstep) | Offset each *template's* clock by `n` ms so a multi-template show scrolls out of phase instead of in unison. |
| **bright min** | `--bright-min <f>` | 0 | Floor of the `emission/brightness` pulse, on the leaf's **real `0..200` scale** (no hidden division — the number you type is the number written). Step 1, coarse (Shift) 10. |
| **bright max** | `--bright-max <f>` | 0 (**off**) | Ceiling of the pulse. **`<= 0` means OFF**: the leaf is *never written* and each template keeps its authored brightness — that is not the same as writing 0, which would make the plume invisible. `0 < min == max` pins the leaf to that constant; `min < max` makes each template drift between random targets so the exhaust throbs. |
| **bright time** | `--bright-ms <n>` | 600 | How long each brightness target holds before drifting to the next, ms — the brightness clock is fully independent of the stripe clock. |
| **bright steps** | `--bright-steps <n>` | 0 (continuous) | Quantize the brightness drift to `<n>` values per segment — the same write-volume lever as **fade steps**, for brightness. |

Plus one non-row knob: **`--no-batch`**, which turns off the atomic `/sim/ctl/batch` dispatch and writes
each leaf separately (the title bar shows `batch on|off`). It is saved in profiles. Keep it on unless
you are measuring write volume apples-to-apples.

```sh
cargo run -- --nyan                                        # the classic
cargo run -- --steps 0 --color-ms 2000                     # a slow, smooth aurora instead of stripes
cargo run -- --nyan --slot-step 2                          # a coarser rainbow across the plume
cargo run -- --nyan --slot-step 0                          # solid plume that cycles the palette
cargo run -- --nyan --tpl-stagger-ms 300                   # multi-template chase
cargo run -- --nyan --bright-min 20 --bright-max 60 --bright-ms 150   # add a throb
cargo run -- --nyan --no-batch                             # one write per leaf (the dancy-party dispatch)
```

## Profiles

A **profile** is a reusable snapshot of the *look* of a burn — the ordered palette plus every setting
(frame rate, fade steps, the stripe clock, both staggers, the window width, the whole brightness block,
and the batch flag) — serialized to a small, hand-editable YAML file. The **armed templates are
deliberately not saved**: a profile is the look, and you re-pick which plumes it plays on each run.

- **Save:** press `w` on the show screen, type a name, `Enter`. The status line reports the file
  written.
- **Load:** start with `--profile <name>`. It restores the palette and all settings; you then arm
  templates and burn as usual. Any timing flag *after* `--profile` overrides that one knob.

A bare name resolves to `<profiles_dir>/<name>.yaml`; an argument that looks like a path (has a `/` or
`\`, or ends in `.yaml`/`.yml`) is used verbatim. The profiles directory is `$NYAN_PROFILE_DIR` if set,
else `~/.nyan-fire/profiles` (`$HOME`, or `%USERPROFILE%` on Windows), else `./nyan-profiles`.

The file is plain YAML — edit or version-control it by hand:

```yaml
# nyan-fire-rs profile
hz: 30
steps: 1
color_ms: 220
slot_stagger_ms: 0
slot_step: 1
tpl_stagger_ms: 0
bright_min: 0
bright_max: 0
bright_ms: 600
bright_steps: 0
batch: true
colors:
  - "#ff0000"
  - "#ff9900"
  - "#ffff00"
  - "#33ff00"
  - "#0099ff"
  - "#6633ff"
```

## Hide mode

Press `h` on the show screen to **hide** the entire UI down to a single status bar (`h` or `Esc`
restores it). The bar shows the burn state, the armed count, and the four live gradient stops as
swatches, and keeps three live buttons — **start/stop the burn**, **reset**, and **show** — so you can
run and steer the show while the rest of the screen stays transparent and the game plays through
unobstructed. The typical flow: set up the palette and timing (or `--nyan`), arm your templates, then
`h` to get out of the way and go fly.

In hide mode `Enter`/`P` toggles the burn, `g` resets, `h`/`Esc` shows the full UI again, and `q`
quits — or click the bar's buttons.

## Controls

- **Templates:** `↑`/`↓` (or `j`/`k`) move · `space` arm/disarm · `a` all · `c` capture that template's
  four stops into the palette · `r` rescan · `Enter`/`p` show · `q` quit. Click a row to arm it.
- **Show:** `Tab` cycle focus (palette / time / button) · `Enter` or `P` toggle the burn · `s` settings ·
  `g` reset the armed templates · `w` save profile · `h` hide · `b`/`Esc` back · `q` quit.
  - *Palette focus:* `↑`/`↓` select · `[`/`]` (or `Shift+↑`/`Shift+↓`) reorder · `a` add RGB/hex ·
    `f` XKCD picker · `d`/`Del` remove. Click the per-row `[↑] [↓] [✕]` buttons too.
  - *Time focus:* `←`/`→` (or `-`/`=`) step the stripe time ±100 ms · `e` type a value.
  - Click the `[ reset ⟲ ]` and `[ settings ]` buttons too.
- **Settings popup:** `↑`/`↓` pick a row · `←`/`→` adjust (Shift = coarse) · scroll to adjust ·
  `Enter` type an exact value · `Esc`/`s` close. Click a row to select it.
- **Setting-input popup:** type a value (the bright rows accept a decimal), `Enter` to apply (clamped to
  the row's range), `Esc` to go back.
- **Add-color modal:** type an RGB triple or hex, `Enter` to add, `Tab` to jump to the XKCD picker.
- **XKCD picker:** type to fuzzy-filter (space = AND), `↑`/`↓`/scroll to browse, `Enter` to add.
- **Save-profile modal:** type a name, `Enter` to write, `Esc` to cancel.
- **Hide bar:** `Enter`/`P` toggle the burn · `g` reset · `h`/`Esc` show · `q` quit (or click).

## No TUI required

The plume is plain files. To scroll a six-stripe rainbow across one template's gradient by hand:

```sh
T=/sim/debug/engineplume/templates/$(ls /sim/debug/engineplume/templates | head -1)
set -- "1 0 0" "1 0.6 0" "1 1 0" "0.2 1 0" "0 0.6 1" "0.4 0.2 1"
k=0
while :; do
  i=0
  while [ $i -lt 4 ]; do
    n=$(( (k + i) % 6 + 1 ))
    eval "c=\${$n}"
    echo "$c" > "$T/emission/color$i"
    i=$(( i + 1 ))
  done
  k=$(( k + 1 ))
  sleep 0.22
done
# …and when you are done:
echo 1 > "$T/reset"
```

All four stops in one game tick, the way the console does it:

```sh
cat > /sim/ctl/batch <<'EOF'
debug/engineplume/templates/Kerolox/emission/color0 1 0 0
debug/engineplume/templates/Kerolox/emission/color1 1 0.6 0
debug/engineplume/templates/Kerolox/emission/color2 1 1 0
debug/engineplume/templates/Kerolox/emission/color3 0.2 1 0
commit
EOF
```

…or the HTTP twins,
`curl -X POST "$GATOS_HTTP/fs/debug/engineplume/templates/Kerolox/emission/color0" -d "1 0 0"`.
This console is just a friendly, scrolling, multi-template face over that surface.

## Regenerating the XKCD palette

`src/xkcd.rs` is the same generated table [`dancy-party-rs`](../dancy-party-rs) ships — regenerate it
there (`bun examples/dancy-party-rs/tools/gen_xkcd.ts`) and copy the file across. It is committed, so
you need neither Bun nor the decompiled sources to build.
