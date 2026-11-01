#!/usr/bin/env bash
# fetch-deployed-plugin-set.sh <out-dir> [<repo>]
#
# Downloads the NEWEST published plugin module set — EVERY module bundle the sealed `plugins`
# publication carries, as the newest promoted main-cd run on `main` handed it on
# (`published-module-set`: the bundles plus the publication's own `modules/_index`) — into <out-dir>.
# That platform is what the fleet self-rolls to, so these are the plugin BYTES the running portals
# load: the "plugin N" of the compatibility ladder (Doc/Architecture/PlatformCompatibilityLadder,
# policy platform-backwards-compatibility). The compatibility gate then links THESE unchanged bytes
# against a candidate platform (this pull request's core build).
#
# 🚨 THE WHOLE PUBLICATION, proven (MeshWeaver#7409). This used to fetch four hard-coded modules
# (AI, Markdown.Collaboration, Maps, Payments.Stripe), so a core change that broke any other published
# module passed the ladder green having checked nothing for it. Now the set is the publication's own
# index, and check-ladder-coverage.py refuses a module the index lists but no downloaded bundle
# provides.
#
# 🚨 Fails RED — never "nothing to check" — when no promoted run carries a set: a verdict over no
# plugins is not a verdict, and a gate that passed on a missing input is the skip-trapdoor AGENTS.md
# forbids. Needs GH_TOKEN with actions:read (the workflow's own token suffices; artifacts of this
# public repository are readable from fork pull requests too).
set -euo pipefail

out="${1:?usage: fetch-deployed-plugin-set.sh <out-dir> [<repo>]}"
repo="${2:-${GITHUB_REPOSITORY:-Systemorph/MeshWeaver}}"
full_set="published-module-set"
# TRANSITION ONLY: the per-module artifacts main-cd handed on before #7409. Read ONLY while no
# unexpired `published-module-set` exists on main at all (main-cd uploads the full set FIRST, so
# once one exists a run carrying only these cannot be newer). The narrower coverage is said out
# loud as a warning — it is four modules' evidence, never presented as the publication's.
legacy_modules=(MeshWeaver.AI MeshWeaver.Markdown.Collaboration MeshWeaver.Maps MeshWeaver.Payments.Stripe)
coverage_check="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/check-ladder-coverage.py"

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
# download_artifact <name> <dir> — the artifact <name> of the CHOSEN run, unpacked into <dir>, through
# the same classifier (MeshWeaver#7448). This was `gh run download … || "if gh printed a rate limit
# above, this is NO VERDICT"`: a sentence that asked the reader to classify the outage the script had
# not. Now a rate limit / 5xx / dropped connection past the budget exits 3 naming the reset, and a 404
# (the artifact expired between the listing and the download) is a plain red, never NO VERDICT.
download_artifact() {
  local name="$1" dir="$2" id rc=0 zip
  id=$(jq -r --arg n "$name" '[.artifacts[] | select(.expired == false and .name == $n) | .id] | first // empty' <<<"$chosen_arts")
  [ -n "$id" ] || { echo "::error::run $chosen advertises $name but its listing carries no id for it — no verdict." >&2; exit 1; }
  zip="$(mktemp "${TMPDIR:-/tmp}/deployed-set.XXXXXX")"
  python3 "$api_helper" "repos/$repo/actions/artifacts/$id/zip" --download "$zip" || rc=$?
  case "$rc" in
    0) ;;
    4) rm -f "$zip"; echo "::error::run $chosen advertised $name (artifact $id) but GitHub answered 404 for its download — it expired or was deleted after the listing; no verdict over a set that cannot be read." >&2; exit 1 ;;
    *) rm -f "$zip"; stop_unless_absent "$rc" "downloading $name (artifact $id) of run $chosen" ;;
  esac
  mkdir -p "$dir"
  python3 -m zipfile -e "$zip" "$dir" \
    || { rm -f "$zip"; echo "::error::the $name artifact of run $chosen downloaded but is not a readable zip — no verdict." >&2; exit 1; }
  rm -f "$zip"
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
# main_runs_of <artifact-name> — the run ids (newest first) on main that hold an unexpired artifact of
# that name. A 404 for the listing is an empty answer; anything else that is not an answer stops RED.
main_runs_of() {
  local listing rc=0
  listing=$(api "repos/$repo/actions/artifacts?name=$1&per_page=100") || rc=$?
  if [ "$rc" -ne 0 ]; then
    [ "$rc" -ne 4 ] || return 0
    stop_unless_absent "$rc" "listing the $1 artifacts on $repo"
  fi
  jq -r '[.artifacts[] | select(.expired == false and .workflow_run.head_branch == "main") | .workflow_run.id] | unique_by(.) | reverse | .[]' <<<"$listing"
}

