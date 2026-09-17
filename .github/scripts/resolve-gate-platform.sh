#!/usr/bin/env bash
# resolve-gate-platform.sh --volume-root <dir> [--tester-digest <sha256:…>] [--portal-digest <sha256:…>]
#                          [--set <3.0.0-ci.N>] [--wait-seconds <N>]
#
# Decides WHERE a gate shard takes the platform from, and — when that is the CI platform volume —
# proves the set on it is the one the caller pinned and is COMPLETE, before a single assembly is
# read. Prints `key=value` lines to stdout for the lane to consume (and to $GITHUB_OUTPUT when set).
#
# THE TWO MODES, and why the choice is a MEASUREMENT and not an input (MeshWeaver#4113):
#
#   volume     `<volume-root>` (the runner pod's read-only mount of the CI platform share,
#              /opt/platform — Systemorph/Memex deployments/aks/ci-runners/ci-platform.yaml) EXISTS.
#              The tester's /app and the portal's /app are already on the node; the shard runs the
#              tester as a PROCESS on the runner's .NET and needs no Docker daemon, no registry
#              credential and no pull.
#   container  `<volume-root>` does not exist — a GitHub-hosted `ubuntu-latest` runner, which has a
#              Docker daemon and no such mount. The shard pulls both images and runs the tester in
#              a container, exactly as the lane did before #4113.
#
# 🚨 THERE IS NO FALLBACK BETWEEN THEM, and that is the whole point. Once the mount is there, every
# failure below is RED and NAMES `ci-platform-refresh`; a set that is absent, half-written or paired
# with another portal must never degrade into `docker pull`. A silent fallback would turn "the
# refresh job has been dead for a day" into "the gate is a bit slower today", which is precisely the
# class of failure this lane exists to refuse. The mount's presence is a reliable signal rather than
# a guess: it is a Kubernetes volumeMount in the ARC scale sets' pod template, so it is there on
# every runner pod of both sets and on no GitHub-hosted runner (`/opt` is EMPTY in
# ghcr.io/actions/actions-runner:2.337.0 — measured 2026-09-12).
#
# THE LAYOUT it reads (owned by ci-platform-refresh.py; Azure Files forbids `:` in a name, so the
# directory is the digest with the first `:` turned into `-`):
#
#   <root>/current                       the newest set's TESTER digest, a FILE, read ONCE
#   <root>/sha256-<hex>/app/             the tester image's /app        (89 files' worth of CLI)
#   <root>/sha256-<hex>/platform-refs/   the PORTAL image's /app        (the reference surface)
#   <root>/sha256-<hex>/platform.json    set, core sha, both digests, installed-at
#   <root>/sha256-<hex>/.complete        the tester digest — the set is COMPLETE only with it
#   <root>/sha256-<hex>.tmp.<pod>/       an install IN FLIGHT, being extracted by that refresh pod
#
# 🚨 A SET CAN BE ABSENT FOR THREE REASONS, AND THEY HAVE OPPOSITE REMEDIES (#4281). Until
# 2026-09-14 this script named two of them — "older than the three kept, or never sealed" — and
# sent the reader to bump the caller's digest. Two refusals measured that morning were NEITHER:
#   • Education run 34809613064 (05:42Z) wanted sha256:cda259b4… and the volume listing printed
#     INSIDE that very refusal contained `sha256-cda259b4….tmp.ci-platform-refresh-29822740-7x7xb`
#     — the exact digest, being extracted as the shard looked. ARRIVING.
#   • Manufacturing PR#89 (~06:5xZ) wanted sha256:0860c392… while the volume held 4c327117,
#     cda259b4 and ce95ac38 — by then cda259b4 had finished, so the volume had advanced and the
#     wanted set had advanced past it. AHEAD.
# In both the caller's digest was RIGHT and merely early, so the remedy the message gave would have
# pinned a gate to an older platform to work around a wait. The three cases:
#
#   ARRIVING     `<SET_DIR>.tmp.<pod>` exists, or `<SET_DIR>` exists without `.complete`. A refresh
#                pod is extracting exactly this digest right now (~200 s on the share).
#   AHEAD        the caller's set has a HIGHER core-CD run number than the newest set installed.
#                The refresh is a CronJob on a 10-minute schedule while a satellite run is
#                triggered by the SEAL, so a run is ahead of the volume by design, for up to one
#                period plus one install.
#   GONE         the caller's set is older than the oldest kept, or was overtaken (a newer set
#                sealed before the refresh's next tick). Whether it ever comes BACK is not a
#                property of this state: since Systemorph/Memex#329 the refresh installs the newest
#                sealed set AND the set each subscribed repository's main last PASSED, so a GONE set
#                that is its repository's main-passed set is re-installed within a tick, and one
#                that is not never returns. The refusals below decide that per case; do not
#                re-introduce a blanket "will never be installed" here.
#
# The first two are WAITED OUT, on the actual condition — `<SET_DIR>/.complete` appearing — under a
# bounded deadline; the third is RED at once, because waiting for it could never end. The wait
# polls, because the share is SMB (no inotify, and `actimeo=30` caches a negative dentry for up to
# 30 s) — it is a wait on a condition, never a sleep for time to pass. `--set` is what makes AHEAD
# and GONE distinguishable; without it only ARRIVING can be seen, and the other two are RED at once.
#
# WHAT IT DOES NOT DO: it never runs `docker`, never reaches a registry, and never writes to the
# volume. It is pure enough to be self-tested against synthetic directories, which is the only way
# the refusals above are known to be able to fire at all.
set -euo pipefail

