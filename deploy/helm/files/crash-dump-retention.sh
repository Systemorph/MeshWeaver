#!/bin/sh
# Crash-dump retention and headroom gate for the portal. It runs TWICE per container lifetime
# shape, and both runs are the same script:
#   * as the `crash-dump-prepare` INIT CONTAINER, synchronously, before the portal process exists
#     (the first start of every pod);
#   * as the portal container's postStart hook, on every container start, so that each restart in
#     place after a crash (the CrashLoopBackOff case) is also followed by retention.
# Doc/Architecture/DebuggingNativeCrashes → "Production: where a dump lands, and how long it stays".
#
# Layout: dumps live under $MEMEX_CRASHDUMP_ROOT/<pod name>/coredump.<pid>.<time>, on a volume that
# outlives the pod (a dedicated dump claim, or else the shared /data claim). DOTNET_DbgMiniDumpName
# names that per-pod directory, and createdump does NOT create directories: no directory, no dump.
#
# What it does, in order:
#   0. move this pod's existing non-empty directory aside to <pod>.<epoch> FIRST, so a restart with
#      too little headroom is not left writing into the directory armed by an earlier start;
#   1. delete every dump older than MEMEX_CRASHDUMP_MAX_AGE_DAYS;
#   2. keep only the newest MEMEX_CRASHDUMP_KEEP dumps across EVERY pod's directory, but never a
#      file modified within the last MEMEX_CRASHDUMP_ACTIVE_MINUTES: a dump still being written
#      keeps advancing its mtime, so a file that recent may be another pod's write in progress;
#   3. remove EMPTY parked directories (<pod>.<epoch>, <pod>.held-<epoch>). Another pod's armed
#      directory is never removed, empty or not: it may belong to a live pod;
#   4. HEADROOM GATE: arm (create) this pod's directory only if the volume has room for
#      MEMEX_CRASHDUMP_HEADROOM_DUMPS dumps of MEMEX_MEMORY_LIMIT_BYTES each (a type-2 heap dump is
#      at most the process's memory) plus MEMEX_CRASHDUMP_RESERVE_MIB. The chart defaults the
#      dump count to the replica CEILING, so every pod that could crash at once is counted.
#      Otherwise no directory is created and the crash writes nothing.
#   This is a snapshot at start, not a reservation. On a DEDICATED dump claim that is enough: the
#   worst a dump can fill is the dump claim. On the shared /data claim it bounds, but cannot
#   guarantee, the space other writers leave.
#
# It always exits 0. A postStart hook that fails KILLS the container, and losing dump retention
# must never take the portal down with it. Every outcome, including every error, is printed as
# "[crash-dumps] …" on the container's stdout (in the postStart hook: PID 1's stdout), so it
# reaches Loki under the pod's labels. "armed" is printed only after the directory is confirmed
# present, "HELD" only after it is confirmed absent.
#
# Executed by deploy/aks/scripts/test-crash-dump-retention.sh (chart-gate.yml).

out=/dev/stdout
if [ -w /proc/1/fd/1 ] && [ "${MEMEX_CRASHDUMP_LOG_TO_STDOUT:-}" != "1" ]; then out=/proc/1/fd/1; fi
log() { printf '[crash-dumps] %s %s\n' "${MEMEX_CRASHDUMP_PHASE:-}" "$*" >>"$out" 2>/dev/null || :; }

root="${MEMEX_CRASHDUMP_ROOT:-}"
pod="${MEMEX_POD_NAME:-}"
keep="${MEMEX_CRASHDUMP_KEEP:-3}"
age="${MEMEX_CRASHDUMP_MAX_AGE_DAYS:-14}"
active="${MEMEX_CRASHDUMP_ACTIVE_MINUTES:-30}"
factor="${MEMEX_CRASHDUMP_HEADROOM_DUMPS:-2}"
reserve_mib="${MEMEX_CRASHDUMP_RESERVE_MIB:-2048}"
limit="${MEMEX_MEMORY_LIMIT_BYTES:-}"
target="$root/$pod"

# Leaves this pod's directory ABSENT, or says loudly that it could not.
disarm() {
  if [ -d "$target" ]; then
    if ! rmdir "$target" 2>/dev/null; then
      mv "$target" "$target.held-$(date +%s)" 2>/dev/null
    fi
  fi
  if [ -e "$target" ]; then
    log "ERROR: could not remove or move aside $target — dumps REMAIN ARMED there although $1"
  else
    log "HELD: dumps are DISABLED for this container start: $1. A crash now writes no dump."
  fi
}

