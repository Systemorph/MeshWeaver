#!/usr/bin/env bash
# check-release-availability.sh <release-version|--identity <id>> [<source> ...]
#
# 🚨 THE SAME QUESTION `ReleaseAvailability.IsUpdatable` ANSWERS, asked from CI — is the target
# release actually served by the artifact store? — expressed against the very layout
# `publish-bake-bundles.sh` writes, and living beside it so the two can never drift.
#
#   exit 0 = every named source is SEALED for that release
#   exit 1 = it is not, or the answer could not be determined
#
# Two callers, one rule:
#   * main-cd's post-promote assertion (#1754): "the release we just armed has a published content
#     bake, so an environment rolled to it adopts instead of recompiling". No <source> given
#     defaults to `meshweaver-content`, the platform's own segment.
#   * the node repos' build gate (#1755): "every upstream I stage has published for the framework
#     I am about to build against". Callers pass --identity (resolved from the image they will bake
#     in) plus their upstream segments, e.g. `plugins`.
#
# 🚨 FAIL SAFE. "Cannot determine" is NOT "clear to proceed": an unresolvable identity, an
# unreadable share, or a missing marker all exit 1 with a reason that says which happened. An
# availability failure must never be reported as a compatibility verdict, and neither may pass.
#
# 🚨 THREE OUTCOMES, THREE HEADLINES — never one count (MeshWeaver#3583). They are greppable and
# they must stay that way; a test asserts on this text, because a gate whose arms cannot be told
# apart in a log is the defect:
#
#   CANNOT RESOLVE …   no identity could be resolved at all. A REFUSAL: there is no directory to
#                      ask about, so NOTHING was checked and no source may be called absent.
#   CANNOT DETERMINE … an identity was resolved, but one or more probes ERRORED. Also a refusal:
#                      with probes failing, "N of M absent" has an unknown denominator, so the
#                      absent count is reported only as a FLOOR, under this headline, never as the
#                      verdict.
#   release availability: N of M source(s) are not available …
#                      every probe answered, and the answer was NO. This is the only one that is a
#                      statement about an upstream, and the only one a reader should act on by
#                      waiting for or triggering that upstream.
#
# All three exit 1. Distinguishing them changes what the reader DOES, never whether the gate fails.
#
# 🚨 FAIL LOUD and DISTINGUISHABLE. Every refusal writes ::error:: lines AND a step summary naming
# exactly what is missing. GitHub renders a skipped job with the same tick as a passed one, so this
# script never exits 0 on "could not check" — the trapdoor AGENTS.md forbids.
#
# ENVIRONMENT
#   BAKE_PUBLISH_TARGETS   the same whitespace-separated <account>/<share>[/<base-path>] list the
#                          publisher writes to. Only the FIRST target is read: every target
#                          receives the identical publication, so one is the answer and querying
#                          all of them would only add ways to be flaky.
#
# AUTH: `az login` must already have happened (the CD jobs use OIDC); data-plane access uses
# --auth-mode login --backup-intent, i.e. the "Storage File Data Privileged Contributor" role.
set -uo pipefail

USAGE="usage: check-release-availability.sh <release-version|--identity <id>> [--identity-origin <text>] [<source> ...]"

IDENTITY=""
VERSION=""
# 🚨 WHY the caller states the origin: the identity is resolved somewhere else (a repo PIN, a wake
# payload, a moving release tag), and when this gate holds, "why is it waiting" is a question about
# THAT step, not this one. Without the origin in the log the reader has to guess which trigger path
# produced the identity, and the three paths want three different actions — move the pin, wait for
# the wave, or stop expecting a moving tag to converge (MeshWeaver#3583).
IDENTITY_ORIGIN=""
if [ "${1:-}" = "--identity" ]; then
  IDENTITY="${2:?$USAGE}"
  shift 2
else
  VERSION="${1:?$USAGE}"
  shift
fi
if [ "${1:-}" = "--identity-origin" ]; then
  IDENTITY_ORIGIN="${2:?$USAGE}"
  shift 2
fi

SOURCES=("$@")
[ "${#SOURCES[@]}" -eq 0 ] && SOURCES=("meshweaver-content")

# The marker directory must match PublishedBundleCatalogue.ReleaseMarkerDirectoryName and
# publish-bake-bundles.sh's RELEASES_DIR; the sentinel must match
# ShippedPrebuiltBundles.CompletionSentinelFileName.
RELEASES_DIR="_releases"
SENTINEL="_complete"

summary() { [ -n "${GITHUB_STEP_SUMMARY:-}" ] && echo "$1" >> "$GITHUB_STEP_SUMMARY"; return 0; }
die() { echo "::error::$1"; summary "- ❌ $1"; exit 1; }

if [ -z "${BAKE_PUBLISH_TARGETS:-}" ] || ! grep -q '[^[:space:]]' <<<"${BAKE_PUBLISH_TARGETS:-}"; then
  die "BAKE_PUBLISH_TARGETS is not set — the artifact store cannot be reached, so availability CANNOT BE DETERMINED. That is a hold, not a pass: provision the repo variable with the portals' storage targets (<account>/<share>[/<base-path>])."
fi

# shellcheck disable=SC2086
set -- $BAKE_PUBLISH_TARGETS
TARGET="$1"
ACCOUNT="${TARGET%%/*}"
REST="${TARGET#*/}"
SHARE="${REST%%/*}"
BASE=""
case "$REST" in */*) BASE="${REST#*/}";; esac
if [ -z "$ACCOUNT" ] || [ -z "$SHARE" ] || [ "$ACCOUNT" = "$TARGET" ]; then
  die "malformed BAKE_PUBLISH_TARGETS entry '$TARGET' — expected <account>/<share>[/<base-path>]"
