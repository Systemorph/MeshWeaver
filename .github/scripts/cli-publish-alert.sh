#!/usr/bin/env bash
# The ONE standing issue for "nuget.org does not carry the memex CLI of the build `3-latest` names"
# (#6843) — RECONCILED FROM THE TRUTH, never driven by events.
#
# An event-driven alert (open on this red, close on that green) is wrong whenever the events are lost,
# reordered or overlap: parallel per-version publications finish in any order, a concurrency group
# keeps only one pending reporter, and a late older success would close a newer failure (#6843
# review, three rounds). So this script takes no outcome at all. It reads two facts and derives the
# alert from them:
#
#   target    the version `3-latest` names — the `org.opencontainers.image.version` label of
#             ghcr.io/<owner>/memex-portal-ai:3-latest (public, read anonymously)
#   listed    whether nuget.org lists MeshWeaver.Cli at `target` (the registration leaf)
#
#   listed                                   → close the sentinel if open
#   not listed, a `Publish cli <target>` run
#     is queued or in progress                → leave it; that run's own reconcile decides
#   not listed, nothing in flight             → open / update the sentinel naming `target`
#
# Idempotent and order-independent: running it twice, late, or out of order gives the same answer,
# so a reporter that was dropped or superseded loses nothing — the next one reads the same truth.
# Callers still serialise on ONE concurrency group (`cli-publish-alert`) so two never interleave a
# lookup with a create (sentinel-issue.py's precondition).
#
# Usage: cli-publish-alert.sh <run url>
# Env:   GH_TOKEN (issues: write, actions: read), REPO (owner/name),
#        SELF_RUN_ID — the calling publish-packages run, which is itself "in flight" while it reports
set -euo pipefail

RUN_URL="$1"
LABEL="cli-publish-failure"
HERE="$(cd "$(dirname "$0")" && pwd)"
OWNER=$(printf '%s' "${REPO%%/*}" | tr '[:upper:]' '[:lower:]')
IMAGE="$OWNER/memex-portal-ai"
ACCEPT="application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.list.v2+json, application/vnd.docker.distribution.manifest.v2+json"

# ── fact 1: the version 3-latest names ──
tok=$(curl -fsS "https://ghcr.io/token?scope=repository:$IMAGE:pull" | jq -r .token)
man=$(curl -fsS -H "Authorization: Bearer $tok" -H "Accept: $ACCEPT" "https://ghcr.io/v2/$IMAGE/manifests/3-latest")
cfg=$(printf '%s' "$man" | jq -r '.config.digest // empty')
if [ -z "$cfg" ]; then   # an index: every platform carries the same version label, read the first
  sub=$(printf '%s' "$man" | jq -r '.manifests[0].digest')
  cfg=$(curl -fsS -H "Authorization: Bearer $tok" -H "Accept: $ACCEPT" "https://ghcr.io/v2/$IMAGE/manifests/$sub" | jq -r .config.digest)
fi
TARGET=$(curl -fsSL -H "Authorization: Bearer $tok" "https://ghcr.io/v2/$IMAGE/blobs/$cfg" \
           | jq -r '.config.Labels["org.opencontainers.image.version"] // empty')
# Fail closed: no target is no evidence either way — red, never "nothing to report".
[[ "$TARGET" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || { echo "::error::cannot read the version 3-latest names from ghcr.io/$IMAGE (got '$TARGET')"; exit 1; }

# ── fact 2: is MeshWeaver.Cli listed at it ──
leaf=$(curl -sS --compressed -w '\n%{http_code}' "https://api.nuget.org/v3/registration5-gz-semver2/meshweaver.cli/$TARGET.json")
code=${leaf##*$'\n'}
case "$code" in
  200) LISTED=$(printf '%s' "${leaf%$'\n'*}" | jq -r '.listed // false') ;;
  404) LISTED=false ;;
  *)   echo "::error::nuget.org answered HTTP $code for MeshWeaver.Cli $TARGET — cannot tell"; exit 1 ;;
esac

LOOKUP=$(python3 "$HERE/sentinel-issue.py" find --repo "$REPO" --label "$LABEL" \
           --ensure-label --color "d93f0b" \
           --description "The ONE standing issue: nuget.org lacks the memex CLI of the build 3-latest names — do not remove")
NUM=$(printf '%s' "$LOOKUP" | jq -r '.number // empty')
ISSUE_STATE=$(printf '%s' "$LOOKUP" | jq -r '.state // empty')

if [ "$LISTED" = true ]; then
  echo "3-latest = $TARGET and MeshWeaver.Cli $TARGET is LISTED"
  if [ -n "$NUM" ] && [ "$ISSUE_STATE" = open ]; then
    gh api "repos/$REPO/issues/$NUM/comments" --silent \
      -f body="\`3-latest\` names \`$TARGET\` and \`MeshWeaver.Cli $TARGET\` is LISTED on nuget.org (reconciled by $RUN_URL) — closing. It reopens when they diverge."
    gh api -X PATCH "repos/$REPO/issues/$NUM" -f state=closed --silent
    echo "closed #$NUM"
  fi
  exit 0
fi

inflight=$(gh api "repos/$REPO/actions/workflows/publish-packages.yml/runs?event=workflow_dispatch&per_page=50" \
             --jq "[.workflow_runs[] | select(.display_title == \"Publish cli $TARGET\" and .status != \"completed\" and .id != ${SELF_RUN_ID:-0})] | length")
if [ "$inflight" -gt 0 ]; then
  echo "MeshWeaver.Cli $TARGET is not listed yet, but its publication is in flight — that run reconciles when it ends"
  exit 0
fi

TITLE="memex CLI not on nuget.org: 3-latest is $TARGET, MeshWeaver.Cli $TARGET is not listed"
# shellcheck disable=SC2016  # the backticks are literal markdown for the issue body, not a
# command substitution; single quotes keep them literal. Suppressed narrowly.
BODY=$(printf '`3-latest` names **%s**, and nuget.org does not list **MeshWeaver.Cli %s**, with no publication of it in flight. `dotnet tool install -g MeshWeaver.Cli` therefore does not give the CLI of the build the fleet runs.\n\nReconciled by %s at %s.\n\nPublish it: `gh workflow run publish-packages.yml --ref main -f version=%s -f ref=<the core commit of that build> -f packages=cli`, or let the next arming publish a newer one.\n\n---\nMaintained by `.github/scripts/cli-publish-alert.sh`, which derives this issue from those two facts on every CLI publication and every arming (Doc/Architecture/NuGetPackageRetirement §1a). It closes itself once they agree. Do not edit the body — edit the script.\n' \
  "$TARGET" "$TARGET" "$RUN_URL" "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$TARGET")
if [ -n "$NUM" ]; then
  gh api -X PATCH "repos/$REPO/issues/$NUM" -f title="$TITLE" -f body="$BODY" -f state=open --silent
  echo "updated #$NUM (was $ISSUE_STATE)"
else
  NEW=$(gh api "repos/$REPO/issues" -f title="$TITLE" -f body="$BODY" -f "labels[]=$LABEL" --jq '.number')
  echo "opened #$NEW"
  python3 "$HERE/sentinel-issue.py" await-visible --repo "$REPO" --label "$LABEL" --number "$NEW"
  NUM="$NEW"
fi
python3 "$HERE/../../scripts/check-issue-taxonomy.py" stamp --repo "$REPO" --number "$NUM" --label bug --label sev:M
