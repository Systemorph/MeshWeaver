#!/usr/bin/env bash
# The MODULE-OWNED MeshWeaver.* assemblies of a node repo: every MeshWeaver.* project DIRECTORY in
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
# An EMPTY result (a repo whose every MeshWeaver.* project is image-shipped, or one with none) is
# valid too, and prints an empty line rather than failing.
set -euo pipefail
src="${1:?usage: module-owned-platform.sh <node-repo>/src}"
shipped=""
if [ -f "$src/platform-shipped.txt" ]; then
  shipped="$(grep -v '^[[:space:]]*#' "$src/platform-shipped.txt" | sed 's/[[:space:]]*$//' | grep -v '^$' || true)"
fi
own=()
while IFS= read -r dir; do
  name="$(basename "$dir")"
  if ! grep -qxF "$name" <<<"$shipped"; then own+=("$name"); fi
done < <(find "$src" -mindepth 1 -maxdepth 1 -type d -name 'MeshWeaver.*' | sort)
( IFS=';'; printf '%s\n' "${own[*]-}" )
