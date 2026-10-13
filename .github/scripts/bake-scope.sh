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
#   scope, reason, baseline, range-base (the oldest build the publication carries), mount
# ARTEFACTS in $STATE_DIR
#   published-bundles.txt   the sealed publication's `_complete` listing (scope=narrowed, or
#                           whenever WITHHOLD_PACKAGES is set)
#   published-commit.txt / published-modules.txt / published-packages.json / publication-state
#                           the rest of what a per-package seal carries from (seal-packages.py)
#   affected.json           the selector's full answer (scope=narrowed)
#
# OPTIONAL: WITHHOLD_PACKAGES — the packages this run must not seal fresh (node-repo-publish-bake.yml
# `withhold-packages`); when set, the sealed publication is read up front whatever the scope.
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
group="$2"          # file | directory
verb=""
for a in "$@"; do case "$a" in exists|download|show) verb="$a"; break;; esac; done
account=""; share=""; path=""; dest=""; name=""
while [ $# -gt 0 ]; do
  case "$1" in
    --account-name) account="$2"; shift 2;;
    --share-name)   share="$2";   shift 2;;
    --path)         path="$2";    shift 2;;
    --dest)         dest="$2";    shift 2;;
    --name)         name="$2";    shift 2;;
    *) shift;;
  esac
