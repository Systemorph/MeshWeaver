#!/usr/bin/env bash
# bake-publication-lock.sh -- <command> [args…]
#
# Runs <command> while holding the per-prefix PUBLICATION LOCK (MeshWeaver#6877) for
# prebuilt-bundles/<IDENTITY>/<SOURCE> — the same lease publish-bake-bundles.sh and
# rebind-bake-scope.sh take — on EVERY target, in the one canonical lock order (sorted,
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

# 🚨 EVERY configured target, in the ONE canonical order — not "the first one" (Copilot's review of
# #6878). Two callers can configure DIFFERENT target sets for the same source × identity: a list
# change in flight (an older run with `A B`, a newer one with `B`), or two repositories publishing
# one prefix with their own variables. "The first sorted target" is then A for one and B for the
# other, both enter the read–decide–tag, and the slower, older one moves the tag back. Locking every
# target means any two publishers whose target sets SHARE a share exclude each other on it; sorted,
# so differently ordered lists cannot wait on each other (rebind-bake-scope.sh locks the same way).
# A target that cannot be locked (a share that is gone, a holder that outlasts the bound) is named
# and skipped — a dead target must not starve the registry push — but at least ONE must be held.
# The one case this cannot exclude is two publishers with DISJOINT target sets: they share no share
# to hold a lease on, and that would need a declared lock location outside the portal shares.
TARGETS=$(printf '%s\n' $BAKE_PUBLISH_TARGETS | sed '/^[[:space:]]*$/d' | LC_ALL=C sort -u)
lock_of() { # <target> → "<account> <share> <lock path>" (must match publish-bake-bundles.sh)
  local t="$1" a r s b
  a="${t%%/*}"; r="${t#*/}"; s="${r%%/*}"; b=""
  case "$r" in */*) b="${r#*/}";; esac
  if [ -z "$a" ] || [ -z "$s" ] || [ "$a" = "$t" ]; then
    echo "::error::malformed BAKE_PUBLISH_TARGETS entry '$t' — expected <account>/<share>[/<base-path>]" >&2
    return 1
  fi
  printf '%s %s %s\n' "$a" "$s" "${b:+$b/}prebuilt-bundles/$IDENTITY/_publishing.$SOURCE"
}

HELD=()
if [ -n "${BAKE_PUBLISH_LOCK_HOLDER:-}" ]; then
  # The caller holds the prefix across steps: PROVE it, target by target; the release stays with it.
  for t in $TARGETS; do
    line=$(lock_of "$t") || continue
    read -r account share path <<< "$line"
    if "$PYTHON" "$HELPER" lock check --account "$account" --share "$share" --path "$path" \
         --holder "$BAKE_PUBLISH_LOCK_HOLDER" < /dev/null; then
      HELD+=("$t")
    else
      echo "::warning::the caller does not hold the publication lock on $t — that target does not take part in excluding other publishers from this step."
    fi
  done
  [ "${#HELD[@]}" -gt 0 ] || { echo "::error::the caller holds the publication lock on none of '$BAKE_PUBLISH_TARGETS' (as $BAKE_PUBLISH_LOCK_HOLDER) — refusing to run '$1' unserialised."; exit 1; }
  "$@"
  exit $?
fi

HOLDER="$(printf '%s-%s-%s' \
  "${GITHUB_REPOSITORY:-local}" "${GITHUB_RUN_ID:-$$}" "${GITHUB_RUN_ATTEMPT:-$(date -u +%s)}" \
  | tr -c 'A-Za-z0-9._-' '-')-registry-pid$$-r$RANDOM$RANDOM"
release_on_exit() {
  local rc=$? t line account share path
  for t in ${HELD[@]+"${HELD[@]}"}; do
    line=$(lock_of "$t") || continue
    read -r account share path <<< "$line"
    if ! "$PYTHON" "$HELPER" lock release --account "$account" --share "$share" --path "$path" \
         --holder "$HOLDER" < /dev/null; then
      echo "::error::the publication lock $account/$share/$path could not be released — every other publisher of this prefix waits on it until it is recovered as abandoned."
      [ "$rc" -ne 0 ] || rc=1
    fi
  done
  exit "$rc"
}
trap release_on_exit EXIT
for t in $TARGETS; do
  line=$(lock_of "$t") || continue
  read -r account share path <<< "$line"
  if "$PYTHON" "$HELPER" lock acquire --account "$account" --share "$share" --path "$path" \
       --holder "$HOLDER" < /dev/null; then
    HELD+=("$t")
  else
    echo "::error::the publication lock on $t could not be taken (see above) — going ahead under the targets that ARE held, so a dead target cannot starve this step."
  fi
done
[ "${#HELD[@]}" -gt 0 ] || { echo "::error::not one publication lock of '$BAKE_PUBLISH_TARGETS' could be taken — refusing to run '$1' unserialised."; exit 1; }
"$@"
