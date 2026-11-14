#!/usr/bin/env bash
# ensure-portal-ai-base.sh — make the portal-ai BASE that THIS tree describes exist in the registry,
# and print its content tag. Every image layered on the base (main-cd's portal-ai and control legs,
# edge-images) resolves its ContainerBaseImage through this script, never through a moving tag.
#
#   ensure-portal-ai-base.sh --registry <host> --acr-name <name> [--force]
#       stdout: the content tag (src-<16 hex>); also written as `tag=` to $GITHUB_OUTPUT when set
#
# WHY (portal-ai-base.py carries the full account): CD layered a net11.0 app on `:latest`, which
# still held the aspnet:10.0 base built before the tree's Dockerfile moved to 11.0, and the host
# could not start. The tag is now a function of deploy/base-images/portal-ai, so a Dockerfile change
# IS a new base, built here the first time any run needs it; a tree whose base already exists pays
# one tag listing. --force rebuilds an existing tag (the hand runbook, to refresh the bundled CLIs).
#
# Fails RED on every unknown: a tag listing that cannot be read is never read as "absent" (that would
# rebuild on every registry hiccup) nor as "present" (that would layer on nothing).
set -euo pipefail

REGISTRY="" ACR_NAME="" FORCE=false
while [ $# -gt 0 ]; do
  case "$1" in
    --registry) REGISTRY="$2"; shift 2 ;;
    --acr-name) ACR_NAME="$2"; shift 2 ;;
    --force) FORCE=true; shift ;;
    *) echo "::error::unknown argument '$1'" >&2; exit 2 ;;
  esac
done
[ -n "$REGISTRY" ] && [ -n "$ACR_NAME" ] || { echo "::error::--registry and --acr-name are required" >&2; exit 2; }

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
REPO=memex-portal-ai-base

# The tree must be coherent BEFORE anything is built: aspnet runtime == the platform's TFM.
python3 "$HERE/portal-ai-base.py" check --root "$ROOT" >&2
TAG="$(python3 "$HERE/portal-ai-base.py" tag --root "$ROOT")"
IMAGE="$REGISTRY/$REPO:$TAG"

# `show-tags` on a repository that does not exist yet is an error too; tell that one apart from a
# failed read by asking whether the repository is listed at all.
present=false
if tags="$(az acr repository show-tags -n "$ACR_NAME" --repository "$REPO" -o tsv 2>/dev/null)"; then
  grep -qx "$TAG" <<<"$tags" && present=true
else
  repos="$(az acr repository list -n "$ACR_NAME" -o tsv)" || { echo "::error::could not list $ACR_NAME's repositories — refusing to guess whether $IMAGE exists" >&2; exit 1; }
  if grep -qx "$REPO" <<<"$repos"; then
    echo "::error::$REPO exists in $ACR_NAME but its tags could not be read — refusing to guess whether $TAG exists" >&2
    exit 1
  fi
fi

if [ "$present" = true ] && [ "$FORCE" = false ]; then
  echo "base $IMAGE already exists — built from this tree's deploy/base-images/portal-ai" >&2
else
  echo "base $IMAGE $([ "$present" = true ] && echo "rebuilt (--force)" || echo "is ABSENT — building it from deploy/base-images/portal-ai (multi-arch)")" >&2
  docker buildx build --platform linux/amd64,linux/arm64 -t "$IMAGE" --push "$ROOT/deploy/base-images/portal-ai" >&2
fi

# Both architectures must be in the index: the arm64 publish leg layers on the arm64 child, and a
# single-arch base fails it (or, worse, a present tag from a flattened push would).
arches="$(docker buildx imagetools inspect --raw "$IMAGE" | python3 -c 'import json,sys; print("\n".join(m.get("platform",{}).get("architecture","") for m in json.load(sys.stdin).get("manifests",[])))')"
for a in amd64 arm64; do
  grep -qx "$a" <<<"$arches" || { echo "::error::$IMAGE has no linux/$a child (children: $(paste -sd, - <<<"$arches")) — every publish leg needs both" >&2; exit 1; }
done
echo "OK: $IMAGE is a multi-arch index (linux/amd64 + linux/arm64)" >&2

[ -n "${GITHUB_OUTPUT:-}" ] && echo "tag=$TAG" >> "$GITHUB_OUTPUT"
echo "$TAG"
