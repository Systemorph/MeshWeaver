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
# 🚨 values.observability.yaml is NOT optional. On chart defaults Loki is BestEffort with an
# emptyDir store and no nodeSelector — i.e. the first pod evicted when a workload balloons, on
# the same node pool as that workload, losing its ENTIRE store when it goes. That is not a
# hypothetical: it is how the 2026-08-12 memex-cloud incident window was destroyed, taking the
# red-log ticketing (mw-log-watcher reads FROM Loki) blind with it. Read that file's header.
set -uo pipefail
: "${GRAFANA_PW:?set GRAFANA_PW (Grafana admin password)}"
VALUES="${VALUES:-values.observability.yaml}"
[ -f "$VALUES" ] || { echo "FATAL: $VALUES not found — pass it with --file (see header)." >&2; exit 1; }
helm repo add grafana https://grafana.github.io/helm-charts >/dev/null 2>&1 || true
helm repo update >/dev/null 2>&1

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
if ! helm upgrade --install loki grafana/loki-stack -n monitoring --create-namespace \
  -f "$VALUES" \
  --set grafana.enabled=true --set prometheus.enabled=true \
  --set grafana.adminPassword="$GRAFANA_PW" --set grafana.service.type=ClusterIP \
  --wait --timeout 10m
then
  echo "FATAL: helm upgrade failed — the stack was NOT installed/updated." >&2
  echo "If it complains about immutable StatefulSet fields, that is the persistence change:" >&2
  echo "  kubectl -n monitoring delete sts loki --cascade=orphan   # then re-run, then delete pod loki-0" >&2
  exit 1
fi

echo
echo "Verify the three things chart defaults get wrong (all must be non-empty / true):"
echo "  kubectl -n monitoring get pod loki-0 -o jsonpath='{.status.qosClass} {.spec.nodeName}'   # NOT BestEffort, NOT a silos node"
echo "  kubectl -n monitoring get pvc | grep storage-loki-0                                      # the store is durable"
echo "  kubectl -n monitoring get sts loki -o jsonpath='{.spec.template.spec.nodeSelector}'      # agentpool: system"
kubectl -n monitoring get pods
echo
echo "Access (private cluster -> via the P2S VPN):"
echo "  az aks get-credentials -g <aks-resource-group> -n <aks-cluster>"
echo "  kubectl -n monitoring port-forward svc/loki-grafana 3000:80"
echo "  open http://localhost:3000   (user: admin / pass: \$GRAFANA_PW)"
echo "Loki datasource is pre-wired; query e.g.  {namespace=\"memex\"}  in Grafana Explore."
