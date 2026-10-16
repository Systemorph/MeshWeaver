#!/usr/bin/env bash
# Observability stack for the AKS deployment: Grafana + Loki + Promtail + Prometheus
# (the grafana/loki-stack chart — Promtail ships every pod's stdout into Loki, datasources
# auto-wired in Grafana). Run via az aks command invoke on the private cluster — note that
# --file takes BOTH files, and `command invoke` runs in a FRESH pod each time, so the repo
# add/update has to happen in the same invocation as the install:
#
#   export GRAFANA_PW='<pick a strong password>'
#   az aks command invoke -g <aks-resource-group> -n <aks-cluster> \
#     --command "GRAFANA_PW=$GRAFANA_PW bash install-observability.sh" \
#     --file install-observability.sh --file values.observability.yaml
#
# EXIT CODES — the caller reads these, so they are a contract:
#   0  installed/updated, every workload Ready, Loki's storage is a Bound PVC
#   1  NOT installed/updated (helm refused, a stuck release could not be recovered, the values file
#      is missing, or Loki's storage is not durable) — the stack on the cluster is unchanged or unsafe
#   2  installed/updated (the release is `deployed`), but a workload did not become Ready inside the
#      budget — the log names it and carries its events and the tail of its previous log
#
# 🚨 IT FINISHES INSIDE `az aks command invoke`'s CAP, AND THAT IS A DESIGN PROPERTY, NOT A TIMEOUT.
# `command invoke` gives up after roughly ten minutes and then returns NO exit code and NO logs, and
# it kills this script mid-flight. Until 2026-10-15 the upgrade ran `--wait --timeout 10m`: when a
# workload could not become Ready (a Prometheus crash-looping on a full volume), helm's own wait
# outlived the invoke, the invoke pod was killed while helm held the release, and the release was
# left `pending-upgrade` — after which EVERY later run failed at once with "another operation
# (install/upgrade/rollback) is in progress", on a caller that could see neither code nor log. So:
#   - helm APPLIES (no --wait): the release reaches `deployed` in seconds and is never left pending;
#   - readiness is checked SEPARATELY, per workload, bounded by what is left of OBS_BUDGET_SECONDS
#     (default 540 s, well inside the cap), and reported with a real exit code (2) and diagnostics;
#   - a release already stuck in pending-* is recovered FIRST (see recover_pending_release).
#
# 🚨 values.observability.yaml is NOT optional. On chart defaults Loki is BestEffort with an
# emptyDir store and no nodeSelector — i.e. the first pod evicted when a workload balloons, on
# the same node pool as that workload, losing its ENTIRE store when it goes. That is not a
# hypothetical: it is how the 2026-08-12 memex-cloud incident window was destroyed, taking the
# red-log ticketing (mw-log-watcher reads FROM Loki) blind with it. Read that file's header.
set -uo pipefail
: "${GRAFANA_PW:?set GRAFANA_PW (Grafana admin password)}"
VALUES="${VALUES:-values.observability.yaml}"
# 🚨 THE INVOKE POD IS DISTROLESS. `az aks command invoke` runs this in an image with bash, awk,
# grep, sort, tail, date, sleep, kubectl and helm — and NO sed (measured 2026-10-15; the first real
# run lost its recovery script and its workload list to "sed: command not found"). Use only what is
# listed here; test-observability-recovery.py runs the script on exactly that toolset.
for tool in awk grep sort tail date sleep cat kubectl helm; do
  command -v "$tool" >/dev/null 2>&1 || { echo "FATAL: '$tool' is not available here — nothing was done." >&2; exit 1; }
done
[ -f "$VALUES" ] || { echo "FATAL: $VALUES not found — pass it with --file (see header)." >&2; exit 1; }

# The release and namespace are FIXED: the Loki storage verification reads `loki-0`, the Grafana
# service is `loki-grafana`, and the fleet watch reads exactly this release.
NS=monitoring
RELEASE=loki
# 540 s when this script IS the invoke's command. A caller that runs it inside a LONGER invoke
# (deploy.sh) must pass what is left of ITS cap: SECONDS starts with this shell, not the invoke.
BUDGET_SECONDS="${OBS_BUDGET_SECONDS:-540}"
PROMETHEUS_DEPLOY="$RELEASE-prometheus-server"
PROMETHEUS_CONTAINER=prometheus-server
# The recovery prunes the oldest TSDB blocks until the volume is at most this full. Prometheus then
# starts with room for a fresh WAL, and its own size retention (values file) takes over from there.
TSDB_RECOVERY_TARGET_PERCENT="${TSDB_RECOVERY_TARGET_PERCENT:-60}"
# Validated at startup, before anything is scaled or deleted: the clean-up compares integers, so
# "60%" or a negative value would never stop it pruning.
case "$TSDB_RECOVERY_TARGET_PERCENT" in
  ''|*[!0-9]*) echo "FATAL: TSDB_RECOVERY_TARGET_PERCENT must be an integer 1-95, got '$TSDB_RECOVERY_TARGET_PERCENT'." >&2; exit 1 ;;
