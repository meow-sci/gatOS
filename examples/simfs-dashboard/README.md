# simfs-dashboard (Rust TUI example)

A **DIY dashboard builder** for gatOS, built with [ratatui](https://ratatui.rs) — keyboard **and**
mouse. Where the sibling [`dashboard-rs`](../dashboard-rs) is a *fixed* fleet view over the atomic
HTTP snapshot, this one starts **empty**: you search the `/sim` filesystem in a popup, place the
fields you care about as widgets, arrange them into a grid, and save the layout to a file.

The point is **first-party knowledge of what each `/sim` file means**. The program knows that
`…/ctl/throttle` is a throttle command, so it renders a **clickable throttle bar** — not a number;
that `…/lights/0/on` is a flag, so it renders a **toggle**; that `…/ctl/attitude_mode` takes a
token, so activating it opens a **mode picker**. Read-only sensors render as formatted values,
gauges, or vectors. Build the cockpit you want out of the raw filesystem.

## Data interface — the `/sim` filesystem

This client is built on the **field-level** surface (one value per path), not the JSON snapshot.

- **In the guest (default):** it reads the **real `/sim` mount** directly with the filesystem —
  a field read is one `read()`, a control write is one `echo value > file` (the control files
  actuate on the newline and report the real errno), and the search popup *walks the directory
  tree*. No flags needed.
- **On the host (dev):** pass `--url $GATOS_HTTP` to use the mod's HTTP `/v1/fs/<path>` mirror
  instead — the same paths, the same writes, served over slirp. (Auto-selected when `/sim` is
  absent and `$GATOS_HTTP` is set.)

Either way, the **same `/sim` paths** drive everything — this is the transport-parity surface the
9p tree, HTTP and MQTT all expose.

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo run --release                # reads /sim directly; press 'a' to add a field
```

On the host (for dev), point it at the mod's HTTP server, or at a `/sim`-shaped fixture directory:

```sh
cargo run -- --url http://127.0.0.1:4242/v1     # HTTP /v1/fs mode
cargo run -- --root ./some-fixture              # any directory laid out like /sim
cargo run -- --file mission.toml                # load a saved dashboard at startup
```

```
USAGE: simfs-dashboard [--file <layout.toml>] [--root <dir> | --url <base>]
                       [--interval <ms>] [--columns <n>] [--border-opacity <0-100>]
```

## How it polls — `--interval`

One background worker thread owns the source and, **once per `--interval`, re-reads only the fields
currently on the dashboard** (the placed widgets' paths, deduped). So the read cost is
O(placed widgets) — it never walks the whole tree to refresh; the directory walk happens only when
you open the search popup. The toolbar shows the live cadence (e.g. `poll 250ms (4 Hz)`), exactly
like `dashboard-rs`.

The default is a relaxed **250 ms (~4 Hz)**, floored at 1 ms — plenty for a hand-built panel, and
gentle on the filesystem. Lower it for snappier readouts (1 ms polls hard; useful mostly when the
in-game `sample_rate_hz` is also high). (As with `dashboard-rs`, the poll rate is
only one of two freshness gates; the other is the in-game `sample_rate_hz`, the rate the host
samples the game — raise it there for the values to actually change at this cadence.)

## Building a dashboard

1. **Add** (`a`, or click `[+ add]`): a search popup lists every addable `/sim` field. The filter is
   **fuzzy and AND-combinatorial** — space-separated terms must *all* match (case-insensitive), each
   as a substring or subsequence of the path or name, ranked best-match first. So `rocket vel surf`
   narrows to fields matching `rocket` **and** `vel` **and** `surf`. `↑`/`↓` select, `Enter`/click
   places it. In `/sim` (filesystem) mode the list is the live directory walk, so every
   engine/light/tank index shows up. Over HTTP the list is the curated catalog expanded per
   vessel/body; to add an exotic or indexed path that isn't listed, just type the full path — an
   **"add custom path"** row appears.
2. **Arrange:** `↑↓←→`/`hjkl` (or the wheel) move the selection; `[` / `]` move the selected widget
   earlier/later in the grid; `R` renames it; `x`/`Del` removes it. The grid reflows automatically.
3. **Manage** (`m`, or click `[manage]`): a popup lists every widget with per-row **`[↑] [↓] [✕]`**
   buttons — move it up, move it down, or delete it — plus the same `[` `]` / `x` keys. The cleanest
   way to reorder and prune a busy dashboard.
4. **Customize:** `s` (or `[settings]`) opens a modal with a **columns** stepper (1–8) and a
   **border-opacity** slider (the card borders fade toward invisible over the game).
5. **Save** (`w`, or click `[save]`): prompts for a filename and writes the layout as **TOML** to
   the current directory. Reload it next time with `--file`.

## Controls

**Dashboard:** `a` add · `m` manage · `↑↓←→`/`hjkl`/wheel select · `Enter`/`Space` or click activate
a control · `-`/`=` nudge the selected throttle/number · `[` `]` move widget · `R` rename · `x`
remove · `w` save · `s` settings · `q` quit.

**Manage popup:** `↑`/`↓` select · `[` `]` (or the `[↑]`/`[↓]` buttons) reorder · `x` (or `[✕]`)
delete · `Esc` close.

**Control widgets** (the interactive ones — derived from first-party knowledge):

- **Throttle / fraction bar** (`…/ctl/throttle`, `engines/n/min_throttle`, `animations/n/goal`,
  `solar/n/goal`): click anywhere along the bar to set the value; `-`/`=` nudge by 5 %.
- **Toggle** (`…/ctl/lights`, `…/ctl/rcs`, `engines/n/active`, `rcs/n/active`, `lights/n/on`):
  click the `[ ON ]`/`[ off ]` button to flip it.
- **Trigger button** (`…/ctl/ignite` `shutdown` `stage`, `decouplers/n/fire`, the debug refills):
  click `[ Ignite ]` to fire it.
- **Number stepper** (`lights/n/brightness`, `debug/time/warp`): `[-]` / `[+]` buttons (warp steps
  ×2 / ÷2).
- **Mode picker** (`…/ctl/attitude_mode`, `…/ctl/attitude_frame`): activating opens a token list —
  pick a mode (or `manual` to drop the autopilot).

Read-only sensors render as formatted values (m, km, m/s, t, kN, W, °, durations), `0..1` **gauges**
(battery, tank/engine fraction), `ON/off` flags, space-separated **vectors**, or the one-line
`telemetry` **JSON** doc. Every control write surfaces its errno (`EINVAL`, `EACCES`, `EBUSY`, …) in
the status bar — the same vocabulary the `/sim` control files report.

## Layout file

A saved layout is small, human-editable TOML: global settings plus one `[[widget]]` table per card.
Only `{title, path}` is stored — the **widget kind is re-derived from the path** on load, so a
hand-edited path always gets the right widget.

```toml
columns = 3
border_opacity = 100
interval_ms = 250

[[widget]]
title = "Throttle"
path = "vessels/by-id/Kerbal-1/ctl/throttle"

[[widget]]
title = "Radar alt"
path = "vessels/by-id/Kerbal-1/altitude/radar"

[[widget]]
title = "Engine 0"
path = "vessels/by-id/Kerbal-1/engines/0/active"
```

## No TUI required

The same fields are plain files — `cat /sim/vessels/by-id/<id>/altitude/radar` to read,
`echo 0.5 > /sim/vessels/by-id/<id>/ctl/throttle` to fly — or the HTTP twins
`curl "$GATOS_HTTP/fs/vessels/by-id/<id>/altitude/radar"` and
`curl -X POST "$GATOS_HTTP/fs/vessels/by-id/<id>/ctl/throttle" -d 0.5`. This dashboard is just a
friendly, mouse-driven face over that surface.