REFRESH_JOB="ci-platform-refresh (Systemorph/Memex deployments/aks/ci-runners/ci-platform.yaml)"

die() { echo "::error::resolve-gate-platform: $1"; exit 1; }

emit() {  # <key> <value>
  printf '%s=%s\n' "$1" "$2"
  [ -n "${GITHUB_OUTPUT:-}" ] && printf '%s=%s\n' "$1" "$2" >> "$GITHUB_OUTPUT"
  return 0
}

# ── self-test ────────────────────────────────────────────────────────────────────────────────
#     .github/scripts/resolve-gate-platform.sh --self-test
# 🚨 THE "COULD THIS ASSERTION FAIL?" PROOF. Every refusal below is exercised against a synthetic
# volume, and each is asserted to (a) exit non-zero, (b) NAME the refresh job, and (c) emit no
# `mode=container` — i.e. never degrade into a pull. Deleting any one guard from the body makes
# this self-test RED; that is the property that makes the guard worth having. Run on every platform
# PR (dotnet-test.yml preflight) beside compose-gate-host.sh's.
if [ "${1:-}" = "--self-test" ]; then
  set +e
  self="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/$(basename "${BASH_SOURCE[0]}")"
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  fail() { echo "SELF-TEST FAILED: $1"; exit 1; }
  D1="sha256:1111111111111111111111111111111111111111111111111111111111111111"
  D2="sha256:2222222222222222222222222222222222222222222222222222222222222222"
  D3="sha256:3333333333333333333333333333333333333333333333333333333333333333"
  P1="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
  P2="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

  make_set() {  # <root> <tester-digest> <portal-digest> [set name]
    local root="$1" d="$2" p="$3" name="${4:-3.0.0-ci.1}" dir
    dir="$root/${2/:/-}"
    mkdir -p "$dir/app" "$dir/platform-refs"
    printf 'CLI'  > "$dir/app/mw-plugin-test.dll"
    printf '{}'   > "$dir/app/mw-plugin-test.runtimeconfig.json"
    printf 'X=1\n' > "$dir/platform-refs/meshweaver-surface.manifest"
    printf '{"tfm":"net10.0"}' > "$dir/platform-refs/Memex.Portal.Distributed.runtimeconfig.json"
    printf '{"set":"%s","sha":"deadbeef","image-digest":"%s","portal-image-digest":"%s"}\n' "$name" "$d" "$p" \
      > "$dir/platform.json"
    printf '%s\n' "$d" > "$dir/.complete"
    printf '%s\n' "$d" > "$root/current"
    printf '%s' "$dir"
  }

  # 1. No mount at all ⇒ container mode, exit 0. (The ubuntu-latest path stays byte-identical.)
  out="$("$self" --volume-root "$tmp/absent" --tester-digest "$D1" --portal-digest "$P1" 2>&1)" \
    || fail "a missing volume root must select container mode and exit 0 (got: $out)"
  grep -q '^mode=container$' <<<"$out" || fail "a missing volume root must emit mode=container (got: $out)"

  # 2. The happy path ⇒ volume mode, with every path resolved.
  good="$tmp/good"; dir="$(make_set "$good" "$D1" "$P1")"
  out="$("$self" --volume-root "$good" --tester-digest "$D1" --portal-digest "$P1" 2>&1)" \
    || fail "a complete matching set must be accepted (got: $out)"
  grep -q '^mode=volume$' <<<"$out"                   || fail "a complete set must emit mode=volume (got: $out)"
  grep -q "^tester_app=$dir/app$" <<<"$out"           || fail "tester_app must be <set>/app (got: $out)"
  grep -q "^portal_app=$dir/platform-refs$" <<<"$out" || fail "portal_app must be <set>/platform-refs (got: $out)"
  grep -q '^set=3.0.0-ci.1$' <<<"$out"                || fail "the set name must be reported (got: $out)"
  grep -qi 'docker' <<<"$out" && fail "volume mode must never mention docker (got: $out)"

  # 3. An empty pin (allow-unpinned) follows `current`, read ONCE.
  out="$("$self" --volume-root "$good" --tester-digest "" --portal-digest "" 2>&1)" \
    || fail "an empty pin must follow <root>/current (got: $out)"
  grep -q "^tester_app=$dir/app$" <<<"$out" || fail "an empty pin must resolve the set that 'current' names (got: $out)"

  # ── the refusals. Each must exit non-zero, name the refresh job, and never say mode=container. ──
  # 🚨 `--wait-seconds 0` on every one of them. The absence cases now WAIT (see the header), so a
  # refusal case that did not disable the wait would sit here for seven minutes and then pass the
  # assertion for the wrong reason — a self-test that measures patience rather than the refusal.
  # The wait itself gets its own cases (5a–5d), where it is the subject rather than an obstacle.
  refuses() {  # <label> <root> <tester-digest> <portal-digest> [extra args…]
    local label="$1" root="$2" d="$3" p="$4" o rc; shift 4
    o="$("$self" --volume-root "$root" --tester-digest "$d" --portal-digest "$p" --wait-seconds 0 "$@" 2>&1)"; rc=$?
    [ "$rc" -ne 0 ] || fail "$label must be refused, got exit 0: $o"
    grep -q 'ci-platform-refresh' <<<"$o" || fail "$label must name the refresh job (got: $o)"
    grep -q '^mode=container$' <<<"$o" && fail "$label must NOT fall back to a registry pull (got: $o)"
    return 0
  }

  # 3a. The pinned set is simply not installed (it is outside the refresh's kept window and
  #     is no subscribed repository's main-passed set — never a number here, see the
  #     refusals below).
  refuses "a set the volume does not carry" "$good" "$D2" "$P1"
  # 3b. A half-install: the directory exists, `.complete` does not.
  half="$tmp/half"; hdir="$(make_set "$half" "$D1" "$P1")"; rm "$hdir/.complete"
  refuses "a set without .complete" "$half" "$D1" "$P1"
  # 3c. `.complete` naming ANOTHER digest — a directory reused, or a truncated write.
  wrong="$tmp/wrongmarker"; wdir="$(make_set "$wrong" "$D1" "$P1")"; printf '%s\n' "$D2" > "$wdir/.complete"
  refuses "a .complete that names another digest" "$wrong" "$D1" "$P1"
  # 3d. No CLI in app/ — the directory is not a tester /app.
  nocli="$tmp/nocli"; ndir="$(make_set "$nocli" "$D1" "$P1")"; rm "$ndir/app/mw-plugin-test.dll"
  refuses "a set whose app/ has no mw-plugin-test.dll" "$nocli" "$D1" "$P1"
  # 3e. No surface manifest in platform-refs/ — a host that resolves the fallback identity.
  noman="$tmp/nomanifest"; mdir="$(make_set "$noman" "$D1" "$P1")"; rm "$mdir/platform-refs/meshweaver-surface.manifest"
  refuses "a set whose platform-refs/ has no meshweaver-surface.manifest" "$noman" "$D1" "$P1"
  # 3f. The set pairs a DIFFERENT portal than the caller pinned — gating against another platform.
  refuses "a set paired with another portal digest" "$good" "$D1" "$P2"
  # 3g. `current` is absent and no pin was given — nothing names a set.
  nocur="$tmp/nocurrent"; make_set "$nocur" "$D1" "$P1" > /dev/null; rm "$nocur/current"
  refuses "an empty pin with no <root>/current" "$nocur" "" ""
  # 3h. platform.json missing — the set cannot be named, so it cannot be reported.
  nojson="$tmp/nojson"; jdir="$(make_set "$nojson" "$D1" "$P1")"; rm "$jdir/platform.json"
  refuses "a set without platform.json" "$nojson" "$D1" "$P1"

  # 4. Usage refusals.
  "$self" > /dev/null 2>&1 && fail "a missing --volume-root must be refused"
  "$self" --volume-root "$good" --tester-digest "not-a-digest" > /dev/null 2>&1 \
    && fail "a malformed digest must be refused"
  "$self" --volume-root "$good" --tester-digest "$D1" --wait-seconds later > /dev/null 2>&1 \
    && fail "a non-numeric --wait-seconds must be refused"

  # ── 5. THE WAIT (#4281). The measured failure was a shard refusing a set that was ARRIVING. ────
  # 🚨 Each of these has a "could it fail?" partner: 5a would go red if the wait were removed, 5b
  # and 5c would go red if the wait were UNBOUNDED or blind, and 5d proves the classification is
  # what decides — not the clock.

  # 5a. ARRIVING: the set's `.tmp.<pod>` is there and `.complete` lands 3 s later. The shard must
  #     WAIT and then succeed. (This is the 2026-09-14 05:41Z case, four satellites at once.)
  arr="$tmp/arriving"; mkdir -p "$arr"
  adir="$(make_set "$arr" "$D1" "$P1" "3.0.0-ci.8547")"
  mv "$adir" "$adir.tmp.ci-platform-refresh-abc12"
  ( sleep 3; mv "$arr/${D1/:/-}.tmp.ci-platform-refresh-abc12" "$arr/${D1/:/-}" ) &
  started=$(date +%s)
  out="$("$self" --volume-root "$arr" --tester-digest "$D1" --portal-digest "$P1" \
         --set 3.0.0-ci.8547 --wait-seconds 60 2>&1)" \
    || fail "a set that is ARRIVING must be waited for, not refused (got: $out)"
  elapsed=$(( $(date +%s) - started ))
  wait
  grep -q '^mode=volume$' <<<"$out" || fail "an arriving set must end in volume mode (got: $out)"
  grep -qi 'INSTALLING right now' <<<"$out" || fail "the wait must say the set is installing (got: $out)"
  [ "$elapsed" -ge 2 ] || fail "5a completed in ${elapsed}s — it cannot have waited for anything, so it proves nothing"
  [ "$elapsed" -le 40 ] || fail "5a took ${elapsed}s — the wait is not polling the condition"

  # 5b. ARRIVING but it never lands ⇒ RED at the deadline, naming the in-flight directory, and
  #     bounded: the assertion is on the CLOCK, so an unbounded wait fails here rather than hanging.
  stuck="$tmp/stuck"; mkdir -p "$stuck"
  sdir="$(make_set "$stuck" "$D1" "$P1" "3.0.0-ci.8547")"; mv "$sdir" "$sdir.tmp.ci-platform-refresh-dead"
  started=$(date +%s)
  o="$("$self" --volume-root "$stuck" --tester-digest "$D1" --portal-digest "$P1" \
       --set 3.0.0-ci.8547 --wait-seconds 12 2>&1)"; rc=$?
  elapsed=$(( $(date +%s) - started ))
  [ "$rc" -ne 0 ] || fail "an install that never completes must be refused at the deadline (got: $o)"
  [ "$elapsed" -le 45 ] || fail "the wait is not bounded — it ran ${elapsed}s against a 12s deadline"
  grep -q 'INSTALLING when this shard arrived' <<<"$o" || fail "the timeout must name the in-flight case (got: $o)"
  grep -q 'Do NOT bump' <<<"$o" || fail "the timeout must not send the reader to bump the digest (got: $o)"
  grep -q '^mode=container$' <<<"$o" && fail "a timed-out wait must NOT fall back to a pull (got: $o)"

  # 5c. AHEAD: the caller's set is newer than everything installed and no `.tmp.` yet — the
  #     2026-09-14 03:43Z / 04:36Z case. Waited, then RED naming the cadence, never the pin.
  ahead="$tmp/ahead"; make_set "$ahead" "$D2" "$P1" "3.0.0-ci.8500" > /dev/null
  refuses "a set AHEAD of the volume" "$ahead" "$D1" "$P1" --set 3.0.0-ci.8547
  o="$("$self" --volume-root "$ahead" --tester-digest "$D1" --portal-digest "$P1" \
       --set 3.0.0-ci.8547 --wait-seconds 0 2>&1)"
  grep -q 'had not reached the platform volume' <<<"$o" || fail "the AHEAD refusal must name the cadence (got: $o)"
  grep -q 'Do NOT bump' <<<"$o" || fail "the AHEAD refusal must not send the reader to bump the digest (got: $o)"

  # 5d. OVERTAKEN inside the kept window: the volume holds #8400 and #8600, the caller wants #8547.
  #     Absent and bracketed ⇒ it was never installed and never will be, so this is RED AT ONCE
  #     even with a generous deadline. The elapsed assertion is what proves the CLASSIFICATION
  #     decided it rather than the clock: remove the early exit and this case waits 600 s.
  past="$tmp/overtaken"; make_set "$past" "$D2" "$P1" "3.0.0-ci.8600" > /dev/null
  make_set "$past" "$D3" "$P1" "3.0.0-ci.8400" > /dev/null
  printf '%s\n' "$D2" > "$past/current"
  started=$(date +%s)
  o="$("$self" --volume-root "$past" --tester-digest "$D1" --portal-digest "$P1" \
       --set 3.0.0-ci.8547 --wait-seconds 600 2>&1)"; rc=$?
  elapsed=$(( $(date +%s) - started ))
  [ "$rc" -ne 0 ] || fail "an overtaken set must be refused (got: $o)"
  [ "$elapsed" -le 20 ] || fail "an overtaken set must be refused AT ONCE, not waited out (${elapsed}s)"
  grep -q 'moved PAST it' <<<"$o" || fail "the overtaken refusal must say the refresh moved past (got: $o)"

  # 5e. BELOW the oldest kept ⇒ RED at once, and the message must name BOTH histories (purged /
  #     overtaken) rather than asserting one, and point at the retention issue.
  gone="$tmp/gone"; make_set "$gone" "$D2" "$P1" "3.0.0-ci.8600" > /dev/null
  o="$("$self" --volume-root "$gone" --tester-digest "$D1" --portal-digest "$P1" \
       --set 3.0.0-ci.8001 --wait-seconds 0 2>&1)"; rc=$?
  [ "$rc" -ne 0 ] || fail "a set older than retention must be refused (got: $o)"
  grep -q 'below the OLDEST set kept' <<<"$o" || fail "the retention refusal must name retention (got: $o)"
  grep -q 'PURGED' <<<"$o" || fail "the retention refusal must offer the purged history (got: $o)"
  grep -q 'OVERTAKEN' <<<"$o" || fail "the retention refusal must offer the overtaken history too (got: $o)"
  grep -q 'Memex#329' <<<"$o" || fail "the retention refusal must name the retention issue (got: $o)"
  # 🚨 THE NEW CONTRACT, pinned — without these the diagnostic could regress to the old, WRONG
  # remedy ("re-run the job so it re-resolves") while every assertion above still passed. That is
  # the defect this whole change is about, so leaving it unguarded would be the same mistake again.
  grep -q 'MAIN_PASSED_REPOS' <<<"$o" \
    || fail "the refusal must name the list that decides whether the set comes back (got: $o)"
  grep -q "cannot re-resolve AT ALL" <<<"$o" \
    || fail "the refusal must say that re-running the FAILED jobs cannot re-resolve (got: $o)"
  grep -q 'while your main is red, is forever' <<<"$o" \
    || fail "the refusal must say a FULL re-run returns the same set while main is red (got: $o)"
  grep -q 'WAIT FOR THE SET, NOT FOR THE TICK' <<<"$o" \
    || fail "the refusal must not send the reader to re-run the moment a tick fires (got: $o)"
  # \U0001f6a8 The SHAPE, not one word order. The first version of this guard rejected only
  # `keeps ... the <n> newest`, so `retains the 3 newest`, `keeps the newest 3` and `keeps 3 sets`
  # would all have walked a numeric retention claim back in while --self-test stayed green — a
  # guard narrower than the contract it advertises is the bug it exists to prevent. Two patterns:
  # a retention VERB reaching a digit, and a digit reaching a retention NOUN.
  grep -qiE '(keep|retain|hold)[a-z]*( only)?( the)?( newest| most recent)? [0-9]+' <<<"$o" \
    && fail "no refusal may assert a retention NUMBER — the window is the cluster's to set and a \
number written here goes stale the first time it moves (got: $o)"
  grep -qiE '[0-9]+ (newest|most recent|sealed set|sets kept)|(newest|most recent) [0-9]+' <<<"$o" \
    && fail "no refusal may count the sets kept — see above (got: $o)"
  grep -q 'only ever installs the NEWEST' <<<"$o" \
    && fail "the refresh also installs a missing main-passed set — Memex#329 (got: $o)"

  # 5g. An EMPTY volume with a pinned caller: the refresh has completed no run, which is the same
  #     "has not caught up" family — waited out, then RED naming the empty volume, never retention.
  empty="$tmp/empty"; mkdir -p "$empty"
  o="$("$self" --volume-root "$empty" --tester-digest "$D1" --portal-digest "$P1" \
       --set 3.0.0-ci.8547 --wait-seconds 0 2>&1)"; rc=$?
  [ "$rc" -ne 0 ] || fail "an empty volume must be refused (got: $o)"
  grep -q 'still core CD #none' <<<"$o" || fail "an empty volume must be named as such (got: $o)"
  grep -q 'OLDER' <<<"$o" && fail "an empty volume is not a retention miss (got: $o)"

  # 5f. Without `--set`, the three absences cannot be told apart — and the message must SAY SO
  #     rather than asserting one of them, which is the defect this change is about.
  o="$("$self" --volume-root "$good" --tester-digest "$D2" --portal-digest "$P1" --wait-seconds 0 2>&1)"
  grep -q 'cannot be told apart' <<<"$o" || fail "an unclassifiable absence must say so (got: $o)"
  grep -q 'either older than those three or has never been sealed' <<<"$o" \
    && fail "the old FALSE DICHOTOMY is back — it excluded the case that actually occurs (got: $o)"

  echo "resolve-gate-platform.sh self-test: OK (1 container case, 2 volume cases, 8 refusals, 3 usage refusals, 7 wait cases)"
  exit 0
