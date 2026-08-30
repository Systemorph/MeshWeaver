#!/usr/bin/env bash
# Pack what the memex-local formula installs — deploy/homebrew (CLI, share assets, formula) and
# deploy/helm (the chart it vendors) — into ONE reproducible tarball, laid out under a
# memex-local-<version>/ prefix so the formula's `install` paths are identical for a HEAD install
# (git checkout) and a stable one (this tarball).
#
# Usage: make-tarball.sh <version> <out.tar.gz> [<git ref>]
#
# Used by .github/workflows/homebrew.yml twice: on every PR to install + test the formula from the
# PR's own tree (file:// URL), and on main to attach the tarball to a release of the published tap
# (Systemorph/homebrew-memex). Nothing else in the repository is needed at install time — the
# whole MeshWeaver tree (~100 MB gzipped) would make `brew install` a source download of the
# platform for a ~100 KB CLI.
set -euo pipefail

version="${1:?usage: make-tarball.sh <version> <out.tar.gz> [<git ref>]}"
out="${2:?usage: make-tarball.sh <version> <out.tar.gz> [<git ref>]}"
ref="${3:-HEAD}"

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
[ -f "$repo/MeshWeaver.slnx" ] || { echo "not a MeshWeaver checkout: $repo" >&2; exit 1; }

mkdir -p "$(dirname "$out")"
# `gzip -n` omits the timestamp so the same tree yields the same bytes — the sha256 the formula
# pins is then a statement about CONTENT, and a re-run of the publish step is a no-op.
git -C "$repo" archive --format=tar --prefix="memex-local-${version}/" "$ref" \
  deploy/homebrew deploy/helm | gzip -n > "$out"

# A tarball that does not carry the CLI is a green step that installs nothing — assert the one
# file everything else depends on is inside, never "tar exited 0".
# 🚨 List to a variable, then grep — never `tar -tzf … | grep -q`: grep -q exits on its first match
# and closes the pipe, tar dies with "stdout: write error", and under pipefail the check reports
# the CLI as MISSING from a tarball that carries it (main's first tap publish, 2026-08-30).
entries="$(tar -tzf "$out")"
if ! grep -qx "memex-local-${version}/deploy/homebrew/bin/memex-local" <<<"$entries"; then
  echo "tarball $out does not contain deploy/homebrew/bin/memex-local" >&2
  exit 1
fi
printf '%s\n' "$out"
