#!/usr/bin/env bash
# stage-sealed-upstream.sh --repo <owner/name> --source <segment> --modules "<dir> <dir>…"
#                          --provenance <dir> --dest <dir> [--caller <dir>] [--resolved-out <file>]
#                          [--provided-out <file>]
# stage-sealed-upstream.sh --self-test
#
# Stage an UPSTREAM's module SOURCE (its node definitions, its package floors) at exactly the
# commits its SEALED publication's bytes were baked from — the publication the availability gate
# cleared, as `check-release-availability.sh` recorded it under SOURCE_PROVENANCE_DIR — together
# with the transitive `requires` CLOSURE of the modules named.
#
# 🚨 WHY. node-repo-publish-bake.yml composes the upstream's sealed BYTES and installs the staged
# DEFINITIONS beside them. It used to stage the upstream repo's default-branch HEAD at whatever
# moment the job started, so the verdict depended on when a queued job began: on 2026-10-17 a
# Plugins merge raised Store's `minMeshVersion` past the newest sealed platform set and every
# satellite bake that started after it went RED on `PackagePlatformFloorException` (Manufacturing,
# Crm, SocialMedia, Reinsurance), while the same dispatch that started earlier was green.
#
# 🚨 PER PACKAGE, not per publication. A per-package seal (seal-packages.py) CARRIES a waiting
# package's earlier build forward, so the publication commit (`source-commit.txt`) can be newer than
# one package's bytes. Each staged module is therefore checked out at its own `sourceCommit` from the
# publication's `sealed-packages.json`:
#   listing present, module listed       → that entry's sourceCommit (RED when it records none)
#   listing present, module NOT listed   → the publication commit: a bundle-less (content-only)
#                                          package is sealed by no listing entry, and the commit the
#                                          publication was taken from is the only one it rides with
#   listing absent (sealed before #3507) → the publication commit, for every module
#   listing unreadable                   → RED: which commit a carried package came from is unknown
# There is NO fall-back to the default branch anywhere: an unknown commit is RED, naming the gap.
#
# 🚨 THE CLOSURE, not a hand-kept list. `--modules` names what the caller USES. Every package those
# `requires` (root index.json → content.requires), transitively, is staged with them by the same
# per-package rule, and each dependency's own `requires` is read at the commit it is staged at.
# A dependency the caller's own checkout carries (`--caller`) is satisfied there and never staged
# over it — but its own `requires` are walked as well; one found nowhere is RED, naming it and who
# requires it. `--resolved-out` receives the staged set, one module per line — the list every later
# step of the lane must use — and `--provided-out` the caller packages the closure reached, which a
# narrowed bake must mount although its affected selector never saw the upstream edge to them. (MeshWeaver.
# SocialMedia#246: AI gained `requires: Training`, the caller's list did not, and the bake failed
# "Install of 'AI' failed: NodeType(s) not registered: Training/Tour".)
#
# The App installation token, when the repo is private, comes in as $STAGE_TOKEN (never argv).
set -euo pipefail

