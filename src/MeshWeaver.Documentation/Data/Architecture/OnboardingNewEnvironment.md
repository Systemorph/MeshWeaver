---
Name: Onboarding a New Environment
Description: "Stand up a new Memex portal environment (own domain, database, sign-in) on the shared AKS cluster: what's shared vs separate, the scaffold + provisioning steps, sign-in/invitation/email wiring, and the hard-won gotchas (chart config pass-through, empty int/bool config, CSI secret envFrom order)."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>
Category: Architecture
---

# Onboarding a New Environment

A "new environment" is an additional Memex portal — its own domain, database, and
sign-in — running on the **shared AKS cluster** (`memexaks-cluster` / `memex-aks-rg`,
swedencentral). `atioz.meshweaver.cloud` and the migrated `memex.meshweaver.cloud`
are the worked examples; both live under [`deploy/aks/envs/<env>/`](../../../../deploy/aks/envs).
The shared platform (cluster, ingress, Postgres server, Key Vault, ACR) is brought up
once — see the [AKS deploy runbook](../../../../deploy/aks/DEPLOY-RUNBOOK.md); this guide
adds an environment on top of it.

## Shared vs. separate

| Resource | Shared across envs | Separate per env |
|---|---|---|
| AKS cluster + node pools | ✅ `memexaks-cluster` | |
| Ingress controller (app-routing nginx, one public IP) | ✅ | |
| Postgres **server** | ✅ `memexaks-pg` | **database** (`atioz`, `memexcloud`, …) |
| Key Vault | ✅ `Systemorph` | secret **names** (`<env>-*`) + **master key** |
| ACR + portal image | ✅ `meshweaver.azurecr.io/memex-portal-ai` | image **tag** (a commit sha) |
| Kubernetes namespace | | ✅ `<env>` |
| Public host + TLS cert | | ✅ `<host>` + `<env>-tls` |
| Entra app (sign-in) | | ✅ its own app registration |

## 1. Scaffold the env folder

Copy an existing env (`atioz` is the minimal template) to `deploy/aks/envs/<env>/`:

| File | What to change |
|---|---|
| `values.<env>.yaml` | host, `MEMEX_DATABASENAME`, TLS `secretName`, AI + auth config, resources |
| `portal-pvcs.yaml` | `namespace: <env>` on every PVC |
| `portal-ingress.yaml` | `namespace`, host, TLS secret, affinity cookie name |
| `secretproviderclass.yaml` | `namespace`, synced secret name `<env>-portal-ai-secrets`, KV `objectName`s |
| `portal-patch.json` | (usually unchanged — binds PVCs + the CSI secret mount + `envFrom`) |
| `deploy.sh` / `tls.sh` | `NS`, `RELEASE`, host |

> `values.<env>.yaml` and `secretproviderclass.yaml` are **git-ignored** (see
> `deploy/aks/envs/.gitignore`) — they carry deployment-specific ids/sender/KV refs and
> are managed out-of-band. The scripts read them from disk.

## 2. Provision Azure (control-plane; no cluster access needed)

```bash
RG=memex-aks-rg; PG=memexaks-pg; KV=Systemorph; ZONE=meshweaver.cloud
INGRESS_IP=$(az aks command invoke -g $RG -n memexaks-cluster \
  --command "kubectl get svc -n app-routing-system nginx -o jsonpath='{.status.loadBalancer.ingress[0].ip}'" --query logs -o tsv | tr -d '\r\n ')
# 1. Database on the shared server
az postgres flexible-server db create -g $RG -s $PG -d <env>
# 2. DNS A-record -> the SHARED ingress IP
az network dns record-set a add-record -g dns -z $ZONE -n <sub> --ipv4-address "$INGRESS_IP" --ttl 300
# 3. Sign-in app (MULTI-TENANT so any org can sign in; invitation-only gates access)
az ad app create --display-name "<Env> Portal (<host>)" \
  --sign-in-audience AzureADMultipleOrgs \
  --web-redirect-uris "https://<host>/signin-microsoft"
az ad app credential reset --id <appId> --display-name <env> --years 1   # -> client secret
# 4. KV secrets. FRESH master key only for an EMPTY db; for a MIGRATED db REUSE the
#    source's master key (else stored enc: provider keys become undecryptable).
az keyvault secret set --vault-name $KV --name <env>-Ai-KeyProtection-MasterKey --value "$(openssl rand -base64 32)"
az keyvault secret set --vault-name $KV --name <env>-Authentication-Microsoft-ClientSecret --value "<entra-secret>"
```

## 3. Deploy + issue TLS

