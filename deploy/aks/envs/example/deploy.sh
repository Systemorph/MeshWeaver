#!/usr/bin/env bash
# Deploy the migrated portal.example.com portal onto the shared AKS cluster,
# namespace `example`, on the D16s_v5 `silos` pool, against the `exampledb`
# database (already loaded with the prod data — see migrate-db.sh).
#
# The db-migration DOES run here, as the chart's run-once Job, and that is correct: the data was
# restored from prod at its current schema version, so phase 1 (idempotent schema init) re-applies
# cleanly and phase 2 (versioned repairs, gated on admin.mesh_nodes.db_version) fast-forwards
# without repeating anything. Stage like the prod env:
#   STAGE with this script + values.exampledb.yaml + portal-pvcs.yaml +
#   portal-ingress.yaml + secretproviderclass.yaml + portal-patch.json + ./helm
#   export MEMEX_PG_CONN='Host=<pg-server>.postgres.database.azure.com;...;Database=exampledb;SslMode=Require;Trust Server Certificate=true'
#   export IMAGE_TAG=<sha>
#   az aks command invoke -g <aks-resource-group> -n <aks-cluster> \
#     --command "MEMEX_PG_CONN='$MEMEX_PG_CONN' IMAGE_TAG='$IMAGE_TAG' bash deploy.sh" --file .
set -uo pipefail
NS=example
RELEASE=exampledb
ACR=meshweaver.azurecr.io
IMAGE_TAG="${IMAGE_TAG:-latest}"
: "${MEMEX_PG_CONN:?set MEMEX_PG_CONN to the Flexible Server connection string for the exampledb database}"

kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -f ./portal-pvcs.yaml

helm upgrade --install "$RELEASE" ./helm \
  -f ./helm/values.yaml -f ./values.exampledb.yaml -n "$NS"

# Use the shared Flexible Server (`exampledb`) — don't run the chart's in-cluster pg.
kubectl -n "$NS" scale statefulset memex-postgres-statefulset --replicas=0 || true
# 🚨 The migration is NOT scaled away here any more, and it does not need to be. It is a run-once
# Job (deploy/helm/templates/memex-migration/job.yaml), so there is no Deployment to scale to zero —
# the line that tried named an object the chart does not define (#1788). Running it against a
# restored prod database is safe AND is what you want: phase 1 (schema init) is idempotent and
# always runs, and phase 2 (versioned repairs) is gated on admin.mesh_nodes.db_version, so a DB
# restored at the current version fast-forwards without repeating anything. See
# src/Memex.Database.Migration/Program.cs in MeshWeaver.Plugins (the migration worker moved there with the hosts).

kubectl -n "$NS" set image deployment/memex-portal-deployment memex-portal="$ACR/memex-portal-ai:$IMAGE_TAG"

kubectl apply -f ./secretproviderclass.yaml
kubectl -n "$NS" patch deployment memex-portal-deployment --type=json --patch-file ./portal-patch.json

kubectl -n "$NS" patch secret memex-portal-secrets --type merge \
  -p "{\"stringData\":{\"ConnectionStrings__memex\":\"${MEMEX_PG_CONN}\"}}" || true

kubectl apply -f ./portal-ingress.yaml
kubectl -n "$NS" rollout restart deployment/memex-portal-deployment || true
echo "=== example deployed ==="; kubectl -n "$NS" get deploy,pvc,svc,ingress -o wide
