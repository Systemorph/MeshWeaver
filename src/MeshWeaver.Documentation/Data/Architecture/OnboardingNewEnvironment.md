---
Name: Onboarding a New Environment
Description: "Stand up a new Memex portal environment (own domain, database, sign-in) on the shared AKS cluster: what's shared vs separate, the scaffold + provisioning steps, sign-in/invitation/email wiring, and the hard-won gotchas (chart config pass-through, empty int/bool config, CSI secret envFrom order)."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="2" y="2" width="20" height="8" rx="2"/><rect x="2" y="14" width="20" height="8" rx="2"/><line x1="6" y1="6" x2="6.01" y2="6"/><line x1="6" y1="18" x2="6.01" y2="18"/></svg>
Category: Architecture
---

# Onboarding a New Environment

A "new environment" is an additional Memex portal — its own domain, database, and
sign-in — running on the **shared AKS cluster** (`memexaks-cluster` / `memex-aks-rg`,
swedencentral). `memex.meshweaver.cloud` is the worked example; it lives under
`deploy/aks/envs/<env>/`.
The shared platform (cluster, ingress, Postgres server, Key Vault, ACR) is brought up
once — see the AKS deploy runbook (`deploy/aks/DEPLOY-RUNBOOK.md` in the repository); this guide
adds an environment on top of it.

## Shared vs. separate

| Resource | Shared across envs | Separate per env |
|---|---|---|
| AKS cluster + node pools | ✅ `memexaks-cluster` | |
| Ingress controller (app-routing nginx, one public IP) | ✅ | |
| Postgres **server** | ✅ `memexaks-pg` | **database** (`memexcloud`, …) |
| Key Vault | ✅ `Systemorph` | secret **names** (`<env>-*`) + **master key** |
| ACR + portal image | ✅ `meshweaver.azurecr.io/memex-portal-ai` | image **tag** (a commit sha) |
| Kubernetes namespace | | ✅ `<env>` |
| Public host + TLS cert | | ✅ `<host>` + `<env>-tls` |
| Entra app (sign-in) | | ✅ its own app registration |

## 1. Scaffold the env folder

Copy an existing env folder under `deploy/aks/envs/` to `deploy/aks/envs/<env>/`:

| File | What to change |
|---|---|
| `values.<env>.yaml` | host, `MEMEX_DATABASENAME`, TLS `secretName`, AI + auth config, resources, `selfUpdate.azureClientId` (the shared `portalIdentityClientId` — same value for every env) |
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
# 5. Self-update (ACR polling): federate the SHARED portal UAMI to THIS namespace's memex-portal-sa
#    so the in-pod self-updater can list ACR tags. Preferred: add the namespace to `portalNamespaces`
#    in infra/main.bicep and re-run the (idempotent) infra deploy. Quick out-of-band equivalent:
ISSUER=$(az aks show -g $RG -n memexaks-cluster --query oidcIssuerProfile.issuerURL -o tsv)
az identity federated-credential create -g $RG --identity-name memexaks-portal-mi \
  --name "memex-portal-<env>" --issuer "$ISSUER" \
  --subject "system:serviceaccount:<env>:memex-portal-sa" --audience "api://AzureADTokenExchange"
