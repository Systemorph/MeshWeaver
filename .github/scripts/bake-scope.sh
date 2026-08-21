#!/usr/bin/env bash
# bake-scope.sh — decide what a node repo's CI bake must actually REBUILD.
#
# "When updating plugins, we should check from git history which modules are affected, and we
# should rebuild only these." Until now the answer was always "everything": every main push, and
# every scheduled poll, ran the full tester over every module — a ~40-minute Roslyn compile of
# ~40 packages to republish bundles that, for all but a handful of them, are byte-for-byte the
# ones already sealed in storage.
#
# This script answers the narrower question, and it answers "full" whenever it cannot answer it
# SAFELY. That bias is the whole design: narrowing a build is the shape that produces a silent
# under-build — a module that should have been recompiled and was not becomes a stale assembly
# every portal seeds at boot, and the evidence of the miss is the absence of evidence. So every
# uncertainty below (no publication yet, disagreeing targets, an unreachable baseline, a diff the
# selector refuses, a tooling change) resolves to a FULL bake, out loud, naming which one.
#
#     scope=full      rebuild and republish every module (today's behaviour, and every fallback)
#     scope=narrowed  rebuild the affected closure; carry every other bundle forward unchanged
#     scope=none      this exact content is already sealed for this identity — build nothing
#
# 🚨 THE BASELINE IS THE PUBLICATION, NOT `github.event.before`. The question a bake asks is
# "what changed since the bundles that are currently published?", and only the publication knows
# that: it carries `source-commit.txt` beside its bundles. Diffing against the push's `before`
# instead would silently under-build after ANY run that did not publish — a cancelled run, a
# superseded push, a red gate, a re-run of an older commit, or the concurrency group dropping a
# run — because those commits' modules would never appear in any later diff. Reading the baseline
# off the sealed publication makes the answer self-correcting: whatever was actually published is
# what we diff against, however many runs it took to get there.
#
# 🚨 …AND THE PUBLICATION STAYS COMPLETE. A narrowed bake produces bundles only for the modules
# it rebuilt, but `_complete` (written LAST, listing the whole set) is what portals seed from —
# publishing just the delta would REPLACE that sentinel and shrink what every portal adopts to
# the delta. So this script also records the currently-sealed bundle listing, and
# `carry-forward-bundles.sh` re-hydrates every bundle the narrowed bake did not produce before
# the publish step runs. The publication is byte-identical in shape to a full bake's; only the
# COMPILE was narrowed.
#
# ENVIRONMENT (all required)
#   BAKE_PUBLISH_TARGETS  whitespace-separated <account>/<share>[/<base-path>]
#   IDENTITY              the framework identity this bake targets (from the image itself)
#   SOURCE                the bake-source segment (plugins, education, …)
#   HEAD_SHA              the content commit about to be baked
#   REPO_DIR              the caller repo checkout (git history + scripts/affected-modules.py)
#   EVENT_NAME            the triggering GitHub event
#   STATE_DIR             a writable directory for the decision's artefacts
#
# OUTPUTS (to $GITHUB_OUTPUT when set, and always to stdout)
#   scope, reason, baseline, mount
# ARTEFACTS in $STATE_DIR
#   published-bundles.txt   the sealed publication's `_complete` listing (scope=narrowed)
#   affected.json           the selector's full answer (scope=narrowed)
#
# AUTH: `az login` must already have happened; data-plane reads use --auth-mode login
# --backup-intent, as publish-bake-bundles.sh does.
set -uo pipefail

# ── self-test ────────────────────────────────────────────────────────────────────────────────
# 🚨 THIS SCRIPT DECIDES WHAT DOES NOT GET REBUILT, so it owes its own proof that it says "full"
# when it cannot answer safely. Every fallback below is one that, if it silently narrowed
# instead, would ship a stale assembly to every portal with nothing red anywhere.
#
#     .github/scripts/bake-scope.sh --self-test
#
# It runs the REAL script as a subprocess against a fixture git repo and a stub `az` whose
# "remote" is a directory tree, so the branch taken is the branch the runner would take.
if [ "${1:-}" = "--self-test" ]; then
  ST_TMP=$(mktemp -d)
  trap 'rm -rf "$ST_TMP"' EXIT
  ME="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"

  # A stub `az` whose remote is a directory tree: <root>/<account>/<share>/<path>.
  mkdir -p "$ST_TMP/bin"
  cat > "$ST_TMP/bin/az" <<'AZ'
