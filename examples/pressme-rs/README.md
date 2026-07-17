# pressme-rs (Rust TUI example)

🔘 A panel of **big full-width buttons** for gatOS, built with [ratatui](https://ratatui.rs) — you
supply each button's **label**, a **hex color**, and a **shell command**, and pressing the button
runs the command. The terminal height splits evenly across the buttons, so the whole screen is one
tall stack of colored, labeled buttons.

Where [`gogogo-rs`](../gogogo-rs) is a purpose-built throttle quadrant and [`the90s`](../the90s) is
a soundboard, this one is a **generic launcher**: it speaks no gatOS API of its own. But inside the
guest the commands are ordinary Alpine userland, so pointing them at the `/sim` mount turns it into a
one-key control board (the shipped `pressme.toml` does exactly that).

The button itself is a small **custom ratatui widget** (`src/button.rs`) modeled on the framework's
[custom-widget example](https://ratatui.rs/examples/widgets/custom_widget/): a solid color fill with
a bevel (a bright top edge, a dark bottom edge) and the label centered on top. Because a colored
button *is* the point, this example deliberately opts out of the repo's usual
transparent-over-the-game rule and paints backgrounds; the label ink is auto-chosen black or white
for contrast against whatever hex you give it.

## What it does

- One **full-width button per configured command**, stacked top-to-bottom; the terminal height
  divides evenly across them (a leftover row goes to the top buttons).
- Each button shows its **label centered**, a small **shortcut number** in the corner (`1`–`9`), and,
  once pressed, a **result subtitle**: a braille spinner while running, then `✓ <first output line>`
  on success or `✗ exit <code>` on failure.
- Commands run **off the UI thread** (a thread per press), so a slow command never freezes the panel,
  and pressing several buttons runs them concurrently. A button ignores re-presses while its own
  command is still running.

## Configuring the buttons

Two sources — use either, or both (file first, then flags):

### A TOML file (recommended)

The clean, unambiguous form. One `[[button]]` table each, in file order (see the shipped
[`pressme.toml`](pressme.toml)):

```toml
[[button]]
label = "Full throttle"
color = "#2ea043"                # #rrggbb, #rgb, or bare hex digits
command = "echo 1 > /sim/vessels/active/ctl/throttle && echo throttle up"

[[button]]
label = "Abort — kill engines"
color = "#f85149"
command = "echo 0 > /sim/vessels/active/ctl/engine && echo ENGINES OFF"
```

```sh
pressme-rs                       # reads ./pressme.toml when present
pressme-rs --config panel.toml   # or point at another file
```

### Repeatable `--button` flags

Handy for a throwaway panel. Each flag is `LABEL:COLOR:COMMAND` — only the **first two** colons
split fields off, so the command may itself contain colons (URLs, timestamps):

```sh
pressme-rs \
  -b 'Ping:#1f6feb:ping -c1 10.0.2.2' \
  -b 'Uptime:#8957e5:uptime' \
  -b 'Curl v1:#2ea043:curl -s http://10.0.2.2:8080/v1/time/ut'
```

## Controls

| Key / action                | Effect                                  |
|-----------------------------|-----------------------------------------|
| `↑`/`↓` or `k`/`j`          | move the selection                      |
| `Home` / `End`              | first / last button                     |
| `Enter` / `space`           | press the selected button               |
| `1`–`9`                     | press that button directly              |
| mouse move / click          | hover to select, click to press         |
| `q` / `Esc` / `Ctrl-C`      | quit                                    |

## Build & run

Standalone crate (like the other Rust examples — not part of the .NET solution):

```sh
cd examples/pressme-rs
cargo run --release                    # uses ./pressme.toml
cargo run --release -- -b 'Hi:#2ea043:echo hello'
cargo test                             # unit tests (config, colors, layout, runner)
```

In the guest, copy the binary in (or run from a `/mnt` host mount), drop a `pressme.toml` next to
it, and press buttons. The commands run through `sh -c` (on Windows the host shell is `cmd /C`).
