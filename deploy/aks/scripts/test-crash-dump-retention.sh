#!/usr/bin/env bash
#
# Executes deploy/helm/files/crash-dump-retention.sh (the portal's crash-dump-prepare init container
# and postStart hook) against scratch directories and asserts what it deleted, kept, parked, armed
# and held.
#
#   deploy/aks/scripts/test-crash-dump-retention.sh
#   exit 0 = every case held; exit 1 = a case failed, or fewer cases ran than declared
#
# Why it is executed rather than read: the script deletes files on a volume that outlives the pod,
# and it is what stands between a crash loop and that volume filling up
# (Doc/Architecture/DebuggingNativeCrashes → "Production: where a dump lands"). A shell script that
# merely renders proves nothing about what it removes. It runs under `sh`, exactly as the pod runs
# it. Every input is in this repo, so there is no condition under which it may decline to run.
set -uo pipefail

SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SELF_DIR/../../helm/files/crash-dump-retention.sh"
[ -f "$SCRIPT" ] || { echo "::error::$SCRIPT is missing"; exit 1; }

WORK="$(mktemp -d)"
trap 'chmod -R u+w "$WORK" 2>/dev/null; rm -rf "$WORK"' EXIT
fails=0
cases=0
EXPECTED_CASES=11
MiB=1048576

fail() { echo "::error::crash-dump retention: $*"; fails=$((fails + 1)); }

# run <root> <pod> [VAR=value ...] — runs the script; its output in $WORK/out
run() {
  local root="$1" pod="$2"; shift 2
  env -i PATH="$PATH" MEMEX_CRASHDUMP_LOG_TO_STDOUT=1 \
    MEMEX_CRASHDUMP_ROOT="$root" MEMEX_POD_NAME="$pod" \
    MEMEX_CRASHDUMP_KEEP=3 MEMEX_CRASHDUMP_MAX_AGE_DAYS=14 MEMEX_CRASHDUMP_ACTIVE_MINUTES=30 \
    MEMEX_CRASHDUMP_HEADROOM_DUMPS=2 MEMEX_CRASHDUMP_RESERVE_MIB=1 \
    MEMEX_MEMORY_LIMIT_BYTES="$MiB" "$@" \
    sh "$SCRIPT" >"$WORK/out" 2>&1
  local rc=$?
  [ "$rc" -eq 0 ] || fail "the script exited $rc — a failing postStart KILLS the container. Output: $(cat "$WORK/out")"
}

# dump <path> <minutes ago>
stamp() { local t=$(( $(date +%s) - $1 * 60 )); date -r "$t" +%Y%m%d%H%M.%S 2>/dev/null || date -d "@$t" +%Y%m%d%H%M.%S; }
dump() { mkdir -p "$(dirname "$1")"; : >"$1"; touch -t "$(stamp "$2")" "$1"; }
NO_ROOM=$((1024 * 1024 * MiB * 1024))   # a 1 PiB memory limit: no volume has room for two

# ---- 1. keep the newest N across EVERY pod's directory ----------------------
cases=$((cases + 1))
R="$WORK/c1"
dump "$R/pod-a/coredump.1.100" 500
dump "$R/pod-a/coredump.1.200" 400
dump "$R/pod-b/coredump.1.300" 300
dump "$R/pod-b/coredump.1.400" 200
dump "$R/pod-x/coredump.1.500" 100
run "$R" pod-c
for f in pod-a/coredump.1.100 pod-a/coredump.1.200; do
  [ ! -e "$R/$f" ] || fail "case 1: $f is beyond the newest 3 and was NOT deleted"
done
for f in pod-b/coredump.1.300 pod-b/coredump.1.400 pod-x/coredump.1.500; do
  [ -e "$R/$f" ] || fail "case 1: $f is among the newest 3 and was deleted"
done
[ -d "$R/pod-c" ] || fail "case 1: enough headroom, yet this pod's dump directory was not created"
grep -q "armed: dumps go to $R/pod-c" "$WORK/out" || fail "case 1: no 'armed' line. Output: $(cat "$WORK/out")"

