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
# COMPLETED runs only — an in-progress run may already carry the bundles of a set it has not
# finished promoting — and below, only one whose `promote` job SUCCEEDED: those bundles shipped with a
# promoted set, which is what the fleet rolls to. (A run can be red for other reasons — a satellite
# leg, an arm64 bake — and still have promoted; the promote job, not the run's colour, is the fact.)
# 🚨 The candidates come from the ARTIFACTS, not from a window of runs. This used to scan the last 40
# main-cd runs; a CD outage of ~30 failing/cancelled runs (2026-10-05/06, #10008→#10036) pushed the last
# promoted run out of that window while its four bundles were still downloadable for days — and every
# core pull request's ladder went red over a set that existed. The question is "the newest unexpired
# bundle set from a promoted main-cd run on main", so it is asked of the artifact listing itself, newest
# first; retention (not a run count) is the only horizon.
runs=$(gh api "repos/$repo/actions/artifacts?name=module-bundle-${modules[0]}&per_page=100" \
  --jq '[.artifacts[] | select(.expired == false and .workflow_run.head_branch == "main") | .workflow_run.id] | unique_by(.) | reverse | .[]' ) \
  || { echo "::error::could not list the module-bundle-${modules[0]} artifacts on $repo — the deployed plugin set cannot be located"; exit 1; }

chosen=""
for run in $runs; do
  # The artifact must come from main-cd (any other workflow uploading the same name is not a set).
  wf=$(gh api "repos/$repo/actions/runs/$run" --jq '[.path, .status] | join(" ")') || continue
  case "$wf" in ".github/workflows/main-cd.yml completed") ;; *) continue ;; esac
  names=$(gh api "repos/$repo/actions/runs/$run/artifacts?per_page=100" \
    --jq '[.artifacts[] | select(.expired == false) | .name] | join(" ")') || continue
  complete=1
  for m in "${modules[@]}"; do
    case " $names " in *" module-bundle-$m "*) ;; *) complete=0 ;; esac
  done
  [ "$complete" -eq 1 ] || continue
  promoted=$(gh api "repos/$repo/actions/runs/$run/jobs?per_page=100" \
    --jq '[.jobs[] | select(.name | startswith("Promote:")) | .conclusion] | first // empty') || continue
  if [ "$promoted" = "success" ]; then chosen="$run"; break; fi
done

[ -n "$chosen" ] || { echo "::error::no COMPLETED main-cd run on main that still holds an unexpired module-bundle artifact both PROMOTED its set and holds all four module-bundle-* artifacts (${modules[*]}) — the deployed plugin set is not available, so no compatibility verdict can be given. A green main-cd run re-publishes them (published-modules reads the sealed plugins publication); do NOT waive this check."; exit 1; }

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
