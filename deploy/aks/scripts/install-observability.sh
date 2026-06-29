#!/usr/bin/env bash
# Observability stack for the AKS deployment: Grafana + Loki + Promtail + Prometheus
# (the grafana/loki-stack chart — Promtail ships every pod's stdout into Loki, datasources
# auto-wired in Grafana). Run via az aks command invoke on the private cluster:
#
#   export GRAFANA_PW='<pick a strong password>'
#   az aks command invoke -g memex-aks-rg -n memexaks-cluster \
#     --command "GRAFANA_PW=$GRAFANA_PW bash install-observability.sh" --file install-observability.sh
set -uo pipefail
: "${GRAFANA_PW:?set GRAFANA_PW (Grafana admin password)}"
helm repo add grafana https://grafana.github.io/helm-charts >/dev/null 2>&1 || true
helm repo update >/dev/null 2>&1
helm upgrade --install loki grafana/loki-stack -n monitoring --create-namespace \
  --set grafana.enabled=true --set prometheus.enabled=true \
  --set grafana.adminPassword="$GRAFANA_PW" --set grafana.service.type=ClusterIP \
  --wait --timeout 10m
kubectl -n monitoring get pods
echo
echo "Access (private cluster -> via the P2S VPN):"
echo "  az aks get-credentials -g memex-aks-rg -n memexaks-cluster"
echo "  kubectl -n monitoring port-forward svc/loki-grafana 3000:80"
echo "  open http://localhost:3000   (user: admin / pass: \$GRAFANA_PW)"
echo "Loki datasource is pre-wired; query e.g.  {namespace=\"memex\"}  in Grafana Explore."
