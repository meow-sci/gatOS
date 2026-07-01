# starwars (Rust TUI example)

A [ratatui](https://ratatui.rs) program that reads text from stdin and scrolls it **bottom-to-top**
smoothly, like the *Star Wars* opening crawl. It's deliberately *just* the flat scrolling-text part —
no 3D perspective/shrink-into-the-distance effect is simulated. Pair it with a purrTTY terminal
window already tilted in the game's 3D space and this program supplies the moving text for it.

## Why braille, not characters

A terminal cell is the coarsest unit ratatui can address — scrolling text one *row* at a time looks
like it's jumping, not gliding. To scroll smoothly, the program rasterizes the (vendored) **Star
Jedi** TTF into a tall coverage bitmap with [`fontdue`](https://docs.rs/fontdue) (pure Rust, no
system font dependency — see `src/canvas.rs`), then downsamples a window of that bitmap into
**Unicode braille characters** (`src/braille.rs`): each cell covers a 2x4 dot matrix, so the canvas
can scroll **one dot at a time** — 4x the vertical resolution of scrolling by character rows — while
still being plain text the terminal already knows how to draw. A lit dot's brightness is shaded by
its source coverage value, which gives glyph edges a cheap anti-aliased look at this resolution.

Backgrounds are never set (only lit dots get a foreground color), matching the rest of this repo's
purrTTY-floating-over-the-game convention: an unstyled cell's default background is left for purrTTY
to render as transparent, so whatever is behind the terminal (the game) shows through every cell this
program doesn't draw a glyph into.

## Usage

```bash
cat crawl.txt | cargo run --release
# or, from a release build:
cat crawl.txt | ./target/release/starwars
```

It reads all of stdin to EOF before starting (so the input can be piped from a static text file),
then scrolls until the whole crawl has exited past the top of the terminal, and exits. `q` / `Esc` /
`Ctrl+C` quit early at any time; resizing the terminal re-wraps and restarts the crawl.

Blank input lines become paragraph gaps; non-blank lines are kept as authored (a crawl's line breaks
are usually hand-placed for pacing) and only soft-wrapped if a line is too wide for the terminal.

### Options

| flag | default | meaning |
| --- | --- | --- |
| `--color <name\|#rrggbb>` | `yellow` | text color (also: white, red, green, blue, cyan, magenta, orange) |
| `--speed <dots/sec>` | `6` | scroll speed, in canvas dots per second |
| `--font-size <dots>` | `24` | glyph size passed to fontdue |
| `--margin <0-45>` | `12` | left/right margin, percent of terminal width |
| `--threshold <0-255>` | `90` | glyph coverage cutoff for a dot to count as "lit" |
| `--fps <n>` | `30` | redraw rate |
| `--uppercase` | off | force input to upper case (Star Jedi is a caps-first display font) |
| `--loop` | off | restart from the bottom once the crawl finishes, instead of exiting |

## The font

`assets/Starjedi.ttf` is vendored (embedded into the binary via `include_bytes!`, no external file
needed at runtime) from the freeware **Star Jedi** font package — the original archive, font guide,
and sibling variants (outline, special-edition, logo styles) are kept alongside it under
[`star_jedi/`](star_jedi/) for reference; they aren't used by the build. It's a separate freeware
asset, not covered by this crate's own MIT license.
