#!/usr/bin/env bash
# rebind-bake-scope.sh            hold the publication lock on every target, then re-bind the bake's
#                                 scope to the publication that is live NOW
# rebind-bake-scope.sh --release  release the locks this run took (an `if: always()` step)
#
# WHY (MeshWeaver.Plugins#1785). A narrowed bake reads the sealed publication BEFORE it compiles
# (bake-scope.sh: the listing to carry forward, the baseline commit, the publication stamp the carry
# is bound to) and only carries and publishes ~15 minutes later. Two main runs of one repo overlap
# routinely, so a sibling often seals in between, and carry-forward-bundles.sh then — correctly —
# refuses: "The publication moved between the read and the carry". Three times on one day
# (Plugins jobs 116960864159, 116999419620, 117031138508), each leaving main red for a publication
# that was perfectly fine.
#
# The refusal is right and stays. What was missing is a way for the LATER run to re-read under
# exclusion instead of failing. So this script, run just before the carry steps:
#
#   1. takes the per-prefix publication lock (publish-bake-files.py `lock`, MeshWeaver#6877) on
#      EVERY target, in BAKE_PUBLISH_TARGETS order, and hands its token to the publish step as
#      BAKE_PUBLISH_LOCK_HOLDER — so from here to the seal no current publisher can move anything;
#   2. re-runs bake-scope.sh against the shelf as it is NOW, into a fresh state directory;
#   3. decides:
#        unchanged   the live publication is the one the scope read  → carry and publish as before
#        (a) superseded  the live publication already contains this content (bake-scope says `none`:
#                    the same commit, a newer one containing it, or nothing between it and this
#                    commit reaches a module) → publish NOTHING; green, and said so
#        (b) rebound the live publication moved, and every module the fresh scope calls affected was
#                    REBUILT by this bake (or this bake was full) → the fresh state replaces the old
#                    one, so the carry takes the live publication's bundles, bound to its stamp
#        (c) refused anything else — the fresh scope wants a module this bake did not rebuild (a
#                    revert between the two baselines is the measured shape), or a full bake →
#                    RED, naming why; nothing has been written
#
# 🚨 (b) is sound only because of the subset test. A module this bake did not rebuild is carried
# from the live publication; that is right exactly when nothing between the live baseline and this
# commit touches it — which is what "the fresh scope does not call it affected" means, computed by
# the same selector, over the same edges, from the live baseline.
#
# Release is a separate invocation because the lock must be held THROUGH the publish step; the
# workflow runs `--release` in an `if: always()` step, which GitHub runs on cancellation too.
#
# ENVIRONMENT — the same as bake-scope.sh (BAKE_PUBLISH_TARGETS, IDENTITY, SOURCE, HEAD_SHA,
# REPO_DIR, EVENT_NAME, STATE_DIR, WITHHOLD_PACKAGES), plus
#   ORIGINAL_SCOPE       the scope bake-scope.sh decided before the bake (narrowed | full)
#   PUBLISH_BAKE_PYTHON  optional: a python carrying the pinned SDK (else a venv is built here and
#                        exported to the later steps through GITHUB_ENV)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HELPER="$HERE/publish-bake-files.py"
: "${BAKE_PUBLISH_TARGETS:?BAKE_PUBLISH_TARGETS is required}"
: "${IDENTITY:?IDENTITY is required}"
: "${SOURCE:?SOURCE is required}"
: "${STATE_DIR:?STATE_DIR is required}"
STATE_DIR="${STATE_DIR%/}"
LOCKS_HELD="$STATE_DIR.locks-held"

# 🚨 THE HOLDER IS UNIQUE PER INVOCATION, not per run (Copilot's review of #6878): two jobs of one
# run share repository, run id and attempt, and a run-shaped holder would let one read the other's
# lease as its own. It starts with the run's token (so every log line still names the run), adds
# this process and a random part, and is PERSISTED beside the state directory — the release is a
# later step, i.e. another process, and must release exactly this holder's lease.
HOLDER_FILE="$STATE_DIR.lock-holder"
if [ "${1:-}" = "--release" ] && [ -s "$HOLDER_FILE" ]; then
  HOLDER="$(cat "$HOLDER_FILE")"
else
  HOLDER="$(printf '%s-%s-%s' \
    "${GITHUB_REPOSITORY:-local}" "${GITHUB_RUN_ID:-$$}" "${GITHUB_RUN_ATTEMPT:-$(date -u +%s)}" \
    | tr -c 'A-Za-z0-9._-' '-')-lane-pid$$-r$RANDOM$RANDOM"
fi

# The pinned SDK — ONE home for the pins: SDK_PINS in publish-bake-bundles.sh, read from there.
if [ -n "${PUBLISH_BAKE_PYTHON:-}" ]; then
  PYTHON="$PUBLISH_BAKE_PYTHON"