#!/usr/bin/env bash
verb=""
for a in "$@"; do case "$a" in exists|download) verb="$a"; break;; esac; done
account=""; share=""; path=""; dest=""
while [ $# -gt 0 ]; do
  case "$1" in
    --account-name) account="$2"; shift 2;;
    --share-name)   share="$2";   shift 2;;
    --path)         path="$2";    shift 2;;
    --dest)         dest="$2";    shift 2;;
    *) shift;;
  esac
done
file="$MOCK_AZ_ROOT/$account/$share/$path"
case "$verb" in
  exists)   { [ -f "$file" ] && echo true; } || echo false;;
  download) [ -f "$file" ] || exit 1; mkdir -p "$(dirname "$dest")"; cp "$file" "$dest";;
  *) exit 1;;
esac
AZ
  chmod +x "$ST_TMP/bin/az"
  PATH="$ST_TMP/bin:$PATH"

  # A fixture caller repo: two commits, module dirs, and a stub selector whose answer each case
  # chooses. The selector CONTRACT this script depends on is exit code + JSON, and that is what
  # the fixture varies — the selector's own semantics are pinned by its own --self-test, in the
  # repo that owns it (scripts/affected-modules.py --self-test).
  REPO="$ST_TMP/repo"
  mkdir -p "$REPO/scripts" "$REPO/Store" "$REPO/Edu"
  ( cd "$REPO" && git init -q . && git config user.email t@t && git config user.name t
    echo '{"id":"Store"}' > Store/index.json
    echo '{"id":"Edu"}'   > Edu/index.json
    git add -A && git commit -qm base ) > /dev/null
  BASE_SHA=$(git -C "$REPO" rev-parse HEAD)
  ( cd "$REPO" && echo change >> Edu/index.json && git add -A && git commit -qm change ) > /dev/null
  HEAD_SHA=$(git -C "$REPO" rev-parse HEAD)
  # A commit on a DIVERGED branch — resolvable, but not an ancestor of HEAD.
  ( cd "$REPO" && git checkout -q -b other "$BASE_SHA" && echo x >> Store/index.json \
      && git add -A && git commit -qm diverged && git checkout -q - ) > /dev/null 2>&1
  OTHER_SHA=$(git -C "$REPO" rev-parse other)

  write_selector() {  # <exit-code> <stdout>
    { echo "import sys"
      printf 'sys.stdout.write(%s)\n' "$(python3 -c 'import json,sys; print(json.dumps(sys.argv[1]))' "$2")"
      echo "sys.exit($1)"
    } > "$REPO/scripts/affected-modules.py"
  }
  seal() {  # <account/share[/base]> <source-sha> <bundle…>
    local target="$1" sha="$2"; shift 2
    local account="${target%%/*}" rest="${target#*/}"
    local share="${rest%%/*}" base=""
    case "$rest" in */*) base="${rest#*/}";; esac
    local dir="$ST_TMP/remote/$account/$share/${base:+$base/}prebuilt-bundles/ID1/plugins"
    mkdir -p "$dir"
    printf '%s\n' "$sha" > "$dir/source-commit.txt"
    printf '%s\n' "$@" > "$dir/_complete"
  }

  run_case() {  # <event> <targets> [<head-sha>]
    ( unset GITHUB_STEP_SUMMARY GITHUB_ENV GITHUB_OUTPUT
      export MOCK_AZ_ROOT="$ST_TMP/remote"
      BAKE_PUBLISH_TARGETS="$2" IDENTITY=ID1 SOURCE=plugins \
      HEAD_SHA="${3:-$HEAD_SHA}" REPO_DIR="$REPO" EVENT_NAME="$1" \
      STATE_DIR="$ST_TMP/state" bash "$ME" 2>&1 )
  }

  FAILED=0
  expect() {  # <name> <expected-scope> <output>
    local got
    got=$(printf '%s\n' "$3" | sed -n 's/^scope=//p' | tail -1)
    if [ "$got" = "$2" ]; then
      printf '  OK   %s\n' "$1"
    else
      printf '  FAIL %s — expected scope=%s, got scope=%s\n' "$1" "$2" "${got:-<none>}"
      printf '%s\n' "$3" | sed 's/^/       /'
      FAILED=$((FAILED + 1))
    fi
  }
  expect_line() {  # <name> <key> <expected> <output>
    local got
    got=$(printf '%s\n' "$4" | sed -n "s/^$2=//p" | tail -1)
    if [ "$got" = "$3" ]; then
      printf '  OK   %s\n' "$1"
    else
      printf '  FAIL %s — %s is "%s", expected "%s"\n' "$1" "$2" "$got" "$3"
      FAILED=$((FAILED + 1))
    fi
  }

  OK_JSON='{"runAll": false, "mount": ["Store", "Edu"], "affected": ["Edu"], "skipped": ["Chess"], "support": ["Store"]}'

  echo "fallbacks to a FULL bake (the bias that makes narrowing safe):"
  rm -rf "$ST_TMP/remote" "$ST_TMP/state"
  write_selector 0 "$OK_JSON"
  seal acct/share "$BASE_SHA" Store.zip Edu.zip Chess.zip
  mv "$REPO/scripts/affected-modules.py" "$ST_TMP/selector.py"
  expect "no scripts/affected-modules.py in the caller repo" full "$(run_case push acct/share)"
  mv "$ST_TMP/selector.py" "$REPO/scripts/affected-modules.py"
  expect "a framework-release dispatch (NEW identity — everything is recompiled against it)" \
    full "$(run_case repository_dispatch acct/share)"
  rm -rf "$ST_TMP/state"
  expect "no sealed publication yet for this identity (the FIRST bake)" full "$(run_case push other/share)"
  rm -rf "$ST_TMP/state"
  expect "a malformed publish target" full "$(run_case push nonsense)"
  rm -rf "$ST_TMP/state"; seal two/share "$OTHER_SHA" Store.zip Edu.zip Chess.zip
  expect "publish targets that disagree on the published SOURCE" full \
    "$(run_case push "acct/share two/share")"
  rm -rf "$ST_TMP/state"; seal two/share "$BASE_SHA" Store.zip
  expect "publish targets that disagree on the published BUNDLES" full \
    "$(run_case push "acct/share two/share")"
  rm -rf "$ST_TMP/state"; seal solo/share unknown Store.zip
  expect "a publication whose recorded source is 'unknown'" full "$(run_case push solo/share)"
  rm -rf "$ST_TMP/state"; seal gone/share 0000000000000000000000000000000000000000 Store.zip
  expect "a baseline commit this checkout does not have (shallow clone)" full "$(run_case push gone/share)"
  rm -rf "$ST_TMP/state"; seal div/share "$OTHER_SHA" Store.zip
  expect "a baseline that is NOT an ancestor of HEAD (history rewritten)" full \
    "$(run_case push div/share)"
  rm -rf "$ST_TMP/state"; write_selector 1 ""
  expect "the selector REFUSING — an empty diff or unresolvable range" full "$(run_case push acct/share)"
  rm -rf "$ST_TMP/state"; write_selector 0 '{"runAll": true, "mount": ["Store","Edu"]}'
  expect "the selector answering ALL modules (scripts/, .github/, a DELETED module)" full \
    "$(run_case push acct/share)"
  rm -rf "$ST_TMP/state"; write_selector 0 'not json at all'
  expect "a selector answer that cannot be parsed" full "$(run_case push acct/share)"

  echo "verified nothing-to-do (a POSITIVE finding, never an absent input):"
  rm -rf "$ST_TMP/state"; write_selector 0 "$OK_JSON"
  seal head/share "$HEAD_SHA" Store.zip Edu.zip
  expect "this exact content is already sealed for this identity (the 30-minute release poll)" \
    none "$(run_case schedule head/share)"
  rm -rf "$ST_TMP/state"
  write_selector 0 '{"runAll": false, "mount": [], "affected": [], "skipped": ["Store","Edu"], "support": []}'
  expect "a diff that reaches no module at all (docs/, e2e/, .claude/)" none "$(run_case push acct/share)"

  echo "narrowed:"
  rm -rf "$ST_TMP/state"; write_selector 0 "$OK_JSON"
  OUT=$(run_case push acct/share)
  expect "a module change since the published baseline" narrowed "$OUT"
  expect_line "the mount is the selector's order, dependencies first" mount "Store Edu" "$OUT"
  expect_line "the baseline is the PUBLICATION's commit, not github.event.before" \
    baseline "$BASE_SHA" "$OUT"
  # 🚨 The carry-forward's input. Without this listing the publish would seal a SHORTER sentinel
  # and every portal would silently stop adopting the bundles it dropped.
  if [ "$(tr -d '[:space:]' < "$ST_TMP/state/published-bundles.txt" 2>/dev/null)" = "Store.zipEdu.zipChess.zip" ]; then
    printf '  OK   %s\n' "the sealed bundle listing is recorded for the carry-forward"
  else
    printf '  FAIL published-bundles.txt is "%s"\n' "$(cat "$ST_TMP/state/published-bundles.txt" 2>&1)"
    FAILED=$((FAILED + 1))
  fi

  if [ "$FAILED" -gt 0 ]; then
    echo "::error title=bake-scope self-test failed::$FAILED case(s) — this script decides what is NOT rebuilt; a fallback that stops falling back is a silent under-build."
    exit 1
  fi
  echo ""
  echo "bake-scope self-test: 12 full-bake fallbacks, 2 verified nothing-to-do verdicts, 4 narrowed assertions — all green."
  exit 0
