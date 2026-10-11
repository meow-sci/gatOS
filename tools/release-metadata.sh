#!/usr/bin/env bash
set -euo pipefail

# Only branch pushes and manual branch runs may publish. PRs and tags build only.
if [[ "$GITHUB_EVENT_NAME" != push && "$GITHUB_EVENT_NAME" != workflow_dispatch ]] ||
   [[ "$GITHUB_REF" != refs/heads/* ]]; then
  echo 'publish=false' >> "$GITHUB_OUTPUT"
  exit 0
fi
BRANCH="${GITHUB_REF#refs/heads/}"
PRERELEASE=false
CHANNEL=main
case "$BRANCH" in
  main) ;;
  feature/*) PRERELEASE=true; CHANNEL=feature ;;
  *) echo 'publish=false' >> "$GITHUB_OUTPUT"; exit 0 ;;
esac

# GitHub increments this workflow-wide counter across branches; reruns keep it.
if [[ ! "${GITHUB_RUN_NUMBER:-}" =~ ^[1-9][0-9]*$ ]]; then
  echo '::error::Invalid workflow run number' >&2
  exit 1
fi
MAJOR=1
NAME="$MAJOR.$GITHUB_RUN_NUMBER.0"
if [[ "$PRERELEASE" == true ]]; then
  NAME+=-beta
fi
{
  echo 'publish=true'
  echo "version=$NAME"
  echo "modversion=$NAME"
  echo "tag=v$NAME"
  echo "title=gatOS $NAME"
  echo "prerelease=$PRERELEASE"
  echo "channel=$CHANNEL"
} >> "$GITHUB_OUTPUT"
