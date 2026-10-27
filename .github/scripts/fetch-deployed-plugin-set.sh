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
# 🚨 EVERY lookup goes through github-api-get.py, never `gh api … || continue` (2026-10-27). That
# shape read a rate limit, a 5xx or a dropped connection as "this run is not a candidate", skipped
# every candidate, and concluded "the deployed plugin set is not available" — a fake verdict over a
# set that existed. Now only a genuine 404 skips a candidate; a rate limit / 5xx / transport failure
# that outlasts the retry budget ends the script RED as NO VERDICT (exit 3, the helper prints when to
# re-run), and any other refusal ends it red (exit 1). One budget covers every lookup below.
api_helper="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/github-api-get.py"
export GITHUB_API_DEADLINE="${GITHUB_API_DEADLINE:-$(( $(date +%s) + ${FETCH_API_BUDGET_SECONDS:-240} ))}"
# api <path> — the JSON on stdout. 404 → return 4 (the caller skips); anything else → exit the script.
api() {
  local rc=0
  python3 "$api_helper" "$1" || rc=$?
  return "$rc"
}
# stop_unless_absent <rc> <what> — return 0 for a 404 (skip it), otherwise end the script red.
stop_unless_absent() {
  case "$1" in
    4) return 0 ;;
    3) echo "::error::$2: NO VERDICT — the GitHub API could not be asked (see the line above); this is not a finding that the deployed plugin set is missing. Re-run after the reset." >&2; exit 3 ;;
    *) echo "::error::$2 failed (exit $1) — the deployed plugin set cannot be located; no verdict." >&2; exit 1 ;;
  esac
}

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
listing=$(api "repos/$repo/actions/artifacts?name=module-bundle-${modules[0]}&per_page=100") || {
  rc=$?; [ "$rc" -ne 4 ] || rc=1
  stop_unless_absent "$rc" "listing the module-bundle-${modules[0]} artifacts on $repo"
}
runs=$(jq -r '[.artifacts[] | select(.expired == false and .workflow_run.head_branch == "main") | .workflow_run.id] | unique_by(.) | reverse | .[]' <<<"$listing")

chosen=""
chosen_sha=""
for run in $runs; do
  # The artifact must come from main-cd (any other workflow uploading the same name is not a set).
  run_json=$(api "repos/$repo/actions/runs/$run") || { stop_unless_absent $? "reading run $run"; continue; }
  wf=$(jq -r '[.path, .status] | join(" ")' <<<"$run_json")
  case "$wf" in ".github/workflows/main-cd.yml completed") ;; *) continue ;; esac
  arts=$(api "repos/$repo/actions/runs/$run/artifacts?per_page=100") || { stop_unless_absent $? "listing the artifacts of run $run"; continue; }
  names=$(jq -r '[.artifacts[] | select(.expired == false) | .name] | join(" ")' <<<"$arts")
  complete=1
  for m in "${modules[@]}"; do
    case " $names " in *" module-bundle-$m "*) ;; *) complete=0 ;; esac
  done
  [ "$complete" -eq 1 ] || continue
  jobs=$(api "repos/$repo/actions/runs/$run/jobs?per_page=100") || { stop_unless_absent $? "listing the jobs of run $run"; continue; }
  promoted=$(jq -r '[.jobs[] | select(.name | startswith("Promote:")) | .conclusion] | first // empty' <<<"$jobs")
  if [ "$promoted" = "success" ]; then chosen="$run"; chosen_sha=$(jq -r '.head_sha' <<<"$run_json"); break; fi
done

[ -n "$chosen" ] || { echo "::error::no COMPLETED main-cd run on main that still holds an unexpired module-bundle artifact both PROMOTED its set and holds all four module-bundle-* artifacts (${modules[*]}) — the deployed plugin set is not available, so no compatibility verdict can be given. A green main-cd run re-publishes them (published-modules reads the sealed plugins publication); do NOT waive this check."; exit 1; }

for m in "${modules[@]}"; do
  gh run download "$chosen" -R "$repo" -n "module-bundle-$m" -D "$out/$m" \
    || { echo "::error::run $chosen advertises module-bundle-$m but it could not be downloaded — if gh printed a rate limit above, this is NO VERDICT: re-run after the reset"; exit 1; }
done
n=$(find "$out" -name '*.module.nupkg' | wc -l | tr -d ' ')
[ "$n" -eq "${#modules[@]}" ] || { echo "::error::expected ${#modules[@]} module bundles from run $chosen, found $n"; exit 1; }
sha="$chosen_sha"
echo "deployed plugin set: main-cd run $chosen (platform commit $sha) — $(find "$out" -name '*.module.nupkg' -exec basename {} \; | sort | tr '\n' ' ')"
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  { echo "run=$chosen"; echo "sha=$sha"; } >> "$GITHUB_OUTPUT"
fi
