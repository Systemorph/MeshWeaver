#!/bin/sh
# Crash-dump retention and headroom gate for the portal container. Runs as the container's
# postStart hook, so it runs on EVERY container start: the first start of a pod, and every
# restart in place after a crash (the CrashLoopBackOff case). That makes it the step after each
# dump is written. See Doc/Architecture/DebuggingNativeCrashes → "Where a dump lands, and how long
# it stays".
#
# Layout: dumps live on the /data volume (the shared /data claim on AKS) under
#   $MEMEX_CRASHDUMP_ROOT/<pod name>/coredump.<pid>.<time>
# so a dump outlives the pod that wrote it. DOTNET_DbgMiniDumpName names
# <root>/<pod name>/…, and createdump does NOT create directories.
#
# What it does, in order:
#   1. delete every dump older than MEMEX_CRASHDUMP_MAX_AGE_DAYS;
#   2. keep only the newest MEMEX_CRASHDUMP_KEEP dumps across EVERY pod's directory;
#   3. remove the empty directories of other pods;
#   4. HEADROOM GATE: create this pod's dump directory only if the volume has room for
#      MEMEX_CRASHDUMP_HEADROOM_DUMPS dumps of MEMEX_MEMORY_LIMIT_BYTES each (a type-2 heap dump
#      is at most the process's memory, so at most the container's memory limit) plus
#      MEMEX_CRASHDUMP_RESERVE_MIB. Without that room the directory is moved aside instead
#      (`<pod>.held-<epoch>`, its dumps kept and still counted by step 2). createdump then cannot
#      open its output file and the crash writes nothing, so a dump can never fill the volume that
#      also holds the DataProtection keys and the assembly cache.
#
# It always exits 0. A postStart hook that fails KILLS the container, and losing dump retention
# must never take the portal down with it. Every outcome, including every error, is logged to the
# container's own stdout (PID 1's fd 1), so it reaches Loki under the pod's labels, prefixed
# "[crash-dumps]".
#
# Executed by deploy/aks/scripts/test-crash-dump-retention.sh (chart-gate.yml).

out=/dev/stdout
if [ -w /proc/1/fd/1 ] && [ "${MEMEX_CRASHDUMP_LOG_TO_STDOUT:-}" != "1" ]; then out=/proc/1/fd/1; fi
log() { printf '[crash-dumps] %s\n' "$*" >>"$out" 2>/dev/null || :; }

root="${MEMEX_CRASHDUMP_ROOT:-}"
pod="${MEMEX_POD_NAME:-}"
keep="${MEMEX_CRASHDUMP_KEEP:-3}"
age="${MEMEX_CRASHDUMP_MAX_AGE_DAYS:-14}"
factor="${MEMEX_CRASHDUMP_HEADROOM_DUMPS:-2}"
reserve_mib="${MEMEX_CRASHDUMP_RESERVE_MIB:-2048}"
limit="${MEMEX_MEMORY_LIMIT_BYTES:-}"

if [ -z "$root" ] || [ -z "$pod" ]; then
  log "ERROR: MEMEX_CRASHDUMP_ROOT='$root' or MEMEX_POD_NAME='$pod' is empty. Dumps are DISABLED for this container start (no target directory)."
  exit 0
fi
for n in "$keep" "$age" "$factor" "$reserve_mib" "$limit"; do
  case "$n" in ''|*[!0-9]*)
    log "ERROR: a numeric setting is not a whole number (keep='$keep' maxAgeDays='$age' headroomDumps='$factor' reserveMiB='$reserve_mib' memoryLimitBytes='$limit'). Dumps are DISABLED for this container start."
    exit 0 ;;
  esac
done
if [ "$keep" -lt 1 ]; then keep=1; fi

if ! mkdir -p "$root" 2>/dev/null; then
  log "ERROR: cannot create $root. Dumps are DISABLED for this container start."
  exit 0
fi

# 1. Age. -mtime +N means strictly more than N whole days old.
find "$root" -mindepth 2 -maxdepth 2 -type f -name 'coredump.*' -mtime +"$age" 2>/dev/null |
  while IFS= read -r f; do
    if rm -f "$f" 2>/dev/null; then log "deleted (older than $age days): $f"; else log "ERROR: could not delete $f"; fi
  done

# 2. Count, newest first, across every pod's directory (held ones included). The newest are kept,
#    so a dump another pod is still writing right now is never the one removed.
# shellcheck disable=SC2012 # dump names are coredump.<pid>.<epoch>: no whitespace, no newlines.
ls -1t "$root"/*/coredump.* 2>/dev/null | tail -n +"$((keep + 1))" |
  while IFS= read -r f; do
    if rm -f "$f" 2>/dev/null; then log "deleted (beyond the newest $keep): $f"; else log "ERROR: could not delete $f"; fi
  done

# 3. Empty directories of other pods (a rolled-away pod that never crashed).
for d in "$root"/*/; do
  [ -d "$d" ] || continue
  d="${d%/}"
  [ "$d" = "$root/$pod" ] && continue
  rmdir "$d" 2>/dev/null || :
done

# 4. Headroom gate.
free_kib="$(df -Pk "$root" 2>/dev/null | awk 'NR == 2 { print $4 }')"
case "$free_kib" in ''|*[!0-9]*)
  log "ERROR: could not read the free space of $root (df said '$free_kib'). Dumps are DISABLED for this container start."
  [ -d "$root/$pod" ] && mv "$root/$pod" "$root/$pod.held-$(date +%s)" 2>/dev/null
  exit 0 ;;
esac
free=$((free_kib * 1024))
need=$((limit * factor + reserve_mib * 1024 * 1024))
count="$(ls -1 "$root"/*/coredump.* 2>/dev/null | wc -l | tr -d ' ')"
if [ "$free" -ge "$need" ]; then
  if mkdir -p "$root/$pod" 2>/dev/null; then
    log "armed: dumps go to $root/$pod (free $((free / 1048576)) MiB >= needed $((need / 1048576)) MiB = $factor x $((limit / 1048576)) MiB limit + $reserve_mib MiB reserve; $count dump(s) retained, keep $keep, max age $age days)"
  else
    log "ERROR: cannot create $root/$pod. Dumps are DISABLED for this container start."
  fi
else
  if [ -d "$root/$pod" ]; then
    held="$root/$pod.held-$(date +%s)"
    mv "$root/$pod" "$held" 2>/dev/null || log "ERROR: could not move $root/$pod aside"
  fi
  log "HELD: dumps are DISABLED for this container start: free $((free / 1048576)) MiB < needed $((need / 1048576)) MiB ($factor x $((limit / 1048576)) MiB limit + $reserve_mib MiB reserve). A crash now writes no dump. Free space on the volume or lower crashDumps.keep; $count dump(s) retained."
fi
exit 0
