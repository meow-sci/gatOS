#!/usr/bin/env bash
# Fetch the pinned guest image release into guest/out/ (OS_PLAN.md T2.5) so
# consumers (dev builds, dist CI, integration tests) never have to build it.
# Pin = guest/GUEST_VERSION -> guest-v<N> in guest/GUEST_REPOSITORY.
# No-op when out/manifest.toml already matches the pin.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
OUT="$SCRIPT_DIR/out"
REPO="${GATOS_GUEST_REPO:-$(tr -d '[:space:]' < "$SCRIPT_DIR/GUEST_REPOSITORY")}"
VERSION="$(tr -d '[:space:]' < "$SCRIPT_DIR/GUEST_VERSION")"
TAG="guest-v$VERSION"
[[ "$REPO" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || { echo 'Invalid guest repository' >&2; exit 1; }
[[ "$VERSION" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid guest version' >&2; exit 1; }
ASSETS=(base.qcow2 vmlinuz-virt initramfs-virt manifest.toml
        id_ed25519 id_ed25519.pub host_key_fingerprint.txt sha256sums.txt)

if [ -f "$OUT/manifest.toml" ] && grep -q "^guest_version = $VERSION\$" "$OUT/manifest.toml"; then
    echo "guest/out/ already at guest_version=$VERSION — nothing to do"
    exit 0
fi

TMP="$(mktemp -d "$SCRIPT_DIR/.fetch-tmp.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

echo "==> fetching $TAG from $REPO"
# These are public assets: no GitHub login or publishing credential is needed,
# even when gh is installed but unauthenticated on the developer's machine.
for a in "${ASSETS[@]}"; do
    curl -fsSL -o "$TMP/$a" "https://github.com/$REPO/releases/download/$TAG/$a"
done

echo "==> verifying checksums"
if command -v sha256sum >/dev/null; then
    ( cd "$TMP" && sha256sum -c --quiet sha256sums.txt )
else
    ( cd "$TMP" && shasum -a 256 -c --quiet sha256sums.txt )   # macOS
fi

mkdir -p "$OUT"
for a in "${ASSETS[@]}"; do
    mv -f "$TMP/$a" "$OUT/$a"
done
chmod 0600 "$OUT/id_ed25519"
echo "==> guest/out/ ready (guest_version=$VERSION)"
