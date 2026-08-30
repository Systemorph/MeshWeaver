#!/usr/bin/env bash
# Render the PUBLISHED memex-local formula from the template in ../Formula/memex-local.rb: the same
# formula with a concrete version and a stable `url` + `sha256` (the tarball make-tarball.sh
# produced, attached to a release of the tap, or a file:// path on a CI runner).
#
# Usage: render-formula.sh <version> <url> <sha256>   → the formula on stdout
#
# The template is the single source of truth — dependencies, install steps, caveats and the test
# block are never duplicated here; only the three lines that differ between "HEAD of a checkout"
# and "a released tarball" are rewritten. The template stays HEAD-capable, so a checkout can still
# install the tip of main from it directly.
set -euo pipefail

version="${1:?usage: render-formula.sh <version> <url> <sha256>}"
url="${2:?usage: render-formula.sh <version> <url> <sha256>}"
sha="${3:?usage: render-formula.sh <version> <url> <sha256>}"

case "$version" in
  *[!0-9.]*|"") echo "version must be dotted numerics (got '$version')" >&2; exit 1 ;;
esac
case "$sha" in
  [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]*) [ "${#sha}" -eq 64 ] || { echo "sha256 must be 64 hex chars" >&2; exit 1; } ;;
  *) echo "sha256 must be 64 hex chars (got '$sha')" >&2; exit 1 ;;
esac

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
template="$here/../Formula/memex-local.rb"
[ -f "$template" ] || { echo "template not found: $template" >&2; exit 1; }

# 🚨 The rendered formula must still contain the lines it edits; a template that renamed one would
# otherwise render silently unchanged and the tap would publish the OLD version forever.
grep -q '^  version "' "$template" || { echo "template has no version line" >&2; exit 1; }
grep -q '^  head "'    "$template" || { echo "template has no head line" >&2; exit 1; }

# The tarball is named memex-local-<version>.tar.gz, so Homebrew scans the version FROM THE URL —
# `brew audit --strict` refuses an explicit `version` beside it as redundant, and wants `url` before
# `license`. The template's `version` line therefore becomes the `url` + `sha256` pair, in place
# (desc → homepage → url → sha256 → license → head, the order audit expects).
case "$url" in
  *"memex-local-${version}.tar.gz") ;;
  *) echo "url must end in memex-local-${version}.tar.gz so Homebrew scans the same version (got '$url')" >&2; exit 1 ;;
esac

awk -v url="$url" -v sha="$sha" '
  /^  version "/ {
    print "  url \"" url "\""
    print "  sha256 \"" sha "\""
    next
  }
  { print }
' "$template"
