#!/usr/bin/env bash
# resolve-settled-content.sh — the newest SETTLED commit of a content repository's trunk, judged by
# that repository's OWN settle check — the commit a sealed set may bind to.
#
#   resolve-settled-content.sh --repo-dir <clone> --tip <sha> [--max-walk <n>] [--output <file>]
#
# `--repo-dir` is a clone of the content repository (a blobless clone is enough: the scripts fetch
# what they read) whose `origin` the settle check may reach. Walks the trunk's FIRST-PARENT chain
# from `--tip`, at most `--max-walk` commits (default 10), and at each one runs exactly what the
# content repository's own `settle-locks` job runs to decide whether its run may publish:
#
#     python3 scripts/gen-manifests.py --settle             # a lock that MOVES ⇒ unsettled
#     python3 scripts/mesh-floors.py check --require-stamped # where present; red ⇒ floors pending
#
# The first commit both leave clean is the answer: `sha=<full sha>` is appended to `--output` (or
# printed to stdout without it), together with `tip_settled=true|false`.
#
# 🚨 WHY THIS EXISTS (incident 2026-10-04). Core's main-cd resolved MeshWeaver.Plugins' raw
# `refs/heads/main` and packed, baked and SEALED the Plugins publication from it. Plugins' own lane
# had judged that very commit (fd3dbd1e, run 37208121672) "NOT settled (22 lock(s) would move) —
# this run publishes, seals and tags nothing", and the settle pull request that would have
# published it never merged. The seal then carried a Hosting prebuilt bound to an AI build no
# registry ever published (1.21 under a 1.20.4 lock), the control instance kept its installed AI
# 1.20.4, and the reviewer died on `MissingMethodException: set_Group`. A content repository's
# publication rule — "nothing is ever published from an unsettled tree" — held in its own lane and
# was bypassed by the platform's. This script is that rule, asked at the platform's door.
#
# 🚨 THE VERDICT IS THE CONTENT REPOSITORY'S, NEVER A COPY. The scripts are read from the commit
# being judged, so the platform agrees with the lane that withheld the commit by construction, and
# a content repository changing its settle rule changes this answer with it.
#
# 🚨 NO SILENT FALLBACK. An unsettled tip is NAMED — the commit and every lock that would move —
# as a workflow warning, and the commit bound instead is named next to it. An unsettled trunk with
# no settled commit inside the walk, a missing settle script, a settle check that FAILS (exit ≠ 0:
# it could not read its baseline — that is an instrument fault, not a verdict) all fail RED. A
# sealed set never binds to a commit the content repository refused to publish.
set -euo pipefail

repo_dir="" tip="" max_walk=10 output=""
while [ $# -gt 0 ]; do
  case "$1" in
    --repo-dir) repo_dir="${2:-}"; shift 2 ;;
    --tip)      tip="${2:-}";      shift 2 ;;
    --max-walk) max_walk="${2:-}"; shift 2 ;;
    --output)   output="${2:-}";   shift 2 ;;
    *) echo "::error::resolve-settled-content.sh: unknown argument '$1'"; exit 2 ;;
  esac
done
[ -n "$repo_dir" ] || { echo "::error::resolve-settled-content.sh: --repo-dir is required"; exit 2; }
[ -n "$tip" ]      || { echo "::error::resolve-settled-content.sh: --tip is required"; exit 2; }
case "$max_walk" in ''|*[!0-9]*|0) echo "::error::resolve-settled-content.sh: --max-walk must be a positive integer, not '$max_walk'"; exit 2 ;; esac
[ -d "$repo_dir/.git" ] || [ -f "$repo_dir/.git" ] || { echo "::error::resolve-settled-content.sh: '$repo_dir' is not a git clone"; exit 2; }

emit() { # <key> <value>
  if [ -n "$output" ]; then printf '%s=%s\n' "$1" "$2" >> "$output"; else printf '%s=%s\n' "$1" "$2"; fi
}

tip_full=$(git -C "$repo_dir" rev-parse --verify --quiet "${tip}^{commit}" || true)
[ -n "$tip_full" ] || { echo "::error::resolve-settled-content.sh: the tip '$tip' is not a commit in $repo_dir"; exit 1; }

chain=()
while IFS= read -r c; do chain+=("$c"); done < <(git -C "$repo_dir" rev-list --first-parent --max-count="$max_walk" "$tip_full")

# Reset the clone to the commit under judgement, clean — the settle WRITES locks it would move.
restore() { git -C "$repo_dir" checkout -q -f -- . 2>/dev/null || true; git -C "$repo_dir" clean -qfd 2>/dev/null || true; }

tip_moved="" tip_floors=""
examined=0
for commit in "${chain[@]}"; do
  examined=$((examined + 1))
  short="${commit:0:10}"
  git -C "$repo_dir" checkout -q -f --detach "$commit"
  restore
  if [ ! -f "$repo_dir/scripts/gen-manifests.py" ]; then
    echo "::error title=Content commit cannot be judged::$short carries no scripts/gen-manifests.py — whether it is settled cannot be decided, so no seal may bind to it."
    exit 1
  fi
  log="$(mktemp)"
  if ! ( cd "$repo_dir" && python3 scripts/gen-manifests.py --settle ) > "$log" 2>&1; then
    tail -n 40 "$log"
    echo "::error title=Settle check failed::gen-manifests.py --settle exited non-zero on $short — the content repository's own settle check could not decide (an instrument fault, not a verdict), so no seal may bind to it."
    rm -f "$log"; restore
    exit 1
  fi
  rm -f "$log"
  moved="$(git -C "$repo_dir" status --porcelain --untracked-files=all -- '*/manifest.lock' | sed -e 's/^...//' | tr '\n' ' ' | sed -e 's/ *$//')"
  restore
  floors=stamped
  if [ -f "$repo_dir/scripts/mesh-floors.py" ]; then
    ( cd "$repo_dir" && python3 scripts/mesh-floors.py check --require-stamped ) > /dev/null 2>&1 || floors=pending
    restore
  fi
  if [ "$commit" = "$tip_full" ]; then tip_moved="$moved"; tip_floors="$floors"; fi
  if [ -z "$moved" ] && [ "$floors" = stamped ]; then
    if [ "$commit" = "$tip_full" ]; then
      echo "${short} (the tip) is settled — every lock describes its tree and every floor is stamped"
      emit tip_settled true
    else
      n_moved=$(printf '%s' "$tip_moved" | wc -w | tr -d ' ')
      echo "::warning title=Tip not settled — bound to the newest settled commit::${tip_full:0:10} is NOT settled (${n_moved} lock(s) would move: ${tip_moved:-none}; floors ${tip_floors}) — its own lane publishes nothing from it, so this set binds to ${short}, the newest settled first-parent commit ($((examined - 1)) commit(s) back). The settle pull request's merge makes the tip sealable."
      emit tip_settled false
    fi
    emit sha "$commit"
    exit 0
  fi
  echo "${short}: NOT settled — locks that would move: ${moved:-none}; floors ${floors}"
done

echo "::error title=No settled content commit::none of the ${examined} first-parent commit(s) from ${tip_full:0:10} is settled. The tip's locks that would move: ${tip_moved:-none}; floors ${tip_floors}. A sealed set never binds to a tree its own repository refused to publish — merge the settle pull request (ci/settle-manifest-locks), then re-run."
exit 1
