#!/usr/bin/env bash
# Issue the Let's Encrypt cert for memex.meshweaver.cloud on the memex-cloud
# ingress (reuses the cluster-wide cert-manager + letsencrypt-prod issuer).
# Run ONLY AFTER memex.meshweaver.cloud's DNS A-record points to the AKS ingress
# IP (HTTP-01 validates over the internet) — i.e. at/after the DNS cutover.
#   az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "bash tls.sh" --file tls.sh
set -uo pipefail
NS=memex-cloud
HOST="${INGRESS_HOST:-memex.meshweaver.cloud}"
kubectl -n "$NS" annotate ingress memex-portal cert-manager.io/cluster-issuer=letsencrypt-prod --overwrite
kubectl -n "$NS" patch ingress memex-portal --type=json \
  -p "[{\"op\":\"add\",\"path\":\"/spec/tls\",\"value\":[{\"hosts\":[\"${HOST}\"],\"secretName\":\"memexcloud-tls\"}]}]" || true
echo "=== memexcloud cert issuing (HTTP-01); watch: kubectl -n $NS get certificate memexcloud-tls -w ==="