fi

: "${BAKE_PUBLISH_TARGETS:?BAKE_PUBLISH_TARGETS is required}"
: "${IDENTITY:?IDENTITY is required}"
: "${SOURCE:?SOURCE is required}"
: "${HEAD_SHA:?HEAD_SHA is required}"
: "${REPO_DIR:?REPO_DIR is required}"
: "${EVENT_NAME:?EVENT_NAME is required}"
: "${STATE_DIR:?STATE_DIR is required}"

SENTINEL="_complete"
SOURCE_MARKER="source-commit.txt"
mkdir -p "$STATE_DIR"

SCOPE="full"
REASON=""
BASELINE=""
MOUNT=""

emit() {
  echo "scope=$SCOPE"
  echo "reason=$REASON"
  echo "baseline=$BASELINE"
  echo "mount=$MOUNT"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    {
      echo "scope=$SCOPE"
      echo "reason=$REASON"
      echo "baseline=$BASELINE"
      echo "mount=$MOUNT"
    } >> "$GITHUB_OUTPUT"
  fi
}

# Every exit from this script other than "narrowed"/"none" goes through here, so a fallback can
# never be silent: it names itself in the log AND in the job summary, where an operator looking at
# a 40-minute bake can see why it was 40 minutes.
full() {
  SCOPE="full"
  REASON="$1"
  echo "::notice title=Full bake::$REASON"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '### 🧱 Full bake\n\n%s\n' "$REASON" >> "$GITHUB_STEP_SUMMARY"
  fi
  emit
  exit 0
}

