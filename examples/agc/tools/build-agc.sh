#!/bin/sh
# build-agc.sh — build the Virtual AGC toolchain + flight ropes + the gatOS AGC example bins.
#
# Runs in the gatOS guest (Alpine, musl) or on any Linux host. Idempotent; safe to re-run.
#
#   VIRTUALAGC=/opt/src/virtualagc ./tools/build-agc.sh
#
# Steps:
#   1. cmake-build yaAGC (emulator), yaYUL (assembler), oct2bin (binsource cross-check)
#   2. assemble Luminary099 (LM) + Comanche055 (CM) from source with yaYUL
#   3. HARD-FAIL unless each rope is byte-identical to its .binsource reference
#   4. cargo build --release the bridge / dsky / padload bins
#
# Prereqs (guest):  apk add --no-cache build-base cmake git cargo rust
# Source drop:      git clone --depth 1 https://github.com/alex-sherwin/virtualagc /opt/src/virtualagc
#                   (or point VIRTUALAGC at a /mnt/<name> host mount of a checkout)
set -eu

VIRTUALAGC="${VIRTUALAGC:-/opt/src/virtualagc}"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
ROM="$HERE/rom"
JOBS="${JOBS:-$(nproc 2>/dev/null || echo 2)}"

[ -d "$VIRTUALAGC/yaAGC" ] || {
    echo "error: no Virtual AGC tree at $VIRTUALAGC (set VIRTUALAGC=...)" >&2
    echo "hint:  git clone --depth 1 https://github.com/alex-sherwin/virtualagc $VIRTUALAGC" >&2
    exit 1
}

echo "== [1/4] cmake: yaAGC + yaYUL + oct2bin ($VIRTUALAGC)"
cmake -S "$VIRTUALAGC" -B "$VIRTUALAGC/build" -DCMAKE_BUILD_TYPE=Release >/dev/null
cmake --build "$VIRTUALAGC/build" --target yaAGC yaYUL oct2bin -j"$JOBS" >/dev/null
YAYUL="$VIRTUALAGC/build/yaYUL/yaYUL"
OCT2BIN="$VIRTUALAGC/build/Tools/oct2bin"

assemble() { # $1 = rope dir name
    echo "== [2/4] yaYUL: $1"
    ( cd "$VIRTUALAGC/$1" && "$YAYUL" --unpound-page MAIN.agc > MAIN.agc.lst 2>&1 ) || {
        tail -20 "$VIRTUALAGC/$1/MAIN.agc.lst" >&2
        echo "error: yaYUL failed on $1" >&2; exit 1
    }
    echo "== [3/4] verify $1 vs .binsource"
    ( cd "$VIRTUALAGC/$1" && "$OCT2BIN" < "$1.binsource" >/dev/null 2>&1 \
        && cmp MAIN.agc.bin oct2bin.bin ) || {
        echo "error: $1 rope does NOT match its .binsource reference — refusing to install" >&2
        exit 1
    }
    mkdir -p "$ROM"
    cp "$VIRTUALAGC/$1/MAIN.agc.bin"    "$ROM/$1.bin"
    cp "$VIRTUALAGC/$1/MAIN.agc.symtab" "$ROM/$1.symtab" 2>/dev/null || true
    echo "   $ROM/$1.bin  ($(wc -c < "$ROM/$1.bin") bytes, byte-identical to binsource)"
}

assemble Luminary099
assemble Comanche055

echo "== [4/4] cargo build --release (agc-bridge, dsky, agc-padload)"
( cd "$HERE" && cargo build --release )

echo "OK. Next: ./tools/install-agc.sh"