# The shared UAMI already has AcrPull on meshweaver.azurecr.io — set its clientId as
# selfUpdate.azureClientId in values.<env>.yaml (same value as every other env):
PORTAL_MI_CLIENT_ID=$(az identity show -g $RG -n memexaks-portal-mi --query clientId -o tsv); echo "$PORTAL_MI_CLIENT_ID"
```

> **`<env>` is the Kubernetes namespace.** The federated-credential subject must be EXACTLY
> `system:serviceaccount:<env>:memex-portal-sa` — a mismatch silently fails the ACR token exchange
> (the in-pod deployment PATCH still works; only tag discovery is blocked). See
> [DeploymentAKS → Portal self-update](/Doc/Architecture/DeploymentAKS).

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

## Self-update: first-install checklist

A new environment should run on **self-update from day one** — that is the steady state.
The manual [AKS runbook](/Doc/Architecture/DeploymentAKS) (`kubectl set image` + rollout) is the
**bootstrap / break-glass** path only (the very first install, or forcing a specific tag). Once per
environment, in this order:

1. **Deploy the `portal-identity` bicep.** It provisions the shared portal UAMI
   (`memexaks-portal-mi`) + one federated credential per portal namespace
   (`deployPortalIdentity: true`, default). For a brand-new namespace, add it to `portalNamespaces`
   and re-run the (idempotent) infra deploy, or create the federated credential out-of-band
   (§2 step 5). The subject must be exactly `system:serviceaccount:<env>:memex-portal-sa`.
2. **Grant the portal UAMI `AcrPull` on the shared ACR.** `meshweaver.azurecr.io` lives in
   `meshweaver-shared` — cross-RG from `memex-aks-rg` — so grant it out-of-band exactly like the
   kubelet's grant (or set `grantSharedAcrPull=true` for pure-IaC). One grant covers every namespace
   (one shared UAMI). Without it the in-pod Deployment PATCH still works; only ACR tag discovery is
   blocked.
3. **Set `selfUpdate.azureClientId`** in `values.<env>.yaml` to the shared `portalIdentityClientId`
   (the **same** value for every env). This authenticates the tag-list call; the chart wires the
   workload-identity annotation/label + `AZURE_CLIENT_ID` from it.
4. **Set `Admin/UpdatePolicy` for the env.** Settings → Updates (platform admin) writes the
   `Admin/UpdatePolicy` node. Recommended: **Continuous for dev/test** (always rolls to the newest
   build-numbered image), **Stable for prod** (rolls only to the newest clean release). See
   [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy).

## Plugins — wire the environment to a registry

A new environment starts with **no plugins**, and its **Settings ▸ Administration ▸ Plugin Catalog**
tab reads "not configured" until you point it at a registry. Plugins live in (usually private) git
repos; **one** installation is the [registry](/Doc/Architecture/PluginRegistry) — it alone holds the
git credential and re-serves the catalog over HTTP — and every other installation is a credential-free
**consumer**.

Consumer (the normal case) — in `values.<env>.yaml`:

```yaml
pluginCatalog:
  registryUrl: "https://<registry-host>"      # or `registries: [{name, url, ref}]` for several
config:
  memex_portal:
    PluginCatalog__AutoUpdateByDefault: "true"   # installs track their repo; install-time seed only
secrets:
  memex_portal:
    PluginCatalog__RegistryToken: "<token issued to this installation>"