esac
if [ "$TSDB_RECOVERY_TARGET_PERCENT" -lt 1 ] || [ "$TSDB_RECOVERY_TARGET_PERCENT" -gt 95 ]; then
  echo "FATAL: TSDB_RECOVERY_TARGET_PERCENT must be an integer 1-95, got '$TSDB_RECOVERY_TARGET_PERCENT'." >&2
  exit 1
fi

remaining() { echo $(( BUDGET_SECONDS - SECONDS )); }
recovery() { echo "RECOVERY: $*"; }
# Short API calls: each is bounded by 20 s AND by what is left of the run's deadline (less 10 s to
# report), so a slow API server can never carry the run past the budget; once the budget is spent
# every call fails fast and the run reports RED. Waits are bounded by their own --timeout.
#
# 🚨 NEVER bound kubectl with a CONNECTION flag (`--request-timeout`, `--server`, `--token`,
# `--kubeconfig`, `--context`, …). `az aks command invoke` gives kubectl NO kubeconfig: it runs on
# the pod's in-cluster service-account config, and client-go uses that fallback ONLY while the
# merged client config equals the default — any such flag makes it differ, kubectl drops to
# http://localhost:8080 and every call is refused. Measured 2026-10-15: `kubectl get cm` worked in
# the invoke pod, `kubectl --request-timeout=20s get cm` answered "localhost:8080 was refused", and
# the first release of this deadline work failed on its first real run for exactly that. The bound
# is the process watchdog (`bounded`), which changes nothing kubectl sees.
request_seconds() {
  local t=$(( $(remaining) - 10 ))
  [ "$t" -gt "${1:-20}" ] && t=${1:-20}
  [ "$t" -lt 1 ] && t=1
  echo "$t"
}
kc() { bounded "$(request_seconds)" kubectl -n "$NS" "$@"; }

# ONE deadline for the whole run. Every stage that can wait takes its bound from what is left of
# OBS_BUDGET_SECONDS, minus what must stay in hand for what comes after it, and a stage whose
# minimum no longer fits is not started: the run fails RED with its log intact instead of being
# cut off by the invoke, mid-operation, with neither.
#   stage_seconds <ceiling> <minimum> <keep-back> <stage name>   → sets STAGE_SECONDS, or returns 1
stage_seconds() {
  local left=$(( $(remaining) - $3 ))
  [ "$left" -gt "$1" ] && left=$1
  if [ "$left" -lt "$2" ]; then
    echo "FATAL: the ${BUDGET_SECONDS}s budget is spent — not starting '$4' (needs ${2}s, ${left}s left after keeping ${3}s back)." >&2
    return 1
  fi
  STAGE_SECONDS=$left
}
# Kept back while recovering: tearing the recovery down (~40 s), the upgrade's minimum (60 s) and
# room for the readiness report.
RECOVERY_KEEP_BACK=180   # + the rollback's minimum: the TSDB recovery runs BEFORE it (see below)
ROLLBACK_KEEP_BACK=120

# helm has no request timeout of its own, and its --timeout does not bound every call (status,
# history, repo, API discovery). `bounded <seconds> cmd…` runs a command under a watchdog that
# kills it at the bound, so a stalled call fails RED instead of carrying the run past the cap.
# The watchdog's output goes to /dev/null so `$(bounded …)` never waits on it. The command keeps
# the caller's stdin explicitly (`<&0`): a background job in a non-interactive shell otherwise
# reads /dev/null, and `… | kc apply -f -` would apply nothing. Environment, working directory and
# arguments are passed through untouched.
bounded() {
  local secs=$1 pid watchdog rc
  shift
  [ "$secs" -lt 1 ] && secs=1
  "$@" <&0 &
  pid=$!
  ( sleep "$secs"; kill -TERM "$pid" 2>/dev/null ) >/dev/null 2>&1 &
  watchdog=$!
  wait "$pid"; rc=$?
  kill "$watchdog" 2>/dev/null; wait "$watchdog" 2>/dev/null
  return "$rc"
}
hb() { bounded "$(request_seconds 30)" helm "$@"; }    # a helm metadata/repo call
# A workload's pod selector as `k=v,k=v` (the go-template's `$k`/`$v` are template variables).
selector_of() {
  local labels
  # shellcheck disable=SC2016
  labels=$(kc get "$1" -o go-template='{{range $k, $v := .spec.selector.matchLabels}}{{$k}}={{$v}},{{end}}' 2>/dev/null)
  printf '%s' "${labels%,}"
}

hb repo add grafana https://grafana.github.io/helm-charts >/dev/null 2>&1 || true
hb repo update >/dev/null 2>&1

