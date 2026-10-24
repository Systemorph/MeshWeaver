#!/usr/bin/env bash
# Pack what the memex-local formula installs — deploy/homebrew (CLI, share assets, formula) and
# deploy/helm (the chart it vendors) — into ONE reproducible tarball, laid out under a
# memex-local-<version>/ prefix so the formula's `install` paths are identical for a HEAD install
# (git checkout) and a stable one (this tarball).
#
# 🏠 THE CHART IS NOT IN THIS REPOSITORY. It moved to Systemorph/MeshWeaver.Plugins (core slimming;
# Plugins Hosting/ChartHome), so the caller hands its directory in as <chart dir> and it is packed at
# deploy/helm inside the tarball — the path the formula installs from, unchanged. The tarball stays
# byte-reproducible: entries sorted, every mtime/owner fixed, gzip without a timestamp.
#
# Usage: make-tarball.sh <version> <out.tar.gz> <chart dir> [<git ref>]
#
# Used by .github/workflows/homebrew.yml twice: on every PR to install + test the formula from the
# PR's own tree (file:// URL), and on main to attach the tarball to a release of the published tap
# (Systemorph/homebrew-memex). Nothing else in the repository is needed at install time — the
# whole MeshWeaver tree (~100 MB gzipped) would make `brew install` a source download of the
# platform for a ~100 KB CLI.
set -euo pipefail

usage="usage: make-tarball.sh <version> <out.tar.gz> <chart dir> [<git ref>]"
version="${1:?$usage}"
out="${2:?$usage}"
chart="${3:?$usage}"
ref="${4:-HEAD}"
[ -f "$chart/Chart.yaml" ] || { echo "no chart at $chart (no Chart.yaml) — pass a checkout of MeshWeaver.Plugins deploy/helm" >&2; exit 1; }

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../../.." && pwd)"
[ -f "$repo/MeshWeaver.slnx" ] || { echo "not a MeshWeaver checkout: $repo" >&2; exit 1; }

mkdir -p "$(dirname "$out")"
# `gzip -n` omits the timestamp so the same tree yields the same bytes — the sha256 the formula
# pins is then a statement about CONTENT, and a re-run of the publish step is a no-op.
stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
git -C "$repo" archive --format=tar --prefix="memex-local-${version}/" "$ref" deploy/homebrew \
  | tar -x -C "$stage"
mkdir -p "$stage/memex-local-${version}/deploy"
cp -R "$chart" "$stage/memex-local-${version}/deploy/helm"
# One deterministic writer on both macOS (bsdtar has no --sort/--mtime) and Linux: sorted entries,
# mtime 0, uid/gid 0, no user/group names, modes normalised (an executable stays executable).
python3 - "$stage" "memex-local-${version}" "$out" <<'PY'
import gzip, os, sys, tarfile
stage, top, out = sys.argv[1], sys.argv[2], sys.argv[3]
paths = []
for d, dirs, files in os.walk(os.path.join(stage, top)):
    dirs.sort()
    paths.append(d)
    paths.extend(os.path.join(d, f) for f in sorted(files))
with open(out, "wb") as raw, gzip.GzipFile(fileobj=raw, mode="wb", mtime=0, filename="") as gz, \
        tarfile.open(fileobj=gz, mode="w", format=tarfile.PAX_FORMAT) as tar:
    for path in sorted(paths):
        info = tar.gettarinfo(path, arcname=os.path.relpath(path, stage))
        info.mtime = 0
        info.uid = info.gid = 0
        info.uname = info.gname = ""
        info.mode = 0o755 if (info.isdir() or info.mode & 0o111) else 0o644
        if info.isfile():
            with open(path, "rb") as f:
                tar.addfile(info, f)
        else:
            tar.addfile(info)
PY

# A tarball that does not carry the CLI is a green step that installs nothing — assert the one
# file everything else depends on is inside, never "tar exited 0".
# 🚨 List to a variable, then grep — never `tar -tzf … | grep -q`: grep -q exits on its first match
# and closes the pipe, tar dies with "stdout: write error", and under pipefail the check reports
# the CLI as MISSING from a tarball that carries it (main's first tap publish, 2026-08-30).
entries="$(tar -tzf "$out")"
if ! grep -qxF "memex-local-${version}/deploy/homebrew/bin/memex-local" <<<"$entries"; then
  echo "tarball $out does not contain deploy/homebrew/bin/memex-local" >&2
  exit 1
fi
if ! grep -qxF "memex-local-${version}/deploy/helm/Chart.yaml" <<<"$entries"; then
  echo "tarball $out does not contain deploy/helm/Chart.yaml — the vendored chart is missing" >&2
  exit 1
fi
printf '%s\n' "$out"