fi

VOLUME_ROOT=""
TESTER_DIGEST=""
PORTAL_DIGEST=""
CALLER_SET=""
# The bound on waiting for a set that is ARRIVING or AHEAD, in seconds. It is not a guess and it is
# not a knob to turn up when a run fails: it is the refresh's worst-case latency, and it is
# arithmetic over values that are declared elsewhere —
#   600 s  the CronJob's schedule AS DEPLOYED (Systemorph/Memex ci-platform.yaml, `*/10`). A run
#          that is AHEAD at the instant a tick fires waits one FULL period before the next tick
#          even starts installing; a bound below one period reds the ordinary AHEAD case by
#          arithmetic, whatever the install takes. (Review on #4282 caught a draft of this that
#          budgeted 120 s for a cadence change that had not been applied — the wait ran out at
#          7 min and still reddened a run that would have arrived on the next tick.)
# + 210 s  one install, measured in-cluster at 197 s with the browser on the share
# +  90 s  the runner mount's `actimeo=30` attribute cache, twice over, plus slack
# = 900 s. That is 15 min inside this lane's 30-min job cap — the wait can run out and still leave
# the job time to say why. A shard that waits longer than that is not waiting for the refresh; it
# is waiting for a refresh that is not coming, which is what the refusals below say. Raising it
# would only move a red later into the job; if the CronJob cadence is ever shortened, LOWER this.
WAIT_SECONDS=900
while [ $# -gt 0 ]; do
  case "$1" in
    --volume-root)   VOLUME_ROOT="${2:-}"; shift 2 ;;
    --tester-digest) TESTER_DIGEST="${2:-}"; shift 2 ;;
    --portal-digest) PORTAL_DIGEST="${2:-}"; shift 2 ;;
    --set)           CALLER_SET="${2:-}"; shift 2 ;;
    --wait-seconds)  WAIT_SECONDS="${2:-}"; shift 2 ;;
    *) die "unknown argument '$1' (usage: --volume-root <dir> [--tester-digest <sha256:…>] [--portal-digest <sha256:…>] [--set <3.0.0-ci.N>] [--wait-seconds <N>])" ;;
  esac
