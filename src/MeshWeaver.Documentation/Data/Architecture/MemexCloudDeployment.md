---
Name: Memex Cloud Deployment
Description: "Step-by-step architecture and operations manual for deploying the Memex portal on a private Azure Kubernetes Service cluster."
---

# Deploying the Memex Portal to a Private AKS Cluster

A configuration manual for standing up the Memex portal on a **private** Azure Kubernetes Service
cluster, with everything but the portal kept off the public internet. It explains *what* runs
where, *how* it's provisioned, and *how* to operate it.

> **Conventions.** The running example uses placeholder **names** — domain `memex.systemorph.com`,
> registry `meshweaver`, resource group `memex-aks-rg` — substitute your own. **Sensitive values**
> (IP addresses, tenant/app GUIDs, passwords, client secrets) are shown as `<placeholders>`; never
> commit real ones — keep them in Key Vault. The exact, ordered command sequence lives in
> [`deploy/aks/DEPLOY-RUNBOOK.md`](../../../../deploy/aks/DEPLOY-RUNBOOK.md); this doc is the
> architecture + operations layer around it.

> **Model:** one Aspire AppHost (`deploy/aspire/Memex.Deploy.AppHost`) describes the workload from
> published images; the Aspire **Kubernetes publisher** generates the Helm chart (`deploy/helm`);
> the AKS *platform* (cluster, Postgres, VPN, TLS) is Bicep + a thin overlay.

---

## 1. Architecture at a glance

| Concern | Choice |
|---|---|
| Region | a single region close to your users (the example uses a prod region) |
| Cluster | **Private** AKS (private API server), e.g. 2× `Standard_D4s_v3` sized to your vCPU quota |
| Public surface | **Only the portal on `:443`.** Everything else (API server, Postgres, Grafana) is private. |
| Mesh data | **Postgres Flexible Server**, VNet-injected (private IP only), admin user + password + SSL |
| Object/cache/keys | **Filesystem backend** on RWX **Azure Files** (`/data`, `/mnt/content`) |
| Container registry | **One shared ACR** (e.g. `meshweaver.azurecr.io`) across all your solutions |
| Secrets | **one shared Key Vault** via the CSI Secrets Store add-on |
| Ingress / TLS | AKS **app routing** (managed nginx) + **cert-manager** + Let's Encrypt (HTTP-01) |
| Admin access | **P2S VPN** → `kubectl` / Grafana; nothing admin is public |
| Auth | external OIDC (Microsoft/Entra, Google, LinkedIn) — each provider opt-in |
| Orleans | single replica → `Localhost` clustering (multi-replica needs `AzureTables`/`AdoNet`) |

The ingress's public IP is assigned by Azure (`kubectl get svc -n app-routing-system`); point your
domain's A-record at it (§5).

## 2. Images (shared ACR)

Three images, pushed to the shared ACR (grant the AKS kubelet `AcrPull` on it, cross-RG if needed):
- `<registry>/memex-portal-ai-base:latest` — `aspnet:10.0` + node20 + the co-hosted CLIs (Claude
  Code + Copilot). This is the **one** hand-authored Dockerfile (`deploy/base-images/portal-ai`),
  built with `az acr build`.
- `<registry>/memex-portal-ai:<tag>` — the portal app, an SDK container build on the base image.
  **Must pass `-r linux-x64`** (the Copilot SDK keys its binary off the RID).
- `<registry>/memex-migration:<tag>` — the one-shot DB migration.

