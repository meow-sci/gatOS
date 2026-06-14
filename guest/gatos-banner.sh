#!/bin/sh
# Print the gatOS login banner: the logo, a blank line, then the tagline.
#
# Single source of truth = the sibling art files gatos-banner and gatos-tagline
# (raw 24-bit ANSI colour escapes). Edit those to change the banner; this script
# only stitches them together with a blank line between.
#
# Used two ways:
#   * local preview  — just run it to see the banner in your terminal
#   * image build    — build-image.sh runs it to bake the guest's /etc/gatos/banner,
#                      which /etc/profile.d/zz-gatos-banner.sh cats on each SSH login.
set -eu

dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)

# Emit an art file: strip CR (the art may be authored on Windows / CRLF) and
# guarantee a trailing newline (the art files have none, so a bare cat would
# leave the cursor mid-line).
emit() { tr -d '\r' < "$1"; [ -z "$(tail -c1 -- "$1")" ] || echo; }

emit "$dir/gatos-banner"
echo
emit "$dir/gatos-tagline"
