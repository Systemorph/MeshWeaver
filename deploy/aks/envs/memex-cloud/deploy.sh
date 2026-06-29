#!/usr/bin/env bash
# Deploy the migrated memex.meshweaver.cloud portal onto the shared AKS cluster,
# namespace `memex-cloud`, on the D16s_v5 `silos` pool, against the `memexcloud`
# database (already loaded with the prod data — see migrate-db.sh).
#
# The db-migration is NOT run here: the data was restored from prod at its
# current schema version, so we deploy against it as-is (run the migration
# deliberately only if a schema delta is needed). Stage like the atioz env:
#   STAGE with this script + values.memexcloud.yaml + portal-pvcs.yaml +
#   portal-ingress.yaml + secretproviderclass.yaml + portal-patch.json + ./helm
#   export MEMEX_PG_CONN='Host=10.42.18.4;...;Database=memexcloud;SslMode=Require;Trust Server Certificate=true'
#   export IMAGE_TAG=<sha>
#   az aks command invoke -g memex-aks-rg -n memexaks-cluster \
#     --command "MEMEX_PG_CONN='$MEMEX_PG_CONN' IMAGE_TAG='$IMAGE_TAG' bash deploy.sh" --file .
set -uo pipefail
NS=memex-cloud
RELEASE=memexcloud
ACR=meshweaver.azurecr.io
IMAGE_TAG="${IMAGE_TAG:-latest}"
: "${MEMEX_PG_CONN:?set MEMEX_PG_CONN to the Flexible Server connection string for the memexcloud database}"

kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -
kubectl apply -f ./portal-pvcs.yaml

helm upgrade --install "$RELEASE" ./helm \
  -f ./helm/values.yaml -f ./values.memexcloud.yaml -n "$NS"

# Use the shared Flexible Server (`memexcloud`) — don't run the chart's in-cluster
# pg, and DON'T run the migration (data already restored from prod).
kubectl -n "$NS" scale statefulset memex-postgres-statefulset --replicas=0 || true
kubectl -n "$NS" scale deployment  memex-migration-deployment --replicas=0 || true

kubectl -n "$NS" set image deployment/memex-portal-deployment memex-portal="$ACR/memex-portal-ai:$IMAGE_TAG"

kubectl apply -f ./secretproviderclass.yaml
kubectl -n "$NS" patch deployment memex-portal-deployment --type=json --patch-file ./portal-patch.json

kubectl -n "$NS" patch secret memex-portal-secrets --type merge \
  -p "{\"stringData\":{\"ConnectionStrings__memex\":\"${MEMEX_PG_CONN}\"}}" || true

kubectl apply -f ./portal-ingress.yaml
kubectl -n "$NS" rollout restart deployment/memex-portal-deployment || true
echo "=== memex-cloud deployed ==="; kubectl -n "$NS" get deploy,pvc,svc,ingress -o wide