```

The consumer's token is the `mwi_` instance key issued when the new installation is **registered**
on the registry portal (Settings ▸ Instances — self-service, shown once). Registration grants
nothing by itself; what the instance may pull is decided per `(source, package)` by a platform
admin on the registry — except sources the registry opted into `PluginCatalog:DefaultGrants`
(typically the platform `Plugins/*` repo), which every new registration is granted automatically.
So with defaults configured, a fresh environment needs no admin grant step to see the platform
plugins — install them from the Plugin Catalog tab once the consumer wiring lands.

Registry (only when this environment *is* the registry):

```yaml
pluginCatalog:
  sources:
    - {name: Plugins, repoPath: "https://github.com/<org>/<plugins-repo>", ref: main}
  defaultGrants: ["Plugins/*"]   # granted to every NEW registration; never private/paid sources
secrets:
  memex_portal:
    PluginCatalog__RegistryTokens: ["<token-per-registered-installation>"]
```

> 🚨 **A registry with an empty `RegistryTokens` list answers ANY anonymous caller** with the full
> catalog *and* every package's file content — that is the local-dev / e2e stub mode. Always
> configure tokens on a production registry, and verify with an unauthenticated
> `curl https://<registry-host>/api/plugins` (want `401`). Prefer sourcing the token from Key Vault
> via the SecretProviderClass over putting it in the values file.

## Sign-in, invitations, email

- **Microsoft, multi-tenant.** Set `Authentication__Microsoft__ClientId` + leave the tenant
  as `organizations` (authority `…/organizations/v2.0`). The client **secret** comes from the
  Key Vault via the SecretProviderClass. Empty a provider's `ClientId` (`""`) to hide it — that
  overrides the image's baked `appsettings.json` default (e.g. the inlined LinkedIn id).
- **Invitation-only** (`Features__Onboarding__InvitationOnly=true`): the **first** user (empty
  user table) always bootstraps to **global admin** — the gate exempts the first user, so the
  env can never lock itself out — then invites others. See [Invitation-Only Onboarding](/Doc/Architecture/InvitationOnlyOnboarding).
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

See [Memex Cloud Deployment](/Doc/Architecture/MemexCloudDeployment) for the prod-grade specifics.

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
- **🩺 Crash dumps need the MOUNT, not just the env vars.** `DOTNET_DbgEnableMiniDump` +
  `DOTNET_DbgMiniDumpName=/data/dumps/…` are worthless on their own: `createdump` does **not create
  directories**, so without a volume mounted at that path every crash fails with *"Could not create
  output file … No such file or directory"* — **destroying its own evidence** — and burns ~6s plus a
  ~350k-line log storm on the way down. The chart mounts a dedicated `memex-dumps` emptyDir there;
  an env whose live pod lacks it produces **zero dumps while looking fully instrumented**. Verified
  2026-07-28: all three environments had the env vars pointing at a non-existent directory, so every
  production `exit=139` since had left nothing to analyse. Check the MOUNT, never the env:
  ```bash
  kubectl get deploy memex-portal-deployment -n <env> \
    -o jsonpath='{range .spec.template.spec.containers[0].volumeMounts[*]}{.name} -> {.mountPath}{"\n"}{end}' \
    | grep dumps || echo "NO DUMP MOUNT — crashes will produce nothing"
  ```
- **The per-env patch replaces volumes BY INDEX.** `deploy/aks/envs/<env>/portal-patch.json` targets
  `/spec/template/spec/volumes/0`, `/1`, … so **adding or reordering a volume in the chart silently
  misaligns every environment**, and a cluster can keep running an older volume set while the chart
  in git looks correct. After any deploy, diff the live mounts against the chart — do not assume the
  chart is what is running.
- **KEDA overrides `kubectl scale`.** A `ScaledObject` with `minReplicaCount: 2` silently restores
  replicas, so "scale to zero" (the documented heal for a wedged mesh) **does not take** and you
  conclude the heal failed when it never ran. Check first — and note `PAUSED`:
  ```bash
  kubectl get scaledobject -n <env>
  ```
- **HTTP 200 proves nothing.** The Blazor shell returns 200 for a page that renders an error, a
  paywall redirect, or nothing at all. Verify a deploy with response **headers** for static assets
  and with actual rendered content (`get @<Node>/area/<Area>` through the MCP, or a real browser) —
  never with a status code.

## Verifying a rollout (and what "normal turbulence" looks like)

A new image means **fresh pods**, and every dynamic NodeType's cached assembly is ABI-stale against
the new framework build — so they all recompile. Expect a window where pages and `/static/…` return
errors like *"No response received … for request `GetDataRequest`/`SubscribeRequest` → target X"*.
That is cold-compile, not a bad image.

```bash
# 1. BEFORE rolling: the manifest must have every arch leg. A partial manifest list is an
#    ImagePullBackOff on the missing arch, which reads as "the deploy hung".
az acr manifest show -r <registry> -n memex-portal-ai:<tag> \
  | jq -r '.manifests[]?.platform | "\(.os)/\(.architecture)"'

# 2. Roll and wait.
kubectl set image deploy/memex-portal-deployment memex-portal=<registry>/memex-portal-ai:<tag> -n <env>
kubectl rollout status deploy/memex-portal-deployment -n <env> --timeout=600s

# 3. Verify with real signals, in a LOOP (one probe can hit a warm pod and lie).
for i in $(seq 1 10); do curl -s -o /dev/null -w "%{http_code} " https://<host>/static/<space>/content/<file>; done
```

- **Do not cycle pods while it warms.** Deleting or scaling pods restarts the compile work from
  cold and makes the window longer, not shorter.
- **A steady ratio is a signal, not warm-up.** Warm-up converges toward 100%; a stable ~50% across
  many minutes means *one replica is failing consistently* — investigate that, do not wait it out.
- **Probe budgets** (`startupProbe` 300s, `liveness` 90s, `readiness` 30s): liveness and readiness
  do not run until the startup probe succeeds, so a **slow boot is safe**. What is not safe is a
  hang or crash *after* startup — 90s of failed `/alive` and kubelet restarts the container.

## Related

- AKS Deploy Runbook — `deploy/aks/DEPLOY-RUNBOOK.md` in the repository — the one-time shared-platform bring-up.
- [Memex Cloud Deployment](/Doc/Architecture/MemexCloudDeployment) · [Deployment Options](/Doc/Architecture/DeploymentOptions)
- [Invitation-Only Onboarding](/Doc/Architecture/InvitationOnlyOnboarding) · [Feature Flags](/Doc/Architecture/FeatureFlags)
