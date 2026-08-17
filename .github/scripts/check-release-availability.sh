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

USAGE="usage: check-release-availability.sh <release-version|--identity <id>> [<source> ...]"

IDENTITY=""
VERSION=""
if [ "${1:-}" = "--identity" ]; then
  IDENTITY="${2:?$USAGE}"
  shift 2
else
  VERSION="${1:?$USAGE}"
  shift
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
    die "release '$VERSION' has no marker at $ROOT/$RELEASES_DIR/$VERSION — its framework identity is unknown, so NO package can be shown available for it. Cannot determine ≠ clear to proceed."
  fi
  IDENTITY=$(tr -d '[:space:]' < "$MARKER_LOCAL")
  [ -n "$IDENTITY" ] || die "the release marker for '$VERSION' is empty — the producer recorded no framework identity."
  echo "release $VERSION → framework identity $IDENTITY"
fi

# ── Assert every named source is SEALED under that identity ─────────────────────────────────────
# Keyed on the sentinel, never on "the directory exists": the sentinel is written strictly LAST, so
# a publish that died mid-way leaves a directory the portal's own seeder refuses. Counting it here
# would clear a release the portal then recompiles — the very outcome the gate exists to prevent.
MISSING=()
for source in "${SOURCES[@]}"; do
  exists=$(az storage file exists --account-name "$ACCOUNT" --share-name "$SHARE" \
    --path "$ROOT/$IDENTITY/$source/$SENTINEL" --auth-mode login --backup-intent \
    --query exists -o tsv --only-show-errors 2>/dev/null || echo "unknown")
  case "$exists" in
    true)  echo "sealed: $source (identity $IDENTITY)"; summary "- ✅ \`$source\` is published for \`$IDENTITY\`";;
    false) MISSING+=("$source — no sealed publication under $ROOT/$IDENTITY/$source");;
    # 🚨 An errored probe is NOT an absent one, and it is NOT a present one either. Both readings
    # would be a lie; the honest answer is a hold naming the unreadability.
    *)     MISSING+=("$source — the share could not be queried, so availability CANNOT BE DETERMINED");;
  esac
done

if [ "${#MISSING[@]}" -gt 0 ]; then
  echo "::error::release availability: ${#MISSING[@]} of ${#SOURCES[@]} source(s) are not available for framework identity $IDENTITY${VERSION:+ (release $VERSION)}."
  for m in "${MISSING[@]}"; do echo "::error::  • $m"; summary "- ❌ $m"; done
  exit 1
fi

echo "release availability: all ${#SOURCES[@]} source(s) are published for identity $IDENTITY${VERSION:+ (release $VERSION)}."
summary "- ✅ all ${#SOURCES[@]} source(s) published for \`$IDENTITY\`"