done
file="$MOCK_AZ_ROOT/$account/$share/$path"
dir="$MOCK_AZ_ROOT/$account/$share/$name"
case "$group/$verb" in
  directory/exists) { [ -d "$dir" ] && echo true; } || echo false;;
  file/exists)      { [ -f "$file" ] && echo true; } || echo false;;
  file/download)    [ -f "$file" ] || exit 1
                    # A file that EXISTS and cannot be downloaded (a transient fault, a permission).
                    case "$path" in *"${MOCK_AZ_FAIL_DOWNLOAD:-//none//}") exit 1;; esac
                    mkdir -p "$(dirname "$dest")"; cp "$file" "$dest";;
  file/show)        # The one query bake-scope.sh makes: the sentinel's `publication` stamp,
                    # held in a sidecar `<file>.publication`; '-' when the seal predates the stamp.
                    [ -f "$file" ] || exit 1
                    if [ -f "$file.publication" ]; then cat "$file.publication"; else echo "-"; fi;;
  *) echo "stub-az: unmodelled '$group $verb' — teach the stub rather than letting the script fall back" >&2; exit 64;;
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

  # A publication under a GENERATION directory, with the pointer naming it (MeshWeaver#3461).
  seal_generation() {  # <account/share[/base]> <generation> <source-sha> <bundle…>
    local target="$1" gen="$2" sha="$3"; shift 3
    local account="${target%%/*}" rest="${target#*/}"
    local share="${rest%%/*}" base=""
    case "$rest" in */*) base="${rest#*/}";; esac
    local prefix="$ST_TMP/remote/$account/$share/${base:+$base/}prebuilt-bundles/ID1/plugins"
    mkdir -p "$prefix/$gen"
    printf '%s\n' "$sha" > "$prefix/$gen/source-commit.txt"
    printf '%s\n' "$@" > "$prefix/$gen/_complete"
    printf '%s\n' "$gen" > "$prefix/_current"
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
  # Per-module deploy: a later run sealed first. Sealing the OLDER tree would move every instance's
  # sources backwards, so the older run bakes nothing — and a diverged seal is NOT that case: it
  # still falls through to the rewritten-history full bake above.
  rm -rf "$ST_TMP/state"; write_selector 0 "$OK_JSON"
  seal newer/share "$HEAD_SHA" Store.zip Edu.zip
  expect "a NEWER commit is already sealed (runs finished out of order) — never seal backwards" \
    none "$(run_case push newer/share "$BASE_SHA")"
  # The control, one variable apart: the same guard with a sealed commit this run's tree is NOT in
  # (a diverged line — OTHER forks from BASE, so HEAD is not its ancestor) must not skip; it falls
  # through to the rewritten-history full bake.
  rm -rf "$ST_TMP/state"; seal newer/share "$OTHER_SHA" Store.zip Edu.zip
  expect "…while a sealed commit that does NOT contain this run's tree is still a full bake" \
    full "$(run_case push newer/share)"

  # ── THE POINTER. The flat copy and the generation deliberately record DIFFERENT baselines, so
  # ── the verdict says which one was read: a diverged baseline forces `full`, an ancestor one
  # ── narrows. A resolver that ignored the pointer would read the flat copy and answer `full`.
  echo "the publication pointer (MeshWeaver#3461) — read the generation, not the prefix:"
  rm -rf "$ST_TMP/state"; write_selector 0 "$OK_JSON"
  seal gen/share "$OTHER_SHA" Store.zip Edu.zip Chess.zip
  seal_generation gen/share Systemorph-MeshWeaver-42-1 "$BASE_SHA" Store.zip Edu.zip Chess.zip
  OUT=$(run_case push gen/share)
  expect "the baseline comes from the generation _current names, not the flat copy beside it" \
    narrowed "$OUT"
  expect_line "…and it is that generation's recorded commit" baseline "$BASE_SHA" "$OUT"
  # The control, one variable apart: the same fixture with a pointer nothing backs must fall back
  # to the flat copy — and say so, so a silent fallback cannot be mistaken for a resolution.
  rm -rf "$ST_TMP/state"
  printf '%s\n' "Systemorph-MeshWeaver-9999-1" > "$ST_TMP/remote/gen/share/prebuilt-bundles/ID1/plugins/_current"
  OUT=$(run_case push gen/share)
  expect "a pointer naming a generation that is not there falls back to the flat copy" full "$OUT"
  if printf '%s\n' "$OUT" | grep -q "is not on the share"; then
    printf '  OK   %s\n' "…and names the dangling pointer rather than falling back in silence"
  else
    printf '  FAIL %s\n' "the dangling-pointer fallback said nothing"
    FAILED=$((FAILED + 1))
  fi

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
  expect_line "with no per-package listing the range starts at the publication commit" \
    range-base "$BASE_SHA" "$OUT"

  # ── PER-PACKAGE SEALS (MeshWeaver.Plugins#3507). The publication is sealed at HEAD but still
  # ── carries Store's build from BASE; the next run (NEXT) must diff from BASE, or Store's change
  # ── between BASE and HEAD would never be rebuilt.
  echo "a publication sealed per package (the range covers every carried build):"
  ( cd "$REPO" && echo next >> Store/index.json && git add -A && git commit -qm next ) > /dev/null
  NEXT_SHA=$(git -C "$REPO" rev-parse HEAD)
  listing_with() {  # <account/share> <store-commit> — a listing carrying Store at that commit
    printf '{"schema":"mw-sealed-packages/1","publicationCommit":"%s","packages":[{"package":"Edu","sourceCommit":"%s"},{"package":"Store","sourceCommit":"%s","carried":true}]}\n' \
      "$HEAD_SHA" "$HEAD_SHA" "$2" > "$ST_TMP/remote/${1%%/*}/${1#*/}/prebuilt-bundles/ID1/plugins/sealed-packages.json"
  }
  rm -rf "$ST_TMP/state"; write_selector 0 "$OK_JSON"
  seal carry/share "$HEAD_SHA" Store.zip Edu.zip
  listing_with carry/share "$BASE_SHA"
  OUT=$(run_case push carry/share "$NEXT_SHA")
  expect "a carried build narrows" narrowed "$OUT"
  expect_line "…from the OLDEST carried build, not the publication commit" range-base "$BASE_SHA" "$OUT"
  expect_line "…while the baseline (never-seal-backwards) stays the publication commit" baseline "$HEAD_SHA" "$OUT"
  # The control, one variable apart: nothing carried — the range is the publication commit.
  rm -rf "$ST_TMP/state"; listing_with carry/share "$HEAD_SHA"
  OUT=$(run_case push carry/share "$NEXT_SHA")
  expect_line "NEGATIVE CONTROL: a listing that carries nothing narrows from the publication commit" \
    range-base "$HEAD_SHA" "$OUT"
  rm -rf "$ST_TMP/state"; listing_with carry/share "$OTHER_SHA"
  expect "a carried build from a commit that is not the publication's ancestor is a full bake" \
    full "$(run_case push carry/share "$NEXT_SHA")"
  rm -rf "$ST_TMP/state"
  printf 'not json\n' > "$ST_TMP/remote/carry/share/prebuilt-bundles/ID1/plugins/sealed-packages.json"
  expect "an unparseable per-package listing is a full bake" full "$(run_case push carry/share "$NEXT_SHA")"

  echo "the publication a per-package seal carries from (read whatever the scope):"
  rm -rf "$ST_TMP/state"; listing_with carry/share "$BASE_SHA"
  ( unset GITHUB_STEP_SUMMARY GITHUB_ENV GITHUB_OUTPUT; export MOCK_AZ_ROOT="$ST_TMP/remote"
    WITHHOLD_PACKAGES="Store" BAKE_PUBLISH_TARGETS=carry/share IDENTITY=ID1 SOURCE=plugins HEAD_SHA="$NEXT_SHA" \
    REPO_DIR="$REPO" EVENT_NAME=repository_dispatch STATE_DIR="$ST_TMP/state" bash "$ME" > /dev/null 2>&1 )
  if [ "$(cat "$ST_TMP/state/publication-state" 2>/dev/null)" = "sealed" ] \
     && [ "$(cat "$ST_TMP/state/published-commit.txt" 2>/dev/null)" = "$HEAD_SHA" ] \
     && [ -s "$ST_TMP/state/published-packages.json" ] && [ -s "$ST_TMP/state/published-bundles.txt" ]; then
    printf '  OK   %s\n' "a FULL bake (a dispatch) still records the sealed listing, commit and per-package listing"
  else
    printf '  FAIL %s\n' "the carry's reading was not recorded on a full bake (state: $(cat "$ST_TMP/state/publication-state" 2>&1))"
    FAILED=$((FAILED + 1))
  fi
  rm -rf "$ST_TMP/state"
  ( unset GITHUB_STEP_SUMMARY GITHUB_ENV GITHUB_OUTPUT; export MOCK_AZ_ROOT="$ST_TMP/remote"
    WITHHOLD_PACKAGES="Store" BAKE_PUBLISH_TARGETS=empty/share IDENTITY=ID1 SOURCE=plugins HEAD_SHA="$NEXT_SHA" \
    REPO_DIR="$REPO" EVENT_NAME=push STATE_DIR="$ST_TMP/state" bash "$ME" > /dev/null 2>&1 )
  if [ "$(cat "$ST_TMP/state/publication-state" 2>/dev/null)" = "absent" ]; then
    printf '  OK   %s\n' "NEGATIVE CONTROL: a target with no seal records 'absent' — nothing to carry, never 'sealed'"
  else
    printf '  FAIL %s\n' "a target with no seal recorded '$(cat "$ST_TMP/state/publication-state" 2>&1)'"
    FAILED=$((FAILED + 1))
  fi

  # ── ABSENT ONLY WHEN CONFIRMED. A per-package listing or module index that EXISTS and cannot
  # ── be downloaded is not an absent one: read as absent, a carried package's bytes would be
  # ── attributed to the publication commit, and a waiting package's module package dropped.
  echo "an optional file that exists but cannot be read is UNREADABLE, never absent:"
  with_withheld() {  # <target> <event> — run with Store withheld
    ( unset GITHUB_STEP_SUMMARY GITHUB_ENV GITHUB_OUTPUT; export MOCK_AZ_ROOT="$ST_TMP/remote"
      WITHHOLD_PACKAGES="Store" BAKE_PUBLISH_TARGETS="$1" IDENTITY=ID1 SOURCE=plugins HEAD_SHA="$NEXT_SHA" \
      REPO_DIR="$REPO" EVENT_NAME="$2" STATE_DIR="$ST_TMP/state" bash "$ME" 2>&1 )
  }
  rm -rf "$ST_TMP/state"; listing_with carry/share "$BASE_SHA"
  OUT=$(MOCK_AZ_FAIL_DOWNLOAD=sealed-packages.json with_withheld carry/share repository_dispatch)
  case "$(cat "$ST_TMP/state/publication-state" 2>/dev/null)" in
    unreadable*) printf '  OK   %s\n' "a per-package listing that exists but cannot be downloaded marks the publication unreadable" ;;
    *) printf '  FAIL %s\n' "an undownloadable per-package listing recorded '$(cat "$ST_TMP/state/publication-state" 2>&1)'"; FAILED=$((FAILED + 1)) ;;
  esac
  rm -rf "$ST_TMP/state"
  OUT=$(MOCK_AZ_FAIL_DOWNLOAD=sealed-packages.json run_case push carry/share "$NEXT_SHA")
  expect "…and the same fault on the narrowing read is a full bake, never a narrowing from the wrong range" full "$OUT"
  mkdir -p "$ST_TMP/remote/carry/share/prebuilt-bundles/ID1/plugins/modules"
  printf 'store.module.nupkg\n' > "$ST_TMP/remote/carry/share/prebuilt-bundles/ID1/plugins/modules/_index"
  rm -rf "$ST_TMP/state"
  OUT=$(MOCK_AZ_FAIL_DOWNLOAD=modules/_index with_withheld carry/share repository_dispatch)
  case "$(cat "$ST_TMP/state/publication-state" 2>/dev/null)" in
    unreadable*) printf '  OK   %s\n' "a module index that exists but cannot be downloaded marks the publication unreadable" ;;
    *) printf '  FAIL %s\n' "an undownloadable module index recorded '$(cat "$ST_TMP/state/publication-state" 2>&1)'"; FAILED=$((FAILED + 1)) ;;
  esac
  # The control, one variable apart: the same publication with the download working is sealed, the
  # module index and the stamp the carry is bound to are recorded.
  printf 'Systemorph-MeshWeaver.Plugins-9-1\n' > "$ST_TMP/remote/carry/share/prebuilt-bundles/ID1/plugins/_complete.publication"
  rm -rf "$ST_TMP/state"
  OUT=$(with_withheld carry/share repository_dispatch)
  if [ "$(cat "$ST_TMP/state/publication-state" 2>/dev/null)" = "sealed" ] \
     && [ "$(cat "$ST_TMP/state/published-modules.txt" 2>/dev/null)" = "store.module.nupkg" ] \
     && [ "$(cat "$ST_TMP/state/published-publication.txt" 2>/dev/null)" = "Systemorph-MeshWeaver.Plugins-9-1" ]; then
    printf '  OK   %s\n' "NEGATIVE CONTROL: readable, it is sealed — module index and the publication stamp recorded"
  else
    printf '  FAIL %s\n' "readable control: state '$(cat "$ST_TMP/state/publication-state" 2>&1)', stamp '$(cat "$ST_TMP/state/published-publication.txt" 2>&1)'"
    FAILED=$((FAILED + 1))
  fi

  if [ "$FAILED" -gt 0 ]; then
    echo "::error title=bake-scope self-test failed::$FAILED case(s) — this script decides what is NOT rebuilt; a fallback that stops falling back is a silent under-build."
    exit 1
  fi
  echo ""
  echo "bake-scope self-test: 12 full-bake fallbacks, 2 verified nothing-to-do verdicts, 4 pointer-resolution assertions, 5 narrowed assertions, 12 per-package-seal assertions — all green."
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

