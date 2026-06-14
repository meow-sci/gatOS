# gogogo-rs (Rust TUI example)

A **tiny floating control panel** for gatOS, built with [ratatui](https://ratatui.rs) — *just* two
widgets:

- a **drag-slide throttle slider**, and
- an **ignite / shutdown toggle button**.

That's the whole UI. It's meant to live in a small floating terminal window next to the game while
you fly — no menus, no chrome, nothing to read. Where the sibling [`simfs-dashboard`](../simfs-dashboard)
is a build-your-own dashboard and [`dashboard-rs`](../dashboard-rs) is a fleet view, this one is a
single-purpose throttle quadrant for the **active vessel**.

## What it does

- **Throttle slider** — drag anywhere along the bar to set the throttle live (one write per 1 %
  crossed, so a held sweep is smooth, not a flood). `↑`/`↓` (or `-`/`=`) nudge by 5 %, `0` cuts,
  `g` goes full.
- **Ignite/shutdown toggle** — click it (or press `space`/`enter`) to light the engines or shut
  them down. The button shows the real state: green **`[ IGNITE ]`** when cold, red
  **`[ SHUTDOWN ]`** when lit (engine state is read back from `/sim`; over the HTTP source it
  reflects the last command instead).
- **Auto orientation** — the layout picks **vertical** (tall, narrow window) or **horizontal**
  (wide, short window) from the terminal size, and **flips live** when you resize the window. The
  slider runs along the long axis either way.
- **Disabled when there's nothing to fly** — if there is no valid active vessel, every control
  greys out and goes inert, with a `no active vessel` note.

It floats over the game: like the other examples it leaves backgrounds unset so purrTTY shows the
sim through, coloring only the foreground (the throttle bar's fill is the one painted background).

## Data interface — the `/sim` filesystem

Everything is driven through the active vessel's `vessels/active/…` control files — the same
field-level surface `simfs-dashboard` uses, so no vessel id is needed:

| widget                | reads                                   | writes                          |
| --------------------- | --------------------------------------- | ------------------------------- |
| throttle slider       | `vessels/active/ctl/throttle`           | `vessels/active/ctl/throttle`   |
| ignite/shutdown       | `vessels/active/engines/<n>/active`     | `vessels/active/ctl/{ignite,shutdown}` |
| active-vessel gate    | `vessels/active/id`                      | —                               |

- **In the guest (default):** it reads the **real `/sim` mount** directly with the filesystem — a
  read is one `read()`, a control write is one `echo value > file` (the control files actuate on the
  newline and report the real errno on the status line). No flags needed.
- **On the host (dev):** pass `--url $GATOS_HTTP` to use the mod's HTTP `/v1/fs/<path>` mirror —
  the same paths, served over slirp. (Auto-selected when `/sim` is absent and `$GATOS_HTTP` is set.)

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo run --release                # reads /sim, drives the active vessel
```

On the host (for dev), point it at the mod's HTTP server, or at a `/sim`-shaped fixture directory:

```sh
cargo run -- --url http://127.0.0.1:4242/v1     # HTTP /v1/fs mode
cargo run -- --root ./some-fixture              # any directory laid out like /sim
cargo run -- -o vertical                        # force an orientation (default: auto)
```

```
USAGE: gogogo-rs [--root <dir> | --url <base>] [--interval <ms>]
                 [--orientation auto|vertical|horizontal]
```

`--interval` (default 120 ms) is how often it re-polls the throttle/engine state and how promptly a
drag write lands; the floor is 1 ms. As with the other examples, the second freshness gate is the
in-game `sample_rate_hz` — the rate the host samples the game.

## Controls

- **Mouse:** drag the slider to scrub the throttle · click the button to ignite/shut down · wheel
  nudges the throttle ±5 %.
- **Keys:** `space`/`enter` toggle · `↑`/`↓` or `-`/`=` nudge · `0` cut throttle · `g` full throttle
  · `q`/`Esc` quit.

## No TUI required

The same fields are plain files — `echo 0.5 > /sim/vessels/active/ctl/throttle` to throttle up,
`echo 1 > /sim/vessels/active/ctl/ignite` to light it — or the HTTP twins
`curl -X POST "$GATOS_HTTP/fs/vessels/active/ctl/throttle" -d 0.5`. This panel is just a friendly,
draggable face over that surface.
