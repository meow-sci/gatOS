# the90s (Rust TUI example)

📼 A **90's-flash-app soundboard** for gatOS, built with [ratatui](https://ratatui.rs) — load a
YAML file of named sound clips, and mash buttons to play them through the game's speakers via the
`/sim/audio` userland playback surface ([SPEC §3.9](../../SPEC_9P_FILESYSTEM.md)).

Where [`dancy-party-rs`](../dancy-party-rs) abuses the light files and [`gogogo-rs`](../gogogo-rs)
is a throttle quadrant, this one is pure Newgrounds-era nostalgia over the same filesystem API.

## What it does

Two screens over the same sound list (`Tab` flips between them):

1. **List** (default) — one row per sound with keyboard nav: `↑`/`↓` pick, `Enter`/`space` play,
   `s` stop. Each row shows a live **`▶N`** badge (how many copies are playing right now) and a
   clickable **`[■]`** stop button.
2. **Board** — the full-screen flash-app soundboard: one chunky neon-bordered button per sound in
   a masonry grid (every row splits the full width among its buttons, so a partial last row
   stretches instead of leaving a hole), with the sound's name under each box. Arrows move across
   the grid; click a box to play it.

Both screens share:

- a **volume slider** on the left edge — click, drag, or scroll it (also `-`/`=`). It scales new
  plays *and* live-adjusts everything already playing.
- the red **`[ OMG STOP ]`** button (key `o`) — stops **every** live channel, immediately.

The playback model is maximal 90s:

- **Every press layers a new copy.** Play the same clip as many times as you want — each press is
  a new FMOD channel (`audio/play` auto-ids them `#1`, `#2`, …).
- **Mashed presses start in the same game tick.** Presses that pile up within one worker interval
  are dispatched as a single `/sim/ctl/batch` group ([SPEC §3.10](../../SPEC_9P_FILESYSTEM.md)) —
  one write, one command group, zero sim time between the starts.
- **Stop stops all copies of that sound.** `audio/stop <clip>` fans out to every live channel of
  that clip by name; other sounds keep playing.
- Multiple different sounds play simultaneously, of course. The `audio_max_channels` cap (default
  16) is the ceiling; a play past it returns `EBUSY` on the status line.

At startup the program **registers** each configured file into the gatOS clip store
(`/sim/audio/file/<clip>`): a clip already in the store with the same name **and byte count** is
reused (instant), otherwise the file's bytes are uploaded. Rows are inert (greyed) until their
clip is registered; a bad path shows `✗` and the error.

It floats over the game: backgrounds are left unset so purrTTY shows the sim through the text —
only the volume bar's fill paints anything.

## Config — the soundboard YAML

```yaml
title: TOTALLY RAD SOUNDBOARD
sounds:
  Airhorn: sounds/airhorn.mp3          # relative to this file
  "You've Got Mail": /mnt/sounds/mail.wav
  Dial-Up: /root/sounds/dialup.ogg
```

- `title` — shown across the top. `sounds` — one `name: path` entry per button; file order is
  button order; quote a name containing `:` or `#`.
- Formats: anything FMOD sniffs — mp3 / ogg / wav / flac (the extension is for humans).
- Each sound registers as `/sim/audio/file/<clip>` where `<clip>` is the display name sanitized
  to the store's `[A-Za-z0-9._-]` grammar plus the file extension (`You've Got Mail` →
  `You_ve_Got_Mail.wav`). Two names that collide on the same clip are a load error.
- Getting files into the guest: `scp` them in, or point the config at a **host folder mount**
  (`/mnt/<name>`, the `[mounts]` section of gatos.toml) and keep your sound library on the host.

## Data interface — the `/sim` filesystem

| what                  | path                          | how                                      |
| --------------------- | ----------------------------- | ---------------------------------------- |
| register a sound      | `audio/file/<clip>`           | write the file's bytes (skipped when already there with the same size) |
| play (one press)      | `audio/play`                  | `<clip> vol=<v>` — every press = a new channel |
| play (mashed presses) | `ctl/batch`                   | `audio/play <clip> vol=<v>` lines + `commit` — same-tick starts |
| stop one sound        | `audio/stop`                  | `<clip>` — stops **all** channels of that clip (`ENOENT` = already finished) |
| OMG STOP              | `audio/stop`                  | `all` (idempotent)                       |
| volume slider         | `audio/set`                   | `<clip> vol=<v>` fan-out to every playing clip |
| live `▶N` badges      | `audio/status`                | polled per `--interval`; one `id name state …` line per channel |
| connectivity probe    | `time/ut`                     | tells "audio disabled" from "not connected" |

- **In the guest (default):** it reads/writes the real `/sim` mount with `std::fs`. No flags
  needed. Requires `[audio] audio_enabled = true` (the default) — otherwise every row greys out
  with a hint.
- **On the host (dev):** pass `--url $GATOS_HTTP` to use the mod's HTTP `/v1` mirror — the same
  control lines through `POST /v1/fs/audio/…`, plus the dedicated binary upload route
  (`PUT /v1/audio/file/<clip>`, chunked under the server's 1 MiB body cap) and the
  `/v1/audio/files` clip list. (Auto-selected when `/sim` is absent and `$GATOS_HTTP` is set.)

## Build & run (inside the guest)

Alpine ships Rust:

```sh
apk add --no-cache cargo rust      # one-time, in the guest
cargo run --release                # reads ./the90s.yaml, drives /sim/audio
```

On the host (for dev), point it at the mod's HTTP server, or at a `/sim`-shaped fixture directory:

```sh
cargo run -- --url http://127.0.0.1:4242/v1     # HTTP /v1 mode
cargo run -- --config boards/party.yaml         # a different soundboard
cargo run -- --screen board                     # open straight onto the flash-app grid
```

```
USAGE: the90s [--config <file>] [--root <dir> | --url <base>]
              [--interval <ms>] [--screen list|board]
```

`--interval` (default 150 ms) is how often the `▶N` badges re-poll `audio/status` and the latency
floor for a queued press burst to dispatch.

## Controls

- **Mouse:** click a sound to play it (again and again — it layers) · right-click to stop all its
  copies · click a list row's `[■]` for the same · drag or scroll the left slider for volume ·
  click `[ OMG STOP ]` when the neighbors complain.
- **Keys:** `↑`/`↓` (board: also `←`/`→`) pick · `Enter`/`space`/`p` play · `s`/`x` stop that
  sound · `o`/`S` OMG STOP · `-`/`=` volume · `Tab`/`f` flip screens (`1` list, `2` board) ·
  `Esc` board→list, then quit · `q` quit.

## No TUI required

The soundboard is plain files. The whole program by hand:

```sh
cat airhorn.mp3 > /sim/audio/file/airhorn.mp3    # register
echo 'airhorn.mp3 vol=0.8' > /sim/audio/play     # play (repeat to layer)
cat /sim/audio/status                            # the ▶N badges
echo 'airhorn.mp3' > /sim/audio/stop             # stop all copies of it
echo 'all' > /sim/audio/stop                     # OMG STOP
```

…or the HTTP twins: `curl -T airhorn.mp3 "$GATOS_HTTP/audio/file/airhorn.mp3"` then
`curl -X POST --data 'airhorn.mp3 vol=0.8' "$GATOS_HTTP/fs/audio/play"`. This board is just a
friendly, neon, mashable face over that surface.
