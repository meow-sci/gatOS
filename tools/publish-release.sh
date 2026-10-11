#!/usr/bin/env bash
set -euo pipefail

ZIP="$GATOS_DIST_DIR/gatOS-$VERSION.zip"
shopt -s nullglob
ZIPS=("$GATOS_DIST_DIR"/*.zip)
if [[ ${#ZIPS[@]} -ne 1 || "${ZIPS[0]}" != "$ZIP" || ! -s "$ZIP" ]]; then
  echo '::error::Expected exactly one nonempty gatOS-<version>.zip' >&2
  exit 1
fi
if gh release view "$TAG" --repo "$GITHUB_REPOSITORY" >/dev/null 2>&1; then
  # Reruns keep their version and preserve already-published assets and tags.
  echo "Preserving existing release $TAG"
  exit 0
fi

GUESTVER="$(tr -d '[:space:]' < guest/GUEST_VERSION)"
NOTES="$(mktemp)"
trap 'rm -f "$NOTES"' EXIT
cat > "$NOTES" <<EOF
One gatOS archive for Windows and Linux, including guest image v$GUESTVER and Windows QEMU.

- Windows uses the bundled QEMU; no separate QEMU installation is needed.
- Linux uses system QEMU: install both \`qemu-system-x86_64\` and \`qemu-img\`. The bundled Windows files are unused.

**Updating:** close KSA and back up your gatOS data folder (especially \`gatos.toml\` and \`disks/\`). It can be the same folder as the installed mod on Windows. Replace the old mod files with this archive, preserving your data; do not delete the entire folder without a backup. Extracting over old files alone can leave obsolete DLLs behind.
EOF

EXTRA=(--repo "$GITHUB_REPOSITORY")
[[ "$PRERELEASE" == "true" ]] && EXTRA+=(--prerelease --latest=false)
gh release create "$TAG" "$ZIP" \
  --target "$GITHUB_SHA" \
  --title "$TITLE" \
  --notes-file "$NOTES" \
  --generate-notes \
  "${EXTRA[@]}"
