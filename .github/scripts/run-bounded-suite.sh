#!/usr/bin/env bash
# run-bounded-suite.sh --deadline <seconds> --label <name> --dump-dir <dir> -- <command…>
#
# Runs ONE test suite command and ends it, WITH EVIDENCE, if it has not finished by the deadline.
#
# Why it exists: a module suite that wedged (MeshWeaver.Plugins PR #4155, run 40115238778 —
# MeshWeaver.AI went silent and stayed silent) ran until the JOB's 45-minute cap, and a cancelled
# job runs no further step: no trx, no phase trace, no dump, nothing naming the test. The leg read
# `cancelled`, which looks like infra. Core's own shards have always bounded each host below the
# job cap (`timeout 8m` in dotnet-test.yml) for exactly this reason; this is the module lane's
# counterpart. The deadline is a STUCK detector, not a budget a suite may grow into — the caller
# sets it from what is left of the job, so the evidence steps after it still run.
#
# On the deadline it:
#   1. names every TEST START in /tmp/meshweaver-test-trace.log that has no TEST END (the test,
#      or the class init/teardown, the host is stuck in) and prints the trace's last lines;
#   2. asks every .NET process under the command to dump ITSELF (diag-dump.py — the runtime's
#      diagnostics socket, so no ptrace and nothing to install) into --dump-dir, mini dumps:
#      every managed stack, which is what names a lock-order deadlock's two threads;
#   3. TERMs the process tree, KILLs what is left 30 s later, and exits 124 (timeout(1)'s code).
# Otherwise it exits with the command's own code. A dump that cannot be written is SAID and
# never changes the verdict: the suite did not finish, whatever the dump did.
#
# --self-test runs the wrapper against shell children (no .NET needed): a fast exit keeps its
# code, a hung child is ended at the deadline with 124 and leaves no survivor, and the trace
# reader names the open test.
set -uo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

descendants() {
  local child
  for child in $(pgrep -P "$1" 2>/dev/null); do
    echo "$child"
    descendants "$child"
  done
}

open_tests() {
  # The phase trace (test/MeshWeaver.Fixture TestTraceLog) is one line per phase:
  #   HH:MM:SS.fff pid=<n> [<Class>] <PHASE> [elapsed=…] [<method>] …
  # A phase that opened and never closed is where the host is stuck:
  #   TEST_START <m>     … TEST_END elapsed=… <m>
  #   INIT_START         … INIT_DONE
  #   DISPOSE_START      … DISPOSE_DONE | DISPOSE_SHARED_SKIP
  [ -f "$1" ] || { echo "  (no phase trace at $1)"; return; }
  awk '
    $2 ~ /^pid=/ && $3 ~ /^\[.*\]$/ {
      key = $2 " " substr($3, 2, length($3) - 2)
      if ($4 == "TEST_START")         { open[key "." $5] = $1 }
      else if ($4 == "TEST_END")      { m = ($5 ~ /^elapsed=/) ? $6 : $5; delete open[key "." m] }
      else if ($4 == "INIT_START")    { init[key] = $1 }
      else if ($4 == "INIT_DONE")     { delete init[key] }
      else if ($4 == "DISPOSE_START") { disp[key] = $1 }
      else if ($4 == "DISPOSE_DONE" || $4 == "DISPOSE_SHARED_SKIP") { delete disp[key] }
    }
    END {
      for (k in open) { print "  still in test (since " open[k] "): " k; any = 1 }
      for (k in init) { print "  class init not done (since " init[k] "): " k; any = 1 }
      for (k in disp) { print "  class dispose not done (since " disp[k] "): " k; any = 1 }
      if (!any) print "  every phase that opened also closed — the host is stuck outside a test (assembly fixture, run teardown, or the reporter itself)"
    }' "$1"
}

run() {
  # The fixture writes Path.GetTempPath()/meshweaver-test-trace.log — /tmp on the Linux runners.
  local TRACE="${MESHWEAVER_TEST_TRACE:-/tmp/meshweaver-test-trace.log}"
  local deadline="" label="" dump_dir=""
  while [ $# -gt 0 ]; do
    case "$1" in
      --deadline) deadline="$2"; shift 2 ;;
      --label) label="$2"; shift 2 ;;
      --dump-dir) dump_dir="$2"; shift 2 ;;
      --) shift; break ;;
      *) echo "run-bounded-suite: unknown argument '$1'" >&2; return 64 ;;
    esac
  done
  [[ "$deadline" =~ ^[0-9]+$ ]] && [ "$deadline" -gt 0 ] || { echo "run-bounded-suite: --deadline must be a positive number of seconds, got '$deadline'" >&2; return 64; }
  [ -n "$label" ] && [ -n "$dump_dir" ] && [ $# -gt 0 ] || { echo "run-bounded-suite: --label, --dump-dir and a command are required" >&2; return 64; }

  "$@" &
  local pid=$! started=$SECONDS
  while kill -0 "$pid" 2>/dev/null; do
    if [ $(( SECONDS - started )) -ge "$deadline" ]; then
      echo "[CI] $label TIMEOUT — not finished after ${deadline}s; the host is stuck, not slow. Collecting evidence before ending it."
      echo "[CI] $label open phases in $TRACE:"
      open_tests "$TRACE"
      [ -f "$TRACE" ] && { echo "[CI] $label last lines of the phase trace:"; tail -n 25 "$TRACE" | sed 's/^/  /'; }
      mkdir -p "$dump_dir"
      local tree p dumped=0
      tree="$(descendants "$pid")"
      for p in $pid $tree; do
        if python3 -I "$here/diag-dump.py" "$p" "$dump_dir/hang-$p.dmp" --type mini 2>/dev/null; then
          dumped=$((dumped + 1))
        fi
      done
      echo "[CI] $label wrote $dumped dump(s) into $dump_dir (only .NET processes answer; 0 means no dump, not no wedge)"
      # shellcheck disable=SC2086 # word-splitting the pid list is intended
      kill -TERM $tree "$pid" 2>/dev/null
      local waited=0
      while kill -0 "$pid" 2>/dev/null && [ "$waited" -lt 30 ]; do sleep 1; waited=$((waited + 1)); done
      local late
      late="$(descendants "$pid")"
      # shellcheck disable=SC2086
      kill -KILL $late $tree "$pid" 2>/dev/null
      wait "$pid" 2>/dev/null
      return 124
    fi
    sleep 2
  done
  wait "$pid"
}

