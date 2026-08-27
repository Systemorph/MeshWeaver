#!/usr/bin/env bash
# The MODULE-OWNED MeshWeaver.* assemblies of a node repo: every MeshWeaver.* project directory in
# its src/ EXCEPT the ones its src/platform-shipped.txt names — projects that live there but ship
# in the portal IMAGE (the storage backends and transports that moved out of the platform with the
# hosts). A module bundle may carry the former (they exist nowhere in /app) and must never carry the
# latter (a same-identity duplicate beside the app's copy). Printed semicolon-separated, the shape
# `meshweaver-plugin-build module-pack --own-platform` takes; the bundle inspection in
# node-repo-module-pack.yml calls this same script so the two cannot disagree.
#
#   module-owned-platform.sh <node-repo>/src
#
# An absent platform-shipped.txt means "nothing is image-shipped" — the state of every node repo
# before the storage carve-out — and is deliberately not an error: the list DECLARES exclusions.
set -euo pipefail
src="${1:?usage: module-owned-platform.sh <node-repo>/src}"
shipped=""
if [ -f "$src/platform-shipped.txt" ]; then
  shipped="$(grep -v '^[[:space:]]*#' "$src/platform-shipped.txt" | sed 's/[[:space:]]*$//' | grep -v '^$' || true)"
fi
ls "$src" 2>/dev/null | grep '^MeshWeaver\.' | while read -r name; do
  if ! grep -qxF "$name" <<<"$shipped"; then printf '%s\n' "$name"; fi
done | paste -sd';' -