# ── 1. A release stuck in pending-* ─────────────────────────────────────────────────────────────
# helm marks the release pending-install/-upgrade/-rollback BEFORE it touches the cluster and
# deployed/failed AFTER. A run killed in between (the invoke cap, a cancelled job) leaves the
# pending revision behind forever, and helm refuses every later operation on the release. This
# script is the release's only writer and holds the install lock (section 0), so a pending
# revision seen here is a dead run's — never a live one.
# Recovery is helm's own: roll back to the last `deployed` revision (no --wait: it only has to
# re-apply known-good manifests and record a fresh `deployed` revision), then upgrade as usual.
recover_pending_release() {
  local status last_deployed
  local raw
  if ! raw=$(hb status "$RELEASE" -n "$NS" 2>&1); then
    # Not-found is the first install; anything else (a stalled or refused call) is not an answer.
    case "$raw" in
      *"not found"*) return 0 ;;
      *) echo "FATAL: could not read the state of release $RELEASE: $raw" >&2; exit 1 ;;
    esac
  fi
  status=$(printf '%s\n' "$raw" | awk -F': ' '$1 == "STATUS" { print $2; exit }')
  case "$status" in
    pending-install|pending-upgrade|pending-rollback) ;;
    *) return 0 ;;
  esac
  local history
  if ! history=$(hb history "$RELEASE" -n "$NS" --max 1000 2>&1); then
    echo "FATAL: release $RELEASE is stuck in '$status' and its history could not be read: $history" >&2
    exit 1
  fi
  last_deployed=$(printf '%s\n' "$history" \
    | awk 'NR > 1 { for (i = 2; i <= NF; i++) if ($i == "deployed") print $1 }' | tail -n 1)
  if [ -n "$last_deployed" ]; then
    recovery "release $RELEASE is stuck in '$status' (a run that was killed mid-operation)."
    recovery "rolling back to its last deployed revision $last_deployed, then upgrading as usual."
    stage_seconds 60 20 "$ROLLBACK_KEEP_BACK" "helm rollback" || exit 1
    if ! bounded $(( STAGE_SECONDS + 10 )) helm rollback "$RELEASE" "$last_deployed" -n "$NS" --timeout "${STAGE_SECONDS}s"; then
      echo "FATAL: release $RELEASE is stuck in '$status' and 'helm rollback $RELEASE $last_deployed' failed." >&2
      hb history "$RELEASE" -n "$NS" --max 5 >&2 || true
      exit 1
    fi
  elif [ "$status" = pending-install ]; then
    # Nothing was ever deployed: there is no known-good state to return to, only a half-made first
    # install. Removing it lets the install below start clean. StatefulSet volume claims (Loki's
    # store) survive an uninstall; the chart's own PVCs belonged to an install that never completed.
    recovery "release $RELEASE is stuck in 'pending-install' and was never deployed — uninstalling the half-made first install."
    stage_seconds 60 20 "$ROLLBACK_KEEP_BACK" "helm uninstall" || exit 1
    if ! bounded $(( STAGE_SECONDS + 10 )) helm uninstall "$RELEASE" -n "$NS" --timeout "${STAGE_SECONDS}s"; then
      echo "FATAL: release $RELEASE is stuck in 'pending-install' and could not be uninstalled." >&2
      exit 1
    fi
  else
    echo "FATAL: release $RELEASE is stuck in '$status' and has no deployed revision to roll back to." >&2
    hb history "$RELEASE" -n "$NS" --max 10 >&2 || true
    exit 1
  fi
  recovery "release $RELEASE is $(hb status "$RELEASE" -n "$NS" 2>/dev/null | awk -F': ' '$1 == "STATUS" { print $2; exit }' || echo unknown) again."
}

# ── 2. A Prometheus whose TSDB volume is full ───────────────────────────────────────────────────
# A full volume never recovers by itself: Prometheus replays its WAL at startup, the replay has to
# preallocate a segment, and with no space left it panics (`preallocate: no space left on device`)
# BEFORE size retention ever runs. Every restart repeats it. Raising retention or the volume in the
# values file does not reach it either — the new pod replays the same WAL on the same full disk.
#
# 🚨 THE DATA THIS DELETES IS DISPOSABLE METRICS, AND THAT IS WHY IT MAY BE DELETED HERE. The
# Prometheus TSDB holds scraped metrics for dashboards and alerts — no record, no log (logs are
# Loki's, on their own volume, untouched), nothing any instance reads back as state. Losing the WAL
# loses the last couple of hours of samples; pruning blocks loses the oldest days. Nothing else is
# lost, and the alternative is a Prometheus that never starts.
#
# It acts ONLY on positive evidence — the server container has restarted AND its previous run's log
# says "no space left on device" — so a healthy Prometheus, or one failing for any other reason, is
# never touched. It scales the server to 0 (RWO disk: it must detach), runs a one-shot pod from the
# server's OWN image (no new pull source) on the server's own volume that deletes the WAL, the
# head chunks, leftover temporary blocks and, oldest samples first, whole blocks until the volume
# is at most TSDB_RECOVERY_TARGET_PERCENT full, and scales the server back.
tsdb_recovery_script() {
  cat <<'RECOVER'
set -eu
dir="${TSDB_DIR:-/data}"
target="${TARGET_PERCENT:-60}"
# Every decision to delete rests on a usage reading, so a reading that is not a number STOPS the
# script before it deletes anything more — an empty one would otherwise fail every comparison and
# prune every block.
measure() {
  used=$(df -P "$dir" 2>/dev/null | awk 'NR == 2 { sub("%", "", $5); print $5 }') || used=""
  case "$used" in
    ''|*[!0-9]*) echo "tsdb-recovery: cannot read the volume's usage (got '$used') — stopping, nothing more is deleted"; exit 1 ;;
  esac
}
measure
echo "tsdb-recovery: before: $used% of $dir used"
cd "$dir"
rm -rf wal chunks_head lock
for leftover in *.tmp*; do
  if [ -e "$leftover" ]; then echo "tsdb-recovery: removing temporary $leftover"; rm -rf "$leftover"; fi