# ══════════════ THE PUBLICATION POINTER (MeshWeaver#3461) ══════════════
#
# A source directory MAY hold `_current`: one line naming the SUBDIRECTORY that holds the
# publication which currently applies. Absent, it IS its own publication directory — the flat
# layout, which every publication ALSO writes as a compatibility copy for readers that predate the
# pointer. Since #3461 phase 4 the lane's `publication-layout` defaults to `generation`, so a
# prefix any producer has published since carries a pointer and the resolved directory is a
# generation; an absent pointer now means "not published since the flip", not "the fleet is flat".
#
# 🚨 THE RULES ARE THE READER'S, EXACTLY (ShippedPrebuiltBundles.PublicationDirectoryOf, and the
# resolver in publish-bake-bundles.sh). This file, bake-scope.sh and publish-bake-bundles.sh are
# fetched at ONE `platform-ref`, so no pin can carry half of them — but they must also AGREE, or
# this lane would read what portals do not serve. Absent, blank, unreadable, not a single path
# segment, or naming a directory that is not there ⇒ the source directory. A pointer is a NAME: it
# must never be able to address bytes outside its own source directory.
POINTER="_current"
RESOLVED_DIR=""
resolve_publication_dir() { # <account> <share> <source-dir>
  _rp_account="$1"; _rp_share="$2"; _rp_source="$3"
  RESOLVED_DIR="$_rp_source"
  _rp_exists=$(az storage file exists --account-name "$_rp_account" --share-name "$_rp_share" \
    --path "$_rp_source/$POINTER" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  [ "$_rp_exists" = "true" ] || return 0
  _rp_local="$(mktemp)"
  if ! az storage file download --account-name "$_rp_account" --share-name "$_rp_share" \
      --path "$_rp_source/$POINTER" --dest "$_rp_local" \
      --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
    rm -f "$_rp_local"
    return 0
  fi
  _rp_named=$(sed -e 's/[[:space:]]*$//' -e 's/^[[:space:]]*//' "$_rp_local" | grep -m1 '[^[:space:]]' || true)
  rm -f "$_rp_local"
  [ -n "$_rp_named" ] || return 0
  case "$_rp_named" in
    # A rooted name always contains a '/', so `*/*` already covers it — shellcheck SC2222 is
    # right that a separate `/*` arm can never match anything this one does not.
    .|..|*/*|*\\*)
      echo "::warning::$_rp_source/$POINTER names '$_rp_named', which is not a single directory name — reading $_rp_source as its own publication directory."
      return 0 ;;
  esac
  _rp_exists=$(az storage directory exists --account-name "$_rp_account" --share-name "$_rp_share" \
    --name "$_rp_source/$_rp_named" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  if [ "$_rp_exists" != "true" ]; then
    echo "::warning::$_rp_source/$POINTER names generation '$_rp_named', which is not on the share (exists=$_rp_exists) — reading $_rp_source as its own publication directory."
    return 0
  fi
  RESOLVED_DIR="$_rp_source/$_rp_named"
  return 0
}

mkdir -p "$STATE_DIR"

SCOPE="full"
REASON=""
BASELINE=""
RANGE_BASE=""
MOUNT=""

emit() {
  echo "scope=$SCOPE"
  echo "reason=$REASON"
  echo "baseline=$BASELINE"
  echo "range-base=$RANGE_BASE"
  echo "mount=$MOUNT"
  if [ -n "${GITHUB_OUTPUT:-}" ]; then
    {
      echo "scope=$SCOPE"
      echo "reason=$REASON"
      echo "baseline=$BASELINE"
      echo "range-base=$RANGE_BASE"
      echo "mount=$MOUNT"
    } >> "$GITHUB_OUTPUT"
  fi
}

# ══════════════ THE PUBLICATION A PER-PACKAGE SEAL CARRIES FROM (MeshWeaver.Plugins#3507) ══════════════
#
# A caller that seals PER PACKAGE (`withhold-packages` on node-repo-publish-bake.yml) replaces every
# waiting package's fresh bytes by the build the sealed publication already holds for it. That needs
# the sealed listing whatever this bake's scope turns out to be — a FULL bake (a framework dispatch,
# a tooling diff) still seals only what is publishable — so it is read up front, from the FIRST
# target (the carry's source, as for a narrowed bake), and recorded as ONE of three states:
#
#   sealed      the listing, the commit, the module index and (when present) the per-package
#               listing are in $STATE_DIR — the carry can verify every byte it takes against them;
#   absent      the target holds no sealed publication for this identity: nothing was ever sealed,
#               so a waiting package has nothing to carry and stays out;
#   unreadable  it could not be READ. seal-packages.py refuses on it — carrying from an unknown
#               publication either shrinks it or seals a guess.
#
# Read only when something is withheld, so every other caller's decision is byte-identical to before.
# 🚨 ABSENT ONLY WHEN ABSENCE IS CONFIRMED. An optional file that EXISTS and cannot be downloaded
# is not an absent one: a per-package listing read as absent attributes a carried package's bytes
# to the publication commit (and narrows from the wrong range-base), and a module index read as
# absent drops a waiting package's sealed module package from the next seal. So each optional file
# is probed first; `false` is the only answer that means absent. Anything else sets
# SEALED_FILES_FAULT and returns 1 — the caller marks the publication unreadable, or bakes full.
SEALED_FILES_FAULT=""
read_optional_file() { # <account> <share> <remote-path> <local-path>
  local exists
  rm -f "$4"
  exists=$(az storage file exists --account-name "$1" --share-name "$2" \
    --path "$3" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  case "$exists" in
    false) return 0 ;;
    true)
      az storage file download --account-name "$1" --share-name "$2" \
        --path "$3" --dest "$4" --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1 && return 0
      rm -f "$4"
      SEALED_FILES_FAULT="$3 exists but could not be downloaded"; return 1 ;;
    *) SEALED_FILES_FAULT="az could not say whether $3 exists (answered '$exists')"; return 1 ;;
  esac
}
# The `publication` stamp publish-bake-bundles.sh writes on `_complete` — which publication this
# reading IS. carry-forward-bundles.sh is bound to it (EXPECT_PUBLICATION_FILE), so a carry that
# re-resolves the pointer after it advanced cannot take another publication's bytes under this
# reading's listing. Empty for a seal that predates the stamp (the carry then proves what it can).
SEAL_PUBLICATION=""
read_seal_stamp() { # <account> <share> <dest>
  local props
  if ! props=$(az storage file show --account-name "$1" --share-name "$2" \
       --path "$3/$SENTINEL" --auth-mode login --backup-intent \
       --query "[[metadata.publication || '-']]" -o tsv --only-show-errors 2>/dev/null); then
    SEALED_FILES_FAULT="the publication stamp of $3/$SENTINEL could not be read"; return 1
  fi
  SEAL_PUBLICATION=$(printf '%s' "$props" | awk 'NR == 1 { print $1 }')
  [ "$SEAL_PUBLICATION" = "-" ] && SEAL_PUBLICATION=""
  return 0
}
read_sealed_files() { # <account> <share> <dest> <workdir> — the OPTIONAL files beside the sentinel
  SEALED_FILES_FAULT=""
  read_optional_file "$1" "$2" "$3/sealed-packages.json" "$4/sealed-packages.json" || return 1
  read_optional_file "$1" "$2" "$3/modules/_index" "$4/modules-index" || return 1
  read_seal_stamp "$1" "$2" "$3" || return 1
}
record_sealed_state() { # <workdir> — the five artefacts seal-packages.py and the carry read
  cp "$1/$SENTINEL" "$STATE_DIR/published-bundles.txt"
  printf '%s\n' "$SEAL_PUBLICATION" > "$STATE_DIR/published-publication.txt"
  tr -d '[:space:]' < "$1/$SOURCE_MARKER" > "$STATE_DIR/published-commit.txt"
  if [ -f "$1/modules-index" ]; then cp "$1/modules-index" "$STATE_DIR/published-modules.txt"; else rm -f "$STATE_DIR/published-modules.txt"; fi
  if [ -f "$1/sealed-packages.json" ]; then cp "$1/sealed-packages.json" "$STATE_DIR/published-packages.json"; else rm -f "$STATE_DIR/published-packages.json"; fi
  echo "sealed" > "$STATE_DIR/publication-state"
}
read_publication_for_carry() {
  local target account rest share base prefix dest exists work
  target=$(printf '%s\n' $BAKE_PUBLISH_TARGETS | head -n 1)
  account="${target%%/*}"; rest="${target#*/}"; share="${rest%%/*}"; base=""
  case "$rest" in */*) base="${rest#*/}";; esac
  if [ -z "$account" ] || [ -z "$share" ] || [ "$account" = "$target" ]; then
    echo "unreadable the first publish target '$target' is malformed" > "$STATE_DIR/publication-state"; return 0
  fi
  prefix="${base:+$base/}prebuilt-bundles/$IDENTITY/$SOURCE"
  resolve_publication_dir "$account" "$share" "$prefix"
  dest="$RESOLVED_DIR"
  exists=$(az storage file exists --account-name "$account" --share-name "$share" \
    --path "$dest/$SENTINEL" --auth-mode login --backup-intent --query exists -o tsv \
    --only-show-errors 2>/dev/null || echo "unknown")
  case "$exists" in
    false) echo "absent" > "$STATE_DIR/publication-state"
           echo "withheld packages: $target holds no sealed publication under $dest — nothing to carry forward"
           return 0 ;;
    true) ;;
    *) echo "unreadable az could not say whether $target holds a seal under $dest (answered '$exists')" > "$STATE_DIR/publication-state"; return 0 ;;
  esac
  work="$STATE_DIR/carry-read"; mkdir -p "$work"
  if ! az storage file download --account-name "$account" --share-name "$share" \
         --path "$dest/$SENTINEL" --dest "$work/$SENTINEL" --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1 \
     || ! az storage file download --account-name "$account" --share-name "$share" \
         --path "$dest/$SOURCE_MARKER" --dest "$work/$SOURCE_MARKER" --auth-mode login --backup-intent --only-show-errors > /dev/null 2>&1; then
    echo "unreadable the sealed publication under $target/$dest exists but its $SENTINEL or $SOURCE_MARKER could not be downloaded" > "$STATE_DIR/publication-state"
    return 0
  fi
  if ! read_sealed_files "$account" "$share" "$dest" "$work"; then
    echo "unreadable the sealed publication under $target/$dest: $SEALED_FILES_FAULT" > "$STATE_DIR/publication-state"
    return 0
  fi
  record_sealed_state "$work"
  if [ -n "${GITHUB_ENV:-}" ]; then echo "BAKE_CARRY_TARGET=${target}" >> "$GITHUB_ENV"; fi
  echo "withheld packages: their sealed builds are carried from $target/$dest (sealed at $(cat "$STATE_DIR/published-commit.txt"))"
}
if [ -n "$(printf '%s' "${WITHHOLD_PACKAGES:-}" | tr -d '[:space:]')" ]; then
  read_publication_for_carry
