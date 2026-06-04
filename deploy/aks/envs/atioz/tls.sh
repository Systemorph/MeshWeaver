#!/usr/bin/env bash
# Issue the Let's Encrypt TLS cert for atioz.meshweaver.cloud on the atioz portal
# ingress (HTTP-01 via the AKS app-routing nginx). Reuses the cluster-wide
# cert-manager + letsencrypt-prod ClusterIssuer already installed for the
# systemorph env — so this only annotates/patches the atioz ingress.
#
# Prereq: the `memex-portal` ingress exists in ns atioz (deploy.sh) AND
# atioz.meshweaver.cloud already resolves publicly to the ingress IP (Let's
# Encrypt validates over the internet — create the DNS A-record first).
#
#   az aks command invoke -g memex-aks-rg -n memexaks-cluster \
#     --command "bash tls.sh" --file tls.sh
set -uo pipefail
NS=atioz
HOST="${INGRESS_HOST:-atioz.meshweaver.cloud}"

# If cert-manager / the ClusterIssuer isn't present yet (fresh cluster), install it.
if ! kubectl get clusterissuer letsencrypt-prod >/dev/null 2>&1; then
  : "${LE_EMAIL:?cert-manager/letsencrypt-prod not found; set LE_EMAIL to bootstrap it}"
  helm repo add jetstack https://charts.jetstack.io >/dev/null 2>&1 || true
  helm repo update >/dev/null 2>&1
  helm upgrade --install cert-manager jetstack/cert-manager -n cert-manager --create-namespace \
    --set crds.enabled=true --wait --timeout 5m
  cat <<EOF | kubectl apply -f -
apiVersion: cert-manager.io/v1
kind: ClusterIssuer
metadata: { name: letsencrypt-prod }
spec:
  acme:
    server: https://acme-v02.api.letsencrypt.org/directory
    email: ${LE_EMAIL}
    privateKeySecretRef: { name: letsencrypt-prod-account }
    solvers:
      - http01:
          ingress:
            ingressClassName: webapprouting.kubernetes.azure.com
EOF
fi

kubectl -n "$NS" annotate ingress memex-portal cert-manager.io/cluster-issuer=letsencrypt-prod --overwrite
kubectl -n "$NS" patch ingress memex-portal --type=json \
  -p "[{\"op\":\"add\",\"path\":\"/spec/tls\",\"value\":[{\"hosts\":[\"${HOST}\"],\"secretName\":\"atioz-tls\"}]}]" || true
echo "=== atioz cert issuing (HTTP-01); watch: kubectl -n $NS get certificate atioz-tls -w ==="
