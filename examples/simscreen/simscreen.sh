#!/bin/sh
# simscreen — render the gatOS /sim/display screen stream in a kitty-capable terminal.
#
# The host (the gatOS mod) captures the KSA viewport, downscales it on the GPU, and serves it at
# /sim/display/stream already encoded as the Kitty terminal graphics protocol — one self-contained,
# in-place frame after another. So the whole consumer is really just `cat`; this script adds the
# niceties (alt-screen, hidden cursor, live resize, clean teardown).
#
# Works the same in an in-game purrTTY tab or any external Kitty-capable terminal (kitty, Ghostty,
# WezTerm, Konsole, …) SSH'd into the guest. Run it, watch the flight, Ctrl-C to stop.
#
# Usage:  simscreen [fps] [width] [height]
#   simscreen            # 15 fps at the configured size
#   simscreen 24 640 360 # 24 fps, 640x360 pixels
#
# Tunables are just files — change them live from another shell while this runs:
#   echo 30  > /sim/display/fps
#   echo 480 > /sim/display/width
set -eu

D=/sim/display
[ -e "$D/stream" ] || { echo "simscreen: $D/stream not found (is the gatOS screen stream available?)" >&2; exit 1; }

# Optional one-shot tuning from the args (out-of-range values are clamped by gatOS, never rejected).
[ "${1:-}" ] && printf '%s\n' "$1" > "$D/fps"
[ "${2:-}" ] && printf '%s\n' "$2" > "$D/width"
[ "${3:-}" ] && printf '%s\n' "$3" > "$D/height"

cleanup() {
    printf '0\n' > "$D/enabled" 2>/dev/null || true   # stop the capture (it idles for free)
    printf '\033[?25h\033[?1049l'                      # show cursor, leave the alternate screen
}
trap cleanup EXIT INT TERM

printf '\033[?1049h\033[?25l\033[2J'   # alternate screen, hide cursor, clear
printf '1\n' > "$D/enabled"            # turn the stream on

# Each read delivers the next frame; a frame is a complete Kitty image drawn in place. Blocks until
# the next frame, forever — Ctrl-C (or closing the tab) trips the trap above.
exec cat "$D/stream"
