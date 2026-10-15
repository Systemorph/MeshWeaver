#!/usr/bin/env bash
# The ONE standing issue for "nuget.org does not carry the memex CLI of the armed set" (#6843).
#
# Its own lifecycle, on purpose: CD's `ci-failure` issue is closed by `verify-images` after any
# successful image promotion, which says nothing about nuget.org — so a CLI outage filed there would
# be cleared by the next image build while the package was still missing. This sentinel is found by
# its own LABEL (sentinel-issue.py: server-side, both states, never a window scan) and is closed
# ONLY by a successful, LISTED CLI publication at a version at least as new as the one that failed.
#
# Usage: cli-publish-alert.sh red   <version> <run url> <what failed>
#        cli-publish-alert.sh green <version> <run url>
# Env:   GH_TOKEN (issues: write), REPO (owner/name)
set -euo pipefail

STATE="$1"; V="$2"; RUN_URL="$3"; DETAIL="${4:-}"
LABEL="cli-publish-failure"
HERE="$(cd "$(dirname "$0")" && pwd)"

run_of() { python3 "$HERE/platform-version.py" ordinal "$1" 2>/dev/null || true; }

LOOKUP=$(python3 "$HERE/sentinel-issue.py" find --repo "$REPO" --label "$LABEL" \
           --ensure-label --color "d93f0b" \
           --description "The ONE standing issue: nuget.org lacks the memex CLI of the armed set — do not remove")
NUM=$(printf '%s' "$LOOKUP" | jq -r '.number // empty')
ISSUE_STATE=$(printf '%s' "$LOOKUP" | jq -r '.state // empty')

if [ "$STATE" = green ]; then
  [ -n "$NUM" ] && [ "$ISSUE_STATE" = open ] || { echo "MeshWeaver.Cli $V published; no CLI publish alert open"; exit 0; }
  # The title names the version that failed. An OLDER version publishing late does not clear a
  # newer failure: only a publication at least as new does.
  failed=$(gh api "repos/$REPO/issues/$NUM" --jq .title | grep -oE '[0-9]+\.[0-9]+\.[0-9]+' | head -1 || true)
  nf=$(run_of "$failed"); nv=$(run_of "$V")
  if [ -n "$nf" ] && [ -n "$nv" ] && [ "$nv" -lt "$nf" ]; then
    echo "MeshWeaver.Cli $V published, but #$NUM is about the newer $failed — left open"; exit 0
  fi
  gh api "repos/$REPO/issues/$NUM/comments" --silent \
    -f body="\`MeshWeaver.Cli $V\` is published and LISTED on nuget.org ($RUN_URL) — closing. It reopens on the next failed CLI publication."
  gh api -X PATCH "repos/$REPO/issues/$NUM" -f state=closed --silent
  echo "closed #$NUM"
  exit 0
fi

TITLE="memex CLI not on nuget.org: MeshWeaver.Cli $V was not published"
# shellcheck disable=SC2016  # the backticks are literal markdown for the issue body, not a
# command substitution; single quotes keep them literal. Suppressed narrowly.
BODY=$(printf '**MeshWeaver.Cli %s** was not published to nuget.org — %s\n\nRun: %s (%s)\n\n`dotnet tool install -g MeshWeaver.Cli` therefore does not give the CLI of the build `3-latest` names. Re-run the publish for this version, or let the next arming publish a newer one: `gh workflow run publish-packages.yml --ref main -f version=<version> -f ref=<armed core commit> -f packages=cli`.\n\n---\nMaintained by `.github/scripts/cli-publish-alert.sh` (main-cd `publish-cli`, publish-packages.yml). Closed automatically by the next successful CLI publication at this version or newer (Doc/Architecture/NuGetPackageRetirement §1a).\n' \
  "$V" "$DETAIL" "$RUN_URL" "$(date -u '+%Y-%m-%dT%H:%M:%SZ')")
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