# ---- 2. delete by age, whatever the count ------------------------------------
cases=$((cases + 1))
R="$WORK/c2"
dump "$R/pod-a/coredump.1.old" $((20 * 24 * 60))
dump "$R/pod-a/coredump.1.new" 60
run "$R" pod-b MEMEX_CRASHDUMP_KEEP=10
[ ! -e "$R/pod-a/coredump.1.old" ] || fail "case 2: a 20-day-old dump survived maxAgeDays=14"
[ -e "$R/pod-a/coredump.1.new" ] || fail "case 2: a 1-hour-old dump was deleted"
grep -q "older than 14 days" "$WORK/out" || fail "case 2: the age deletion was not logged"

# ---- 3. a dump still being WRITTEN is never deleted, even beyond keep --------
# Two other pods are writing right now (mtimes 1 and 2 minutes ago); keep=1. Without the active
# window the older of the two would be unlinked mid-write: evidence lost, space not freed.
cases=$((cases + 1))
R="$WORK/c3"
dump "$R/pod-a/coredump.1.writing" 2
dump "$R/pod-b/coredump.1.writing" 1
dump "$R/pod-a/coredump.1.finished" 100
run "$R" pod-c MEMEX_CRASHDUMP_KEEP=1
[ -e "$R/pod-a/coredump.1.writing" ] || fail "case 3: a dump modified 2 min ago (possibly mid-write) was deleted"
[ -e "$R/pod-b/coredump.1.writing" ] || fail "case 3: the newest dump was deleted"
[ ! -e "$R/pod-a/coredump.1.finished" ] || fail "case 3: a 100-min-old dump beyond keep=1 was NOT deleted"
grep -q "possibly still being written" "$WORK/out" || fail "case 3: the active-window skip was not logged"

# ---- 4. no headroom: HELD, the directory is ABSENT, nothing written is lost --
cases=$((cases + 1))
R="$WORK/c4"
dump "$R/pod-a/coredump.1.100" 100
run "$R" pod-a MEMEX_MEMORY_LIMIT_BYTES=$NO_ROOM
[ ! -e "$R/pod-a" ] || fail "case 4: no headroom, yet $R/pod-a still exists — a crash would write into a full volume"
parked="$(ls -d "$R"/pod-a.[0-9]* 2>/dev/null | head -1)"
[ -n "$parked" ] && [ -e "$parked/coredump.1.100" ] || fail "case 4: the previous dump was not kept (parked) — $(ls -R "$R")"
grep -q "HELD: dumps are DISABLED" "$WORK/out" || fail "case 4: no HELD line. Output: $(cat "$WORK/out")"
# ...and a parked dump still counts toward retention on a later start
dump "$R/pod-b/coredump.1.200" 90
dump "$R/pod-b/coredump.1.300" 80
dump "$R/pod-b/coredump.1.400" 70
run "$R" pod-b
[ ! -e "$parked/coredump.1.100" ] || fail "case 4: a parked dump escaped the keep-newest-3 rule"
[ ! -d "$parked" ] || fail "case 4: the emptied parked directory was not removed"

# ---- 5. a RESTART parks the previous start's dumps before deciding -----------
cases=$((cases + 1))
R="$WORK/c5"
run "$R" pod-a
dump "$R/pod-a/coredump.7.1" 100
run "$R" pod-a
[ -d "$R/pod-a" ] && [ -z "$(ls -A "$R/pod-a")" ] || fail "case 5: after a restart this pod's armed directory is not fresh and empty"
[ -n "$(ls "$R"/pod-a.[0-9]*/coredump.7.1 2>/dev/null)" ] || fail "case 5: the previous start's dump was not parked"
grep -q "parked the previous start's dumps" "$WORK/out" || fail "case 5: parking was not logged"

