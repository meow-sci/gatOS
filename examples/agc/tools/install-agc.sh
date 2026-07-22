#!/bin/sh
# install-agc.sh — install the built AGC stack to /opt/agc and put `agc` + `dsky` on PATH.
# Run after ./tools/build-agc.sh. Needs root in the guest (the default login is root).
set -eu

VIRTUALAGC="${VIRTUALAGC:-/opt/src/virtualagc}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
PREFIX="${PREFIX:-/opt/agc}"

for f in "$HERE/rom/Luminary099.bin" "$HERE/target/release/agc-bridge" \
         "$HERE/target/release/dsky" "$HERE/target/release/agc-padload" \
         "$VIRTUALAGC/build/yaAGC/yaAGC"; do
    [ -e "$f" ] || { echo "error: missing $f — run ./tools/build-agc.sh first" >&2; exit 1; }
done

mkdir -p "$PREFIX/bin" "$PREFIX/rom" "$PREFIX/share" /var/log/agc /run/agc/switches

install -m 755 "$VIRTUALAGC/build/yaAGC/yaAGC"        "$PREFIX/bin/yaAGC"
install -m 755 "$HERE/target/release/agc-bridge"      "$PREFIX/bin/agc-bridge"
install -m 755 "$HERE/target/release/dsky"            "$PREFIX/bin/dsky"
install -m 755 "$HERE/target/release/agc-padload"     "$PREFIX/bin/agc-padload"
install -m 755 "$HERE/tools/agc"                      "$PREFIX/bin/agc"
install -m 644 "$HERE/rom/Luminary099.bin"            "$PREFIX/rom/Luminary099.bin"
install -m 644 "$HERE/rom/Comanche055.bin"            "$PREFIX/rom/Comanche055.bin"
for s in Luminary099 Comanche055; do
    [ -f "$HERE/rom/$s.symtab" ] && install -m 644 "$HERE/rom/$s.symtab" "$PREFIX/rom/$s.symtab"
done
# Downlink field specs for the decoded status page (ship if present in the tree).
for tsv in "$VIRTUALAGC"/yaAGC/ddd-*.tsv; do
    [ -f "$tsv" ] && install -m 644 "$tsv" "$PREFIX/share/$(basename "$tsv")"
done

# yaAGC's EstablishSocket() resolves the local hostname; stock Alpine guests already carry
# "gatos" in /etc/hosts, but ensure it (yaAGC exits at startup otherwise).
HN="$(hostname)"
grep -q "[[:space:]]$HN\$\|[[:space:]]$HN[[:space:]]" /etc/hosts 2>/dev/null || \
    echo "127.0.0.1 $HN" >> /etc/hosts

# PATH: profile.d for login shells.
if [ -d /etc/profile.d ]; then
    printf 'export PATH="$PATH:%s/bin"\n' "$PREFIX" > /etc/profile.d/agc.sh
    chmod 644 /etc/profile.d/agc.sh
fi
ln -sf "$PREFIX/bin/agc"  /usr/local/bin/agc
ln -sf "$PREFIX/bin/dsky" /usr/local/bin/dsky

echo "installed to $PREFIX. Try:  agc start lm   then, in another tab:  dsky"