# ── 1. the selector must exist in the CALLER repo ────────────────────────────────────────────
# It is the caller's own file (scripts/affected-modules.py) because the dependency edges are the
# caller's content. A repo that does not ship it simply is not narrowable — never an error, and
# never a silent narrowing to whatever a default guessed.
SELECTOR="$REPO_DIR/scripts/affected-modules.py"
[ -f "$SELECTOR" ] || full "the caller repo ships no scripts/affected-modules.py, so the affected closure cannot be computed — baking every module."

# ── 2. the event must be one whose diff means anything ───────────────────────────────────────
# A framework release mints a NEW identity: there is no publication to diff against and every
# module must be recompiled against the new framework. Step 3 would reach the same verdict on its
# own (no sealed directory), but saying it here names the real reason instead of "first bake".
case "$EVENT_NAME" in
  push|schedule) ;;
  repository_dispatch) full "a repository_dispatch bake targets a framework identity this repo has not built against — every module must be recompiled against it, so the git diff is irrelevant." ;;
  *) full "event '$EVENT_NAME' has no meaningful content diff — baking every module." ;;
esac

# ── 3. every target must already hold a SEALED publication, and they must AGREE ──────────────
# The carry-forward re-hydrates from the publication, so a narrowed bake is only sound if the
# publication it carries forward is the same everywhere. Targets that disagree (one lagging a
# failed run, a newly added portal) would need per-target scopes; the honest answer is one full
# bake that brings them all back into step.
prev_sha=""
prev_listing=""
targets=0
for target in $BAKE_PUBLISH_TARGETS; do
  account="${target%%/*}"
  rest="${target#*/}"
  share="${rest%%/*}"
  base=""
  case "$rest" in */*) base="${rest#*/}";; esac
  if [ -z "$account" ] || [ -z "$share" ] || [ "$account" = "$target" ]; then
    full "malformed BAKE_PUBLISH_TARGETS entry '$target' — cannot read its publication, so the bake cannot be narrowed. (publish-bake-bundles.sh fails on this too.)"
  fi
  dest="${base:+$base/}prebuilt-bundles/$IDENTITY/$SOURCE"

  complete=$(az storage file exists --account-name "$account" --share-name "$share" \
    --path "$dest/$SENTINEL" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo false)
  [ "$complete" = "true" ] || full "$account/$share holds no sealed publication under $dest — this is the FIRST bake of '$SOURCE' for framework identity $IDENTITY, so there is nothing to carry forward and every module must be built."

  work="$STATE_DIR/probe-$targets"
  mkdir -p "$work"
  az storage file download --account-name "$account" --share-name "$share" \
    --path "$dest/$SOURCE_MARKER" --dest "$work/$SOURCE_MARKER" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1 \
    || full "$account/$share: the sealed publication under $dest carries no $SOURCE_MARKER, so the baseline commit is unknown — baking every module."
  az storage file download --account-name "$account" --share-name "$share" \
    --path "$dest/$SENTINEL" --dest "$work/$SENTINEL" \
    --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1 \
    || full "$account/$share: could not read the $SENTINEL listing under $dest, so the bundles to carry forward are unknown — baking every module."

  sha=$(tr -d '[:space:]' < "$work/$SOURCE_MARKER")
  listing=$(grep -v '^[[:space:]]*$' "$work/$SENTINEL" | sort)
  [ -n "$sha" ] && [ "$sha" != "unknown" ] \
    || full "$account/$share: the publication under $dest records source '$sha' — no usable baseline commit, so the diff cannot be taken. Baking every module (which re-stamps a usable marker)."
  [ -n "$listing" ] || full "$account/$share: the $SENTINEL under $dest lists no bundles — baking every module."

  if [ "$targets" -eq 0 ]; then
    prev_sha="$sha"
    prev_listing="$listing"
    cp "$work/$SENTINEL" "$STATE_DIR/published-bundles.txt"
    CARRY_TARGET="$target"
  else
    [ "$sha" = "$prev_sha" ] \
      || full "the publish targets disagree on what is published: '$prev_sha' vs '$sha' under $dest. A narrowed bake carries forward ONE publication, so it cannot serve both — one full bake brings every target back into step."
    [ "$listing" = "$prev_listing" ] \
      || full "the publish targets disagree on which bundles are published under $dest — one full bake brings every target back into step."
  fi
  targets=$((targets + 1))
