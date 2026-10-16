#!/usr/bin/env bash
# bake-publication-lock.sh -- <command> [args…]
#
# Runs <command> while holding the per-prefix PUBLICATION LOCK (MeshWeaver#6877) for
# prebuilt-bundles/<IDENTITY>/<SOURCE> — the same lease publish-bake-bundles.sh and
# rebind-bake-scope.sh take — on the FIRST target in the one canonical lock order (sorted,
# de-duplicated BAKE_PUBLISH_TARGETS).
#
# WHY (Copilot's review of #6878). The registry publisher (push-bundle-publication.sh) reads the
# identity tag, decides — skip, supersede, republish — and moves the tag LAST. Two publishers that
# overlap there can each read the tag before the other moves it, and the slower, OLDER one then moves
# it back: `C1` reads `C0`, pauses in its upload, `C2` tags and finishes, `C1` tags over it. Ordering
# the two commits cannot help — each compared against what it read. So the whole read–decide–tag
# sequence runs under the same lease every publisher of the prefix takes, and the second one re-reads
# the tag only after the first has moved it.
#
# A caller that already HOLDS the prefix across steps (node-repo-publish-bake.yml's re-bind step
# passes its holder as BAKE_PUBLISH_LOCK_HOLDER) is PROVED to hold it rather than acquiring again,
# and the release stays with that caller. Otherwise this script acquires under an invocation-unique
# holder and releases on every exit; a release that fails, fails the command's step (a lease left
# behind is infinite), preserving the command's own failure when there was one.
#
# ENVIRONMENT: BAKE_PUBLISH_TARGETS, IDENTITY, SOURCE (required); BAKE_PUBLISH_LOCK_HOLDER and
# PUBLISH_BAKE_PYTHON (optional). AUTH as publish-bake-bundles.sh: `az login` already happened.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HELPER="$HERE/publish-bake-files.py"
[ "${1:-}" = "--" ] || { echo "::error::usage: bake-publication-lock.sh -- <command> [args…]"; exit 2; }
shift
[ "$#" -gt 0 ] || { echo "::error::bake-publication-lock.sh: no command to run under the lock"; exit 2; }
: "${BAKE_PUBLISH_TARGETS:?BAKE_PUBLISH_TARGETS is required}"
: "${IDENTITY:?IDENTITY is required}"
: "${SOURCE:?SOURCE is required}"

# The pinned SDK — ONE home for the pins: SDK_PINS in publish-bake-bundles.sh, read from there.
if [ -n "${PUBLISH_BAKE_PYTHON:-}" ]; then
  PYTHON="$PUBLISH_BAKE_PYTHON"
else
  PINS=$(sed -n 's/^SDK_PINS=(\(.*\))$/\1/p' "$HERE/publish-bake-bundles.sh" | tr -d '"')
  [ -n "$PINS" ] || { echo "::error::no SDK_PINS=( … ) in $HERE/publish-bake-bundles.sh — refusing to install an unpinned SDK."; exit 1; }
  VENV="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/publish-bake-venv"
  if [ ! -x "$VENV/bin/python" ]; then
    python3 -m venv "$VENV"
    # shellcheck disable=SC2086 # the pins are whitespace-separated words by construction
    "$VENV/bin/pip" install --quiet --disable-pip-version-check $PINS
  fi
  PYTHON="$VENV/bin/python"
  "$PYTHON" "$HELPER" sdk-check --pins "$PINS"
fi

# The FIRST target in the canonical order — the same order rebind-bake-scope.sh locks in, so a
# caller holding every target holds this one.
target=$(printf '%s\n' $BAKE_PUBLISH_TARGETS | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u | head -n 1)
account="${target%%/*}"; rest="${target#*/}"; share="${rest%%/*}"; base=""
case "$rest" in */*) base="${rest#*/}";; esac
if [ -z "$account" ] || [ -z "$share" ] || [ "$account" = "$target" ]; then
  echo "::error::malformed BAKE_PUBLISH_TARGETS entry '$target' — expected <account>/<share>[/<base-path>]"
  exit 1
fi
# Must match LOCK_PATH in publish-bake-bundles.sh and rebind-bake-scope.sh.
LOCK_PATH="${base:+$base/}prebuilt-bundles/$IDENTITY/_publishing.$SOURCE"

if [ -n "${BAKE_PUBLISH_LOCK_HOLDER:-}" ]; then
  "$PYTHON" "$HELPER" lock check --account "$account" --share "$share" --path "$LOCK_PATH" \
    --holder "$BAKE_PUBLISH_LOCK_HOLDER" < /dev/null
  "$@"
  exit $?
fi

HOLDER="$(printf '%s-%s-%s' \
  "${GITHUB_REPOSITORY:-local}" "${GITHUB_RUN_ID:-$$}" "${GITHUB_RUN_ATTEMPT:-$(date -u +%s)}" \
  | tr -c 'A-Za-z0-9._-' '-')-registry-pid$$-r$RANDOM$RANDOM"
"$PYTHON" "$HELPER" lock acquire --account "$account" --share "$share" --path "$LOCK_PATH" \
  --holder "$HOLDER" < /dev/null
release_on_exit() {
  local rc=$?
  if ! "$PYTHON" "$HELPER" lock release --account "$account" --share "$share" --path "$LOCK_PATH" \
       --holder "$HOLDER" < /dev/null; then
    echo "::error::the publication lock $account/$share/$LOCK_PATH could not be released — every other publisher of this prefix waits on it until it is recovered as abandoned."
    [ "$rc" -ne 0 ] || rc=1
  fi
  exit "$rc"
}
trap release_on_exit EXIT
"$@"