fi
ROOT="${BASE:+$BASE/}prebuilt-bundles"

# ── Resolve the release's framework identity ────────────────────────────────────────────────────
# The identity is a property of the shipped BINARIES, so it can only be learned from what the
# producer recorded. A missing marker means exactly one thing: that release published no platform
# content bake. Guessing is the failure mode this marker exists to remove.
if [ -z "$IDENTITY" ]; then
  MARKER_LOCAL=$(mktemp -d)/marker
  if ! az storage file download --account-name "$ACCOUNT" --share-name "$SHARE" \
        --path "$ROOT/$RELEASES_DIR/$VERSION" --dest "$MARKER_LOCAL" \
        --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
    die "CANNOT RESOLVE a framework identity: release '$VERSION' has no marker at $ROOT/$RELEASES_DIR/$VERSION. This is a REFUSAL, not a verdict about any upstream — with no identity there is no directory to ask about, so nothing below was checked and NO source may be reported absent. Cannot determine ≠ clear to proceed."
  fi
  IDENTITY=$(tr -d '[:space:]' < "$MARKER_LOCAL")
  [ -n "$IDENTITY" ] || die "CANNOT RESOLVE a framework identity: the release marker for '$VERSION' is empty — the producer recorded none. This is a REFUSAL, not a verdict about any upstream; nothing below was checked."
  [ -n "$IDENTITY_ORIGIN" ] || IDENTITY_ORIGIN="the release marker at $ROOT/$RELEASES_DIR/$VERSION"
fi

# 🚨 Printed on EVERY path, before the first probe, whether or not anything is wrong. The identity
# is the directory name every answer below is keyed on, so a log that does not name it leaves the
# reader unable to tell "the upstream is late" from "we asked about the wrong identity" — and those
# have opposite fixes. Stated once, here, rather than only inside a refusal.
ORIGIN_TEXT="$IDENTITY_ORIGIN"
[ -n "$ORIGIN_TEXT" ] || ORIGIN_TEXT="the caller's --identity argument (origin not stated)"
echo "identity resolved: $IDENTITY — from $ORIGIN_TEXT"
summary "- 🎯 asking about framework identity \`$IDENTITY\` — from $ORIGIN_TEXT"

# ── Assert every named source is SEALED under that identity ─────────────────────────────────────
# Keyed on the sentinel, never on "the directory exists": the sentinel is written strictly LAST, so
# a publish that died mid-way leaves a directory the portal's own seeder refuses. Counting it here
# would clear a release the portal then recompiles — the very outcome the gate exists to prevent.
# 🚨 TWO BUCKETS, NEVER ONE. "This upstream has not published" and "I could not ask" are different
# failures with different fixes — wait for/trigger the upstream, versus fix the credential, the
# share or the network — and folding them into one count reported the second as the first. Both
# still exit 1: neither is a pass, and this gate never goes advisory (MeshWeaver#3583).
ABSENT=()       # asked, and the answer was NO
UNDETERMINED=() # could not ask — a REFUSAL, never a smaller number
for source in "${SOURCES[@]}"; do
  exists=$(az storage file exists --account-name "$ACCOUNT" --share-name "$SHARE" \
    --path "$ROOT/$IDENTITY/$source/$SENTINEL" --auth-mode login --backup-intent \
    --query exists -o tsv --only-show-errors 2>/dev/null || echo "unknown")
  case "$exists" in
    true)  echo "sealed: $source (identity $IDENTITY)"; summary "- ✅ \`$source\` is published for \`$IDENTITY\`";;
    false) ABSENT+=("$source — no sealed publication under $ROOT/$IDENTITY/$source");;
    # 🚨 An errored probe is NOT an absent one, and it is NOT a present one either. Both readings
    # would be a lie; the honest answer is a hold naming the unreadability.
    *)     UNDETERMINED+=("$source — the share could not be queried at $ROOT/$IDENTITY/$source");;
  esac
done

# Reported FIRST and on its own, because it invalidates the other number: when some probes errored,
# "N of M are not available" is not a measurement anyone may act on — the denominator is unknown.
if [ "${#UNDETERMINED[@]}" -gt 0 ]; then
  echo "::error::CANNOT DETERMINE release availability for framework identity $IDENTITY${VERSION:+ (release $VERSION)}: ${#UNDETERMINED[@]} of ${#SOURCES[@]} source(s) could not be queried. This is a REFUSAL, not a verdict — the gate could not ask its question, so nothing here says whether the upstream published. Fix the access to the artifact store (az login / BAKE_PUBLISH_TARGETS / the share) and re-run."
  for u in "${UNDETERMINED[@]}"; do echo "::error::  • $u"; summary "- ⛔ $u"; done
  if [ "${#ABSENT[@]}" -gt 0 ]; then
    echo "::error::  (also, ${#ABSENT[@]} source(s) answered NO — but with probes erroring that count is a floor, not the number.)"
    for m in "${ABSENT[@]}"; do echo "::error::    • $m"; done
  fi
  exit 1
fi

if [ "${#ABSENT[@]}" -gt 0 ]; then
  echo "::error::release availability: ${#ABSENT[@]} of ${#SOURCES[@]} source(s) are not available for framework identity $IDENTITY${VERSION:+ (release $VERSION)}."
  for m in "${ABSENT[@]}"; do echo "::error::  • $m"; summary "- ❌ $m"; done
  exit 1
fi

echo "release availability: all ${#SOURCES[@]} source(s) are published for identity $IDENTITY${VERSION:+ (release $VERSION)}."
summary "- ✅ all ${#SOURCES[@]} source(s) published for \`$IDENTITY\`"