done
measure
echo "tsdb-recovery: removed the WAL and head chunks; now $used% used"
# Oldest SAMPLES first, never oldest ULID: compaction mints a NEW ULID for an OLD time range, so
# the ULID orders creation, not age. Each block's meta.json carries its range; its first "minTime"
# is the block's own (the ones under compaction.parents come later). A directory without a
# meta.json is not a complete block (Prometheus discards it at startup) and goes first.
for block in $(for b in $(ls -1 | grep -E '^[0-9A-HJKMNP-TV-Z]{26}$'); do
                 min=$(sed -n 's/.*"minTime": *\(-\{0,1\}[0-9][0-9]*\).*/\1/p' "$b/meta.json" 2>/dev/null | head -n 1)
                 echo "${min:--9223372036854775808} $b"
               done | sort -n | awk '{ print $2 }'); do
  measure
  if [ "$used" -le "$target" ]; then break; fi
  echo "tsdb-recovery: removing oldest block $block"
  rm -rf "$block"
done
measure
echo "tsdb-recovery: after: $used% of $dir used (target <= $target%)"
[ "$used" -le "$target" ] || { echo "tsdb-recovery: still above target — no block left to remove"; exit 1; }
RECOVER
}

recover_full_tsdb() {
  local selector pods pod state restarts ready previous_log out_of_space=false image node_selector tolerations claim replicas
  kc get deploy "$PROMETHEUS_DEPLOY" >/dev/null 2>&1 || return 0
  selector=$(selector_of "deploy/$PROMETHEUS_DEPLOY")
  [ -n "$selector" ] || return 0
  if ! pods=$(kc get pods -l "$selector" -o jsonpath='{.items[*].metadata.name}'); then
    echo "WARN: could not list the Prometheus server pods — no evidence, so no TSDB recovery attempted." >&2
    return 0
  fi
  for pod in $pods; do
    # CURRENT failure, not a historical one: a server that crashed once and has since recovered is
    # Ready, still carries the restart count and the old previous log, and must not be touched.
    state=$(kc get pod "$pod" \
      -o "jsonpath={.status.containerStatuses[?(@.name==\"$PROMETHEUS_CONTAINER\")].restartCount} {.status.containerStatuses[?(@.name==\"$PROMETHEUS_CONTAINER\")].ready}" 2>/dev/null) || continue
    read -r restarts ready <<<"$state"
    [ "${restarts:-0}" -gt 0 ] 2>/dev/null || continue
    [ "$ready" = false ] || continue
    # Captured, not piped into `grep -q`: under pipefail an early-exiting grep SIGPIPEs kubectl and
    # the pipeline reads as "no match" exactly when it matched.
    previous_log=$(kc logs "$pod" -c "$PROMETHEUS_CONTAINER" --previous 2>/dev/null || true)
    if printf '%s\n' "$previous_log" | grep "no space left on device" >/dev/null; then
      out_of_space=true
      recovery "$pod is not Ready, has restarted $restarts times, and its previous run died with 'no space left on device'."
    fi
  done
  [ "$out_of_space" = true ] || return 0

  # Every read must SUCCEED before the server is stopped. An empty default stands only for a field
  # a successful read reports absent — never for a failed request, which would otherwise run the
  # cleaner without the server's scheduling constraints (wrong pool, or Pending on a taint).
  local reads_ok=true
  image=$(kc get deploy "$PROMETHEUS_DEPLOY" \
    -o "jsonpath={.spec.template.spec.containers[?(@.name==\"$PROMETHEUS_CONTAINER\")].image}") || reads_ok=false
  node_selector=$(kc get deploy "$PROMETHEUS_DEPLOY" -o jsonpath='{.spec.template.spec.nodeSelector}') || reads_ok=false
  tolerations=$(kc get deploy "$PROMETHEUS_DEPLOY" -o jsonpath='{.spec.template.spec.tolerations}') || reads_ok=false
  claim=$(kc get deploy "$PROMETHEUS_DEPLOY" \
    -o jsonpath='{.spec.template.spec.volumes[?(@.name=="storage-volume")].persistentVolumeClaim.claimName}') || reads_ok=false
  replicas=$(kc get deploy "$PROMETHEUS_DEPLOY" -o jsonpath='{.spec.replicas}') || reads_ok=false
  [ -n "$replicas" ] && [ "$replicas" -gt 0 ] 2>/dev/null || replicas=1
  if [ "$reads_ok" != true ] || [ -z "$image" ] || [ -z "$claim" ]; then
    echo "FATAL: Prometheus is out of space but the server's image, scheduling or TSDB claim could not be read — not recovering blind; nothing was stopped." >&2
    exit 1
  fi
  recovery "the Prometheus TSDB on PVC $claim is full. Clearing the WAL and pruning the oldest blocks"
  recovery "to <= $TSDB_RECOVERY_TARGET_PERCENT% — DISPOSABLE METRICS ONLY: the last hours of samples and the oldest days go,"
  recovery "logs (Loki, its own volume) are untouched. Size retention takes over once Prometheus starts."

  [ -n "$node_selector" ] || node_selector='{}'
  [ -n "$tolerations" ] || tolerations='[]'
  # If this run is cut off between here and the scale-back below, the chart renders `replicas: 1`
  # for the server and the next run's helm upgrade restores it (three-way merge against live).
  stage_seconds 120 30 "$RECOVERY_KEEP_BACK" "wait for the Prometheus server to stop" || exit 1
  local stop_deadline=$(( SECONDS + STAGE_SECONDS )) left listed
  kc scale deploy "$PROMETHEUS_DEPLOY" --replicas=0 || { echo "FATAL: could not scale $PROMETHEUS_DEPLOY to 0." >&2; exit 1; }
  # Only a SUCCESSFUL, EMPTY listing proves the server stopped. A failed request also prints
  # nothing, and RWO lets a second pod mount the disk on the same node — cleaning under a live
  # Prometheus is exactly what must not happen.
  # The bound is an ABSOLUTE deadline: every request and every sleep is cut to the time left, so
  # slow listings cannot stretch the loop (the replica restore is in RECOVERY_KEEP_BACK).
  while :; do
    left=$(( stop_deadline - SECONDS ))
    if [ "$left" -le 0 ]; then
      echo "FATAL: could not confirm the Prometheus server stopped within ${STAGE_SECONDS}s; the TSDB was NOT touched." >&2
      restore_prometheus "$replicas"
      exit 1
    fi
    if listed=$(bounded "$(request_seconds "$left")" kubectl -n "$NS" get pods -l "$selector" -o name 2>/dev/null) \
      && [ -z "$listed" ]; then
      break
    fi
    left=$(( stop_deadline - SECONDS ))
    [ "$left" -gt 0 ] && sleep "$(( left < 5 ? left : 5 ))"
  done

  local recovery_pod="$RELEASE-prometheus-tsdb-recovery" ok=true
  if ! remove_recovery_pod "$recovery_pod"; then
    # A cleaner that may still be deleting files on this very PVC: no server goes back onto it.
    echo "FATAL: a previous $recovery_pod could not be confirmed gone; the TSDB was NOT touched and" >&2
    echo "       $PROMETHEUS_DEPLOY is LEFT AT 0 so no server shares the disk with it." >&2
    exit 1
  fi
  {
    cat <<MANIFEST
apiVersion: v1
kind: Pod
metadata:
  name: $recovery_pod
  namespace: $NS
spec:
  restartPolicy: Never
  nodeSelector: $node_selector
  tolerations: $tolerations
  securityContext: {runAsUser: 65534, runAsGroup: 65534, runAsNonRoot: true, fsGroup: 65534}
  containers:
    - name: recover
      image: $image
      command: ["/bin/sh", "-c"]
      env:
        - {name: TARGET_PERCENT, value: "$TSDB_RECOVERY_TARGET_PERCENT"}
      args:
        - |
MANIFEST
    tsdb_recovery_script | awk '{ print "          " $0 }'
    cat <<MANIFEST
      volumeMounts:
        - {name: storage, mountPath: /data}
  volumes:
    - name: storage
      persistentVolumeClaim: {claimName: $claim}
MANIFEST
  } | kc apply -f - || ok=false
  if [ "$ok" = true ]; then
    if stage_seconds 180 30 "$RECOVERY_KEEP_BACK" "the TSDB recovery pod"; then
      bounded $(( STAGE_SECONDS + 5 )) kubectl -n "$NS" wait --for=jsonpath='{.status.phase}'=Succeeded \
        pod/"$recovery_pod" --timeout="${STAGE_SECONDS}s" || ok=false
    else
      ok=false
    fi
  fi
  local cleaner_log
  cleaner_log=$(kc logs "$recovery_pod" 2>&1)
  printf '%s\n' "$cleaner_log" | awk '{ print "RECOVERY: " $0 }'
  # `Succeeded` proves only that the container exited 0 — an empty script does that too (measured:
  # a missing tool once rendered the pod with NO script, and it "succeeded" having deleted
  # nothing). Success is the clean-up's own closing line.
  if [ "$ok" = true ] && ! printf '%s\n' "$cleaner_log" | grep "tsdb-recovery: after:" >/dev/null; then
    echo "FATAL: the recovery pod exited 0 but never reported its clean-up — it did NOT run." >&2
    ok=false
  fi
  if [ "$ok" = true ]; then
    # Succeeded: its container has exited, so the server may start while the pod object goes.
    restore_prometheus "$replicas" || exit 1
    kc delete pod "$recovery_pod" --ignore-not-found --wait=false >/dev/null 2>&1 || true
    recovery "the Prometheus TSDB has room again; $PROMETHEUS_DEPLOY scaled back to $replicas."
    return 0
  fi
  # Failed or still running: diagnostics FIRST, then make sure the cleaner is gone before a server
  # is allowed back onto the same disk. If that cannot be confirmed, leave the server at 0 — the
  # chart's `replicas: 1` brings it back on the next run's upgrade.
  echo "FATAL: the Prometheus TSDB recovery pod did not succeed — the volume is still full (log above)." >&2
  kc describe pod "$recovery_pod" 2>&1 | tail -n 25 >&2
  if remove_recovery_pod "$recovery_pod"; then
    restore_prometheus "$replicas"
  else
    echo "FATAL: could not confirm $recovery_pod is gone; $PROMETHEUS_DEPLOY is LEFT AT 0 so no second writer" >&2
    echo "       shares the disk with it. The next run's helm upgrade restores the chart's replicas." >&2
  fi
  exit 1
}

