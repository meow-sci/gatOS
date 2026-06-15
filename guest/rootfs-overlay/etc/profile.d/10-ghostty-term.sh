# Advertise the gatOS guest's terminal as Ghostty so image tools enable the
# kitty graphics protocol. purrTTY (the host terminal) renders kitty graphics
# via libghostty-vt — Ghostty's own VT engine — but detectors like chafa, viu
# and yazi pick the protocol from the terminal *identity*, not a probe. chafa
# matches TERM_PROGRAM=ghostty exactly (see chafa-term-db.c); SSH does not
# forward TERM_PROGRAM from the host, so it must be set here, guest-side.
#
# TERM stays xterm-256color (from the SSH PTY request) so no Ghostty terminfo is
# needed and ncurses apps keep working. Sourced by /etc/profile for each
# interactive login (a player's SSH session through purrTTY); exported so chafa /
# viu / yazi and other children inherit it. The 10- prefix sorts it before the
# zz- banner so the value is set early.
export TERM_PROGRAM=ghostty
export TERM_PROGRAM_VERSION=1.3.2
export COLORTERM=truecolor