fi

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
prev_packages=""
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
  prefix="${base:+$base/}prebuilt-bundles/$IDENTITY/$SOURCE"
  # The publication that currently applies — the generation the pointer names, or the prefix itself
  # when there is none. Reading the prefix under the generation layout would take the baseline off
  # the flat COMPATIBILITY copy while portals served the generation, and a narrowed bake would then
  # diff against a publication nobody serves.
  resolve_publication_dir "$account" "$share" "$prefix"
  dest="$RESOLVED_DIR"

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
  # The per-package listing (MeshWeaver.Plugins#3507), when the publication carries one: it names
  # the commit EACH package's bytes were baked from, and step 5b narrows from the oldest of them.
  read_sealed_files "$account" "$share" "$dest" "$work" \
    || full "$account/$share: the sealed publication under $dest could not be read completely ($SEALED_FILES_FAULT) — which build each package carries is unknown, so the bake cannot be narrowed."
  packages=""
  if [ -f "$work/sealed-packages.json" ]; then packages=$(cat "$work/sealed-packages.json"); fi

  if [ "$targets" -eq 0 ]; then
    prev_sha="$sha"
    prev_listing="$listing"
    prev_packages="$packages"
    record_sealed_state "$work"
    CARRY_TARGET="$target"
  else
    [ "$sha" = "$prev_sha" ] \
      || full "the publish targets disagree on what is published: '$prev_sha' vs '$sha' under $dest. A narrowed bake carries forward ONE publication, so it cannot serve both — one full bake brings every target back into step."
    [ "$listing" = "$prev_listing" ] \
      || full "the publish targets disagree on which bundles are published under $dest — one full bake brings every target back into step."
    [ "$packages" = "$prev_packages" ] \
      || full "the publish targets disagree on the per-package listing under $dest — one full bake brings every target back into step."
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