# Deletes the recovery pod and returns 0 only on a successful listing that no longer shows it.
remove_recovery_pod() {
  local left
  # A WAITING delete (up to 30 s): its own watchdog, not kc's 20-second short-call bound.
  bounded "$(request_seconds 35)" kubectl -n "$NS" delete pod "$1" --ignore-not-found --wait=true --timeout=30s >/dev/null 2>&1 || true
  left=$(kc get pod "$1" --ignore-not-found -o name 2>/dev/null) && [ -z "$left" ]
}

restore_prometheus() {
  if kc scale deploy "$PROMETHEUS_DEPLOY" --replicas="$1"; then
    recovery "$PROMETHEUS_DEPLOY scaled back to $1."
  else
    echo "FATAL: could not scale $PROMETHEUS_DEPLOY back to $1 — the next run's helm upgrade restores it." >&2
    return 1
  fi
}

# ── 0. One installer at a time, cluster-wide ────────────────────────────────────────────────────
# Two `command invoke` pods are two processes; nothing in helm serialises them. Without this, a
# second run could roll back the first run's live upgrade (its revision is `pending-*` only because
# it is IN PROGRESS) or delete the first run's live recovery pod. The lock is a ConfigMap CREATED
# atomically (create fails if it exists) and carries an expiry past which its holder is
# certainly dead: no run outlives its budget plus the invoke's own cap. An expired lock is taken
# over by compare-and-swap (replace at the read resourceVersion), so two takers cannot both win.
LOCK_NAME="$RELEASE-observability-install-lock"
LOCK_HOLDER="${OBS_LOCK_HOLDER:-${HOSTNAME:-installer}-$$-$(date +%s)}"
LOCK_HELD=false
acquire_install_lock() {
  local now expires current holder held_until version
  now=$(date +%s)
  expires=$(( now + BUDGET_SECONDS + 120 ))
  kc get namespace "$NS" >/dev/null 2>&1 || kc create namespace "$NS" >/dev/null 2>&1 || true
  if kc create configmap "$LOCK_NAME" --from-literal=holder="$LOCK_HOLDER" --from-literal=expires="$expires" >/dev/null 2>&1; then
    LOCK_HELD=true
    return 0
  fi
  if ! current=$(kc get configmap "$LOCK_NAME" -o jsonpath='{.data.holder} {.data.expires} {.metadata.resourceVersion}'); then
    echo "FATAL: could neither create nor read the install lock $NS/$LOCK_NAME — refusing to run unserialised." >&2
    exit 1
  fi
  read -r holder held_until version <<<"$current"
  case "$held_until" in ''|*[!0-9]*) held_until=0 ;; esac
  if [ "$now" -lt "$held_until" ]; then
    echo "FATAL: another installation ($holder) holds $NS/$LOCK_NAME until $(( held_until - now ))s from now —" >&2
    echo "       refusing to overlap it. Nothing was rolled back, cleaned or upgraded." >&2
    exit 1
  fi
  recovery "the install lock held by '$holder' expired $(( now - held_until ))s ago — that run is dead; taking it over."
  if ! printf 'apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: %s\n  namespace: %s\n  resourceVersion: "%s"\ndata:\n  holder: "%s"\n  expires: "%s"\n' \
      "$LOCK_NAME" "$NS" "$version" "$LOCK_HOLDER" "$expires" | kc replace -f - >/dev/null; then
    echo "FATAL: lost the race for the expired install lock — another installation took it first." >&2
    exit 1
  fi
  LOCK_HELD=true
}
release_install_lock() {
  [ "$LOCK_HELD" = true ] || return 0
  local holder
  holder=$(kc get configmap "$LOCK_NAME" -o jsonpath='{.data.holder}' 2>/dev/null) || return 0
  [ "$holder" = "$LOCK_HOLDER" ] && kc delete configmap "$LOCK_NAME" --ignore-not-found >/dev/null 2>&1
  return 0
}
trap release_install_lock EXIT

