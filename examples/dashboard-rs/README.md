# gatos-dashboard (Rust TUI example)

An interactive **terminal dashboard + flight console** for gatOS, built with
[ratatui](https://ratatui.rs) — keyboard **and** mouse. It's the second example client alongside the
TypeScript SDK (`../sdk-ts`), and it's **runnable inside the guest**.

- An **htop-style fleet overview**: every vehicle in one live table (situation, altitude, speed,
  mass, fuel %, battery %), sorted at a glance.
- Select a vessel → a **detail screen** with its full telemetry (orbit, velocity, attitude, mass,
  power, engines, tanks) **and control options** to fly it.
- **Transparent over the game:** the UI paints foreground color only — no opaque panels — so purrTTY
  keeps showing the game behind the text. Background fills are used only on the header/status bars.

## Data interface — HTTP

It speaks the **magic HTTP API** at `$GATOS_HTTP` (e.g. `http://sim:4242/v1`). One atomic
`GET /v1/snapshot` returns the whole fleet (each vessel's full record drives both the overview and
the detail screen), and one `POST /v1/command` actuates every control with errno feedback — so the
client is a single, dependency-light reader + writer. (The same surface is on `/sim` and MQTT;
HTTP's atomic snapshot + one command endpoint is the natural fit for a polling TUI.)

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo run --release
```

`$GATOS_HTTP` is preset in the guest shell, so no arguments are needed. On the host (for dev), point
it at the mod's HTTP server:

```sh
cargo run -- --url http://127.0.0.1:4242/v1        # --interval <ms> tunes the poll rate (default 50)
```

The header bar shows the live **poll rate** (e.g. `poll 50ms (20 Hz)`) so the read-back cadence is
always visible. The default `--interval` is **50 ms** (~20 Hz), floored at 10 ms; raise it to poll
less aggressively. (The poll interval is only one of two freshness gates; the other is the in-game
`sample_rate_hz`, the rate the host samples the game — set it high there for the snapshot to actually
change at this cadence.)

## Controls

**Dashboard:** `↑`/`↓` or `j`/`k` (or the mouse wheel) select · `Enter` or click a row open it ·
`s` or click `[settings]` (top-right) opens settings · `q` quit.

**Settings overlay** (`s`, or the `[settings]` button on the top bar of any screen): a modal with a
**border-opacity** slider (0–100) — the pane borders are a bright blue by default, which can be
intrusive over the game; drag/click the slider or use `←`/`→` (or the wheel) to fade them toward
invisible. `Esc` (or a click outside) closes it. The startup value comes from `--border-opacity`
(default 100); the detail-pane borders preview the change live behind the popup.

**Vessel detail** — the header shows the current attitude mode; a keyboard focus ring **and**
clickable buttons drive the same actions: `Tab`/`↑`/`↓` (or wheel) move focus · `Enter`/`Space` or
click activate · `−`/`=` (or `←`/`→`) nudge throttle · `s` settings · `Esc`/`Backspace` back ·
`q` quit. Available controls:

- **Flight:** ignite, shutdown, stage, throttle (−/+, 0 %, 100 %), lights, RCS, attitude mode.
- **Throttle bar:** click anywhere along the throttle bar in the telemetry pane to set it directly.
- **Attitude mode:** activating the attitude control opens a **picker** — choose any mode (or
  `manual` to unset the autopilot); `↑`/`↓` move, `Enter`/click select, `Esc` cancels.
- **Per engine:** toggle each engine on/off.
- **Per light:** toggle each light on/off.
- **Debug** (active only when the server's `[control] debug_namespace` is on — otherwise they report
  `EACCES`): refill fuel, refill battery, warp ÷2 / ×2.

Every control is one `POST /v1/command`; failures show the errno (`EINVAL`, `EBUSY`, `EACCES`, …) in
the status bar, the same vocabulary the `/sim` control files report.

## No TUI required

The same data and controls are plain HTTP — `curl "$GATOS_HTTP/snapshot" | jq` to read,
`curl -X POST "$GATOS_HTTP/command" -d '{"vessel_id":"…","action":"vessel.ignite","value":1}'` to
fly. This dashboard is just a friendly face over that surface.