done
case "$WAIT_SECONDS" in
  ''|*[!0-9]*) die "--wait-seconds '$WAIT_SECONDS' is not a whole number of seconds" ;;
esac
[ -n "$VOLUME_ROOT" ] || die "--volume-root is required — it is the path the runner pod mounts the CI platform share at (/opt/platform)"
for d in "$TESTER_DIGEST" "$PORTAL_DIGEST"; do
  case "$d" in
    "" | sha256:[0-9a-f][0-9a-f]* ) : ;;
    *) die "'$d' is not a digest — pass 'sha256:<hex>' or nothing (allow-unpinned)" ;;
  esac
done

# ── the mode ────────────────────────────────────────────────────────────────────────────────
if [ ! -d "$VOLUME_ROOT" ]; then
  emit mode container
  echo "no platform volume at '$VOLUME_ROOT' on runner '${RUNNER_NAME:-?}' — this shard pulls both images and runs the tester in a container (the pre-#4113 path, which is what a GitHub-hosted ubuntu-latest runner takes). The self-hosted ARC scale sets mount the share at /opt/platform; a runner that should have it and does not is a pod started before the mount was added."
  exit 0
fi

# From here on the mount IS there, so every failure is RED and names the refresh job. Nothing below
# may end in `mode=container`.
resolved="$TESTER_DIGEST"
if [ -z "$resolved" ]; then
  # allow-unpinned: the lane follows the tag, so the volume follows `current` — read ONCE, because
  # the share is one live SMB view and a refresh may land mid-job.
  [ -f "$VOLUME_ROOT/current" ] \
    || die "the platform volume at '$VOLUME_ROOT' has no 'current' file, and this caller pinned no image-digest (allow-unpinned), so nothing names a set to gate with. That file is written by $REFRESH_JOB after it installs a set; an empty volume means the refresh has never completed a run. This gate does NOT fall back to a registry pull."
  resolved="$(tr -d '[:space:]' < "$VOLUME_ROOT/current")"
  [ -n "$resolved" ] \
    || die "the platform volume's '$VOLUME_ROOT/current' is empty — $REFRESH_JOB writes the installed set's tester digest there LAST, so an empty file is an interrupted refresh, not an empty registry."
  echo "allow-unpinned: following '$VOLUME_ROOT/current' → $resolved (read once)"
