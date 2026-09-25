#!/usr/bin/env bash
# fetch-deployed-plugin-set.sh <out-dir> [<repo>]
#
# Downloads the NEWEST published plugin module set — the four module bundles
# (MeshWeaver.AI, .Markdown.Collaboration, .Maps, .Payments.Stripe) that the newest main-cd run on
# `main` packed against the platform it promoted — into <out-dir>. That platform is what the fleet
# self-rolls to, so these are the plugin BYTES the running portals load: the "plugin N" of the
# compatibility ladder (Doc/Architecture/PlatformCompatibilityLadder, policy
# platform-backwards-compatibility). The compatibility gate then links THESE unchanged bytes against a
# candidate platform (this pull request's core build).
#
# 🚨 Fails RED — never "nothing to check" — when no run carries all four: a verdict over no plugins
# is not a verdict, and a gate that passed on a missing input is the skip-trapdoor AGENTS.md forbids.
# Needs GH_TOKEN with actions:read (the workflow's own token suffices; artifacts of this public
# repository are readable from fork pull requests too).
set -euo pipefail

out="${1:?usage: fetch-deployed-plugin-set.sh <out-dir> [<repo>]}"
repo="${2:-${GITHUB_REPOSITORY:-Systemorph/MeshWeaver}}"
modules=(MeshWeaver.AI MeshWeaver.Markdown.Collaboration MeshWeaver.Maps MeshWeaver.Payments.Stripe)

mkdir -p "$out"
runs=$(gh api "repos/$repo/actions/workflows/main-cd.yml/runs?branch=main&per_page=40" --jq '.workflow_runs[].id') \
  || { echo "::error::could not list main-cd.yml runs on $repo — the deployed plugin set cannot be located"; exit 1; }

chosen=""
for run in $runs; do
  names=$(gh api "repos/$repo/actions/runs/$run/artifacts?per_page=100" \
    --jq '[.artifacts[] | select(.expired == false) | .name] | join(" ")') || continue
  complete=1
  for m in "${modules[@]}"; do
    case " $names " in *" module-bundle-$m "*) ;; *) complete=0 ;; esac
  done
  if [ "$complete" -eq 1 ]; then chosen="$run"; break; fi
done

[ -n "$chosen" ] || { echo "::error::none of the last 40 main-cd runs on main still holds all four module-bundle-* artifacts (${modules[*]}) — the deployed plugin set is not available, so no compatibility verdict can be given. Re-run main-cd on main (it re-packs them); do NOT waive this check."; exit 1; }

for m in "${modules[@]}"; do
  gh run download "$chosen" -R "$repo" -n "module-bundle-$m" -D "$out/$m" \
    || { echo "::error::run $chosen advertises module-bundle-$m but it could not be downloaded"; exit 1; }
done
n=$(find "$out" -name '*.module.nupkg' | wc -l | tr -d ' ')
[ "$n" -eq "${#modules[@]}" ] || { echo "::error::expected ${#modules[@]} module bundles from run $chosen, found $n"; exit 1; }
sha=$(gh api "repos/$repo/actions/runs/$chosen" --jq '.head_sha')
echo "deployed plugin set: main-cd run $chosen (platform commit $sha) — $(find "$out" -name '*.module.nupkg' -exec basename {} \; | sort | tr '\n' ' ')"
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  { echo "run=$chosen"; echo "sha=$sha"; } >> "$GITHUB_OUTPUT"
fi