done
[ "$targets" -gt 0 ] || full "BAKE_PUBLISH_TARGETS holds no targets — nothing to compare against."

# ── 4. already published? then build NOTHING ─────────────────────────────────────────────────
# 🚨 This is the case the every-30-minutes release poll hits almost every time it runs: the
# schedule exists because the framework-release dispatch is dormant, and until the platform cuts a
# new identity the poll re-bakes ~40 packages for 40 minutes so that publish-bake-bundles.sh can
# then look at the sealed directory and skip the upload. The skip was already there; the BUILD in
# front of it was not gated by anything.
if [ "$prev_sha" = "$HEAD_SHA" ]; then
  SCOPE="none"
  REASON="content $HEAD_SHA is already sealed for framework identity $IDENTITY — nothing to rebuild and nothing to publish."
  echo "::notice title=Nothing to bake::$REASON"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '### ✅ Already published\n\n%s\n' "$REASON" >> "$GITHUB_STEP_SUMMARY"
  fi
  BASELINE="$prev_sha"
  emit
  exit 0
fi

# ── 5. the baseline must be REACHABLE in this checkout ───────────────────────────────────────
# A commit we cannot resolve is a commit we cannot diff. A commit that is not an ancestor of HEAD
# means the branch was rewritten (force-push, revert-of-a-revert) and the "changes since" question
# has no answer — both are full bakes, not guesses.
git -C "$REPO_DIR" cat-file -e "${prev_sha}^{commit}" 2>/dev/null \
  || full "the published baseline $prev_sha is not present in this checkout (shallow clone, or the commit was rewritten) — the diff cannot be taken, so every module is baked."