fi

SET_DIR="$VOLUME_ROOT/${resolved/:/-}"

# ── the volume as it is RIGHT NOW ────────────────────────────────────────────────────────────
# Every one of these re-reads the share on each call: the whole point is that the picture moves
# while this shard looks at it.

listing() {  # every set directory, in-flight ones NAMED as such rather than listed as sets
  local out="" candidate
  for candidate in "$VOLUME_ROOT"/sha256-*; do
    [ -d "$candidate" ] || continue
    case "$candidate" in
      *.tmp.*) out="$out $(basename "$candidate") (INSTALLING now)" ;;
      *)       out="$out $(basename "$candidate")" ;;
    esac
  done
  printf '%s' "${out:- none}"
}

run_number_of() {  # `3.0.0-ci.8547` → `8547`; empty when the name does not carry one
  printf '%s' "${1:-}" | sed -n 's/.*[.-][cC][iI]\.\([0-9][0-9]*\).*/\1/p'
}

installed_run_numbers() {  # one core-CD run number per COMPLETE set on the volume
  local candidate number
  for candidate in "$VOLUME_ROOT"/sha256-*; do
    case "$candidate" in *.tmp.*) continue ;; esac
    [ -d "$candidate" ] || continue
    [ -f "$candidate/.complete" ] || continue
    number="$(run_number_of "$(sed -n 's/.*"set"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' \
      "$candidate/platform.json" 2>/dev/null | sed -n 1p)")"
    [ -n "$number" ] && printf '%s\n' "$number"
  done
  return 0
}

