#!/usr/bin/env bash
# check-registry-names-live.sh — the guard for the ONE path MeshWeaver.Plugins#1785 added that
# publishes nothing: a lane run SUPERSEDED under the publication lock (rebind-bake-scope.sh,
# `publish=false`). Run it under bake-publication-lock.sh.
#
# Such a run is green without touching the registry, so it must not stay green over a registry that
# does not carry what the shares serve — a sibling can seal the shares and then fail its registry
# push. This READS the tag (push-bundle-publication.sh --published-commit) and:
#   names LIVE_COMMIT in the current layout, or a NEWER commit containing it  → green, said;
#   anything else (stale commit, absent tag, a same-commit tag of an older layout) → RED, naming the
#   tag and the live commit. It repairs nothing on this path; a red here is what makes the stale tag
#   visible instead of a green that hides it.
#
# `--probe` asks the same question for deliver-live-publication.sh (the scope=none path, MeshWeaver#7323),
# which REPAIRS a stale tag: verified → exit 0; not verified → "stale: <why>" on stdout and exit 3
# (no ::error — the caller decides); a registry that cannot be READ → exit 1 either way, because an
# unreadable registry is never "stale" and never "verified". ONE decision, two callers: the
# same-commit-older-layout rule (Copilot on #6878) cannot hold for one path and not the other.
#
# ENVIRONMENT: BUNDLE_REGISTRY, SOURCE, IDENTITY, LIVE_COMMIT (required); GH_TOKEN +
# BAKE_CONTENT_REPOSITORY (to order a different commit); PUSH_BUNDLE_PUBLICATION (the publisher to
# ask; defaults to the one beside this file — the harness substitutes a recorder).
set -euo pipefail

PROBE=0
case "${1:-}" in
  --probe) PROBE=1 ;;
  '') ;;
  *) echo "::error::check-registry-names-live.sh: unknown argument '$1' (only --probe)"; exit 2 ;;
esac

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUSH="${PUSH_BUNDLE_PUBLICATION:-$HERE/push-bundle-publication.sh}"
: "${BUNDLE_REGISTRY:?BUNDLE_REGISTRY is required}"
: "${SOURCE:?SOURCE is required}"
: "${IDENTITY:?IDENTITY is required}"
: "${LIVE_COMMIT:?LIVE_COMMIT is required (the commit the live share publication records)}"
tag="$BUNDLE_REGISTRY/plugins/$SOURCE:$IDENTITY"

# A read that FAILS is refused by name here — it must never fall through as an empty answer, which
# is what an ABSENT tag prints (and --probe would then "repair" a registry it cannot read).
if ! answer=$("$PUSH" --registry "$BUNDLE_REGISTRY" --source "$SOURCE" --identity "$IDENTITY" --published-commit); then
  echo "::error::the registry tag $tag could not be READ (see above) — whether it carries the live publication cannot be established. Not green, and not a repair either."
  exit 1
fi
read -r named layout current <<< "${answer:-- - -}"

if [ "$named" = "$LIVE_COMMIT" ] && [ "$layout" = "$current" ]; then
  echo "registry: $tag names $LIVE_COMMIT (layout $layout) — the publication the shares serve. Verified."
  exit 0
fi
if [ "$named" != "-" ] && [ "$named" != "$LIVE_COMMIT" ] && [ -n "${GH_TOKEN:-}" ] && [ -n "${BAKE_CONTENT_REPOSITORY:-}" ]; then
  if order=$(gh api "repos/$BAKE_CONTENT_REPOSITORY/compare/$LIVE_COMMIT...$named" --jq .status 2>&1) \
     && [ "$order" = "ahead" ]; then
    echo "registry: $tag names $named, NEWER than the shares' $LIVE_COMMIT and containing it. Verified."
    exit 0
  fi
fi
if [ "$named" = "-" ]; then
  why="the tag is ABSENT"
elif [ "$named" = "$LIVE_COMMIT" ]; then
  why="the tag names $LIVE_COMMIT but in layout $layout, not the current $current — a same-commit publication the registry publisher would republish"
else
  why="the tag names $named, not the live $LIVE_COMMIT (nor a newer commit containing it)"
fi
if [ "$PROBE" -eq 1 ]; then
  echo "stale: $why"
  exit 3
fi
echo "::error title=Superseded, but the registry does not carry the live publication::this run was superseded under the publication lock and publishes nothing — but $why. The shares serve $LIVE_COMMIT; the run that sealed it did not complete its registry push. Re-run THAT run's registry push (or the next publication of newer content republishes both). Not green over a stale tag."
exit 1
