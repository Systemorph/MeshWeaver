#!/usr/bin/env bash
# Import Grafana dashboard JSON files into the in-cluster Grafana (loki-stack chart,
# `monitoring` namespace). Each file under deploy/aks/dashboards/ is already in the
# Grafana `/api/dashboards/db` payload shape ({"dashboard":{...},"overwrite":true,...}),
# so this just POSTs every *.json in the working directory. Idempotent (overwrite:true).
#
# Grafana is private (ClusterIP), so run this INSIDE the cluster via command-invoke,
# uploading the script + the dashboards alongside it:
#
#   az aks command invoke -g memex-aks-rg -n memexaks-cluster \
#     --command "bash import-dashboards.sh" \
#     --file deploy/aks/scripts/import-dashboards.sh \
#     --file deploy/aks/dashboards/<dashboard>.json
#
# The admin password is read from the chart's `loki-grafana` secret; no creds to pass.
set -euo pipefail

PW=$(kubectl -n monitoring get secret loki-grafana -o jsonpath='{.data.admin-password}' | base64 -d)
G=${GRAFANA_URL:-http://loki-grafana.monitoring.svc.cluster.local}

shopt -s nullglob
files=(*.json)
if [ ${#files[@]} -eq 0 ]; then
  echo "no dashboard *.json found in $(pwd)"; exit 1
fi

for f in "${files[@]}"; do
  code=$(curl -s -o /tmp/grafana-resp -w '%{http_code}' -u "admin:$PW" \
    -H 'Content-Type: application/json' \
    -X POST "$G/api/dashboards/db" --data-binary @"$f")
  echo "$f -> HTTP $code  $(cat /tmp/grafana-resp)"
done
