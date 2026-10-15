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
[ -f "$VALUES" ] || { echo "FATAL: $VALUES not found — pass it with --file (see header)." >&2; exit 1; }

NS="${OBS_NAMESPACE:-monitoring}"
RELEASE="${OBS_RELEASE:-loki}"
BUDGET_SECONDS="${OBS_BUDGET_SECONDS:-540}"
PROMETHEUS_DEPLOY="$RELEASE-prometheus-server"
PROMETHEUS_CONTAINER=prometheus-server
# The recovery prunes the oldest TSDB blocks until the volume is at most this full. Prometheus then
# starts with room for a fresh WAL, and its own size retention (values file) takes over from there.
TSDB_RECOVERY_TARGET_PERCENT="${TSDB_RECOVERY_TARGET_PERCENT:-60}"

remaining() { echo $(( BUDGET_SECONDS - SECONDS )); }
recovery() { echo "RECOVERY: $*"; }
# A workload's pod selector as `k=v,k=v` (the go-template's `$k`/`$v` are template variables).
selector_of() {
  local labels
  # shellcheck disable=SC2016
  labels=$(kubectl -n "$NS" get "$1" -o go-template='{{range $k, $v := .spec.selector.matchLabels}}{{$k}}={{$v}},{{end}}' 2>/dev/null)
  printf '%s' "${labels%,}"
}

helm repo add grafana https://grafana.github.io/helm-charts >/dev/null 2>&1 || true
helm repo update >/dev/null 2>&1

# ── 1. A release stuck in pending-* ─────────────────────────────────────────────────────────────
# helm marks the release pending-install/-upgrade/-rollback BEFORE it touches the cluster and
# deployed/failed AFTER. A run killed in between (the invoke cap, a cancelled job) leaves the
# pending revision behind forever, and helm refuses every later operation on the release. This
# script is the release's only writer, and it is never left running past the invoke that started
# it, so a pending revision seen at the START of a run is a dead run's — never a live one.
# Recovery is helm's own: roll back to the last `deployed` revision (no --wait: it only has to
# re-apply known-good manifests and record a fresh `deployed` revision), then upgrade as usual.
recover_pending_release() {
  local status last_deployed
  status=$(helm status "$RELEASE" -n "$NS" 2>/dev/null | awk -F': ' '$1 == "STATUS" { print $2; exit }')
  case "$status" in
    pending-install|pending-upgrade|pending-rollback) ;;
    *) return 0 ;;
  esac
  last_deployed=$(helm history "$RELEASE" -n "$NS" --max 1000 2>/dev/null \
    | awk 'NR > 1 { for (i = 2; i <= NF; i++) if ($i == "deployed") print $1 }' | tail -n 1)
  if [ -n "$last_deployed" ]; then
    recovery "release $RELEASE is stuck in '$status' (a run that was killed mid-operation)."
    recovery "rolling back to its last deployed revision $last_deployed, then upgrading as usual."
    if ! helm rollback "$RELEASE" "$last_deployed" -n "$NS" --timeout 2m; then
      echo "FATAL: release $RELEASE is stuck in '$status' and 'helm rollback $RELEASE $last_deployed' failed." >&2
      helm history "$RELEASE" -n "$NS" --max 5 >&2 || true
      exit 1
    fi
  elif [ "$status" = pending-install ]; then
    # Nothing was ever deployed: there is no known-good state to return to, only a half-made first
    # install. Removing it lets the install below start clean. StatefulSet volume claims (Loki's
    # store) survive an uninstall; the chart's own PVCs belonged to an install that never completed.
    recovery "release $RELEASE is stuck in 'pending-install' and was never deployed — uninstalling the half-made first install."
    if ! helm uninstall "$RELEASE" -n "$NS" --timeout 2m; then
      echo "FATAL: release $RELEASE is stuck in 'pending-install' and could not be uninstalled." >&2
      exit 1
    fi
  else
    echo "FATAL: release $RELEASE is stuck in '$status' and has no deployed revision to roll back to." >&2
    helm history "$RELEASE" -n "$NS" --max 10 >&2 || true
    exit 1
  fi
  recovery "release $RELEASE is $(helm status "$RELEASE" -n "$NS" 2>/dev/null | awk -F': ' '$1 == "STATUS" { print $2; exit }' || echo unknown) again."
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
# head chunks, leftover temporary blocks and, oldest first, whole blocks until the volume is at
# most TSDB_RECOVERY_TARGET_PERCENT full, and scales the server back.
tsdb_recovery_script() {
  cat <<'RECOVER'
set -eu
dir="${TSDB_DIR:-/data}"
target="${TARGET_PERCENT:-60}"
used() { df -P "$dir" | awk 'NR == 2 { sub("%", "", $5); print $5 }'; }
echo "tsdb-recovery: before: $(used)% of $dir used"
cd "$dir"
rm -rf wal chunks_head lock
for leftover in *.tmp*; do
  if [ -e "$leftover" ]; then echo "tsdb-recovery: removing temporary $leftover"; rm -rf "$leftover"; fi
done
echo "tsdb-recovery: removed the WAL and head chunks; now $(used)% used"
for block in $(ls -1 | grep -E '^[0-9A-HJKMNP-TV-Z]{26}$' | sort); do
  if [ "$(used)" -le "$target" ]; then break; fi
  echo "tsdb-recovery: removing oldest block $block"
  rm -rf "$block"
done
echo "tsdb-recovery: after: $(used)% of $dir used (target <= $target%)"
[ "$(used)" -le "$target" ] || { echo "tsdb-recovery: still above target — no block left to remove"; exit 1; }
RECOVER
}

