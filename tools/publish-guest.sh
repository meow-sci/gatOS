#!/usr/bin/env bash
# Run from the gatOS workflow; only the release assets live in the guest repo.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO="$(tr -d '[:space:]' < "$ROOT/guest/GUEST_REPOSITORY")"
VERSION="$(tr -d '[:space:]' < "$ROOT/guest/GUEST_VERSION")"
[[ "$REPO" =~ ^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$ ]] || { echo 'Invalid guest repository' >&2; exit 1; }
[[ "$VERSION" =~ ^[1-9][0-9]*$ ]] || { echo 'Invalid guest version' >&2; exit 1; }
: "${GH_TOKEN:?Set GATOS_GUEST_RELEASE_TOKEN in gatOS Actions secrets}"
: "${GITHUB_REPOSITORY:?Missing source repository}"
: "${GITHUB_SHA:?Missing source commit}"
: "${GITHUB_RUN_ID:?Missing workflow run ID}"
if [[ "$(printf '%s' "$REPO" | tr '[:upper:]' '[:lower:]')" == \
      "$(printf '%s' "$GITHUB_REPOSITORY" | tr '[:upper:]' '[:lower:]')" ]]; then
    echo 'Guest releases must use a separate repository from mod releases' >&2
    exit 1
fi

TAG="guest-v$VERSION"
cd "$ROOT/guest/out"
ASSETS=(base.qcow2 vmlinuz-virt initramfs-virt manifest.toml
        id_ed25519 id_ed25519.pub host_key_fingerprint.txt sha256sums.txt)
for asset in "${ASSETS[@]}"; do
    [[ -s "$asset" ]] || { echo "Missing guest asset: $asset" >&2; exit 1; }
done
grep -qx "guest_version = $VERSION" manifest.toml || { echo 'Guest manifest does not match pin' >&2; exit 1; }
if command -v sha256sum >/dev/null; then
    sha256sum -c --quiet sha256sums.txt
else
    shasum -a 256 -c --quiet sha256sums.txt
fi
ALPINE="$(sed -n 's/^alpine_version = "\(.*\)"$/\1/p' manifest.toml)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
cat > "$TMP/notes.md" <<EOF
Alpine $ALPINE guest image v$VERSION for gatOS.

Built from [$GITHUB_REPOSITORY@$GITHUB_SHA](https://github.com/$GITHUB_REPOSITORY/commit/$GITHUB_SHA).
Build: https://github.com/$GITHUB_REPOSITORY/actions/runs/$GITHUB_RUN_ID

Fetch with guest/fetch-guest.sh or guest/fetch-guest.ps1 in the source repository.
EOF

# A failed lookup must not be mistaken for a missing release. Listing the
# destination also verifies it is accessible before any publication mutation.
gh api --paginate "repos/$REPO/releases?per_page=100" --jq '.[].tag_name' > "$TMP/tags"
if grep -Fxq "$TAG" "$TMP/tags"; then
    gh release upload "$TAG" "${ASSETS[@]}" --repo "$REPO" --clobber
    gh release edit "$TAG" --repo "$REPO" --notes-file "$TMP/notes.md"
else
    # GitHub creates the tag on the destination's default branch. The source
    # SHA above belongs to gatOS and is deliberately not passed as --target.
    gh release create "$TAG" "${ASSETS[@]}" --repo "$REPO" \
        --title "gatOS guest image v$VERSION" --notes-file "$TMP/notes.md"
fi
