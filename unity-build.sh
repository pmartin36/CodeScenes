#!/usr/bin/env bash
# unity-build.sh — produce the keyless CodeScenes Asset Store build.
#
# Thin bootstrap: the real "make it keyless" recipe lives in a PRIVATE repo, so it is not published
# here. On first run this shallow-clones that repo over SSH into a gitignored folder, then runs it;
# on later runs it fast-forwards the cached copy. Requires SSH access to the private repo (the same
# key you push with).
#
#   ./unity-build.sh            # stage the keyless package
#   ./unity-build.sh --verify   # stage it AND headlessly confirm it compiles keyless
#
# Output goes to ./build/assetstore-out/com.codescenes — upload that through Unity's Asset Store
# publisher tools. Both the fetched script and the output are gitignored.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PRIVATE_REPO="git@github.com:pmartin36/private-build-scripts.git"
PRIVATE_DIR="$REPO_ROOT/build/private-build-scripts"
SCRIPT="$PRIVATE_DIR/pack-assetstore.sh"
OUT="$REPO_ROOT/build/assetstore-out"

if [[ ! -f "$SCRIPT" ]]; then
  echo "[unity-build] fetching private build tooling (shallow SSH clone)..."
  rm -rf "$PRIVATE_DIR"
  git clone --depth 1 "$PRIVATE_REPO" "$PRIVATE_DIR"
else
  echo "[unity-build] updating cached build tooling..."
  git -C "$PRIVATE_DIR" pull --ff-only --quiet || echo "[unity-build] (pull skipped; using cached copy)"
fi

[[ -f "$SCRIPT" ]] || { echo "ERROR: pack script missing after fetch: $SCRIPT" >&2; exit 1; }

exec bash "$SCRIPT" --source "$REPO_ROOT/com.codescenes" --out "$OUT" "$@"