```bash
STAGE=$(mktemp -d); cp deploy/aks/envs/<env>/* "$STAGE"/; cp -r deploy/helm "$STAGE"/helm
export MEMEX_PG_CONN='Host=<PG_PRIVATE_IP>;Port=5432;Username=memexadmin;Password=<PW>;Database=<env>;SslMode=Require;Trust Server Certificate=true'
export IMAGE_TAG=<sha>
( cd "$STAGE" && az aks command invoke -g memex-aks-rg -n memexaks-cluster \
    --command "MEMEX_PG_CONN='$MEMEX_PG_CONN' IMAGE_TAG='$IMAGE_TAG' bash deploy.sh" --file . )
# Verify BEFORE DNS/TLS (host still unresolved or pointing elsewhere):
curl -sS -k -o /dev/null -w "%{http_code}\n" --resolve <host>:443:$INGRESS_IP https://<host>/
# Then issue the cert (needs the A-record to resolve publicly):
( cd deploy/aks/envs/<env> && az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "bash tls.sh" --file tls.sh )
```

## Sign-in, invitations, email

- **Microsoft, multi-tenant.** Set `Authentication__Microsoft__ClientId` + leave the tenant
  as `organizations` (authority `…/organizations/v2.0`). The client **secret** comes from the
  Key Vault via the SecretProviderClass. Empty a provider's `ClientId` (`""`) to hide it — that
  overrides the image's baked `appsettings.json` default (e.g. the inlined LinkedIn id).
- **Invitation-only** (`Features__Onboarding__InvitationOnly=true`): the **first** user (empty
  user table) always bootstraps to **global admin** — the gate exempts the first user, so the
  env can never lock itself out — then invites others. See [Invitation-Only Onboarding](InvitationOnlyOnboarding.md).
- **Email** (`Email__Enabled=true` + Graph `Mail.Send` app): invitations email. The mailbox the
  portal sends and receives as (`Email__MailboxAddress`) must be a **real mailbox in the tenant**
  (`meshweaver.cloud` is not a mailbox domain; `no-reply@systemorph.com` does not exist — use a
  real/shared mailbox). The Graph app needs the **`Mail.Send` application permission + admin
  consent** (plus **`Mail.ReadWrite`** if you also enable the inbound channel via
  `Email__InboundEnabled=true`).

## Migrating an existing portal (data move)

For a portal moving off another platform (e.g. ACA → AKS), in addition to the above:
1. **Reuse the source master key** in the env's KV (decrypts stored `enc:` provider keys).
2. **DB**: `pg_dump --no-owner --no-acl` the source → restore into the env's database. The
   source may be Entra-auth only — dump from an in-cluster pod with an AAD token (an Entra admin
   on the source server) and a temporary firewall rule for the AKS egress IP.
3. **Content**: copy the blob content collection → the `/mnt/content` Azure Files share.
4. Verify on the ingress IP (`--resolve`), then cut DNS over, keeping the old platform as rollback.

See [Memex Cloud Deployment](MemexCloudDeployment.md) for the prod-grade specifics.

## 🚨 Gotchas (learned the hard way)

- **The chart configMap only emits keys it templates.** `deploy/helm/templates/memex-portal/config.yaml`
  has a fixed key list. Any `Authentication__*` / `Features__*` / `Email__*` / `OTEL_*` value in your
  env overlay is **silently dropped** unless the template passes it through. Symptom: the Microsoft
  button never renders (no `Microsoft:ClientId` reaches the portal).
- **Never emit an empty string for an int/bool config key.** `Anthropic__Order: ""` fails
  `Int32` binding → `AzureClaudeChatClientAgentFactory` throws on DI activation → the chat page
  (the post-onboarding landing) dies with *"exception thrown while activating IChatClientFactory[]"*.
  In the chart, default typed keys to a valid value (`Order` → `"0"`, bools → `"false"`), never `""`.
- **The Key Vault (CSI) secret must be LAST in the container's `envFrom`.** The chart's
  `memex-portal-secrets` carries an **empty** `Authentication__Microsoft__ClientSecret`; the
  CSI-synced `<env>-portal-ai-secrets` carries the real one. Later `envFrom` wins, so the CSI
  secret must come after. Symptom: `AADSTS7000218` (token request had no `client_secret`).
- **The post-helm `portal-patch.json` is not idempotent.** `helm upgrade` can preserve
  kubectl-added volumes, so re-applying the patch fails on duplicate volume adds — which rejects
  the **whole** atomic patch, dropping the `envFrom` CSI secret. After a redeploy, re-verify the
  portal's `envFrom` includes `<env>-portal-ai-secrets` and the data/users volumes are PVCs.
- **Observability is already on.** Grafana + Loki + Promtail run in the `monitoring` namespace
  and scrape **every** namespace — query `{namespace="<env>"}` in Grafana Explore (reach it via
  the P2S VPN + `kubectl -n monitoring port-forward svc/loki-grafana 3000:80`). Loki retains logs
  across pod restarts, unlike `kubectl logs`.

## Related

- [AKS Deploy Runbook](../../../../deploy/aks/DEPLOY-RUNBOOK.md) — the one-time shared-platform bring-up.
- [Memex Cloud Deployment](MemexCloudDeployment.md) · [Deployment Options](DeploymentOptions.md)
- [Invitation-Only Onboarding](InvitationOnlyOnboarding.md) · [Feature Flags](FeatureFlags.md)
