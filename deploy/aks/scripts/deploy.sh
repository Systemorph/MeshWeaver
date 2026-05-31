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
#   export MEMEX_PG_CONN='Host=<PG_PRIVATE_IP>;Port=5432;Username=memexadmin;Password=<PW>;Database=memex;SslMode=Require;Trust Server Certificate=true'
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
kubectl -n "$NS" set image deployment/memex-portal-deployment    memex-portal="$ACR/memex-portal-ai:latest"
kubectl -n "$NS" set image deployment/memex-migration-deployment memex-migration="$ACR/memex-migration:latest" || true
# 1-replica baseline + mount the Azure Files PVCs (/data already mounted by the chart; add /mnt/content).
kubectl -n "$NS" patch deployment memex-portal-deployment --type=json -p '[{"op":"replace","path":"/spec/replicas","value":1},{"op":"replace","path":"/spec/template/spec/volumes/0","value":{"name":"memex-data","persistentVolumeClaim":{"claimName":"memex-data"}}},{"op":"add","path":"/spec/template/spec/volumes/-","value":{"name":"memex-content","persistentVolumeClaim":{"claimName":"memex-content"}}},{"op":"add","path":"/spec/template/spec/containers/0/volumeMounts/-","value":{"name":"memex-content","mountPath":"/mnt/content"}}]'
# Chart-gen gap: the secret template hardcodes the in-cluster pg connection string -> repoint
# both portal + migration at the external Flexible Server (private IP + password + SSL).
for s in memex-portal-secrets memex-migration-secrets; do
  kubectl -n "$NS" patch secret "$s" --type merge -p "{\"stringData\":{\"ConnectionStrings__memex\":\"${MEMEX_PG_CONN}\"}}"
done
kubectl -n "$NS" rollout restart deployment/memex-portal-deployment deployment/memex-migration-deployment
echo "=== deployed ==="; kubectl -n "$NS" get deploy,pvc,svc -o wide
