#!/usr/bin/env bash
# mw-core-file.sh <ref> <path> — print Systemorph/MeshWeaver's <path> at <ref> on stdout.
#
# 🚨 WHY THIS EXISTS: the reusable node-repo lanes used to pull every platform script they run with
# one REST call each (`gh api repos/Systemorph/MeshWeaver/contents/<path>?ref=<ref>`). Those calls are
# made with the CALLER's `github.token`, whose budget is per repository and small (1,000 requests an
# hour on the org's plan), and they were the largest single consumer of it: 20–46 counted per CI run in
# MeshWeaver.Plugins, 30-odd of them from the validate lane alone, ~1,000–2,000 an hour on a busy
# afternoon. When the budget ran out (2026-10-07 11:18–11:25Z, 2026-10-05 ~23:00Z) every job of every
# pull request went red on "API rate limit exceeded for installation" while fetching a file.
# Core is a PUBLIC repository, so the same bytes are readable over anonymous git, which spends no
# REST budget at all. Doc/Architecture/CiRestBudget carries the measurement.
#
# One shallow, blob-less fetch per (job, ref); every later read of that ref in the same job is
# local, and the blobs a read needs are fetched on demand. All reads of one ref within a job
# therefore see ONE commit — a moving `main` can no longer hand two steps of the same job two
# different trees, which per-file REST reads could.
#
# Refs: a branch, a tag, or a FULL commit sha. An abbreviated sha is refused by the server (git
# cannot fetch by prefix), loudly — never resolved to a guess.
#
# Exit status: 0 with the bytes on stdout; non-zero with a line on stderr naming the ref or path
# that could not be read. A caller keeps its own `|| { echo "::error::…"; exit 1; }`.
#
# `--budget` prints one line with the token's REST budget (GET /rate_limit, which GitHub does not
# count against the budget) so a saturated window is visible in every job's log before it bites.
set -euo pipefail

if [ "${1:-}" = "--budget" ]; then
  token=${GH_TOKEN:-${GITHUB_TOKEN:-}}
  [ -n "$token" ] || { echo "REST budget: no token in this step — not read"; exit 0; }
  body=$(curl -fsS --max-time 10 -H "Authorization: Bearer $token" -H "Accept: application/vnd.github+json" \
           "${GITHUB_API_URL:-https://api.github.com}/rate_limit" 2>/dev/null) \
    || { echo "REST budget: /rate_limit did not answer — not read"; exit 0; }
  python3 - "$body" <<'PY'
import datetime, json, sys
core = (json.loads(sys.argv[1]).get("resources") or {}).get("core") or {}
used, limit, reset = core.get("used"), core.get("limit"), core.get("reset")
at = datetime.datetime.fromtimestamp(reset, datetime.UTC).strftime("%H:%M:%SZ") if isinstance(reset, int) else "?"
line = f"REST budget of this job's token: {used} of {limit} used, resets {at}"
if isinstance(used, int) and isinstance(limit, int) and limit and used >= 0.8 * limit:
    print(f"::warning title=REST budget nearly spent::{line} — Doc/Architecture/CiRestBudget")
else:
    print(line)
PY
  exit 0
fi

ref=${1:?mw-core-file: no ref given (usage: mw-core-file.sh <ref> <path>)}
path=${2:?mw-core-file: no path given (usage: mw-core-file.sh <ref> <path>)}
root="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/mw-core/$(printf '%s' "$ref" | tr -c 'A-Za-z0-9._-' '_')"
if [ ! -f "$root/.fetched" ]; then
  rm -rf "$root"
  git init -q "$root"
  git -C "$root" remote add origin "${MW_CORE_GIT_URL:-https://github.com/Systemorph/MeshWeaver.git}"
  git -C "$root" -c advice.fetchShowForcedUpdates=false fetch -q --depth 1 --filter=blob:none --no-tags origin "$ref" \
    || { echo "mw-core-file: could not fetch ref '$ref' of Systemorph/MeshWeaver over git (a branch, a tag or a FULL sha)" >&2; exit 1; }
  git -C "$root" update-ref refs/mw-core/at FETCH_HEAD
  : > "$root/.fetched"
fi
git -C "$root" show "refs/mw-core/at:$path" \
  || { echo "mw-core-file: '$path' does not exist at '$ref' of Systemorph/MeshWeaver" >&2; exit 1; }