# A cleaner left behind by an earlier run (its teardown could not be confirmed, so it left the
# server at 0) must be GONE before anything — a rollback or an upgrade — can bring a server back
# onto its disk. This runs whatever the server's state, because with the server at 0 there is no
# crash evidence left to trigger the recovery path.
ensure_no_leftover_cleaner() {
  local pod="$RELEASE-prometheus-tsdb-recovery" present
  if ! present=$(kc get pod "$pod" --ignore-not-found -o name); then
    echo "FATAL: could not check for a leftover $pod — not starting anything that could restore a server onto its disk." >&2
    exit 1
  fi
  [ -n "$present" ] || return 0
  recovery "a TSDB recovery pod from an earlier run is still present — removing it before anything else."
  kc logs "$pod" 2>&1 | tail -n 20 | awk '{ print "RECOVERY: (earlier run) " $0 }'
  if ! remove_recovery_pod "$pod"; then
    echo "FATAL: the leftover $pod could not be confirmed gone; nothing was rolled back or upgraded." >&2
    exit 1
  fi
}

# The full-volume recovery runs BEFORE the pending-release rollback: a rollback that changes the
# server's pod template replaces the crash-looping pod with a fresh one — zero restarts, no
# previous log — and the evidence the recovery acts on would be gone while the disk stays full.
acquire_install_lock
ensure_no_leftover_cleaner
recover_full_tsdb
recover_pending_release

