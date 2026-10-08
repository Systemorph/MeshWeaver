#!/usr/bin/env bash
#
# Executes deploy/helm/files/crash-dump-retention.sh (the portal's postStart hook) against scratch
# directories and asserts what it deleted, kept, armed and held.
#
#   deploy/aks/scripts/test-crash-dump-retention.sh
#   exit 0 = every case held; exit 1 = a case failed, or fewer cases ran than declared
#
# Why it is executed rather than read: the hook is the only thing standing between a crash loop
# and a full /data volume (Doc/Architecture/DebuggingNativeCrashes → "Where a dump lands"), and
# a shell script that merely renders proves nothing about what it removes. The script runs under
# `sh`, exactly as the hook runs it. No input outside this repo, so there is no condition under
# which it may decline to run.
set -uo pipefail

SELF_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$SELF_DIR/../../helm/files/crash-dump-retention.sh"
[ -f "$SCRIPT" ] || { echo "::error::$SCRIPT is missing"; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
fails=0
cases=0
EXPECTED_CASES=7
MiB=1048576

fail() { echo "::error::crash-dump retention: $*"; fails=$((fails + 1)); }

# run <root> <pod> [VAR=value ...] — runs the hook; stdout in $WORK/out, exit code in $rc
run() {
  local root="$1" pod="$2"; shift 2
  env -i PATH="$PATH" MEMEX_CRASHDUMP_LOG_TO_STDOUT=1 \
    MEMEX_CRASHDUMP_ROOT="$root" MEMEX_POD_NAME="$pod" \
    MEMEX_CRASHDUMP_KEEP=3 MEMEX_CRASHDUMP_MAX_AGE_DAYS=14 \
    MEMEX_CRASHDUMP_HEADROOM_DUMPS=2 MEMEX_CRASHDUMP_RESERVE_MIB=1 \
    MEMEX_MEMORY_LIMIT_BYTES="$MiB" "$@" \
    sh "$SCRIPT" >"$WORK/out" 2>&1
  rc=$?
  [ "$rc" -eq 0 ] || fail "the hook exited $rc — a failing postStart KILLS the container. Output: $(cat "$WORK/out")"
}

# dump <path> <minutes ago>
dump() { mkdir -p "$(dirname "$1")"; : >"$1"; touch -t "$(date -r $(( $(date +%s) - $2 * 60 )) +%Y%m%d%H%M.%S 2>/dev/null || date -d "@$(( $(date +%s) - $2 * 60 ))" +%Y%m%d%H%M.%S)" "$1"; }

# ---- 1. keep the newest N across EVERY pod's directory ----------------------
cases=$((cases + 1))
R="$WORK/c1"
dump "$R/pod-a/coredump.1.100" 50
dump "$R/pod-a/coredump.1.200" 40
dump "$R/pod-b/coredump.1.300" 30
dump "$R/pod-b/coredump.1.400" 20
dump "$R/pod-a/coredump.1.500" 10
run "$R" pod-c
for f in pod-a/coredump.1.100 pod-a/coredump.1.200; do
  [ ! -e "$R/$f" ] || fail "case 1: $f is beyond the newest 3 and was NOT deleted"
done
for f in pod-b/coredump.1.300 pod-b/coredump.1.400 pod-a/coredump.1.500; do
  [ -e "$R/$f" ] || fail "case 1: $f is among the newest 3 and was deleted"
done
[ -d "$R/pod-c" ] || fail "case 1: enough headroom, yet this pod's dump directory was not created"
grep -q "armed: dumps go to $R/pod-c" "$WORK/out" || fail "case 1: no 'armed' line. Output: $(cat "$WORK/out")"

# ---- 2. delete by age, whatever the count ------------------------------------
cases=$((cases + 1))
R="$WORK/c2"
dump "$R/pod-a/coredump.1.old" $((20 * 24 * 60))
dump "$R/pod-a/coredump.1.new" 60
run "$R" pod-a MEMEX_CRASHDUMP_KEEP=10
[ ! -e "$R/pod-a/coredump.1.old" ] || fail "case 2: a 20-day-old dump survived maxAgeDays=14"
[ -e "$R/pod-a/coredump.1.new" ] || fail "case 2: a 1-hour-old dump was deleted"
grep -q "older than 14 days" "$WORK/out" || fail "case 2: the age deletion was not logged"

# ---- 3. no headroom: dumps are HELD, nothing already written is lost ---------
cases=$((cases + 1))
R="$WORK/c3"
dump "$R/pod-a/coredump.1.100" 10
run "$R" pod-a MEMEX_MEMORY_LIMIT_BYTES=$((1024 * 1024 * MiB * 1024))   # 1 PiB: no volume fits two
[ ! -d "$R/pod-a" ] || fail "case 3: no headroom, yet the dump directory still exists — a crash would write into a full volume"
held="$(ls -d "$R"/pod-a.held-* 2>/dev/null | head -1)"
[ -n "$held" ] && [ -e "$held/coredump.1.100" ] || fail "case 3: the existing dump was not kept in a .held directory"
grep -q "HELD: dumps are DISABLED" "$WORK/out" || fail "case 3: no HELD line. Output: $(cat "$WORK/out")"
# ...and a held dump still counts toward retention on the next start
dump "$R/pod-b/coredump.1.200" 5
dump "$R/pod-b/coredump.1.300" 4
dump "$R/pod-b/coredump.1.400" 3
run "$R" pod-b
[ ! -e "$held/coredump.1.100" ] || fail "case 3: a dump in a .held directory escaped the keep-newest-3 rule"

# ---- 4. empty directories of OTHER pods go; this pod's stays ----------------
cases=$((cases + 1))
R="$WORK/c4"
mkdir -p "$R/gone-pod" "$R/pod-a"
run "$R" pod-a
[ ! -d "$R/gone-pod" ] || fail "case 4: an empty directory of a departed pod was not removed"
[ -d "$R/pod-a" ] || fail "case 4: this pod's own directory was removed"

# ---- 5. a missing pod name disables dumps and still exits 0 ------------------
cases=$((cases + 1))
R="$WORK/c5"
run "$R" ""
grep -q "ERROR: .*MEMEX_POD_NAME=''" "$WORK/out" || fail "case 5: an empty pod name was not reported"
[ ! -d "$R" ] || [ -z "$(ls -A "$R")" ] || fail "case 5: a directory was armed without a pod name"

# ---- 6. a non-numeric setting disables dumps and still exits 0 ---------------
cases=$((cases + 1))
R="$WORK/c6"
run "$R" pod-a MEMEX_MEMORY_LIMIT_BYTES=16Gi
grep -q "ERROR: a numeric setting is not a whole number" "$WORK/out" || fail "case 6: a non-numeric limit was not reported"
[ ! -d "$R/pod-a" ] || fail "case 6: dumps were armed on an unparsable limit"

# ---- 7. NEGATIVE CONTROL: the assertions above can fail ----------------------
# Run case 1's layout with keep=10: nothing may be deleted. If the deletion assertions of case 1
# were vacuous (e.g. the hook never ran), this case would not distinguish keep=3 from keep=10.
cases=$((cases + 1))
R="$WORK/c7"
dump "$R/pod-a/coredump.1.100" 50
dump "$R/pod-a/coredump.1.200" 40
dump "$R/pod-b/coredump.1.300" 30
dump "$R/pod-b/coredump.1.400" 20
run "$R" pod-c MEMEX_CRASHDUMP_KEEP=10
n="$(ls -1 "$R"/*/coredump.* 2>/dev/null | wc -l | tr -d ' ')"
[ "$n" = "4" ] || fail "case 7: keep=10 over 4 dumps left $n — the count rule deletes what it must keep"

if [ "$cases" -ne "$EXPECTED_CASES" ]; then
  echo "::error::crash-dump retention: ran $cases cases, declared $EXPECTED_CASES"
  exit 1
fi
if [ "$fails" -gt 0 ]; then
  echo "crash-dump retention: $fails failure(s) over $cases cases"
  exit 1
fi
echo "crash-dump retention: all $cases cases hold (keep-newest, max-age, headroom HOLD, held dumps counted, empty-dir cleanup, two refusal paths, negative control)"
