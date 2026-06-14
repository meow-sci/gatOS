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

## Controls

**Dashboard:** `↑`/`↓` or `j`/`k` (or the mouse wheel) select · `Enter` or click a row open it ·
`q` quit.

**Vessel detail** — the header shows the current attitude mode; a keyboard focus ring **and**
clickable buttons drive the same actions: `Tab`/`↑`/`↓` (or wheel) move focus · `Enter`/`Space` or
click activate · `−`/`=` (or `←`/`→`) nudge throttle · `Esc`/`Backspace` back · `q` quit. Available
controls:

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