# ---- 6. two LIVE pods: neither removes the other's armed directory -----------
cases=$((cases + 1))
R="$WORK/c6"
run "$R" pod-a
run "$R" pod-b
run "$R" pod-a
[ -d "$R/pod-a" ] || fail "case 6: pod-a's armed directory was removed by another pod's start — pod-a's next dump would fail with ENOENT"
[ -d "$R/pod-b" ] || fail "case 6: pod-b's armed directory was removed by another pod's start"

# ---- 7. an unremovable target is reported as STILL ARMED, never as HELD ------
cases=$((cases + 1))
R="$WORK/c7"
if [ "$(id -u)" = "0" ]; then
  fail "case 7 cannot be measured as root (directory permissions do not bind root) — run this as an ordinary user"
else
  mkdir -p "$R/pod-a"
  chmod 0555 "$R"
  run "$R" pod-a MEMEX_MEMORY_LIMIT_BYTES=$NO_ROOM
  chmod 0755 "$R"
  [ -d "$R/pod-a" ] || fail "case 7: control failed — the directory was removed although its parent is read-only"
  grep -q "REMAIN ARMED" "$WORK/out" || fail "case 7: the target could not be removed, but the script did not say dumps REMAIN ARMED. Output: $(cat "$WORK/out")"
  ! grep -q "HELD:" "$WORK/out" || fail "case 7: the script claimed HELD while the target still exists"
fi

# ---- 8. a root that is not a normalised path is refused, nothing touched -----
cases=$((cases + 1))
R="$WORK/c8"
mkdir -p "$R/keep-me"
dump "$R/keep-me/coredump.1.1" $((20 * 24 * 60))
run "$R/keep-me/.." pod-a
grep -q "not a normalised path" "$WORK/out" || fail "case 8: a root with '..' was not refused. Output: $(cat "$WORK/out")"
[ -e "$R/keep-me/coredump.1.1" ] || fail "case 8: a refused root still had files deleted under it"

# ---- 9. a missing pod name: no directory, exit 0 -----------------------------
cases=$((cases + 1))
R="$WORK/c9"
run "$R" ""
grep -q "ERROR: .*MEMEX_POD_NAME=''" "$WORK/out" || fail "case 9: an empty pod name was not reported"
[ ! -d "$R" ] || [ -z "$(ls -A "$R")" ] || fail "case 9: a directory was armed without a pod name"

# ---- 10. a non-numeric setting: disarmed, exit 0 -----------------------------
cases=$((cases + 1))
R="$WORK/c10"
mkdir -p "$R/pod-a"
run "$R" pod-a MEMEX_MEMORY_LIMIT_BYTES=16Gi
grep -q "ERROR: a numeric setting is not a whole number" "$WORK/out" || fail "case 10: a non-numeric limit was not reported"
[ ! -e "$R/pod-a" ] || fail "case 10: dumps stayed armed on an unparsable limit"

# ---- 11. NEGATIVE CONTROL: case 1's layout with keep=10 deletes nothing -------
# If case 1's deletions came from something other than the count rule (or the script never ran),
# this would not distinguish keep=3 from keep=10.
cases=$((cases + 1))
R="$WORK/c11"
dump "$R/pod-a/coredump.1.100" 500
dump "$R/pod-a/coredump.1.200" 400
dump "$R/pod-b/coredump.1.300" 300
dump "$R/pod-b/coredump.1.400" 200
run "$R" pod-c MEMEX_CRASHDUMP_KEEP=10
n="$(ls -1 "$R"/*/coredump.* 2>/dev/null | wc -l | tr -d ' ')"
[ "$n" = "4" ] || fail "case 11: keep=10 over 4 dumps left $n — the count rule deletes what it must keep"

if [ "$cases" -ne "$EXPECTED_CASES" ]; then
  echo "::error::crash-dump retention: ran $cases cases, declared $EXPECTED_CASES"
  exit 1
fi
if [ "$fails" -gt 0 ]; then
  echo "crash-dump retention: $fails failure(s) over $cases cases"
  exit 1
fi
echo "crash-dump retention: all $cases cases hold (keep-newest, max-age, active-writer protection, headroom HOLD, restart parking, two live pods, unremovable target reported, path refusal, two refusal paths, negative control)"