else
  PINS=$(sed -n 's/^SDK_PINS=(\(.*\))$/\1/p' "$HERE/publish-bake-bundles.sh" | tr -d '"')
  [ -n "$PINS" ] || { echo "::error::no SDK_PINS=( … ) in $HERE/publish-bake-bundles.sh — the two scripts are fetched together; refusing to install an unpinned SDK."; exit 1; }
  VENV="${RUNNER_TEMP:-$(dirname "$STATE_DIR")}/publish-bake-venv"
  if [ ! -x "$VENV/bin/python" ]; then
    python3 -m venv "$VENV"
    # shellcheck disable=SC2086 # the pins are whitespace-separated words by construction
    "$VENV/bin/pip" install --quiet --disable-pip-version-check $PINS
  fi
  PYTHON="$VENV/bin/python"
  "$PYTHON" "$HELPER" sdk-check --pins "$PINS"
  if [ -n "${GITHUB_ENV:-}" ]; then echo "PUBLISH_BAKE_PYTHON=$PYTHON" >> "$GITHUB_ENV"; fi
fi

lock_path_of() { # <target> → "<account> <share> <lock path>"
  local target="$1" account rest share base
  account="${target%%/*}"; rest="${target#*/}"; share="${rest%%/*}"; base=""
  case "$rest" in */*) base="${rest#*/}";; esac
  if [ -z "$account" ] || [ -z "$share" ] || [ "$account" = "$target" ]; then
    echo "::error::malformed BAKE_PUBLISH_TARGETS entry '$target' — expected <account>/<share>[/<base-path>]" >&2
    return 1
  fi
  # Must match LOCK_PATH in publish-bake-bundles.sh.
  printf '%s %s %s\n' "$account" "$share" "${base:+$base/}prebuilt-bundles/$IDENTITY/_publishing.$SOURCE"
}

if [ "${1:-}" = "--release" ]; then
  if [ ! -s "$HOLDER_FILE" ]; then
    # No holder was ever minted: the acquire step never reached its first lock, so there is nothing
    # this run could hold. Said, not guessed — releasing under a freshly minted holder is a no-op.
    echo "publication lock: no holder recorded at $HOLDER_FILE — this run acquired nothing; nothing to release."
    exit 0
  fi
  # Releases what THIS run recorded as held — and, when the record is missing (the acquire step died
  # before writing it), every target: releasing a lease this run does not hold is a named no-op.
  targets="$BAKE_PUBLISH_TARGETS"
  if [ -s "$LOCKS_HELD" ]; then targets=$(cat "$LOCKS_HELD"); fi
  rc=0
  for target in $targets; do
    read -r account share path < <(lock_path_of "$target") || { rc=1; continue; }
    "$PYTHON" "$HELPER" lock release --account "$account" --share "$share" --path "$path" \
      --holder "$HOLDER" < /dev/null || rc=1
  done
  rm -f "$LOCKS_HELD"
  [ "$rc" -ne 0 ] || rm -f "$HOLDER_FILE"
  exit "$rc"
fi

: "${ORIGINAL_SCOPE:?ORIGINAL_SCOPE is required (the scope bake-scope.sh decided before the bake)}"
case "$ORIGINAL_SCOPE" in
  narrowed|full) ;;
  *) echo "::error::ORIGINAL_SCOPE='$ORIGINAL_SCOPE' — only a bake that RAN (narrowed or full) has anything to carry or publish."; exit 1 ;;
esac

# ── 1. the lock, on every target, in one fixed order ─────────────────────────────────────────────
printf '%s\n' "$HOLDER" > "$HOLDER_FILE"
: > "$LOCKS_HELD"
for target in $BAKE_PUBLISH_TARGETS; do
  read -r account share path < <(lock_path_of "$target")
  "$PYTHON" "$HELPER" lock acquire --account "$account" --share "$share" --path "$path" \
    --holder "$HOLDER" < /dev/null
  echo "$target" >> "$LOCKS_HELD"
done
if [ -n "${GITHUB_ENV:-}" ]; then echo "BAKE_PUBLISH_LOCK_HOLDER=$HOLDER" >> "$GITHUB_ENV"; fi
echo "publication lock held on $(grep -c . "$LOCKS_HELD") target(s) as $HOLDER — held through the publish step."

out() { # <key> <value>
  echo "$1=$2"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then echo "$1=$2" >> "$GITHUB_OUTPUT"; fi
}

# ── 2. the scope decision again, against the shelf as it is NOW ──────────────────────────────────
FRESH="$STATE_DIR.under-lock"
rm -rf "$FRESH"
mkdir -p "$FRESH"
if ! fresh_out=$(STATE_DIR="$FRESH" GITHUB_OUTPUT="" GITHUB_ENV="" GITHUB_STEP_SUMMARY="" \
                 bash "$HERE/bake-scope.sh" 2>&1); then
  printf '%s\n' "$fresh_out" | sed 's/^/    /'
  echo "::error title=Refusing to publish — the live publication could not be re-read under the lock::bake-scope.sh failed while re-reading the publication under the lock (see above). Nothing has been written."
  exit 1