self_test() {
  local tmp rc out t0 fails=0
  tmp="$(mktemp -d)"
  run --deadline 20 --label fast --dump-dir "$tmp/d" -- bash -c 'exit 3'; rc=$?
  [ "$rc" -eq 3 ] || { echo "✗ a command that exits 3 inside the deadline returned $rc"; fails=1; }

  printf '%s\n' \
    '10:00:00.000 pid=7 [FooTest] INIT_START' \
    '10:00:00.100 pid=7 [FooTest] INIT_DONE elapsed=100ms' \
    '10:00:00.200 pid=7 [FooTest] TEST_START Done' \
    '10:00:00.300 pid=7 [FooTest] TEST_END elapsed=100ms Done outcome=Passed' \
    '10:00:00.400 pid=7 [FooTest] TEST_START Hangs' \
    '10:00:00.500 pid=7 [BarTest] INIT_START' \
    '10:00:00.600 pid=7 [BazTest] DISPOSE_START' \
    '10:00:00.700 pid=7 [BazTest] DISPOSE_SHARED_SKIP' > "$tmp/trace.log"
  t0=$SECONDS
  # The child records its OWN pid: a name match (`pgrep sleep`) would see other sessions' sleeps.
  out="$(MESHWEAVER_TEST_TRACE="$tmp/trace.log" run --deadline 3 --label hung --dump-dir "$tmp/d" -- \
    bash -c 'sleep 300 & echo $! > "$1"; wait' _ "$tmp/child.pid")"; rc=$?
  [ "$rc" -eq 124 ] || { echo "✗ a hung command returned $rc, not 124"; fails=1; }
  [ $(( SECONDS - t0 )) -lt 40 ] || { echo "✗ the hung command was not ended near its deadline ($(( SECONDS - t0 ))s)"; fails=1; }
  grep -q 'still in test (since 10:00:00.400): pid=7 FooTest.Hangs' <<<"$out" || { echo "✗ the open test was not named"; echo "$out"; fails=1; }
  grep -q 'still in test.*FooTest.Done' <<<"$out" && { echo "✗ a finished test was named as open"; fails=1; }
  grep -q 'class init not done (since 10:00:00.500): pid=7 BarTest' <<<"$out" || { echo "✗ the unfinished class init was not named"; fails=1; }
  grep -q 'dispose not done.*BazTest' <<<"$out" && { echo "✗ a dispose closed by DISPOSE_SHARED_SKIP was named as open"; fails=1; }
  grep -q 'init not done.*FooTest' <<<"$out" && { echo "✗ a finished class init was named as open"; fails=1; }
  grep -q 'wrote 0 dump(s)' <<<"$out" || { echo "✗ a tree with no .NET process did not say it wrote no dump"; fails=1; }
  local child
  child="$(cat "$tmp/child.pid" 2>/dev/null)"
  [ -n "$child" ] || { echo "✗ the hung command never started its child"; fails=1; }
  if [ -n "$child" ] && kill -0 "$child" 2>/dev/null; then
    echo "✗ the hung command's child (pid $child) survived"; kill -KILL "$child"; fails=1
  fi

  out="$(MESHWEAVER_TEST_TRACE="$tmp/none.log" open_tests "$tmp/none.log")"
  grep -q 'no phase trace' <<<"$out" || { echo "✗ a missing trace was not said"; fails=1; }
  printf '%s\n' '10:00:00.000 pid=7 [A] TEST_START B' '10:00:00.100 pid=7 [A] TEST_END elapsed=100ms B outcome=Passed' > "$tmp/closed.log"
  grep -q 'stuck outside a test' <<<"$(open_tests "$tmp/closed.log")" || { echo "✗ a fully closed trace did not say the host is outside a test"; fails=1; }

  run --deadline 0 --label bad --dump-dir "$tmp/d" -- true 2>/dev/null; rc=$?
  [ "$rc" -eq 64 ] || { echo "✗ --deadline 0 was accepted (rc $rc)"; fails=1; }
  rm -rf "$tmp"
  [ "$fails" -eq 0 ] || return 1
  echo "✓ run-bounded-suite self-test: a finished command keeps its exit code; a hung one is ended at its deadline with 124, its tree does not survive, the open test and the unfinished class init are named (and nothing that closed), a dump-less tree says so, and a bad deadline is refused"
}

if [ "${1:-}" = "--self-test" ]; then
  self_test
  exit $?
fi
run "$@"
exit $?