git -C "$REPO_DIR" merge-base --is-ancestor "$prev_sha" "$HEAD_SHA" 2>/dev/null \
  || full "the published baseline $prev_sha is not an ancestor of $HEAD_SHA — history was rewritten, so 'what changed since the publication' has no answer. Baking every module."

# ── 6. ask the selector ──────────────────────────────────────────────────────────────────────
# It refuses an empty diff (a CI diff is never empty, so an empty answer is a WRONG answer), it
# classifies scripts/ + .github/ + repo-root files + an unknown top-level dir (a DELETED module)
# as ALL modules, and it closes over transitive DEPENDENTS with the runtime's own edge semantics.
# Every one of those is load-bearing here; a non-zero exit is taken as "cannot narrow", never as
# "nothing affected".
if ! (cd "$REPO_DIR" && python3 "$SELECTOR" --range "${prev_sha}...${HEAD_SHA}" --json) \
      > "$STATE_DIR/affected.json" 2> "$STATE_DIR/affected.log"; then
  sed 's/^/    /' "$STATE_DIR/affected.log" || true
  full "scripts/affected-modules.py could not compute the closure for ${prev_sha}...${HEAD_SHA} (see above) — baking every module."
fi
sed 's/^/    /' "$STATE_DIR/affected.log" || true

run_all=$(jq -r '.runAll' "$STATE_DIR/affected.json" 2>/dev/null || echo "parse-error")
case "$run_all" in
  true)  full "the diff ${prev_sha}...${HEAD_SHA} touches the gate/tooling scope (scripts/, .github/, a repo-root file, or a module directory that no longer exists) — the selector answers ALL modules." ;;
  false) ;;
  *)     full "could not read runAll out of the selector's JSON — baking every module." ;;
esac

MOUNT=$(jq -r '.mount | join(" ")' "$STATE_DIR/affected.json")
if [ -z "$MOUNT" ]; then
  SCOPE="none"
  REASON="nothing between $prev_sha and $HEAD_SHA reaches a module (docs/, e2e/, .claude/ …) — the published bundles are still exactly right, so nothing is rebuilt and nothing is republished."
  echo "::notice title=Nothing to bake::$REASON"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '### ✅ No module content changed\n\n%s\n' "$REASON" >> "$GITHUB_STEP_SUMMARY"
  fi
  BASELINE="$prev_sha"
  emit
  exit 0
fi

SCOPE="narrowed"
BASELINE="$prev_sha"
affected=$(jq -r '.affected | join(", ")' "$STATE_DIR/affected.json")
skipped=$(jq -r '.skipped | join(", ")' "$STATE_DIR/affected.json")
support=$(jq -r '.support | join(", ")' "$STATE_DIR/affected.json")
REASON="rebuilding the affected closure since the published $prev_sha"
{
  echo "### 🎯 Narrowed bake"
  echo ""
  echo "| | |"
  echo "|---|---|"
  echo "| baseline (published) | \`$prev_sha\` |"
  echo "| head | \`$HEAD_SHA\` |"
  echo "| framework identity | \`$IDENTITY\` |"
  echo "| affected (changed + dependents) | \`${affected:-none}\` |"
  echo "| mounted as dependencies | \`${support:-none}\` |"
  echo "| **not rebuilt** (bundles carried forward) | \`${skipped:-none}\` |"
  echo ""
  echo "Every module not rebuilt keeps its currently published bundle: \`carry-forward-bundles.sh\`"
  echo "re-hydrates them before the publish step, so the \`_complete\` sentinel still lists the whole"
  echo "set and no portal adopts less than it does today."
} >> "${GITHUB_STEP_SUMMARY:-/dev/null}"
echo "::notice title=Narrowed bake::rebuilding ${affected:-none} (+ dependencies ${support:-none}); carrying forward ${skipped:-none}"
echo "carry-forward source target: ${CARRY_TARGET}"
if [ -n "${GITHUB_ENV:-}" ]; then
  echo "BAKE_CARRY_TARGET=${CARRY_TARGET}" >> "$GITHUB_ENV"
fi
emit