newest_installed() { installed_run_numbers | sort -rn | sed -n 1p; }
oldest_installed() { installed_run_numbers | sort -n  | sed -n 1p; }

# The refresh pod's scratch directory for THIS digest, or empty. Deliberately a glob loop and not
# `ls … | head`: under `set -o pipefail` a non-matching `ls` makes the whole command substitution
# non-zero, and `x="$(…)"` then EXITS the script under `set -e` — a "no install in flight" reading
# would have killed the shard before it could say anything at all. (Measured while writing this:
# the refusal printed nothing and exited 1.)
in_flight() {
  local candidate
  for candidate in "$SET_DIR".tmp.*; do
    if [ -d "$candidate" ]; then basename "$candidate"; return 0; fi
  done
  return 0
}

# 🚨 THE ONE CONDITION. Every wait below is on exactly this and nothing else — not on a duration,
# not on a retry count. `.complete` is the refresh's publication marker; a directory without it is
# a half-install whose assemblies may be a mix of two sets.
set_is_complete() {
  [ -d "$SET_DIR" ] && [ -f "$SET_DIR/.complete" ] \
    && [ "$(tr -d '[:space:]' < "$SET_DIR/.complete")" = "$resolved" ]
}

# A `.complete` that EXISTS and names another digest is corruption, never an arrival — waiting for
# it could never end, so it is RED the moment it is seen, inside the wait as well as before it.
refuse_if_marker_disagrees() {
  local marker
  [ -f "$SET_DIR/.complete" ] || return 0
  marker="$(tr -d '[:space:]' < "$SET_DIR/.complete")"
  [ "$marker" = "$resolved" ] && return 0
  die "'$SET_DIR/.complete' names '$marker' but this shard asked for '$resolved' — the directory and its marker disagree, which $REFRESH_JOB's write order (extract into <digest>.tmp.<pod>, then rename into place) makes impossible for a completed run. Treat the set as corrupt: delete its .complete so the next refresh rebuilds it in place."
}

CALLER_RUN="$(run_number_of "$CALLER_SET")"
SET_LABEL="${CALLER_SET:+set $CALLER_SET (core CD #$CALLER_RUN), }"
NO_PULL="🚨 This shard does NOT fall back to 'docker pull': a gate that quietly reached the registry would hide a dead refresh job for as long as the registry answers."
NOT_THE_DIGEST="Do NOT bump the caller's image-digest/platform-image-digest: this run resolved a set that exists and is sealed, and the pin is right."

# 🚨 THE THREE CLAUSES EVERY RETENTION REFUSAL SHARES, hoisted so they cannot drift apart. Four
# refusals used to each carry their own wording, and that is exactly how three of them ended up
# still asserting "the refresh only ever installs the NEWEST sealed set" after Systemorph/Memex#329
# stopped being true.
KEPT="$REFRESH_JOB keeps three things and purges the rest: 'current' (the newest sealed set), a bounded window of the newest sets by install time, and THE SET EACH SUBSCRIBED REPOSITORY'S main LAST PASSED — the one a pull request in that repository resolves. Deliberately no number here: the window's size is the cluster's to set (Systemorph/Memex deployments/aks/ci-runners/ci-platform.yaml), and a number written into this message goes stale the first time it moves, which is exactly how this message came to tell three sessions that the volume kept three sets when it was keeping sixteen."
REMEDY="WHAT ACTUALLY FIXES IT turns on one question — is this set the one your repository's main last passed? If YES and your repository is subscribed, the refresh INSTALLS it back (Systemorph/Memex#329 — rule 2 installs a missing main-passed set, it does not merely decline to purge it). 🚨 WAIT FOR THE SET, NOT FOR THE TICK: installing one takes roughly 200s of extraction after the tick that starts it, the share is SMB and caches a negative lookup for up to 30s, and a rule-2 install runs AFTER the newest set's, so re-running the moment a tick fires can still land before '.complete' is published. Re-run once the volume actually lists this digest. If your repository is NOT in the refresh's MAIN_PASSED_REPOS, no refresh will ever install this set — add it there; that, and not a re-run, is the fix."
RERUN="🚨 A RE-RUN IS NOT ITSELF A REMEDY, and believing it is costs hours. 'Re-run failed jobs' cannot re-resolve AT ALL — the job that resolved this set SUCCEEDED, so it is not re-run and its output is replayed verbatim. A FULL re-run does re-resolve, and returns the SAME set for as long as your main has not PASSED on a newer one — which, while your main is red, is forever. Measured 2026-09-17 on MeshWeaver.Plugins#2040: three attempts, one resolution (3.0.0-ci.8820) each time. A re-run helps only AFTER the volume carries the set again."

