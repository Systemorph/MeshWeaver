#!/usr/bin/env bash
# Deploy the Memex portal + migration onto the (private) AKS cluster.
#
# Run from a STAGING dir that contains: this script, aks-extras.yaml, values.deploy.yaml
# (your secrets — see values.deploy.example.yaml), a copy of the Helm chart as ./helm, and a
# copy of ../values.aks.yaml. Then, from a machine with `az`:
#
#   cd <staging>
#   cp -r <repo>/deploy/helm ./helm
#   cp <repo>/deploy/aks/values.aks.yaml .
#   export MEMEX_PG_CONN='Host=<pg-server>.postgres.database.azure.com;Port=5432;Username=memexadmin;Password=<PW>;Database=memex;SslMode=Require;Trust Server Certificate=true'
#   az aks command invoke -g <rg> -n <cluster> --command "MEMEX_PG_CONN='$MEMEX_PG_CONN' bash deploy.sh" --file .
set -uo pipefail
NS=memex
ACR=meshweaver.azurecr.io
: "${MEMEX_PG_CONN:?set MEMEX_PG_CONN to the Flexible Server connection string}"

kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -f ./aks-extras.yaml                                   # StorageClass + RWX PVCs
helm upgrade --install memex ./helm -f ./helm/values.yaml -f ./values.aks.yaml -f ./values.deploy.yaml -n "$NS"

# External managed Postgres -> don't run the chart's in-cluster pg.
kubectl -n "$NS" scale statefulset memex-postgres-statefulset --replicas=0 || true
# The chart hardcodes the ghcr image path -> repoint to the shared ACR.
# 🚨 The PORTAL only. The migration is a run-once Job (deploy/helm/templates/memex-migration/job.yaml),
# created fresh by the helm upgrade above with the image from .Values.migration.image — there is no
# migration Deployment to set an image on. The two lines that used to do that here named an object
# the chart does not define: one was swallowed by `|| true`, the other printed an error on every
# documented deploy (#1788). Override the Job's image through the chart instead:
#   helm upgrade ... --set migration.image="$ACR/memex-migration:latest"
kubectl -n "$NS" set image deployment/memex-portal-deployment    memex-portal="$ACR/memex-portal-ai:latest"
# 🚨 NO replica patch here. The chart OMITS spec.replicas whenever a ScaledObject exists
# (deploy/helm/templates/memex-portal/deployment.yaml), and values.aks.yaml sets keda.enabled: true
# with minReplicas: 2 - so KEDA owns the count. This script used to
# `kubectl patch ... /spec/replicas 1` right afterwards, which re-created BY HAND exactly the
# helm-vs-HPA contradiction that check-chart-invariants.sh invariant #1 exists to catch, and which
# produced the 2026-08-14 production 503 (three files asking for HA, one line vetoing it). Change
# the floor in values.aks.yaml (`keda.minReplicas`) instead - never with a post-helm patch.
#
# The PVC volumes/mounts (data, users, content) render from the CHART too (persistence: in
# values.aks.yaml) - no volume patching here either: the old bolt-on patch meant any plain
# `helm upgrade` reverted /data to emptyDir until the patch re-ran, wiping the
# assembly/nuget/DataProtection caches on every restart in the gap.
# Chart-gen gap: the secret template hardcodes the in-cluster pg connection string -> repoint
# both portal + migration at the external Flexible Server (server FQDN + password + SSL).
for s in memex-portal-secrets memex-migration-secrets; do
  kubectl -n "$NS" patch secret "$s" --type merge -p "{\"stringData\":{\"ConnectionStrings__memex\":\"${MEMEX_PG_CONN}\"}}"
done
kubectl -n "$NS" rollout restart deployment/memex-portal-deployment
echo "=== deployed ==="; kubectl -n "$NS" get deploy,pvc,svc -o wide

# Observability (opt-in, folded into the standard deploy): set GRAFANA_PW to also bring up
# Grafana + Loki + Promtail + Prometheus (Promtail scrapes every pod's stdout into Loki, so the
# portal logs flow with no portal-side config). Stays private — reach it via the P2S VPN +
# `kubectl port-forward` (see README.md, "Grafana + Loki + Prometheus"). Non-fatal: a monitoring failure must not
# fail the app deploy.
if [ -n "${GRAFANA_PW:-}" ] && [ -f ./install-observability.sh ]; then
  echo "=== observability (GRAFANA_PW set) ==="
  GRAFANA_PW="$GRAFANA_PW" bash ./install-observability.sh || echo "WARN: observability install failed (non-fatal)"
fi
