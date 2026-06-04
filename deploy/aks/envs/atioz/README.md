# `atioz.meshweaver.cloud` — AKS environment

A **second portal instance** on the **same** AKS cluster as `memex.systemorph.com`
(`memexaks-cluster` / `memex-aks-rg`, swedencentral), isolated in its own
`atioz` namespace. This folder is the env overlay; the platform (cluster, ingress
controller, Postgres server, Key Vault, ACR) is shared with the systemorph env
and defined under [`../../`](../../) ([DEPLOY-RUNBOOK.md](../../DEPLOY-RUNBOOK.md)).

## Shared vs. separate

| Resource | atioz |
|---|---|
| AKS cluster | **shared** — `memexaks-cluster` / `memex-aks-rg` |
| Ingress controller | **shared** — AKS app-routing nginx (one public LB IP) |
| Postgres server | **shared** — `memexaks-pg` Flexible Server |
| Key Vault | **shared** — `Systemorph` (via the CSI add-on identity) |
| ACR + portal image | **shared** — `meshweaver.azurecr.io/memex-portal-ai` |
| AI backend | **shared** — AzureFoundry/DeepSeek on `s-meshweaver` |
| Namespace | **own** — `atioz` |
| Database | **own** — `atioz` (on the shared PG server) |
| Public host | **own** — `atioz.meshweaver.cloud` |
| TLS cert | **own** — `atioz-tls` (Let's Encrypt) |
| Encryption master key | **own** — `atioz-Ai-KeyProtection-MasterKey` in the KV |
| OAuth / sign-in | **own** — dedicated Entra app in the Systemorph tenant |

## Files

| File | Purpose |
|---|---|
| `values.atioz.yaml` | Helm overlay (host, db, AI, ingress, resources) over `../../../helm/values.yaml` |
| `portal-pvcs.yaml` | RWX Azure Files PVCs in ns `atioz` (no pg PVC — uses the Flexible Server) |
| `portal-ingress.yaml` | Ingress for `atioz.meshweaver.cloud` (cookie affinity) |
| `portal-patch.json` | JSON-6902 patch: binds PVCs + the KV CSI secret mount + `envFrom` onto the chart Deployment |
| `secretproviderclass.yaml` | KV → K8s secret sync (shared AI key + atioz master key + atioz OAuth secret) |
| `deploy.sh` | One-shot deploy into ns `atioz` (helm + PVCs + SPC + patch + conn string + ingress) |
| `tls.sh` | Issue `atioz-tls` via the shared cert-manager / letsencrypt-prod issuer |
| `values.deploy.example.yaml` | Secrets template — only needed when NOT using the KV CSI path |

## One-time provisioning (control-plane; done by setup)

```bash
RG=memex-aks-rg; PG=memexaks-pg; KV=Systemorph
# 1. Database on the shared PG server
az postgres flexible-server db create -g $RG -s $PG -d atioz
# 2. Entra app (Systemorph tenant) for sign-in
az ad app create --display-name "Atioz Portal (atioz.meshweaver.cloud)" \
  --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://atioz.meshweaver.cloud/signin-microsoft"
az ad app credential reset --id <appId> --display-name atioz --years 1   # -> client secret
# 3. KV secrets (master key is fresh — atioz db starts empty)
az keyvault secret set --vault-name $KV --name atioz-Ai-KeyProtection-MasterKey \
  --value "$(openssl rand -base64 32)"
az keyvault secret set --vault-name $KV --name atioz-Authentication-Microsoft-ClientSecret \
  --value "<entra-client-secret>"
# 4. DNS A-record -> the SAME app-routing nginx LB IP as memex.systemorph.com
az network dns record-set a add-record -g dns -z meshweaver.cloud -n atioz \
  --ipv4-address <ingress-ip> --ttl 300
```

## Deploy / redeploy

```bash
# Stage: this folder + a copy of the chart, then run via the cluster (private API).
STAGE=$(mktemp -d); cp -r . "$STAGE"/; cp -r ../../../helm "$STAGE"/helm
export MEMEX_PG_CONN='Host=<PG_PRIVATE_IP>;Port=5432;Username=memexadmin;Password=<PW>;Database=atioz;SslMode=Require;Trust Server Certificate=true'
export IMAGE_TAG=<sha-or-latest>
cd "$STAGE"
az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command "MEMEX_PG_CONN='$MEMEX_PG_CONN' IMAGE_TAG='$IMAGE_TAG' bash deploy.sh" --file .
# First time only — issue the cert AFTER the DNS A-record resolves publicly:
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "bash tls.sh" --file tls.sh
```

Verify: `curl -sS -o /dev/null -w "%{http_code}\n" https://atioz.meshweaver.cloud/`.

> The atioz namespace is independent of `memex` — a failed atioz rollout cannot
> affect the running `memex.systemorph.com` portal (separate namespace, database,
> ingress host, and TLS secret; only additive on the shared platform).
