#!/usr/bin/env bash
# Install cert-manager + a Let's Encrypt ClusterIssuer and enable TLS on the portal ingress
# (HTTP-01 via the app-routing nginx). The ingress 'memex-portal' must already exist and the
# host must resolve publicly to the ingress IP (Let's Encrypt validates over the internet).
#
#   export LE_EMAIL=you@example.com INGRESS_HOST=memex.systemorph.com
#   az aks command invoke -g <rg> -n <cluster> --command "LE_EMAIL=$LE_EMAIL INGRESS_HOST=$INGRESS_HOST bash tls.sh" --file tls.sh
set -uo pipefail
NS=memex
: "${LE_EMAIL:?set LE_EMAIL for the Let's Encrypt account}"
HOST="${INGRESS_HOST:-memex.systemorph.com}"

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

kubectl -n "$NS" annotate ingress memex-portal cert-manager.io/cluster-issuer=letsencrypt-prod --overwrite
kubectl -n "$NS" patch ingress memex-portal --type=json \
  -p "[{\"op\":\"add\",\"path\":\"/spec/tls\",\"value\":[{\"hosts\":[\"${HOST}\"],\"secretName\":\"memex-tls\"}]}]"
echo "=== cert issuing (HTTP-01); watch: kubectl -n $NS get certificate memex-tls -w ==="