# ── 4b. never seal BACKWARDS ─────────────────────────────────────────────────────────────────
# 🚨 Per-module deploy (maintainer, 2026-09-11: "builds are not superseded … however they must be
# atomic"): a repo on per-module deploy never cancels a push run on `main`, so two of them can reach
# the bake in either order. When a NEWER commit is already sealed, this run's tree is contained in
# it, and sealing this one would move every instance's sources BACKWARDS — SealedSyncGate holds a
# repository's sources at the sealed commit — until the next seal. That is a positive finding with
# both shas, not an input-shaped skip. (publish-bake-bundles.sh repeats the check at write time: a
# newer run can seal while this one is still baking.)
if git -C "$REPO_DIR" cat-file -e "${prev_sha}^{commit}" 2>/dev/null \
   && git -C "$REPO_DIR" merge-base --is-ancestor "$HEAD_SHA" "$prev_sha" 2>/dev/null; then
  SCOPE="none"
  REASON="the sealed publication records $prev_sha, which is NEWER than this run's $HEAD_SHA and already contains it — a later run sealed first. Sealing this tree would move every instance's sources backwards; nothing to rebuild and nothing to publish."
  echo "::notice title=Nothing to bake::$REASON"
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf '### ✅ A newer publication is already sealed\n\n%s\n' "$REASON" >> "$GITHUB_STEP_SUMMARY"
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