recover_full_tsdb() {
  local selector pods pod restarts previous_log out_of_space=false image node_selector tolerations claim replicas
  kubectl -n "$NS" get deploy "$PROMETHEUS_DEPLOY" >/dev/null 2>&1 || return 0
  selector=$(selector_of "deploy/$PROMETHEUS_DEPLOY")
  [ -n "$selector" ] || return 0
  pods=$(kubectl -n "$NS" get pods -l "$selector" -o jsonpath='{.items[*].metadata.name}' 2>/dev/null)
  for pod in $pods; do
    restarts=$(kubectl -n "$NS" get pod "$pod" \
      -o "jsonpath={.status.containerStatuses[?(@.name==\"$PROMETHEUS_CONTAINER\")].restartCount}" 2>/dev/null)
    [ "${restarts:-0}" -gt 0 ] 2>/dev/null || continue
    # Captured, not piped into `grep -q`: under pipefail an early-exiting grep SIGPIPEs kubectl and
    # the pipeline reads as "no match" exactly when it matched.
    previous_log=$(kubectl -n "$NS" logs "$pod" -c "$PROMETHEUS_CONTAINER" --previous 2>/dev/null || true)
    if printf '%s\n' "$previous_log" | grep "no space left on device" >/dev/null; then
      out_of_space=true
      recovery "$pod has restarted $restarts times; its previous run died with 'no space left on device'."
    fi
  done
  [ "$out_of_space" = true ] || return 0

  image=$(kubectl -n "$NS" get deploy "$PROMETHEUS_DEPLOY" \
    -o "jsonpath={.spec.template.spec.containers[?(@.name==\"$PROMETHEUS_CONTAINER\")].image}")
  node_selector=$(kubectl -n "$NS" get deploy "$PROMETHEUS_DEPLOY" -o jsonpath='{.spec.template.spec.nodeSelector}')
  tolerations=$(kubectl -n "$NS" get deploy "$PROMETHEUS_DEPLOY" -o jsonpath='{.spec.template.spec.tolerations}')
  claim=$(kubectl -n "$NS" get deploy "$PROMETHEUS_DEPLOY" \
    -o jsonpath='{.spec.template.spec.volumes[?(@.name=="storage-volume")].persistentVolumeClaim.claimName}')
  replicas=$(kubectl -n "$NS" get deploy "$PROMETHEUS_DEPLOY" -o jsonpath='{.spec.replicas}')
  if [ -z "$image" ] || [ -z "$claim" ]; then
    echo "FATAL: Prometheus is out of space but its image or TSDB claim could not be read — not recovering blind." >&2
    exit 1
  fi
  recovery "the Prometheus TSDB on PVC $claim is full. Clearing the WAL and pruning the oldest blocks"
  recovery "to <= $TSDB_RECOVERY_TARGET_PERCENT% — DISPOSABLE METRICS ONLY: the last hours of samples and the oldest days go,"
  recovery "logs (Loki, its own volume) are untouched. Size retention takes over once Prometheus starts."

  [ -n "$node_selector" ] || node_selector='{}'
  [ -n "$tolerations" ] || tolerations='[]'
  # If this run is cut off between here and the scale-back below, the chart renders `replicas: 1`
  # for the server and the next run's helm upgrade restores it (three-way merge against live).
  kubectl -n "$NS" scale deploy "$PROMETHEUS_DEPLOY" --replicas=0 || { echo "FATAL: could not scale $PROMETHEUS_DEPLOY to 0." >&2; exit 1; }
  local waited=0
  while [ -n "$(kubectl -n "$NS" get pods -l "$selector" -o name 2>/dev/null)" ]; do
    if [ "$waited" -ge 180 ]; then
      echo "FATAL: the Prometheus server pod did not terminate in 180s; its disk cannot be attached elsewhere." >&2
      exit 1
    fi
    sleep 5; waited=$(( waited + 5 ))
  done

  local recovery_pod="$RELEASE-prometheus-tsdb-recovery" ok=true
  kubectl -n "$NS" delete pod "$recovery_pod" --ignore-not-found --wait=true >/dev/null 2>&1 || true
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
    tsdb_recovery_script | sed 's/^/          /'
    cat <<MANIFEST
      volumeMounts:
        - {name: storage, mountPath: /data}
  volumes:
    - name: storage
      persistentVolumeClaim: {claimName: $claim}
MANIFEST
  } | kubectl apply -f - || ok=false
  if [ "$ok" = true ]; then
    kubectl -n "$NS" wait --for=jsonpath='{.status.phase}'=Succeeded pod/"$recovery_pod" --timeout=240s || ok=false
  fi
  kubectl -n "$NS" logs "$recovery_pod" 2>&1 | sed 's/^/RECOVERY: /'
  kubectl -n "$NS" delete pod "$recovery_pod" --ignore-not-found --wait=false >/dev/null 2>&1 || true
  kubectl -n "$NS" scale deploy "$PROMETHEUS_DEPLOY" --replicas="${replicas:-1}" \
    || { echo "FATAL: could not scale $PROMETHEUS_DEPLOY back to ${replicas:-1}." >&2; exit 1; }
  if [ "$ok" != true ]; then
    echo "FATAL: the Prometheus TSDB recovery pod did not succeed — the volume is still full (log above)." >&2
    kubectl -n "$NS" describe pod "$recovery_pod" >&2 2>/dev/null || true
    exit 1
  fi
  recovery "the Prometheus TSDB has room again; $PROMETHEUS_DEPLOY scaled back to ${replicas:-1}."
}