runs=$(main_runs_of "$full_set")
shape="full"
if [ -z "$runs" ]; then
  runs=$(main_runs_of "module-bundle-${legacy_modules[0]}")
  shape="legacy"
fi

chosen=""
chosen_arts=""
chosen_sha=""
for run in $runs; do
  # The artifact must come from main-cd (any other workflow uploading the same name is not a set).
  run_json=$(api "repos/$repo/actions/runs/$run") || { stop_unless_absent $? "reading run $run"; continue; }
  wf=$(jq -r '[.path, .status] | join(" ")' <<<"$run_json")
  case "$wf" in ".github/workflows/main-cd.yml completed") ;; *) continue ;; esac
  arts=$(api "repos/$repo/actions/runs/$run/artifacts?per_page=100") || { stop_unless_absent $? "listing the artifacts of run $run"; continue; }
  names=$(jq -r '[.artifacts[] | select(.expired == false) | .name] | join(" ")' <<<"$arts")
  complete=1
  if [ "$shape" = "full" ]; then
    case " $names " in *" $full_set "*) ;; *) complete=0 ;; esac
  else
    for m in "${legacy_modules[@]}"; do
      case " $names " in *" module-bundle-$m "*) ;; *) complete=0 ;; esac
    done
  fi
  [ "$complete" -eq 1 ] || continue
  jobs=$(api "repos/$repo/actions/runs/$run/jobs?per_page=100") || { stop_unless_absent $? "listing the jobs of run $run"; continue; }
  promoted=$(jq -r '[.jobs[] | select(.name | startswith("Promote:")) | .conclusion] | first // empty' <<<"$jobs")
  if [ "$promoted" = "success" ]; then chosen="$run"; chosen_arts="$arts"; chosen_sha=$(jq -r '.head_sha' <<<"$run_json"); break; fi
done

[ -n "$chosen" ] || { echo "::error::no COMPLETED main-cd run on main that still holds an unexpired $full_set artifact (or, before the first one exists, all four legacy module-bundle-* artifacts) also PROMOTED its set — the deployed plugin set is not available, so no compatibility verdict can be given. A green main-cd run re-publishes it (published-modules reads the sealed plugins publication); do NOT waive this check."; exit 1; }

if [ "$shape" = "full" ]; then
  download_artifact "$full_set" "$out"
  # 🚨 Every module the publication's index lists must be on the ladder (#7409) — or RED.
  python3 "$coverage_check" --index "$out/$full_set.index" --bundles-dir "$out"
else
  for m in "${legacy_modules[@]}"; do
    download_artifact "module-bundle-$m" "$out/$m"
  done
  n=$(find "$out" -name '*.module.nupkg' | wc -l | tr -d ' ')
  [ "$n" -eq "${#legacy_modules[@]}" ] || { echo "::error::expected ${#legacy_modules[@]} module bundles from run $chosen, found $n"; exit 1; }
  echo "::warning::LADDER COVERAGE NOT MEASURED: no promoted main-cd run on main holds a $full_set yet, so this ladder links only the ${#legacy_modules[@]} legacy per-module bundles (${legacy_modules[*]}) — NOT every module the sealed plugins publication carries (#7409). The next promoted main-cd run hands on the whole publication."
fi
sha="$chosen_sha"
echo "deployed plugin set: main-cd run $chosen (platform commit $sha, $shape set) — $(find "$out" -name '*.module.nupkg' -exec basename {} \; | sort | tr '\n' ' ')"
if [ -n "${GITHUB_OUTPUT:-}" ]; then
  { echo "run=$chosen"; echo "sha=$sha"; echo "shape=$shape"; } >> "$GITHUB_OUTPUT"
fi
