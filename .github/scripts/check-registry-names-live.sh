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
#   tag and the live commit. It repairs nothing: delivering the live share publication from a run
#   that did not bake it is a separate change (filed to triage), and a red here is what makes the
#   stale tag visible instead of a green that hides it.
#
# ENVIRONMENT: BUNDLE_REGISTRY, SOURCE, IDENTITY, LIVE_COMMIT (required); GH_TOKEN +
# BAKE_CONTENT_REPOSITORY (to order a different commit); PUSH_BUNDLE_PUBLICATION (the publisher to
# ask; defaults to the one beside this file — the harness substitutes a recorder).
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PUSH="${PUSH_BUNDLE_PUBLICATION:-$HERE/push-bundle-publication.sh}"
: "${BUNDLE_REGISTRY:?BUNDLE_REGISTRY is required}"
: "${SOURCE:?SOURCE is required}"
: "${IDENTITY:?IDENTITY is required}"
: "${LIVE_COMMIT:?LIVE_COMMIT is required (the commit the live share publication records)}"
tag="$BUNDLE_REGISTRY/plugins/$SOURCE:$IDENTITY"

answer=$("$PUSH" --registry "$BUNDLE_REGISTRY" --source "$SOURCE" --identity "$IDENTITY" --published-commit)
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
echo "::error title=Superseded, but the registry does not carry the live publication::this run was superseded under the publication lock and publishes nothing — but $why. The shares serve $LIVE_COMMIT; the run that sealed it did not complete its registry push. Re-run THAT run's registry push (or the next publication of newer content republishes both). Not green over a stale tag."
exit 1
