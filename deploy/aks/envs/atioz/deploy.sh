#!/usr/bin/env bash
# Deploy the atioz.meshweaver.cloud portal onto the SHARED AKS cluster
# (memexaks-cluster / memex-aks-rg), in its own `atioz` namespace, against the
# shared Postgres Flexible Server (database `atioz`).
#
# Run from a STAGING dir containing: this script, values.atioz.yaml,
# values.deploy.yaml (your non-KV secrets, if any), portal-pvcs.yaml,
# portal-ingress.yaml, secretproviderclass.yaml, portal-patch.yaml, and a copy
# of the Helm chart as ./helm. Then, from a machine with `az`:
#
#   cd <staging>
#   cp -r <repo>/deploy/helm ./helm
#   export MEMEX_PG_CONN='Host=<PG_PRIVATE_IP>;Port=5432;Username=memexadmin;Password=<PW>;Database=atioz;SslMode=Require;Trust Server Certificate=true'
#   export IMAGE_TAG=<sha-or-latest>     # optional, defaults to latest
#   az aks command invoke -g memex-aks-rg -n memexaks-cluster \
#     --command "MEMEX_PG_CONN='$MEMEX_PG_CONN' IMAGE_TAG='$IMAGE_TAG' bash deploy.sh" --file .
set -uo pipefail
NS=atioz
RELEASE=atioz
ACR=meshweaver.azurecr.io
IMAGE_TAG="${IMAGE_TAG:-latest}"
: "${MEMEX_PG_CONN:?set MEMEX_PG_CONN to the Flexible Server connection string for the atioz database}"

kubectl create namespace "$NS" --dry-run=client -o yaml | kubectl apply -f -

# RWX PVCs (the azurefile-memex StorageClass is cluster-scoped and already exists).
kubectl apply -f ./portal-pvcs.yaml

# Helm release into the atioz namespace.
helm upgrade --install "$RELEASE" ./helm \
  -f ./helm/values.yaml -f ./values.atioz.yaml \
  $([ -f ./values.deploy.yaml ] && echo "-f ./values.deploy.yaml") \
  -n "$NS"

# atioz uses the managed Flexible Server -> don't run the chart's in-cluster pg.
kubectl -n "$NS" scale statefulset memex-postgres-statefulset --replicas=0 || true

# Chart hardcodes the ghcr image path -> repoint to the shared ACR + pinned tag.
kubectl -n "$NS" set image deployment/memex-portal-deployment    memex-portal="$ACR/memex-portal-ai:$IMAGE_TAG"
kubectl -n "$NS" set image deployment/memex-migration-deployment memex-migration="$ACR/memex-migration:$IMAGE_TAG" || true

# Key Vault CSI secrets for atioz.
kubectl apply -f ./secretproviderclass.yaml

# Bind the RWX PVCs + the KV CSI volume + envFrom the synced secret. JSON-6902 patch:
# the chart declares /data + /mnt/users as emptyDir, so we REPLACE those volume objects
# with PVCs (a strategic merge leaves the emptyDir in place -> "more than 1 volume type").
kubectl -n "$NS" patch deployment memex-portal-deployment --type=json --patch-file ./portal-patch.json

# Repoint both portal + migration at the external Flexible Server (db `atioz`).
for s in memex-portal-secrets memex-migration-secrets; do
  kubectl -n "$NS" patch secret "$s" --type merge -p "{\"stringData\":{\"ConnectionStrings__memex\":\"${MEMEX_PG_CONN}\"}}" || true
done

# Ingress for atioz.meshweaver.cloud (TLS secret atioz-tls is issued separately by tls.sh).
kubectl apply -f ./portal-ingress.yaml

kubectl -n "$NS" rollout restart deployment/memex-portal-deployment deployment/memex-migration-deployment || true
echo "=== atioz deployed ==="; kubectl -n "$NS" get deploy,pvc,svc,ingress -o wide