fi
printf '%s\n' "$fresh_out" | sed 's/^/    [re-read under the lock] /'
NEW_SCOPE=$(printf '%s\n' "$fresh_out" | sed -n 's/^scope=//p' | tail -n 1)
NEW_REASON=$(printf '%s\n' "$fresh_out" | sed -n 's/^reason=//p' | tail -n 1)

# What identifies "the publication the scope read": its stamp, its commit and its bundle listing.
# A missing file reads as empty in both, so "no publication then, none now" is unchanged.
publication_of() { # <state-dir>
  local f
  for f in published-publication.txt published-commit.txt published-bundles.txt; do
    printf '%s:' "$f"; cat "$1/$f" 2>/dev/null || true; printf '\n'
  done
}
if [ "$(publication_of "$STATE_DIR")" = "$(publication_of "$FRESH")" ]; then
  rm -rf "$FRESH"
  out rebind unchanged
  out publish true
  echo "the live publication is the one this bake's scope read ($(cat "$STATE_DIR/published-publication.txt" 2>/dev/null || echo 'none sealed')) — carrying and publishing as decided."
  exit 0
fi

was="$(cat "$STATE_DIR/published-publication.txt" 2>/dev/null || true) at $(cat "$STATE_DIR/published-commit.txt" 2>/dev/null || echo '<none>')"
now="$(cat "$FRESH/published-publication.txt" 2>/dev/null || true) at $(cat "$FRESH/published-commit.txt" 2>/dev/null || echo '<none>')"
echo "::notice title=The publication moved while this run baked::the scope was read from publication ${was}; the live one is now ${now}. Re-deciding under the lock (MeshWeaver.Plugins#1785)."

# ── 3a. the live publication already holds this content ──────────────────────────────────────────
if [ "$NEW_SCOPE" = "none" ]; then
  out rebind superseded
  out publish false
  out reason "$NEW_REASON"
  echo "::notice title=Superseded — nothing to publish::$NEW_REASON This run's bake is not published: the live publication ($now) already serves this content."
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '### ✅ Superseded while baking\n\nThe publication moved from %s to %s while this run baked, and it already holds this content: %s\n' \
      "$was" "$now" "$NEW_REASON" >> "$GITHUB_STEP_SUMMARY"
  fi
  rm -rf "$FRESH"
  exit 0
fi

# ── 3b / 3c. re-bind, but only over modules this bake actually rebuilt ───────────────────────────
refuse() {
  echo "::error title=Carry-forward refused — the publication moved and this bake cannot follow it::the scope was read from publication ${was}, the live one is ${now}. $1 Nothing has been written; the next run scopes from the live publication."
  exit 1
}
adopt() {
  rm -rf "$STATE_DIR.read-before-bake"
  mv "$STATE_DIR" "$STATE_DIR.read-before-bake"
  mv "$FRESH" "$STATE_DIR"
  out rebind rebound
  out publish true
  echo "::notice title=Re-bound to the live publication::$1 The carry now takes the live publication's bundles, bound to its stamp $(cat "$STATE_DIR/published-publication.txt" 2>/dev/null || echo '<unstamped>')."
}
if [ "$ORIGINAL_SCOPE" = "full" ]; then
  adopt "This bake was FULL, so every module was rebuilt and none is carried from the moved publication's compile; only its sealed listing (the per-package carry) is re-read."
  exit 0
fi
[ "$NEW_SCOPE" = "narrowed" ] \
  || refuse "Re-read under the lock, the scope is now '$NEW_SCOPE' (${NEW_REASON}), while this bake was narrowed — modules it did not rebuild would have to be."
[ -f "$STATE_DIR/affected.json" ] && [ -f "$FRESH/affected.json" ] \
  || refuse "The affected closure of the original or the fresh scope is missing, so whether this bake covers the live publication cannot be shown."
uncovered=$(jq -r --slurpfile was "$STATE_DIR/affected.json" \
  '.affected - ($was[0].affected) | join(" ")' "$FRESH/affected.json") \
  || refuse "The affected closures could not be compared."
if [ -n "$uncovered" ]; then
  refuse "From the live publication's baseline the selector calls '$uncovered' affected, and this bake did NOT rebuild it (it rebuilt: $(jq -r '.affected | join(" ")' "$STATE_DIR/affected.json")). Carrying the live publication's build of it would seal bytes that do not match this commit — a revert between the two baselines is the measured shape."
fi
adopt "Every module the selector calls affected from the live baseline ($(jq -r '.affected | join(" ")' "$FRESH/affected.json")) was rebuilt by this bake."
