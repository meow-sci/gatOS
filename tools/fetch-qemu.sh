#!/bin/sh
# tools/fetch-qemu.sh - fetch + verify + trim the pinned QEMU win-x64 bundle (OS_PLAN.md T11.1).
#
# POSIX-sh counterpart of fetch-qemu.ps1 for Linux/macOS dev machines and CI (the release
# workflow bundles the Windows QEMU from a Linux runner, T11.4). Pin and file list live in
# tools/qemu-win64-files.txt. Needs 7z (p7zip-full) or 7zz (official 7-Zip for Linux) on PATH
# - the Weil installer is NSIS, which only full 7-Zip can unpack. Re-running is a no-op when
# the bundle is already current. Pass --force to refetch + repopulate regardless.

set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
repo_root=$(dirname -- "$script_dir")
list_file="$script_dir/qemu-win64-files.txt"
qemu_dir="$repo_root/vendor/qemu"
bundle_dir="$qemu_dir/win-x64"
cache_dir="$qemu_dir/.cache"
stamp_file="$qemu_dir/.stamp"

force=0
[ "${1:-}" = "--force" ] && force=1

url=$(sed -n 's/^#pin: url=//p' "$list_file")
sha512=$(sed -n 's/^#pin: sha512=//p' "$list_file")
files=$(grep -v '^[[:space:]]*#' "$list_file" | grep -v '^[[:space:]]*$')
[ -n "$url" ] && [ -n "$sha512" ] && [ -n "$files" ] || {
    echo "Malformed $list_file: need #pin: url=, #pin: sha512= and at least one file entry." >&2
    exit 1
}
installer_name=${url##*/}

# ---- no-op when current ----
if [ "$force" -eq 0 ] && [ -f "$stamp_file" ] && [ "$(head -n1 "$stamp_file")" = "$installer_name" ]; then
    current=1
    for f in $files; do
        [ -f "$bundle_dir/$f" ] || { current=0; break; }
    done
    if [ "$current" -eq 1 ]; then
        echo "vendor/qemu/win-x64 is already current ($installer_name); nothing to do."
        exit 0
    fi
fi

# ---- tools ----
sevenzip=
for c in 7z 7zz; do
    if command -v "$c" >/dev/null 2>&1; then sevenzip=$c; break; fi
done
[ -n "$sevenzip" ] || {
    echo "7-Zip not found: install p7zip-full (7z) or 7-Zip for Linux (7zz)." >&2
    exit 1
}

sha512_of() {
    if command -v sha512sum >/dev/null 2>&1; then sha512sum "$1" | cut -d' ' -f1
    else shasum -a 512 "$1" | cut -d' ' -f1; fi
}

mkdir -p "$cache_dir"

# ---- download + verify ----
installer="$cache_dir/$installer_name"
if [ "$force" -eq 1 ] || [ ! -f "$installer" ] || [ "$(sha512_of "$installer")" != "$sha512" ]; then
    echo "Downloading $url ..."
    curl -sSL --fail --retry 3 -o "$installer" "$url"
fi
actual=$(sha512_of "$installer")
if [ "$actual" != "$sha512" ]; then
    rm -f "$installer"
    echo "sha512 mismatch for $installer_name: expected $sha512, got $actual. Deleted the download." >&2
    exit 1
fi
echo "Verified $installer_name (sha512 ok)."

# ---- extract ----
extract_dir="$cache_dir/extract"
extract_stamp="$cache_dir/.extract-stamp"
if [ "$force" -eq 1 ] || [ ! -f "$extract_stamp" ] || [ "$(head -n1 "$extract_stamp")" != "$installer_name" ]; then
    rm -rf "$extract_dir"
    echo "Extracting $installer_name (this takes a minute)..."
    "$sevenzip" x "$installer" -o"$extract_dir" -y >/dev/null
    echo "$installer_name" > "$extract_stamp"
fi

# ---- populate vendor/qemu/win-x64 from the list ----
rm -rf "$bundle_dir"
for f in $files; do
    src="$extract_dir/$f"
    [ -f "$src" ] || {
        echo "Listed file missing from the extracted installer: $f (stale tools/qemu-win64-files.txt?)" >&2
        exit 1
    }
    mkdir -p "$bundle_dir/$(dirname "$f")"
    cp "$src" "$bundle_dir/$f"
done
echo "$installer_name" > "$stamp_file"

count=$(printf '%s\n' "$files" | wc -l | tr -d ' ')
echo "Populated vendor/qemu/win-x64: $count files ($installer_name)."