die() { echo "::error::$1"; exit 1; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# requires_of <index.json> — the package ids a package root's `content.requires` names, one per
# line. NOT a second reader: it loads seal-packages.py (beside this script) and uses ITS `_requires`
# and `dependency_id` — the lane's port of the runtime's PackageDependencyGraph.DependencyId
# ("Store@^1.0.0" → "Store"; a blank or version-only entry is no dependency). An index.json that is
# absent or not a JSON object exits 2: the closure of a package whose declaration cannot be read is
# UNKNOWN, and an unknown closure is RED, never "no dependencies".
requires_of() {
  python3 -I - "$SCRIPT_DIR/seal-packages.py" "$1" <<'PY_REQUIRES'
import importlib.util, json, sys
spec = importlib.util.spec_from_file_location("seal_packages", sys.argv[1])
mod = importlib.util.module_from_spec(spec)
sys.modules["seal_packages"] = mod
spec.loader.exec_module(mod)
try:
    with open(sys.argv[2], encoding="utf-8") as f:
        doc = json.load(f)
except (OSError, ValueError) as e:
    print(f"{sys.argv[2]}: {e}", file=sys.stderr)
    sys.exit(2)
if not isinstance(doc, dict):
    print(f"{sys.argv[2]}: not a JSON object", file=sys.stderr)
    sys.exit(2)
own = str(doc.get("id") or "")
seen = []
for entry in mod._requires(doc):
    dep = mod.dependency_id(entry)
    if dep and dep != own and dep not in seen:
        seen.append(dep)
print("\n".join(seen))
PY_REQUIRES
}

# in_words <word> <space-separated list>
in_words() { case " $2 " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

# await_reach — called from stage(), reads its $repo, $url, $dest and ${auth[@]}. Waits until the
# stage credential can READ the stage repository, and only then lets the first fetch run.
#
# 🚨 WHY. The lane mints the installation token (actions/create-github-app-token, scoped to the
# stage repo) and fetches with it well under a second later. Measured 2026-10-26 on the satellites'
# publish bakes, same lane commit, same secret, same sealed commit: Reinsurance staged
# MeshWeaver.Plugins@ed336b36dc at 17:56:37Z while Manufacturing's fetch of that very commit, in the
# same second, answered `remote: Repository not found.` — and Crm, Reinsurance and Manufacturing
# each went green and red on alternate attempts (8 of 12 stage steps red from 16:37Z on, 0 of 18
# before). The token step had succeeded every time, which GitHub refuses for an installation
# that cannot reach the named repository. So the 404 is a token whose repository grant had not
# yet reached the git endpoint that served the fetch.
#
# The condition is waited on, never assumed: an `ls-remote` with the same credential, repeated
# while (and ONLY while) the answer is exactly "Repository not found", up to a bounded deadline.
# Every other failure is RED at once, and a grant that never arrives is RED at the deadline,
# naming the App provisioning to check. When the wait was needed, a ::notice:: says how many
# probes and how many seconds it took, so the lag is measured in every run.
#
# The whole wait is bounded: each probe gets only the budget left and is killed when it outlives
# it, the pause between probes never exceeds what is left, and a probe that does not answer in
# time is RED (a stalled connection is not the propagation answer).
await_reach() {
  local deadline="${STAGE_REACH_SECONDS:-90}" interval="${STAGE_REACH_INTERVAL_SECONDS:-3}"
  local start now left probes=0 rc err="$dest.reach-err" probe_dir="$dest.reach-probe"
  [ "$deadline" -ge 1 ] || deadline=1
  rm -rf "$probe_dir"
  git init -q "$probe_dir"
  start=$(date +%s)
  while :; do
    now=$(date +%s)
    left=$((deadline - (now - start)))
    if [ "$left" -le 0 ]; then
      rm -rf "$probe_dir"
      die "$repo still answers 'Repository not found' to the stage credential after $probes probe(s) over $((now - start)) s. This is no propagation delay: the App behind stage-repo-app-id is not installed on $repo, or cannot read its contents. Check that App's installation on the repository's owner and its Contents: Read permission."
    fi
    probes=$((probes + 1))
    rc=0
    probe_once "$left" || rc=$?
    now=$(date +%s)
    if [ "$rc" = 0 ]; then
      rm -rf "$err" "$probe_dir"
      if [ "$probes" -gt 1 ]; then
        echo "::notice::the stage credential reached $repo on probe $probes, $((now - start)) s after the first answered 'Repository not found' — a freshly minted token's repository grant had not yet propagated."
      fi
      return 0
    fi
    if [ "$rc" = 124 ]; then
      rm -rf "$probe_dir"
      die "probe $probes of $repo with the stage credential did not answer within the ${deadline} s budget ($((now - start)) s elapsed) — a stalled connection, not a propagation answer."
    fi
    if ! grep -q 'Repository not found' "$err"; then
      rm -rf "$probe_dir"
      die "could not read $repo with the stage credential: $(tr '\n' ' ' < "$err")"
    fi
    left=$((deadline - (now - start)))
    [ "$left" -gt 0 ] || continue
    if [ "$interval" -lt "$left" ]; then sleep "$interval"; else sleep "$left"; fi
  done
}

# probe_once <budget-seconds> — one ls-remote with the stage credential, killed (exit 124) when it
# outlives the budget. It runs in $probe_dir, a repository of its own, exactly like the fetch: the
# step's working directory is the CALLER's checkout, whose persisted URL-scoped extraheader (the
# caller's own token) would otherwise ride along beside ours and answer "Repository not found" for
# a valid stage token.
probe_once() {
  ( cd "$probe_dir" && exec git "${auth[@]}" ls-remote --exit-code "$url" HEAD ) > /dev/null 2> "$err" &
  local pid=$! ticks=0  # ticks of 0.2 s
  while kill -0 "$pid" 2> /dev/null; do
    if [ "$ticks" -ge $(($1 * 5)) ]; then
      kill "$pid" 2> /dev/null || true
      wait "$pid" 2> /dev/null || true
      return 124
    fi
    sleep 0.2
    ticks=$((ticks + 1))
  done
  wait "$pid"
}

stage() {
  local repo="" source="" modules="" prov="" dest="" caller="" resolved_out="" provided_out=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --repo)         repo="$2"; shift 2 ;;
      --source)       source="$2"; shift 2 ;;
      --modules)      modules="$2"; shift 2 ;;
      --provenance)   prov="$2"; shift 2 ;;
      --dest)         dest="$2"; shift 2 ;;
      --caller)       caller="$2"; shift 2 ;;
      --resolved-out) resolved_out="$2"; shift 2 ;;
      --provided-out) provided_out="$2"; shift 2 ;;
      *) die "stage-sealed-upstream.sh: unknown argument '$1'" ;;
    esac
  done
  for required in repo source modules prov dest; do
    [ -n "${!required}" ] || die "stage-sealed-upstream.sh: --${required} is required"
  done
  local commit_file="$prov/$source.commit" listing_state_file="$prov/$source.listing"
  [ -r "$commit_file" ] || die "no provenance for upstream '$source' at $commit_file — the availability gate did not record the sealed publication this run was cleared against, so which commit to stage is unknown."
  [ -r "$listing_state_file" ] || die "no listing state for upstream '$source' at $listing_state_file — the availability gate did not record whether the publication carries a per-package listing."
  local pub_commit listing_state listing="$prov/$source.sealed-packages.json"
  pub_commit=$(tr -d '[:space:]' < "$commit_file")
  listing_state=$(tr -d '[:space:]' < "$listing_state_file")
  [[ "$pub_commit" =~ ^[0-9a-f]{40}$ ]] || die "the sealed '$source' publication names no source commit ('${pub_commit:-<none>}' in $commit_file) — refusing to stage ${repo}'s default branch instead: its definitions and package floors need not match the sealed bytes this bake composes."
  case "$listing_state" in
    present) jq -e '.packages | type == "array"' "$listing" > /dev/null \
               || die "the sealed '$source' publication's per-package listing ($listing) is not a listing — which commit each carried package came from is unknown." ;;
    absent) echo "::notice::the sealed '$source' publication predates per-package listings — every module is staged at its publication commit $pub_commit." ;;
    *) die "the sealed '$source' publication's per-package listing could not be read (state '$listing_state') — a carried package may be older than the publication commit, so staging it there could pair newer definitions with older bytes." ;;
  esac

  local m c
  read -r -a module_list <<< "$(printf '%s' "$modules" | tr '\n' ' ')"
  [ "${#module_list[@]}" -gt 0 ] || die "stage-sealed-upstream.sh: --modules names no module"

  mkdir -p "$dest"
  local auth=()
  if [ -n "${STAGE_TOKEN:-}" ]; then
    auth=(-c "http.extraheader=AUTHORIZATION: basic $(printf 'x-access-token:%s' "$STAGE_TOKEN" | base64 | tr -d '\n')")
  fi
  local url="$repo"
  case "$repo" in */*) [ -d "$repo" ] || url="https://github.com/$repo" ;; esac
  await_reach


  # Round 1 stages what the caller named; every later round stages the dependencies the previous
  # round's packages declare that are neither staged nor carried by the caller. A fixpoint, so a
  # cycle ends (a staged package is never queued twice).
  local pending="" staged="" provided="" why_file="$dest.why"
  : > "$why_file"
  for m in "${module_list[@]}"; do
    in_words "$m" "$pending" && continue
    pending="$pending $m"
    printf '%s\tnamed by the caller\n' "$m" >> "$why_file"
  done
  local named_count
  # shellcheck disable=SC2086
  named_count=$(set -- $pending; echo $#)

  local round=0 plan="$dest.plan"
  while [ -n "${pending// /}" ]; do
    round=$((round + 1))
    # module → commit, one line each
    : > "$plan"
    for m in $pending; do
      c="$pub_commit"
      if [ "$listing_state" = "present" ]; then
        local listed
        listed=$(jq -r --arg m "$m" '[.packages[] | select((.package | ascii_downcase) == ($m | ascii_downcase))] | length' "$listing")
        if [ "$listed" != "0" ]; then
          c=$(jq -r --arg m "$m" '[.packages[] | select((.package | ascii_downcase) == ($m | ascii_downcase))][0].sourceCommit // ""' "$listing")
          [[ "$c" =~ ^[0-9a-f]{40}$ ]] || die "the sealed '$source' publication lists '$m' but records no source commit for it ('${c:-<none>}') — its bytes cannot be paired with a source."
        else
          echo "::notice::'$m' has no entry in the sealed '$source' listing (a bundle-less package) — staged at the publication commit $pub_commit."
        fi
      fi
      printf '%s %s\n' "$c" "$m" >> "$plan"
    done

    local commits
    commits=$(cut -d' ' -f1 "$plan" | sort -u)
    for c in $commits; do
      local work="$dest.git-$c" mods
      mods=$(awk -v c="$c" '$1 == c { print $2 }' "$plan" | tr '\n' ' ')
      rm -rf "$work"
      git init -q "$work"
      git -C "$work" remote add origin "$url"
      git -C "$work" "${auth[@]}" fetch -q --no-tags --depth 1 --filter=blob:none origin "$c" \
        || die "could not fetch $repo@$c (the commit the sealed '$source' publication names for: $mods) — is the stage-repo App installed on it, and is the commit still reachable?"
      # shellcheck disable=SC2086
      git -C "$work" sparse-checkout set --cone $mods
      git -C "$work" "${auth[@]}" -c advice.detachedHead=false checkout -q FETCH_HEAD \
        || die "could not check out $repo@$c for: $mods"
      for m in $mods; do
        local why
        why=$(awk -F'\t' -v m="$m" '$1 == m { print $2; exit }' "$why_file")
        if [ ! -d "$work/$m" ]; then
          if [ "$why" = "named by the caller" ]; then
            die "$repo@$c has no '$m' directory — the commit the sealed '$source' publication names for it does not carry that module."
          fi
          die "'$m' is in the requires closure of the staged packages ($why), but $repo@$c — the commit the sealed '$source' publication names for it — carries no '$m' directory, and this repository does not carry it either. The bake would install a package whose dependency is absent (\"NodeType(s) not registered\"). Fix the requires entry upstream, or bring '$m' into a source this lane stages."
        fi
        rm -rf "${dest:?}/$m"
        cp -R "$work/$m" "$dest/$m"
        staged="$staged $m"
        echo "staged $repo/$m at $c — the commit the sealed '$source' publication's bytes for it came from ($why)"
      done
      rm -rf "$work"
    done

    # The next round: every dependency of this round's packages not already staged or provided.
    # 🚨 A caller-carried dependency is not a leaf: its OWN requires are walked too (read from the
    # caller's checkout, right away — nothing to fetch), so an upstream package it needs is staged
    # and a further caller package it needs is reported. Every caller package the closure reaches
    # goes to --provided-out, because a NARROWED bake mounts only the affected set plus the staged
    # set, and a caller package reached only THROUGH an upstream one is in neither (review #7042).
    local next="" deps d rc scan="" item m_dir
    for m in $pending; do scan="$scan S:$m"; done
    while [ -n "${scan// /}" ]; do
      # shellcheck disable=SC2086
      set -- $scan
      item="$1"; shift; scan="$*"
      m="${item#?:}"
      case "$item" in S:*) m_dir="$dest/$m" ;; *) m_dir="$caller/$m" ;; esac
      rc=0
      deps=$(requires_of "$m_dir/index.json" 2> "$dest.req-err") || rc=$?
      [ "$rc" = 0 ] || die "the staged '$m' has no readable package declaration ($(cat "$dest.req-err")) — its requires closure is unknown, so what this bake must stage beside it is unknown."
      for d in $deps; do
        if in_words "$d" "$staged $next $provided"; then continue; fi
        if [ -n "$caller" ] && [ -f "$caller/$d/index.json" ]; then
          provided="$provided $d"
          scan="$scan C:$d"
          echo "'$d' (required by '$m') is this repository's own package — satisfied by the checkout, not staged"
          continue
        fi
        next="$next $d"
        printf '%s\trequired by %s\n' "$d" "$m" >> "$why_file"
      done
    done
    pending="$next"
  done
  rm -f "$plan" "$why_file" "$dest.req-err"

  local total
  # shellcheck disable=SC2086
  set -- $staged
  total=$#
  echo "requires closure: $total package(s) staged — $named_count named by the caller, $((total - named_count)) added by their requires, in $round round(s):$staged"
  if [ -n "$resolved_out" ]; then
    printf '%s\n' "$@" > "$resolved_out"
  fi
  if [ -n "$provided_out" ]; then
    # shellcheck disable=SC2086
    set -- $provided
    if [ $# -gt 0 ]; then printf '%s\n' "$@" > "$provided_out"; else : > "$provided_out"; fi
  fi
}

# Each check passes its condition as `$(cond; echo $?)` — the $? of the CONDITION is the point.
# shellcheck disable=SC2319
self_test() {
  local t fails=0
  t=$(mktemp -d)
  trap 'rm -rf "$t"' RETURN
  local up="$t/upstream"
  git init -q "$up"
  git -C "$up" config user.email t@t; git -C "$up" config user.name t
  git -C "$up" config uploadpack.allowFilter true
  git -C "$up" config uploadpack.allowAnySHA1InWant true
  mkdir -p "$up/Store" "$up/Publish" "$up/Edu"
  echo old > "$up/Store/floor"; echo old > "$up/Publish/floor"; echo old > "$up/Edu/floor"
  echo '{"id":"Store","content":{}}' > "$up/Store/index.json"
  echo '{"id":"Publish","content":{"requires":["Store@^1.0.0"]}}' > "$up/Publish/index.json"
  echo '{"id":"Edu","content":{"requires":["Store"]}}' > "$up/Edu/index.json"
  git -C "$up" add -A; git -C "$up" commit -qm A
  local A; A=$(git -C "$up" rev-parse HEAD)
  echo new > "$up/Store/floor"; echo new > "$up/Publish/floor"; echo new > "$up/Edu/floor"
  git -C "$up" add -A; git -C "$up" commit -qm B
  local B; B=$(git -C "$up" rev-parse HEAD)
  echo head > "$up/Store/floor"; git -C "$up" commit -qam HEAD

  # The closure fixture: Alpha requires Beta (versioned), Beta requires Gamma (plus entries that
  # name nothing), Gamma requires nothing. P1 → P2 moves Gamma's bytes; P3 deletes Gamma while
  # Beta still requires it; P4 breaks Alpha's declaration.
  local cl="$t/closure"
  git init -q "$cl"
  git -C "$cl" config user.email t@t; git -C "$cl" config user.name t
  git -C "$cl" config uploadpack.allowFilter true
  git -C "$cl" config uploadpack.allowAnySHA1InWant true
  mkdir -p "$cl/Alpha" "$cl/Beta" "$cl/Gamma" "$cl/Other"
  echo '{"id":"Alpha","content":{"requires":["Beta@^1.0.0"]}}' > "$cl/Alpha/index.json"
  echo '{"id":"Beta","content":{"requires":["Gamma", "  ", "@^2.0.0", "Beta"]}}' > "$cl/Beta/index.json"
  echo '{"id":"Gamma","content":{}}' > "$cl/Gamma/index.json"
  echo '{"id":"Other","content":{}}' > "$cl/Other/index.json"
  echo p1 > "$cl/Gamma/floor"
  git -C "$cl" add -A; git -C "$cl" commit -qm P1
  local P1; P1=$(git -C "$cl" rev-parse HEAD)
  echo p2 > "$cl/Gamma/floor"; git -C "$cl" commit -qam P2
  local P2; P2=$(git -C "$cl" rev-parse HEAD)
  git -C "$cl" rm -rq Gamma; git -C "$cl" commit -qm P3
  local P3; P3=$(git -C "$cl" rev-parse HEAD)
  echo 'not json' > "$cl/Alpha/index.json"; git -C "$cl" commit -qam P4
  local P4; P4=$(git -C "$cl" rev-parse HEAD)
  # P5: Alpha restored; Beta now requires a CALLER package (Local), which itself requires an
  # upstream package (Gamma, restored) and another caller package (Local2).
  echo '{"id":"Alpha","content":{"requires":["Beta@^1.0.0"]}}' > "$cl/Alpha/index.json"
  echo '{"id":"Beta","content":{"requires":["Local@^1.0.0"]}}' > "$cl/Beta/index.json"
  mkdir -p "$cl/Gamma"; echo '{"id":"Gamma","content":{}}' > "$cl/Gamma/index.json"
  git -C "$cl" add -A; git -C "$cl" commit -qm P5
  local P5; P5=$(git -C "$cl" rev-parse HEAD)

  check() { # <name> <condition-exit> <why>
    if [ "$2" = 0 ]; then echo "  ok   $1"; else echo "  FAIL $1 — $3"; fails=$((fails + 1)); fi
  }
  prov() { # <dir> <pubcommit> <listing-state> [listing-json]
    mkdir -p "$1"; echo "$2" > "$1/plugins.commit"; echo "$3" > "$1/plugins.listing"
    rm -f "$1/plugins.sealed-packages.json"
    [ -z "${4:-}" ] || printf '%s' "$4" > "$1/plugins.sealed-packages.json"
  }
  run() { ( set -euo pipefail; stage --repo "$up" --source plugins --modules "$2" --provenance "$1" --dest "$1/out" ) > "$1/log" 2>&1; }
  # runc <dir> <modules> [extra args…] — against the closure fixture, the resolved set in $1/resolved
  runc() {
    local d="$1" mods="$2"; shift 2
    ( set -euo pipefail; stage --repo "$cl" --source plugins --modules "$mods" --provenance "$d" --dest "$d/out" \
        --resolved-out "$d/resolved" "$@" ) > "$d/log" 2>&1
  }
  resolved() { tr '\n' ' ' < "$1/resolved" | sed 's/ $//'; }

  # 1. A carried package (Store at A) beside a fresh one (Publish at B), publication commit B; Edu
  #    bundle-less → publication commit. HEAD is never staged.
  prov "$t/c1" "$B" present "{\"packages\":[{\"package\":\"Store\",\"sourceCommit\":\"$A\",\"carried\":true},{\"package\":\"Publish\",\"sourceCommit\":\"$B\"}]}"
  run "$t/c1" $'Store\nPublish\nEdu'; local rc=$?
  check "per-package commits: a carried package is staged at ITS commit" \
    "$([ $rc = 0 ] && [ "$(cat "$t/c1/out/Store/floor")" = old ] && [ "$(cat "$t/c1/out/Publish/floor")" = new ] && [ "$(cat "$t/c1/out/Edu/floor")" = new ]; echo $?)" \
    "rc=$rc; $(cat "$t/c1/log")"
  # 2. Legacy publication (no listing): every module at the publication commit.
  prov "$t/c2" "$A" absent
  run "$t/c2" "Store Publish"; rc=$?
  check "a publication without a listing stages every module at its publication commit" \
    "$([ $rc = 0 ] && [ "$(cat "$t/c2/out/Store/floor")" = old ]; echo $?)" "rc=$rc; $(cat "$t/c2/log")"
  # 3. Refusals — never a fall-back to HEAD.
  prov "$t/c3" "" absent; run "$t/c3" Store; rc=$?
  check "an unknown publication commit is RED" "$([ $rc != 0 ] && grep -q 'names no source commit' "$t/c3/log"; echo $?)" "rc=$rc; $(cat "$t/c3/log")"
  prov "$t/c4" "$B" unreadable; run "$t/c4" Store; rc=$?
  check "an unreadable listing is RED" "$([ $rc != 0 ] && grep -q 'could not be read' "$t/c4/log"; echo $?)" "rc=$rc; $(cat "$t/c4/log")"
  prov "$t/c5" "$B" present '{"packages":[{"package":"Store","sourceCommit":null}]}'; run "$t/c5" Store; rc=$?
  check "a listed package with no commit is RED" "$([ $rc != 0 ] && grep -q 'records no source commit' "$t/c5/log"; echo $?)" "rc=$rc; $(cat "$t/c5/log")"
  rm -rf "$t/c6"; mkdir -p "$t/c6"; run "$t/c6" Store; rc=$?
  check "missing provenance is RED" "$([ $rc != 0 ] && grep -q 'no provenance' "$t/c6/log"; echo $?)" "rc=$rc; $(cat "$t/c6/log")"
  prov "$t/c7" "$A" absent; run "$t/c7" "Store Video"; rc=$?
  check "a module the commit does not carry is RED" "$([ $rc != 0 ] && grep -q "has no 'Video' directory" "$t/c7/log"; echo $?)" "rc=$rc; $(cat "$t/c7/log")"

  # ── the requires closure ──
  # 8. Naming Alpha stages Alpha, Beta AND Gamma — and the closure keeps the per-package rule:
  #    Gamma is carried at P1 in the listing, so it is staged at P1, not the publication commit P2.
  prov "$t/c8" "$P2" present "{\"packages\":[{\"package\":\"Gamma\",\"sourceCommit\":\"$P1\",\"carried\":true}]}"
  runc "$t/c8" Alpha; rc=$?
  check "naming A stages A, B and C (A requires B requires C), C at ITS sealed commit" \
    "$([ $rc = 0 ] && [ "$(resolved "$t/c8")" = "Alpha Beta Gamma" ] && [ -f "$t/c8/out/Beta/index.json" ] && [ "$(cat "$t/c8/out/Gamma/floor")" = p1 ] && [ ! -d "$t/c8/out/Other" ] && grep -q 'staged .*/Gamma .*(required by Beta)' "$t/c8/log"; echo $?)" \
    "rc=$rc; resolved='$(cat "$t/c8/resolved" 2>/dev/null)'; $(cat "$t/c8/log")"
  # 9. A missing C is RED, naming C and who requires it — never skipped.
  prov "$t/c9" "$P3" absent; runc "$t/c9" Alpha; rc=$?
  check "a missing dependency in the closure is RED, naming it and its dependent" \
    "$([ $rc != 0 ] && grep -q "'Gamma' is in the requires closure of the staged packages (required by Beta)" "$t/c9/log" && [ ! -e "$t/c9/resolved" ]; echo $?)" \
    "rc=$rc; $(cat "$t/c9/log")"
  # 10. The same missing C, carried by the CALLER's checkout: satisfied there, never staged over it.
  prov "$t/c10" "$P3" absent
  mkdir -p "$t/c10/caller/Gamma"; echo '{"id":"Gamma"}' > "$t/c10/caller/Gamma/index.json"
  runc "$t/c10" Alpha --caller "$t/c10/caller"; rc=$?
  check "a dependency the caller's own checkout carries is satisfied there, not staged" \
    "$([ $rc = 0 ] && [ "$(resolved "$t/c10")" = "Alpha Beta" ] && [ ! -d "$t/c10/out/Gamma" ] && grep -q "'Gamma' (required by 'Beta') is this repository's own package" "$t/c10/log"; echo $?)" \
    "rc=$rc; $(cat "$t/c10/log")"
  # 11. An unreadable declaration is an UNKNOWN closure — RED, never "no dependencies".
  prov "$t/c11" "$P4" absent; runc "$t/c11" Alpha; rc=$?
  check "a staged package whose index.json cannot be read is RED" \
    "$([ $rc != 0 ] && grep -q "the staged 'Alpha' has no readable package declaration" "$t/c11/log"; echo $?)" "rc=$rc; $(cat "$t/c11/log")"
  # 12. A dependency the caller ALSO names is staged once, and naming the leaf alone stages the leaf alone.
  prov "$t/c12" "$P2" absent; runc "$t/c12" $'Gamma\nAlpha\nGamma'; rc=$?
  check "a named dependency is staged once; a leaf pulls in nothing" \
    "$([ $rc = 0 ] && [ "$(resolved "$t/c12")" = "Gamma Alpha Beta" ] && [ "$(grep -c 'staged .*/Gamma ' "$t/c12/log")" = 1 ] \
       && grep -q 'requires closure: 3 package(s) staged — 2 named by the caller, 1 added by their requires' "$t/c12/log"; echo $?)" \
    "rc=$rc; resolved='$(cat "$t/c12/resolved" 2>/dev/null)'; $(cat "$t/c12/log")"

  # 13. A caller package reached only THROUGH an upstream one is not a leaf: its own requires are
  #     walked (the upstream Gamma it needs is staged) and every caller package the closure reaches
  #     is reported in --provided-out, so a narrowed mount can carry it (review on #7042).
  prov "$t/c13" "$P5" absent
  mkdir -p "$t/c13/caller/Local" "$t/c13/caller/Local2"
  echo '{"id":"Local","content":{"requires":["Gamma","Local2"]}}' > "$t/c13/caller/Local/index.json"
  echo '{"id":"Local2","content":{}}' > "$t/c13/caller/Local2/index.json"
  runc "$t/c13" Alpha --caller "$t/c13/caller" --provided-out "$t/c13/provided"; rc=$?
  check "a caller package reached through an upstream one has its requires walked and is reported" \
    "$([ $rc = 0 ] && [ "$(resolved "$t/c13")" = "Alpha Beta Gamma" ] && [ "$(tr '\n' ' ' < "$t/c13/provided" | sed 's/ $//')" = "Local Local2" ] \
       && [ ! -d "$t/c13/out/Local" ]; echo $?)" \
    "rc=$rc; resolved='$(cat "$t/c13/resolved" 2>/dev/null)'; provided='$(cat "$t/c13/provided" 2>/dev/null)'; $(cat "$t/c13/log")"

  # ── the credential reach wait ──
  # A fake `git` ahead of the real one answers the first $FAKE_FAILS ls-remote probes with
  # $FAKE_ERR and passes everything else through, counting probes in $FAKE_COUNT.
  local fake="$t/fakegit" real_git
  real_git=$(command -v git)
  mkdir -p "$fake"
  cat > "$fake/git" <<FAKE_GIT