# ── 5b. the RANGE starts at the oldest build the publication still carries (MeshWeaver.Plugins#3507) ──
# A publication sealed per package carries a waiting package's build from an OLDER commit than
# `source-commit.txt`. Diffing from the publication commit alone would never see what that package
# changed before it — it would be carried forward forever and never rebuilt the run it becomes
# publishable. So the range starts at the oldest commit the per-package listing names; every one of
# them must be an ancestor of the publication commit and they must form ONE line, or "what changed
# since every sealed build" has no single base — a full bake, named.
RANGE_BASE="$prev_sha"
if [ -n "$prev_packages" ]; then
  carried_commits=$(printf '%s' "$prev_packages" | jq -r --arg p "$prev_sha" \
      '[.packages[]? | .sourceCommit // empty | select(. != $p)] | unique | .[]' 2>/dev/null) \
    || full "the sealed per-package listing (sealed-packages.json) could not be parsed — which commit each carried build came from is unknown, so the bake cannot be narrowed."
  for c in $carried_commits; do
    git -C "$REPO_DIR" cat-file -e "${c}^{commit}" 2>/dev/null \
      || full "the per-package listing carries a build from $c, which this checkout does not have — what changed since it cannot be taken, so every module is baked."
    git -C "$REPO_DIR" merge-base --is-ancestor "$c" "$prev_sha" 2>/dev/null \
      || full "the per-package listing carries a build from $c, which is not an ancestor of the publication commit $prev_sha — baking every module."
    if git -C "$REPO_DIR" merge-base --is-ancestor "$c" "$RANGE_BASE" 2>/dev/null; then
      RANGE_BASE="$c"
    elif ! git -C "$REPO_DIR" merge-base --is-ancestor "$RANGE_BASE" "$c" 2>/dev/null; then
      full "the per-package listing carries builds from $RANGE_BASE and $c, neither of which contains the other — no single range covers both, so every module is baked."
    fi
  done
  if [ "$RANGE_BASE" != "$prev_sha" ]; then
    echo "::notice title=Narrowing from the oldest carried build::the publication ($prev_sha) still carries package build(s) sealed at older commits; the affected closure is taken from $RANGE_BASE so each of them is rebuilt the run it becomes publishable"
  fi