Build/push the portal (no Dockerfile — the SDK's `PublishContainer` pushes straight to the registry):
```bash
az acr login --name <registry>
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj \
  -c Release -r linux-x64 --no-self-contained -t:PublishContainer \
  -p:ContainerRegistry=<registry>.azurecr.io -p:ContainerRepository=memex-portal-ai \
  -p:ContainerImageTag=<tag> -p:ContainerBaseImage=<registry>.azurecr.io/memex-portal-ai-base:latest
kubectl -n memex set image deployment/memex-portal-deployment memex-portal=<registry>.azurecr.io/memex-portal-ai:<tag>
```
Use a **distinct tag** per build (not `:latest`) so the rollout is guaranteed to pull the new image.

## 3. Platform (Bicep)

`deploy/aks/infra/main.bicep` provisions the cluster, the VNet-injected Postgres Flexible Server,
the VPN gateway, RWX storage, and (optionally) a per-deployment ACR — set `useSharedAcr=true` to
point at your shared registry instead. Parameters in `deploy/aks/infra/main.parameters.json` (region,
node size/count within your vCPU quota, `postgresHighAvailability`, `gatewaySku` — use an AZ SKU such
as `VpnGw1AZ`). The **Postgres connection uses the private IP + password + SSL**
(`SslMode=Require;Trust Server Certificate=true`) — the public FQDN form would trip the portal's
`database.azure.com` → managed-identity-token branch, which doesn't match a password server.

## 4. Workload (Helm + overlay)

`deploy/aks/scripts/deploy.sh` (run via `az aks command invoke` against the private cluster):
namespace + RWX PVCs → `helm upgrade --install` (`deploy/helm` + `values.aks.yaml` +
`values.deploy.yaml`) → scale the chart's in-cluster pg to 0 (you use the Flexible Server) →
`kubectl set image` to the shared ACR → patch the portal to 1 replica + the Azure Files mounts →
**patch the connection-string secret** to the external Postgres.

> **Known chart-gen gaps** (fix at the AddMemex generator):
> - The chart's `secrets.yaml` hardcodes the in-cluster pg connection string → `deploy.sh` patches it post-install.
> - The migration is rendered as a **Deployment**, not a Job, so K8s reruns it after each clean exit → it can show `CrashLoopBackOff` even though every run **succeeds**. Harmless, but should be a `Job`.

## 5. TLS + ingress + DNS

`deploy/aks/scripts/tls.sh`: cert-manager + a Let's Encrypt `ClusterIssuer` (HTTP-01) + the portal
ingress. HTTP→HTTPS redirect is automatic once TLS is on. Add a DNS A-record in your DNS zone →
the nginx LB public IP. **Blazor Server** needs sticky sessions: the ingress sets cookie affinity
(a session-affinity cookie); confirm the SignalR `/_blazor` WebSocket upgrade returns **HTTP 101**
through the managed nginx.

To add another private tool publicly (e.g. Grafana), create a second `Ingress` (same
`ingressClassName` + `cert-manager.io/cluster-issuer` annotation) for its host + service, and an
A-record at the same ingress IP. It is then gated only by that tool's own login — weigh that against
the "only the portal is public" stance.

## 6. External sign-in (OAuth)

Deploy parameters flow through `AddMemex` → `MemexOptions` → portal env
(`Authentication__<Provider>__ClientId/Secret/TenantId`, `Social__LinkedIn__*`). A provider is
offered only when its `ClientId` is set.
- **Microsoft/Entra:** register an app (`<entra-app-client-id>`) in your tenant
  (`<tenant-guid>`); single-tenant (`AzureADMyOrg`) for an internal portal; redirect URI
  `https://<your-domain>/signin-microsoft`. Set `Authentication__Provider=Custom`,
  `Authentication__EnableDevLogin=false`.
- **Google / LinkedIn:** create the OAuth apps (redirects `/signin-google`, `/signin-linkedin`) and
  supply ClientId/Secret to enable.

Sign-in flow: `/auth/login?provider=Microsoft` → the provider → `/signin-microsoft` (OIDC middleware
signs the cookie) → `/auth/callback/Microsoft` (`ExternalAuthController` normalises claims;
**ObjectId = email**) → `/`.

## 7. Onboarding + first admin

`OnboardingMiddleware` (after `UserContextMiddleware`): an authenticated request whose email has no
backing **User node** is redirected to `/onboarding`; `UserOnboardingService` writes the partition-
root User node + a User-catalog mirror, then self-Admin and (first user only) **platform-Admin** at
`Admin/_Access`. All onboarding writes self-impersonate as **System** (infrastructure writes for a
not-yet-existent identity — `PostPipeline` fails closed without a context).

**First-admin bootstrap (operator tool):** `BootstrapController` (`POST/GET /bootstrap/first-admin`)
seeds the first admin server-side via the same `UserOnboardingService` write path, gated by the
`Bootstrap:Secret` config value (disabled when unset). Use it when the interactive `/onboarding`
flow can't be driven, then unset the secret:
```bash
curl -sS "https://<your-domain>/bootstrap/first-admin?secret=<bootstrap-secret>&email=<admin-email>&username=<admin>"
```

## 8. Observability

`deploy/aks/scripts/install-observability.sh` installs the `grafana/loki-stack` chart (Grafana +
Loki + Promtail + Prometheus) into the `monitoring` namespace. **Promtail scrapes every pod's stdout
into Loki** — no portal-side config needed. Folded into the standard deploy: export `GRAFANA_PW`
alongside `MEMEX_PG_CONN` and `deploy.sh` brings the stack up too. At the model level, `AddMemex`'s
`OtlpEndpoint` option wires `OTEL_EXPORTER_OTLP_ENDPOINT` for OTLP traces/metrics (not needed for
logs). Grafana defaults to **ClusterIP (private)** — reach it via the VPN (§9) + port-forward, or
expose it publicly behind its own login (§5).

## 9. Admin access — the P2S VPN

Everything but the portal is private, so `kubectl` (private API server) and Grafana go through the
**point-to-site VPN** (an AZ gateway SKU, OpenVPN + IKEv2, with a client address pool of your choice):
```bash
# A P2S root cert is uploaded to the gateway; the matching client cert lives in the operator's cert store.
az network vnet-gateway vpn-client generate -g <rg> -n <gateway> -o tsv   # download URL
# install + connect, then:
az aks get-credentials -g <rg> -n <cluster>
kubectl -n monitoring port-forward svc/loki-grafana 3000:80   # http://localhost:3000
```
> **`az` gotcha:** recent CLI versions read `--public-cert-data` as a **file path** — pass the path
> to a base64 file, not the inline string and not `@file`.

## 10. Operations

| Task | Command |
|---|---|
| **Logs (no VPN)** | `az aks command invoke -g <rg> -n <cluster> --command "kubectl -n memex logs deployment/memex-portal-deployment --tail=200"` |
| **Logs (Grafana)** | VPN → port-forward (§9) → `{namespace="memex"}` in Explore |
| Restart portal | `kubectl -n memex rollout restart deployment/memex-portal-deployment` (clears in-memory caches + any wedged hub) |
| Pod status | `kubectl -n memex get pods` |
| Run SQL | one-off `kubectl run … --image=postgres:17 … psql -h <pg-private-ip> -U <admin> -d memex` (password from Key Vault) |
| Reach Postgres | private IP only (from inside the VNet / over the VPN) |

All admin commands run against the **private** API server, so they go through
`az aks command invoke -g <rg> -n <cluster> --command "<kubectl…>"` (or `kubectl` directly once
you're on the P2S VPN, §9). Common tasks:

### 10.1 Update the portal to a new image
Build + push (§2), then repoint the deployment and roll:
```bash
kubectl -n memex set image deployment/memex-portal-deployment memex-portal=<registry>.azurecr.io/memex-portal-ai:<tag>
kubectl -n memex rollout status deployment/memex-portal-deployment --timeout=220s
```
Use a **fresh tag** each build so the pull is guaranteed. Roll back by setting the previous tag.

### 10.2 See the logs
- **Quick (no VPN):** `az aks command invoke -g <rg> -n <cluster> --command "kubectl -n memex logs deployment/memex-portal-deployment --since=10m"`
- **Dashboard (Grafana, via the P2S VPN — §9):** `kubectl -n monitoring port-forward svc/loki-grafana 3000:80` → `http://localhost:3000` → Explore → `{namespace="memex"}` (add `|= "error"` to filter).

### 10.3 Enable / configure AI providers
Providers are gated by feature flags; set them on the deployment, then each user supplies credentials:
```bash
kubectl -n memex set env deployment/memex-portal-deployment \
  Features__Ai__Providers__AzureFoundry=true Features__Ai__Providers__AzureOpenAI=true \
  Features__Ai__Providers__Anthropic=true   Features__Ai__Providers__OpenAI=true \
  Features__Ai__Clis__ClaudeCode=true       Features__Ai__Clis__Copilot=true \
  Ai__KeyProtection__MasterKey='<base64-32-byte-key>'    # encrypts stored provider credentials at rest
```
Then in **Settings → Models**: API providers (Anthropic, Azure OpenAI, Azure AI Foundry, OpenAI)
work via **bring-your-own-key** (add the endpoint+key per user). The co-hosted CLIs (Claude Code,
GitHub Copilot) need the **per-user Connect flow** (Phase 1 — see §11); the CLI binaries ship in the
`portal-ai` image but the per-user login isn't wired yet. The master key should live in Key Vault,
not a plaintext env var — see §11.

### 10.4 Restart, scale, inspect
```bash
kubectl -n memex rollout restart deployment/memex-portal-deployment   # also clears in-memory caches + any wedged hub
kubectl -n memex scale deployment/memex-portal-deployment --replicas=1  # >1 needs Orleans AzureTables clustering (§11)
kubectl -n memex get pods -o wide
```

### 10.5 Postgres: query + reset
Postgres is private (VNet IP only). Run SQL via a throwaway pod inside the cluster:
```bash
kubectl -n memex run pg --restart=Never --rm -i --image=postgres:17 \
  --env=PGPASSWORD=<pw> --env=PGSSLMODE=require --command -- \
  psql -h <pg-private-ip> -U <admin> -d memex -c "SELECT count(*) FROM auth.mesh_nodes WHERE node_type='User';"
```
To reset to the post-initialize state, drop the per-user partition schemas + truncate content
(keep `admin.db_version`), then **restart the portal** (direct SQL bypasses the workspace cache).

### 10.6 Secrets via Key Vault (CSI Secrets Store)
Production secrets live in **`meshweaverkeyvault`** (access-policy mode) and are projected into the
pod by the AKS **CSI Secrets Store** add-on — no plaintext env, the vault is the source of truth.
One-time wiring: grant the CSI add-on's identity `get/list` on the vault, store each secret, then a
`SecretProviderClass` maps KV secret names → env-var keys and syncs them into a k8s Secret the
deployment mounts (CSI volume) + reads (`envFrom`):
```bash
# CSI identity object id: az aks show -g <rg> -n <cluster> --query addonProfiles.azureKeyvaultSecretsProvider.identity.objectId -o tsv
az keyvault set-policy -n meshweaverkeyvault --object-id <csi-identity-objectid> --secret-permissions get list
az keyvault secret set --vault-name meshweaverkeyvault --name ai-keyprotection-masterkey --value '<value>'   # dashes only in KV names
# SecretProviderClass `memex-kv` maps ai-keyprotection-masterkey -> Ai__KeyProtection__MasterKey (and the
# PG conn / Microsoft secret / Bootstrap secret) and syncs them into the `memex-kv-secrets` k8s Secret;
# the portal has a CSI volume for `memex-kv` + `envFrom: secretRef: memex-kv-secrets`.
```
**Rotate:** `az keyvault secret set` (new version) → `kubectl -n memex rollout restart deployment/memex-portal-deployment`
(the CSI driver re-reads on the next mount). The `SecretProviderClass` + the CSI volume/`envFrom` are
applied post-`deploy.sh` today (folding them into the chart is the §11 follow-up).

> **Windows `az` gotcha:** the CLI's console writer can't encode non-ASCII characters in cp1252 and
> crashes a raw log dump. Pipe the cluster-side command through `tr -cd '\11\12\15\40-\176'` to strip
> non-ASCII before `az` prints it.

## 11. Known issues / follow-ups

- **Route-derived spurious partitions** — visiting an auth-flow route (`/onboarding`, `/login`,
  `/welcome`) can create a same-named partition *schema*; the router then tries to activate that
  empty partition and its hub deadlocks (messages time out at 30s and retry). Drop the spurious
  schemas and trace the code path that maps a route to a partition address.
- **Static/seed user shadowing onboarding** — if a static node provider seeds a User for the admin
  email, a fresh `CreateUser` refuses with `Node already exists`, and the interactive form shows
  "user exists" even with 0 DB users. Remove the seed so real onboarding can persist the partition root.
- **Secrets in Key Vault (done):** the master key, PG connection string, Microsoft client secret, and
  `Bootstrap:Secret` live in `meshweaverkeyvault`; a `SecretProviderClass` + the AKS CSI Secrets Store
  add-on sync them into a k8s Secret the portal reads via `envFrom` (see §10.6) — no plaintext env.
  Remaining: the Grafana admin password (monitoring namespace), and folding the `SecretProviderClass`
  + the deployment's CSI volume/`envFrom` into the chart/AddMemex so a fresh deploy wires KV
  automatically instead of the current post-`deploy.sh` patch.
- **Multi-replica HA** — needs Orleans `AzureTables`/`AdoNet` clustering wired on the Filesystem backend.
- **Migration as a Job** — see §4.
- **Release image** — replace any temporary debug image tag with a clean `latest`/release tag before
  treating the deployment as final.
