#!/usr/bin/env bash
#
# install-oras.sh [<directory>]
#
# Installs the PINNED ORAS release — the OCI artifact client every bundle publisher and every
# bundle consumer in the fleet uses (Doc/Architecture/PluginBundlesInTheRegistry) — from the
# project's GitHub release, verified against a sha256 recorded here per platform, into
# <directory> (default: $RUNNER_TEMP/oras-<version>, or /tmp/… outside Actions). Prints
# `oras=<path>` on its last line, writes `path=<path>` to $GITHUB_OUTPUT and appends the directory
# to $GITHUB_PATH when those exist, so a workflow step after it can call `oras` bare.
#
# 🚨 ONE PIN, ONE HOME. The bake lanes (main-cd.yml's `publish-bake` and `plugins-bake`, the
# satellites' node-repo-publish-bake.yml) and the chart-gate harness (test-bundle-registry.py) all
# install through THIS script, so the version the harness executes the publisher with is the
# version the lanes publish with. A second pin table anywhere else is drift waiting to happen.
#
# 🚨 The runner's own ORAS, when it has one, is deliberately NOT used: hosted-runner images move
# their tool versions without notice, and an unpinned client is an input nobody can reproduce.
# `$ORAS` naming a binary is honoured — that is the harness's local-developer override — but it
# must report exactly the pinned version, or this refuses.
#
# 🚨 FAIL CLOSED, with a positive signal: the download must exist, the checksum must match, and the
# installed binary must ANSWER `oras version` with the pinned number. "The tarball extracted" is not
# evidence that the binary runs on this platform.
set -euo pipefail

ORAS_VERSION="1.3.4"

die() { echo "::error::install-oras: $1"; exit 1; }

DEST="${1:-${RUNNER_TEMP:-/tmp}/oras-$ORAS_VERSION}"
system=$(uname -s | tr '[:upper:]' '[:lower:]')
machine=$(uname -m)
case "$system/$machine" in
  linux/x86_64)              arch=amd64; sha="f27adb935022d94df8dc77719c322dda592c78a0d57a6f7dcdd8d900b248c454" ;;
  linux/aarch64|linux/arm64) arch=arm64; sha="15702c6e3a4a56a8bd8ac5c17efdbcab56d9bada661ccbcf017f5b10c1d89399" ;;
  darwin/arm64)              arch=arm64; sha="217761a9500242ff473de8656b5aca21136ff39e17e9e61fd8936bbfd902704c" ;;
  darwin/x86_64)             arch=amd64; sha="5e964f3d5a36eb9499a9d3e252a86b09e7adf3e6f6447eec56fd249c6702af7e" ;;
  *) die "no pinned ORAS checksum for $system/$machine — add the release's sha256 for it here (https://github.com/oras-project/oras/releases/tag/v$ORAS_VERSION)" ;;
esac

sha256_of() { # <file> — hex digest, portable across the CI runner and a developer's macOS
  if command -v sha256sum > /dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    shasum -a 256 "$1" | awk '{print $1}'
  fi
}

reported_version() { # <binary> — the number `oras version` prints, or nothing
  "$1" version 2>/dev/null | sed -n 's/^Version:[[:space:]]*\([0-9][0-9.]*\).*/\1/p' | head -n 1
}

finish() { # <binary>
  local bin="$1" got
  got=$(reported_version "$bin")
  [ "$got" = "$ORAS_VERSION" ] || die "$bin reports version '${got:-<none>}', not the pinned $ORAS_VERSION — refusing to hand a lane an unpinned client"
  if [ -n "${GITHUB_PATH:-}" ]; then dirname "$bin" >> "$GITHUB_PATH"; fi
  if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "path=$bin" >> "$GITHUB_OUTPUT"; fi
  echo "oras=$bin"
}

# A binary handed in by the caller (the harness's local override) is accepted only at the pin.
if [ -n "${ORAS:-}" ] && [ "$ORAS" != "oras" ] && command -v "$ORAS" > /dev/null 2>&1; then
  echo "install-oras: using \$ORAS=$ORAS"
  finish "$(command -v "$ORAS")"
  exit 0
fi

# Already installed at the pin in this directory (a second step in the same job): reuse it.
if [ -x "$DEST/oras" ] && [ "$(reported_version "$DEST/oras")" = "$ORAS_VERSION" ]; then
  echo "install-oras: $DEST/oras is already at $ORAS_VERSION"
  finish "$DEST/oras"
  exit 0
fi

name="oras_${ORAS_VERSION}_${system}_${arch}.tar.gz"
url="https://github.com/oras-project/oras/releases/download/v${ORAS_VERSION}/${name}"
mkdir -p "$DEST"
tgz="$DEST/$name"
curl -fsSL --retry 3 --retry-delay 5 -o "$tgz" "$url" || die "could not download $url"
actual=$(sha256_of "$tgz")
[ "$actual" = "$sha" ] || die "$name sha256 $actual != pinned $sha — refusing to install an unverified binary"
tar -xzf "$tgz" -C "$DEST" oras || die "could not extract oras from $name"
rm -f "$tgz"
chmod 0755 "$DEST/oras"
echo "install-oras: $ORAS_VERSION for $system/$arch from $url (sha256 verified)"
finish "$DEST/oras"