fi

# ── 6. ask the selector ──────────────────────────────────────────────────────────────────────
# It refuses an empty diff (a CI diff is never empty, so an empty answer is a WRONG answer), it
# classifies scripts/ + .github/ + repo-root files + an unknown top-level dir (a DELETED module)
# as ALL modules, and it closes over transitive DEPENDENTS with the runtime's own edge semantics.
# Every one of those is load-bearing here; a non-zero exit is taken as "cannot narrow", never as
# "nothing affected".
if ! (cd "$REPO_DIR" && python3 "$SELECTOR" --range "${RANGE_BASE}...${HEAD_SHA}" --json) \
      > "$STATE_DIR/affected.json" 2> "$STATE_DIR/affected.log"; then
  sed 's/^/    /' "$STATE_DIR/affected.log" || true
  full "scripts/affected-modules.py could not compute the closure for ${RANGE_BASE}...${HEAD_SHA} (see above) — baking every module."
fi
sed 's/^/    /' "$STATE_DIR/affected.log" || true

run_all=$(jq -r '.runAll' "$STATE_DIR/affected.json" 2>/dev/null || echo "parse-error")
case "$run_all" in
  true)  full "the diff ${RANGE_BASE}...${HEAD_SHA} touches the gate/tooling scope (scripts/, .github/, a repo-root file, or a module directory that no longer exists) — the selector answers ALL modules." ;;
  false) ;;
  *)     full "could not read runAll out of the selector's JSON — baking every module." ;;
esac

MOUNT=$(jq -r '.mount | join(" ")' "$STATE_DIR/affected.json")
if [ -z "$MOUNT" ]; then
  SCOPE="none"
  REASON="nothing between $RANGE_BASE and $HEAD_SHA reaches a module (docs/, e2e/, .claude/ …) — the published bundles are still exactly right, so nothing is rebuilt and nothing is republished."
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
  echo "| range starts at (oldest carried build) | \`$RANGE_BASE\` |"
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