recover_pending_release
recover_full_tsdb

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
if ! helm upgrade --install "$RELEASE" grafana/loki-stack --version "$CHART_VERSION" -n "$NS" --create-namespace \
  -f "$VALUES" \
  --set grafana.enabled=true --set prometheus.enabled=true \
  --set grafana.adminPassword="$GRAFANA_PW" --set grafana.service.type=ClusterIP \
  --timeout 3m
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
workloads=$(kubectl -n "$NS" get deploy,sts,ds -l "app.kubernetes.io/instance=$RELEASE" -o name 2>/dev/null
            kubectl -n "$NS" get deploy,sts,ds -l "release=$RELEASE" -o name 2>/dev/null)
workloads=$(printf '%s\n' "$workloads" | sed '/^$/d' | sort -u)
if [ -z "$workloads" ]; then
  echo "FATAL: helm reported success but no workload of release $RELEASE exists in $NS." >&2
  exit 1
fi
for workload in $workloads; do
  left=$(( $(remaining) - 30 ))
  [ "$left" -lt 10 ] && left=10
  if ! kubectl -n "$NS" rollout status "$workload" --timeout="${left}s"; then
    not_ready+=("$workload")
  fi
done

# Verify the RUNNING pod, not just the desired StatefulSet: an orphan left over from
# the immutable-volume transition can still be serving from emptyDir (#3773).
if ! storage_volume=$(kubectl -n "$NS" get pod loki-0 -o jsonpath='{.spec.containers[?(@.name=="loki")].volumeMounts[?(@.mountPath=="/data")].name}') \
  || [ -z "$storage_volume" ]; then
  echo "FATAL: cannot identify Loki's running /data volume." >&2
  exit 1
fi
if ! storage_claim=$(kubectl -n "$NS" get pod loki-0 -o "jsonpath={.spec.volumes[?(@.name==\"$storage_volume\")].persistentVolumeClaim.claimName}") \
  || [ -z "$storage_claim" ]; then
  echo "FATAL: Loki's running /data volume is not backed by a PVC; log durability is NOT established." >&2
  exit 1
fi
if ! storage_phase=$(kubectl -n "$NS" get pvc "$storage_claim" -o jsonpath='{.status.phase}') \
  || [ "$storage_phase" != Bound ]; then
  echo "FATAL: Loki's storage PVC is not Bound; log durability is NOT established." >&2
  exit 1
fi

echo
echo "Verified: Loki's running /data volume uses Bound PVC $storage_claim."
kubectl -n "$NS" get pods

if [ ${#not_ready[@]} -gt 0 ]; then
  echo
  echo "NOT READY within the ${BUDGET_SECONDS}s budget: ${not_ready[*]}" >&2
  for workload in "${not_ready[@]}"; do
    echo "── $workload ──" >&2
    kubectl -n "$NS" describe "$workload" 2>&1 | tail -n 25 >&2
    selector=$(selector_of "$workload")
    for pod in $(kubectl -n "$NS" get pods -l "$selector" -o jsonpath='{.items[*].metadata.name}' 2>/dev/null); do
      echo "── pod $pod: events ──" >&2
      kubectl -n "$NS" get events --field-selector "involvedObject.name=$pod" 2>&1 | tail -n 10 >&2
      echo "── pod $pod: previous log (tail) ──" >&2
      kubectl -n "$NS" logs "$pod" --all-containers --previous --tail=20 >&2 2>&1 || true
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