waited=0
if ! set_is_complete; then
  refuse_if_marker_disagrees
  newest="$(newest_installed)"
  flight="$(in_flight)"
  # ARRIVING is an OBSERVATION, not an assumption: either a refresh pod's scratch directory for
  # exactly this digest, or the set directory itself already renamed into place but not yet marked.
  if [ -n "$flight" ] || [ -d "$SET_DIR" ]; then
    reason=arriving
  elif [ -n "$CALLER_RUN" ] && { [ -z "$newest" ] || [ "$CALLER_RUN" -gt "$newest" ]; }; then
    # AHEAD also covers an EMPTY volume: nothing complete on it means the refresh has not finished
    # a run yet, which is the same "the volume has not caught up" family and is waited out the same
    # way. The timeout message below names it (`#none`) rather than claiming a retention miss.
    reason=ahead
  else
    reason=gone
  fi

  if [ "$reason" = gone ]; then
    oldest="$(oldest_installed)"
    # 🚨 BELOW THE OLDEST KEPT IS GENUINELY AMBIGUOUS and the message says so rather than picking
    # one. The volume cannot tell "installed once, then purged" from "never installed, because a
    # newer set sealed first" — both leave exactly no trace. Naming one of them would be the same
    # mistake this whole change is about; the remedy happens to be the same for both.
    if [ -n "$CALLER_RUN" ] && [ -n "$oldest" ] && [ "$CALLER_RUN" -lt "$oldest" ]; then
      die "the platform volume at '$VOLUME_ROOT' does not carry ${SET_LABEL}tester digest ${resolved}: core CD #$CALLER_RUN is below the OLDEST set kept (#$oldest). Two histories end here and the volume cannot tell them apart: the set was installed and has since been PURGED, or it was OVERTAKEN before its turn came and was never installed at all. $KEPT $REMEDY $RERUN $NOT_THE_DIGEST The volume holds:$(listing). $NO_PULL"
    fi
    if [ -n "$CALLER_RUN" ] && [ -n "$newest" ]; then
      die "the platform volume at '$VOLUME_ROOT' will never carry ${SET_LABEL}tester digest ${resolved}: the refresh has moved PAST it — the volume's newest set is core CD #$newest and its oldest is #${oldest:-?}, so #$CALLER_RUN is INSIDE the kept window and absent, which means it was overtaken (a newer set sealed before the refresh's next tick) and never installed. $KEPT $REMEDY $RERUN $NOT_THE_DIGEST The volume holds:$(listing). $NO_PULL"
    fi
    die "the platform volume at '$VOLUME_ROOT' carries no set for tester digest ${resolved}, and this caller passed no --set (or one carrying no core-CD run number — it got '${CALLER_SET:-}'), so which of the three absences this is cannot be told apart here. $REFRESH_JOB runs every 10 minutes. $KEPT So a set is absent because it is (1) PURGED — outside the window and not any subscribed repository's main-passed set; (2) OVERTAKEN — a newer set sealed before the refresh's next tick, so it was never installed; or (3) ARRIVING — which this shard waits out on its own and would have said so. For (1) and (2) alike, see Systemorph/Memex#329: the remedy is the main-passed rule carrying the set, never a re-run. Pass --set <3.0.0-ci.N> (the resolver's \`set\` output) to have the three named apart. The volume holds:$(listing). $NO_PULL"
  fi

  # ── the bounded wait, on the condition itself ───────────────────────────────────────────────
  if [ "$reason" = arriving ]; then
    echo "::notice::the set for ${SET_LABEL}tester ${resolved} is INSTALLING right now (${flight:-$SET_DIR}) — waiting up to ${WAIT_SECONDS}s for its .complete. An install takes ~200 s on the share."
  else
    echo "::notice::this run resolved ${SET_LABEL}which is NEWER than the volume's newest installed set (core CD #${newest:-unknown}) — the refresh is a 10-minute CronJob and this run was triggered by the seal, so it is ahead of the volume. Waiting up to ${WAIT_SECONDS}s for it to arrive."
  fi
  started_waiting="$(date +%s)"
  deadline=$(( started_waiting + WAIT_SECONDS ))
  while ! set_is_complete; do
    refuse_if_marker_disagrees
    waited=$(( $(date +%s) - started_waiting ))
    newest="$(newest_installed)"
    flight="$(in_flight)"
    # The refresh overtook us WHILE we waited: nothing will ever install this set now, so the wait
    # ends here rather than at the deadline — the remedy is a re-run, not more patience.
    if [ -z "$flight" ] && [ ! -d "$SET_DIR" ] && [ -n "$CALLER_RUN" ] && [ -n "$newest" ] \
       && [ "$newest" -gt "$CALLER_RUN" ]; then
      die "while this shard waited ${waited}s for ${SET_LABEL}tester ${resolved}, $REFRESH_JOB installed core CD #$newest instead — #$CALLER_RUN was overtaken. $KEPT $REMEDY $RERUN $NOT_THE_DIGEST The volume holds:$(listing). $NO_PULL"
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      if [ "$reason" = arriving ]; then
        die "the set for ${SET_LABEL}tester digest ${resolved} was INSTALLING when this shard arrived (${flight:-$SET_DIR}) and its .complete did not land within ${waited}s. The digest is right and was merely early, so $NOT_THE_DIGEST An install takes ~200 s on the share, so a wait that runs out means the refresh pod died mid-extract or the share stalled — read that pod's log. $REFRESH_JOB. The volume holds:$(listing). $NO_PULL"
      fi
      die "${SET_LABEL}tester digest ${resolved} had not reached the platform volume at '$VOLUME_ROOT' after ${waited}s — the volume's newest installed set is still core CD #${newest:-none}. $REFRESH_JOB is a 10-minute CronJob that takes ~200 s to install, so a run triggered by the seal is ahead of it by design and this shard waits; a wait that runs out means the refresh is not running, is failing, or is further behind than one period. Read that CronJob's last runs. $NOT_THE_DIGEST The volume holds:$(listing). $NO_PULL"
    fi
    # SMB has no inotify and the runner's mount caches attributes for 30 s (`actimeo=30`), so the
    # condition is re-READ on an interval. This is a poll of the condition, not a sleep for time.
    sleep 10
  done
  waited=$(( $(date +%s) - started_waiting ))
  echo "::notice::${SET_LABEL}tester ${resolved} became complete on the volume after ${waited}s — the shard waited for the refresh instead of failing on a digest that was merely early."