#!/usr/bin/env bash
for a in "\$@"; do
  if [ "\$a" = ls-remote ]; then
    n=\$(( \$(cat "\$FAKE_COUNT" 2>/dev/null || echo 0) + 1 )); echo "\$n" > "\$FAKE_COUNT"
    if [ "\$n" -le "\$FAKE_FAILS" ]; then printf '%b\n' "\$FAKE_ERR" >&2; exit 128; fi
    # A persisted checkout credential in the repository the probe runs in rides along beside the
    # stage token — GitHub then answers as if the stage token could not read the repo.
    if "$real_git" config --get-regexp 'extraheader' 2>/dev/null | grep -q CALLER-CHECKOUT-TOKEN; then
      echo 'remote: Repository not found.' >&2; exit 128
    fi
    if [ -n "\${FAKE_SLEEP:-}" ]; then exec sleep "\$FAKE_SLEEP"; fi
    break
  fi
done
exec "$real_git" "\$@"
FAKE_GIT
  chmod +x "$fake/git"
  local nf=$'remote: Repository not found.\nfatal: repository \'https://github.com/x/y/\' not found'
  # runf <dir> <fails> <err> <deadline> [cwd] [probe-sleep] — stage Store through the fake git,
  # from <cwd> (default: here), each probe that passes the fake stalling <probe-sleep> seconds
  runf() {
    prov "$1" "$A" absent
    ( set -euo pipefail; export PATH="$fake:$PATH" FAKE_COUNT="$1/count" FAKE_FAILS="$2" FAKE_ERR="$3" \
        STAGE_REACH_SECONDS="$4" STAGE_REACH_INTERVAL_SECONDS=0 FAKE_SLEEP="${6:-}"
      cd "${5:-.}"
      stage --repo "$up" --source plugins --modules Store --provenance "$1" --dest "$1/out" ) > "$1/log" 2>&1
  }
  # 14. Not found twice, then reachable: staged, and the lag is reported.
  runf "$t/c14" 2 "$nf" 30; rc=$?
  check "a credential that answers 'not found' and then reads is waited for, and the wait is reported" \
    "$([ $rc = 0 ] && [ "$(cat "$t/c14/count")" = 3 ] && [ "$(cat "$t/c14/out/Store/floor")" = old ] \
       && grep -q '::notice::the stage credential reached .* on probe 3' "$t/c14/log"; echo $?)" \
    "rc=$rc; count=$(cat "$t/c14/count" 2>/dev/null); $(cat "$t/c14/log")"
  # 15. Reachable at once: one probe, no notice.
  runf "$t/c15" 0 "$nf" 30; rc=$?
  check "a credential that reads at once is probed once and reports nothing" \
    "$([ $rc = 0 ] && [ "$(cat "$t/c15/count")" = 1 ] && ! grep -q 'stage credential reached' "$t/c15/log"; echo $?)" \
    "rc=$rc; $(cat "$t/c15/log")"
  # 16. Never reachable: RED at the deadline, naming the App provisioning, nothing staged.
  runf "$t/c16" 1000 "$nf" 0; rc=$?
  check "a credential that never reads is RED at the deadline, naming the App provisioning" \
    "$([ $rc != 0 ] && grep -q "still answers 'Repository not found'" "$t/c16/log" && grep -q 'Contents: Read' "$t/c16/log" \
       && [ ! -d "$t/c16/out/Store" ]; echo $?)" "rc=$rc; $(cat "$t/c16/log")"
  # 17. Any other failure is RED at once — the wait is for the propagation answer only.
  runf "$t/c17" 1000 "fatal: Authentication failed for 'https://github.com/x/y/'" 30; rc=$?
  check "a failure other than 'Repository not found' is RED on the first probe" \
    "$([ $rc != 0 ] && [ "$(cat "$t/c17/count")" = 1 ] && grep -q 'could not read .* with the stage credential: fatal: Authentication failed' "$t/c17/log"; echo $?)" \
    "rc=$rc; count=$(cat "$t/c17/count" 2>/dev/null); $(cat "$t/c17/log")"
  # 18. Run from inside the CALLER's checkout, whose persisted URL-scoped credential (the caller's
  #     own token) would ride along: the probe runs in a repository of its own, so it reads.
  local co="$t/c18-checkout"
  git init -q "$co"
  git -C "$co" config http.https://github.com/.extraheader "AUTHORIZATION: basic CALLER-CHECKOUT-TOKEN"
  runf "$t/c18" 0 "$nf" 3 "$co"; rc=$?
  check "the probe ignores the caller checkout's persisted credential" \
    "$([ $rc = 0 ] && [ "$(cat "$t/c18/count")" = 1 ] && [ "$(cat "$t/c18/out/Store/floor")" = old ]; echo $?)" \
    "rc=$rc; count=$(cat "$t/c18/count" 2>/dev/null); $(cat "$t/c18/log")"
  # 19. A probe that stalls is killed at the budget and is RED — never held to the job timeout.
  local s0 s1
  s0=$(date +%s)
  runf "$t/c19" 0 "$nf" 2 . 30; rc=$?
  s1=$(date +%s)
  check "a stalled probe is killed at the budget and is RED" \
    "$([ $rc != 0 ] && [ $((s1 - s0)) -le 6 ] && grep -q 'did not answer within the 2 s budget' "$t/c19/log" && [ ! -d "$t/c19/out/Store" ]; echo $?)" \
    "rc=$rc; took $((s1 - s0)) s; $(cat "$t/c19/log")"

  [ "$fails" = 0 ] || { echo "stage-sealed-upstream.sh --self-test: $fails failure(s)"; return 1; }
  echo "stage-sealed-upstream.sh --self-test: all cases passed"
}

if [ "${1:-}" = "--self-test" ]; then
  set +e
  self_test
  exit $?
fi
stage "$@"
