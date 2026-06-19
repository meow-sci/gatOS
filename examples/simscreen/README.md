# simscreen — watch the game in your terminal

Renders the gatOS **screen stream** (`/sim/display`) live in a Kitty-graphics terminal. The host
captures the KSA viewport, downscales it on the GPU, and serves it at `/sim/display/stream` already
encoded as the [Kitty terminal graphics protocol](https://sw.kovidgoyal.net/kitty/graphics-protocol/),
so consuming it is mostly just `cat`.

## Quick start (inside the guest)

```sh
echo 1 > /sim/display/enabled      # turn the stream on (it defaults OFF and costs nothing while off)
cat   /sim/display/stream          # render — each frame is a self-contained, in-place Kitty image
# Ctrl-C to stop, then: echo 0 > /sim/display/enabled
```

That's the whole thing. `simscreen.sh` just wraps it with the alternate screen, a hidden cursor,
optional one-shot tuning, and a clean teardown:

```sh
sh simscreen.sh            # 15 fps at the configured size
sh simscreen.sh 24 640 360 # 24 fps, 640x360 pixels
```

## The controls are files

Tune the stream live from any shell (or over HTTP / MQTT — the controls mirror there too). Numeric
writes are clamped, never rejected.

| File | Values | Meaning |
|---|---|---|
| `/sim/display/enabled` | `0` / `1` | master gate (default `0`) |
| `/sim/display/fps` | `1`–`60` | refresh rate, independent of the game's frame rate |
| `/sim/display/width` | `16`–`1920` | downscale width in pixels (the on-screen image size) |
| `/sim/display/height` | `16`–`1920` | downscale height in pixels |
| `/sim/display/encoding` | `rgba-zlib` / `rgba` | wire format (zlib-compressed or raw RGBA) |
| `/sim/display/format` | (read-only) | the live `WxH@fps enc` |

## Terminals

Any terminal that implements the Kitty graphics protocol works — the in-game **purrTTY** tabs, and
external emulators (kitty, Ghostty, WezTerm, Konsole, …) SSH'd into the guest. External terminals
reach the guest at the gatOS SSH port on `127.0.0.1`.

## Notes

- The captured image is the **scene without the UI** (and pre-tonemap), so bright areas may clamp.
- The capture runs only while `enabled` is `1` **and** at least one program has `stream` open — so it
  truly idles when nobody is watching.
- Full design, performance notes, and the follow-up work (deferred no-stall readback, an HTTP/MJPEG
  mirror) are in [`STREAM_PLAN.md`](../../STREAM_PLAN.md).