# 🚨 FIRST TIME ONLY, on a cluster that already runs the emptyDir Loki: enabling persistence adds a
# volumeClaimTemplate, and a StatefulSet's volumeClaimTemplates are IMMUTABLE — `helm upgrade` fails
# with "Forbidden: updates to statefulset spec for fields other than …". Delete the StatefulSet
# first, keeping its pod running so log ingestion continues until the new one is ready:
#
#   kubectl -n monitoring delete sts loki --cascade=orphan
#   # …then run this script; finally remove the orphaned pod:
#   kubectl -n monitoring delete pod loki-0
#
# Accept before doing it: whatever the old emptyDir held is discarded. That is the one-time cost of
# never losing a window again.
# 🚨 Check helm's exit EXPLICITLY. This script runs without `set -e` (the `helm repo add` above is
# allowed to fail when the repo already exists), so without this a failed install would be followed
# by the verification echoes below and the script would still exit 0 — an install failure that reads
# like success. Exactly the shape the repo bans in CI gates, and it belongs here too.
# 🚨 THE CHART VERSION IS PINNED, and the pin is what makes any verification of these values mean
# anything. Until #3773 this read `grafana/loki-stack` with no --version, so every run installed
# whatever upstream had published most recently. That matters more than it looks: this DEPRECATED
# chart is already known to drop a config key silently (`promtail.config.wal` renders to nothing —
# see the values file), so an unannounced upstream restructure of `loki.config` could stop
# templating the ingester WAL and the only symptom would be the next drain losing the logs again.
# An unpinned third-party chart turns a deliberate upgrade into an invisible one.
#
# Moving the pin is a normal, welcome change — make it deliberately, and in the same commit as
# CHART_VERSION in check-observability-values.sh, which renders THIS version and asserts the keys
# still arrive and that the Loki binary of the chart's appVersion accepts the result.
CHART_VERSION="${LOKI_STACK_VERSION:-2.10.3}"

# No --wait (see the header): --timeout bounds helm's own API calls and hooks, and the release is
# recorded `deployed` as soon as the manifests are applied. Readiness is the next block's job.
stage_seconds 120 60 60 "helm upgrade" || exit 1
UPGRADE_SECONDS=$STAGE_SECONDS
if ! bounded $(( UPGRADE_SECONDS + 15 )) helm upgrade --install "$RELEASE" grafana/loki-stack --version "$CHART_VERSION" -n "$NS" --create-namespace \
  -f "$VALUES" \
  --set grafana.enabled=true --set prometheus.enabled=true \
  --set grafana.adminPassword="$GRAFANA_PW" --set grafana.service.type=ClusterIP \
  --timeout "${UPGRADE_SECONDS}s"
then
  echo "FATAL: helm upgrade failed — the stack was NOT installed/updated." >&2
  echo "If it complains about immutable StatefulSet fields, that is the persistence change:" >&2
  echo "  kubectl -n monitoring delete sts loki --cascade=orphan   # then re-run, then delete pod loki-0" >&2
  exit 1
fi