fi
[ -f "$SET_DIR/app/mw-plugin-test.dll" ] \
  || die "'$SET_DIR/app' has no mw-plugin-test.dll — that directory is meant to be the TESTER image's /app, and $REFRESH_JOB asserts the same file before it writes .complete. A set that passed that assertion and lacks it now has been damaged on the share."
[ -f "$SET_DIR/app/mw-plugin-test.runtimeconfig.json" ] \
  || die "'$SET_DIR/app' has no mw-plugin-test.runtimeconfig.json — the CLI cannot be started without it, and compose-gate-host.sh refuses a tester directory without it. See $REFRESH_JOB."
[ -s "$SET_DIR/platform-refs/meshweaver-surface.manifest" ] \
  || die "'$SET_DIR/platform-refs' has no meshweaver-surface.manifest — that directory is meant to be the PORTAL image's /app, and a host without a manifest resolves the fallback identity no bake may be keyed to. See $REFRESH_JOB."
[ -f "$SET_DIR/platform.json" ] \
  || die "'$SET_DIR' has no platform.json — $REFRESH_JOB writes it with the set name, the core sha and both image digests, and without it this shard cannot say WHICH platform it gated against. A run whose platform cannot be named is not a reproducible gate."

set_name="$(sed -n 's/.*"set"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SET_DIR/platform.json" | head -1)"
core_sha="$(sed -n 's/.*"sha"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SET_DIR/platform.json" | head -1)"
portal_on_volume="$(sed -n 's/.*"portal-image-digest"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$SET_DIR/platform.json" | head -1)"

# 🚨 The set is installed as a PAIR, so pinning the tester digest already picks the portal — but a
# caller pins both, and the two pins disagreeing is a caller error worth naming here rather than
# discovering as a framework-identity mismatch two steps later. When the volume's platform.json
# records no portal digest (a set laid down by the manual bootstrap rather than by the refresh),
# say so: the pairing then rests on the lane's own framework-identity assertion, which compares the
# two /app trees' assemblies and is STRICTLY STRONGER than this string compare. It always runs.
if [ -n "$PORTAL_DIGEST" ] && [ -n "$portal_on_volume" ] && [ "$PORTAL_DIGEST" != "$portal_on_volume" ]; then
  die "the set on the volume for tester ${resolved} was installed paired with portal ${portal_on_volume}, but this caller pinned platform-image-digest ${PORTAL_DIGEST}. Gating the caller's portal against another set's tester is the mixed pair this lane refuses by name. Pin BOTH digests from ONE CD wave; $REFRESH_JOB installs them as a pair, so the volume's pairing is the authoritative one."
fi
if [ -z "$portal_on_volume" ]; then
  echo "::notice::'$SET_DIR/platform.json' records no portal-image-digest (a set laid down before $REFRESH_JOB owned the install), so the caller's platform-image-digest could not be cross-checked here. The pairing is still asserted — by the framework-identity step below, which compares the two /app trees themselves."
fi

emit mode volume
emit waited "$waited"
emit dir "$SET_DIR"
emit tester_app "$SET_DIR/app"
emit portal_app "$SET_DIR/platform-refs"
emit digest "$resolved"
emit set "${set_name:-unknown}"
emit core_sha "${core_sha:-unknown}"
echo "platform from the volume: set ${set_name:-unknown} (core ${core_sha:-unknown}) at '$SET_DIR' — app/ $(find "$SET_DIR/app" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ') assemblies, platform-refs/ $(find "$SET_DIR/platform-refs" -maxdepth 1 -name '*.dll' | wc -l | tr -d ' ') assemblies. No registry, no daemon, no pull."