if [ -z "$root" ] || [ -z "$pod" ]; then
  log "ERROR: MEMEX_CRASHDUMP_ROOT='$root' or MEMEX_POD_NAME='$pod' is empty — no target directory, so no dump can be written."
  exit 0
fi
case "$root" in /*) ;; *) log "ERROR: MEMEX_CRASHDUMP_ROOT='$root' is not absolute — refusing to touch it."; exit 0 ;; esac
case "$root/" in */../*|*/./*|*//*) log "ERROR: MEMEX_CRASHDUMP_ROOT='$root' is not a normalised path — refusing to touch it."; exit 0 ;; esac
for n in "$keep" "$age" "$active" "$factor" "$reserve_mib" "$limit"; do
  case "$n" in ''|*[!0-9]*)
    log "ERROR: a numeric setting is not a whole number (keep='$keep' maxAgeDays='$age' activeMinutes='$active' headroomDumps='$factor' reserveMiB='$reserve_mib' memoryLimitBytes='$limit')."
    disarm "a numeric setting is unusable"
    exit 0 ;;
  esac
done
if [ "$keep" -lt 1 ]; then keep=1; fi

if ! mkdir -p "$root" 2>/dev/null; then
  log "ERROR: cannot create $root — no dump can be written."
  exit 0
fi

# 0. Park this pod's previous dumps before anything decides about this start.
if [ -d "$target" ] && [ -n "$(ls -A "$target" 2>/dev/null)" ]; then
  parked="$target.$(date +%s)"
  if mv "$target" "$parked" 2>/dev/null; then
    log "parked the previous start's dumps in $parked"
  else
    log "ERROR: could not park $target"
  fi
fi

# 1. Age. -mtime +N means strictly more than N whole days old.
find "$root" -mindepth 2 -maxdepth 2 -type f -name 'coredump.*' -mtime +"$age" 2>/dev/null |
  while IFS= read -r f; do
    if rm -f "$f" 2>/dev/null; then log "deleted (older than $age days): $f"; else log "ERROR: could not delete $f"; fi
  done

# 2. Count, newest first, across every pod's directory. A file modified within the active window
#    is skipped (it still counts toward `keep`): it may be a dump another pod is writing now.
# shellcheck disable=SC2012 # dump names are coredump.<pid>.<epoch>: no whitespace, no newlines.
recent="$(find "$root" -mindepth 2 -maxdepth 2 -type f -name 'coredump.*' -mmin -"$active" 2>/dev/null)"
ls -1t "$root"/*/coredump.* 2>/dev/null | tail -n +"$((keep + 1))" |
  while IFS= read -r f; do
    if printf '%s\n' "$recent" | grep -qxF -- "$f"; then
      log "kept (beyond the newest $keep, but modified within $active min — possibly still being written): $f"
      continue
    fi
    if rm -f "$f" 2>/dev/null; then log "deleted (beyond the newest $keep): $f"; else log "ERROR: could not delete $f"; fi
  done

# 3. Empty PARKED directories only (<pod>.<digits> or <pod>.held-<digits>).
for d in "$root"/*.[0-9]*/ "$root"/*.held-[0-9]*/; do
  [ -d "$d" ] || continue
  rmdir "${d%/}" 2>/dev/null || :
done

# 4. Headroom gate.
free_kib="$(df -Pk "$root" 2>/dev/null | awk 'NR == 2 { print $4 }')"
case "$free_kib" in ''|*[!0-9]*)
  disarm "the free space of $root could not be read (df said '$free_kib')"
  exit 0 ;;
esac
free=$((free_kib * 1024))
need=$((limit * factor + reserve_mib * 1024 * 1024))
count="$(ls -1 "$root"/*/coredump.* 2>/dev/null | wc -l | tr -d ' ')"
detail="free $((free / 1048576)) MiB, needed $((need / 1048576)) MiB = $factor x $((limit / 1048576)) MiB limit + $reserve_mib MiB reserve; $count dump(s) retained, keep $keep, max age $age days"
if [ "$free" -ge "$need" ]; then
  mkdir -p "$target" 2>/dev/null
  if [ -d "$target" ]; then
    log "armed: dumps go to $target ($detail)"
  else
    log "ERROR: cannot create $target — no dump can be written ($detail)"
  fi
else
  disarm "not enough headroom ($detail). Free space on the volume, give dumps a dedicated claim (persistence.dumps), or lower crashDumps.keep"
fi
exit 0
