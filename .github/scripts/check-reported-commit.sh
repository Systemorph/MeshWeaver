#!/usr/bin/env bash
#
# Does a RUNNING portal report the commit this run built?
#
#   .github/scripts/check-reported-commit.sh <base-url> <expected-sha> [<label>]
#   exit 0 = /api/version reports exactly <expected-sha>   exit 1 = anything else, or unreadable
#
# The commit a portal reports is ASSEMBLY metadata (MeshWeaver.Mesh.PlatformBuildInfo.CommitHash,
# stamped by AddCommitHashMetadata from the CHECKED-OUT core tree's SourceRevisionId). It is not in
# the image config, so check-image-build-identity.sh, which reads the config out of the registry,
# cannot see it: the only place to read it is the booted process, and this is that reading.
#
# 🚨 The expected commit is the gate's TARGET (`needs.gate.outputs.sha`), never the run's head_sha.
# A reconcile run builds the newest commit CI has vouched for, which can be older than main's tip:
# CD run 12472 (head a23f340) built and correctly reported 74de6b7, and the reading was taken for a
# stale stamp (#7913). Comparing against the head would turn that correct image red.
#
# 🚨 FAIL CLOSED: no answer, a non-JSON answer, an absent or empty `commit`, a `version` whose
# `+<sha>` build metadata disagrees with `commit` — every one is exit 1. "Could not read" is never
# reported as "matches".
set -uo pipefail

BASE="${1:?usage: check-reported-commit.sh <base-url> <expected-sha> [<label>]}"
EXPECTED="${2:?usage: check-reported-commit.sh <base-url> <expected-sha> [<label>]}"
LABEL="${3:-$BASE}"

die() { echo "::error::$LABEL: $1"; exit 1; }

# A full 40-hex sha only: a short or empty expectation would make the comparison below meaningless.
[[ "$EXPECTED" =~ ^[0-9a-f]{40}$ ]] || die "expected commit '$EXPECTED' is not a full 40-character sha — refusing to compare against it"

BODY=$(curl -fsS --max-time 10 "$BASE/api/version" 2>/dev/null) || die "GET $BASE/api/version did not answer 200"
COMMIT=$(jq -r '.commit // empty' <<<"$BODY" 2>/dev/null) || die "/api/version answered something that is not JSON: $BODY"
VERSION=$(jq -r '.version // empty' <<<"$BODY" 2>/dev/null)

[ -n "$COMMIT" ] || die "/api/version reports no commit ($BODY) — the build carried no source-control stamp"
[ "$COMMIT" = "$EXPECTED" ] || die "/api/version reports commit $COMMIT but this run built $EXPECTED ($BODY)"
case "$VERSION" in
  *+*) [ "${VERSION##*+}" = "$EXPECTED" ] || die "/api/version's version '$VERSION' carries build metadata '${VERSION##*+}', not the reported commit $COMMIT" ;;
esac

echo "$LABEL: /api/version reports commit $COMMIT (version $VERSION) — the commit this run built"