# ── 3. Readiness, reported on its own ───────────────────────────────────────────────────────────
# Every workload the release owns, bounded by what is left of the budget. A workload that does not
# become Ready is NAMED, with its pods, events and previous log tail, and the script exits 2 — the
# release itself is `deployed` either way, so the next run starts from a clean helm state.
not_ready=()
# The chart's sub-charts label by two conventions; BOTH listings must succeed, or the readiness
# verdict would silently cover only part of the release.
if ! by_instance=$(kc get deploy,sts,ds -l "app.kubernetes.io/instance=$RELEASE" -o name) \
  || ! by_release=$(kc get deploy,sts,ds -l "release=$RELEASE" -o name); then
  echo "FATAL: could not list the workloads of release $RELEASE — readiness was NOT established." >&2
  exit 1
fi
workloads=$(printf '%s\n%s\n' "$by_instance" "$by_release" | awk 'NF' | sort -u)
if [ -z "$workloads" ]; then
  echo "FATAL: helm reported success but no workload of release $RELEASE exists in $NS." >&2
  exit 1
fi
for workload in $workloads; do
  left=$(( $(remaining) - 45 ))      # 45 s stay in hand for the storage check and the diagnostics
  if [ "$left" -lt 5 ]; then
    echo "budget spent — $workload not checked" >&2
    not_ready+=("$workload")
    continue
  fi
  # Bounded as a WHOLE: --timeout limits the watch, not the discovery and reads before it.
  if ! bounded "$left" kubectl -n "$NS" rollout status "$workload" --timeout="${left}s"; then
    not_ready+=("$workload")
  fi
done

# Verify the RUNNING pod, not just the desired StatefulSet: an orphan left over from
# the immutable-volume transition can still be serving from emptyDir (#3773).
if ! storage_volume=$(kc get pod loki-0 -o jsonpath='{.spec.containers[?(@.name=="loki")].volumeMounts[?(@.mountPath=="/data")].name}') \
  || [ -z "$storage_volume" ]; then
  echo "FATAL: cannot identify Loki's running /data volume." >&2
  exit 1
fi
if ! storage_claim=$(kc get pod loki-0 -o "jsonpath={.spec.volumes[?(@.name==\"$storage_volume\")].persistentVolumeClaim.claimName}") \
  || [ -z "$storage_claim" ]; then
  echo "FATAL: Loki's running /data volume is not backed by a PVC; log durability is NOT established." >&2
  exit 1
fi
if ! storage_phase=$(kc get pvc "$storage_claim" -o jsonpath='{.status.phase}') \
  || [ "$storage_phase" != Bound ]; then
  echo "FATAL: Loki's storage PVC is not Bound; log durability is NOT established." >&2
  exit 1
fi

echo
echo "Verified: Loki's running /data volume uses Bound PVC $storage_claim."
kc get pods

if [ ${#not_ready[@]} -gt 0 ]; then
  echo
  echo "NOT READY within the ${BUDGET_SECONDS}s budget: ${not_ready[*]}" >&2
  for workload in "${not_ready[@]}"; do
    # Diagnostics are optional; the verdict is not. Stop collecting them before the budget ends.
    if [ "$(remaining)" -lt 15 ]; then
      echo "(diagnostics cut short: the ${BUDGET_SECONDS}s budget is spent)" >&2
      break
    fi
    echo "── $workload ──" >&2
    kc describe "$workload" 2>&1 | tail -n 25 >&2
    selector=$(selector_of "$workload")
    for pod in $(kc get pods -l "$selector" -o jsonpath='{.items[*].metadata.name}' 2>/dev/null); do
      [ "$(remaining)" -lt 15 ] && break
      echo "── pod $pod: events ──" >&2
      kc get events --field-selector "involvedObject.name=$pod" 2>&1 | tail -n 10 >&2
      echo "── pod $pod: previous log (tail) ──" >&2
      kc logs "$pod" --all-containers --previous --tail=20 >&2 2>&1 || true
    done
  done
  echo "FATAL: the release is deployed, but ${#not_ready[@]} workload(s) are not Ready (above)." >&2
  exit 2
fi

echo "All workloads of release $RELEASE are Ready."
echo "Verify the remaining scheduling properties:"
echo "  kubectl -n monitoring get pod loki-0 -o jsonpath='{.status.qosClass} {.spec.nodeName}'   # NOT BestEffort, NOT a silos node"
echo "  kubectl -n monitoring get sts loki -o jsonpath='{.spec.template.spec.nodeSelector}'      # agentpool: system"
echo
echo "Access (private cluster -> via the P2S VPN):"
echo "  az aks get-credentials -g <aks-resource-group> -n <aks-cluster>"
echo "  kubectl -n monitoring port-forward svc/loki-grafana 3000:80"
echo "  open http://localhost:3000   (user: admin / pass: \$GRAFANA_PW)"
echo "Loki datasource is pre-wired; query e.g.  {namespace=\"memex\"}  in Grafana Explore."
