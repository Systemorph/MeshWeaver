---
Name: Memex Cloud Deployment
Description: "Architecture and operations manual for deploying the Memex portal on a private Azure Kubernetes Service cluster — what runs where, how it is provisioned, and how to operate it day-to-day."
Icon: Cloud
Category: Architecture
---

# Deploying the Memex Portal to a Private AKS Cluster

This guide explains how to stand up the Memex portal on a **private** Azure Kubernetes Service cluster — with everything except the portal itself kept off the public internet. It covers what runs where, how each layer is provisioned, and how to operate it in production.

> **Conventions.** Examples use placeholder names — domain `memex.systemorph.com`, registry `meshweaver`, resource group `memex-aks-rg`. Substitute your own values. **Sensitive values** (IP addresses, tenant/app GUIDs, passwords, client secrets) appear as `<placeholders>`; never commit real ones — keep them in Key Vault.
>
> The exact, ordered command sequence lives in [`deploy/aks/DEPLOY-RUNBOOK.md`](../../../../deploy/aks/DEPLOY-RUNBOOK.md). This document is the architecture and operations layer around it.

> **Deployment model.** One Aspire AppHost (`deploy/aspire/Memex.Deploy.AppHost`) describes the workload from published images. The Aspire **Kubernetes publisher** generates the Helm chart (`deploy/helm`). The AKS *platform* — cluster, Postgres, VPN, TLS — is Bicep plus a thin overlay.

---

## 1. Architecture at a Glance

The cluster has a single public surface: the portal on port 443. Everything else — the Kubernetes API server, Postgres, Grafana — stays private and reachable only over the P2S VPN.

<svg viewBox="0 0 760 400" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
<defs>
<marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
</marker>
</defs>
<rect width="760" height="400" rx="12" fill="#111827" opacity=".0"/>
<rect x="10" y="10" width="740" height="380" rx="10" fill="none" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
<text x="380" y="32" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-style="italic">Private AKS Cluster</text>
<rect x="30" y="42" width="700" height="318" rx="8" fill="#1a2236" stroke="#334155" stroke-width="1"/>
<rect x="50" y="62" width="200" height="72" rx="8" fill="#1e3a5f" stroke="#2563eb" stroke-width="1.5"/>
<text x="150" y="90" text-anchor="middle" fill="#93c5fd" font-weight="bold">Internet / Users</text>
<text x="150" y="110" text-anchor="middle" fill="#60a5fa" font-size="11">HTTPS :443 only</text>
<text x="150" y="126" text-anchor="middle" fill="#60a5fa" font-size="11">External OIDC (Entra, Google)</text>
<rect x="50" y="162" width="200" height="56" rx="8" fill="#1e3a5f" stroke="#2563eb" stroke-width="1.5"/>
<text x="150" y="185" text-anchor="middle" fill="#93c5fd" font-weight="bold">nginx Ingress + TLS</text>
<text x="150" y="203" text-anchor="middle" fill="#60a5fa" font-size="11">cert-manager / Let's Encrypt</text>
<rect x="50" y="246" width="200" height="88" rx="8" fill="#1e3a5f" stroke="#2563eb" stroke-width="1.5"/>
<text x="150" y="270" text-anchor="middle" fill="#93c5fd" font-weight="bold">Portal Pod</text>
<text x="150" y="288" text-anchor="middle" fill="#60a5fa" font-size="11">Memex.Portal.Distributed</text>
<text x="150" y="306" text-anchor="middle" fill="#60a5fa" font-size="11">Azure Files RWX (/data)</text>
<rect x="290" y="62" width="200" height="72" rx="8" fill="#1e3a2f" stroke="#16a34a" stroke-width="1.5"/>
<text x="390" y="90" text-anchor="middle" fill="#86efac" font-weight="bold">Postgres Flexible Server</text>
<text x="390" y="110" text-anchor="middle" fill="#4ade80" font-size="11">VNet-injected · private IP</text>
<text x="390" y="126" text-anchor="middle" fill="#4ade80" font-size="11">SSL · password auth</text>
<rect x="290" y="162" width="200" height="72" rx="8" fill="#1e3a2f" stroke="#16a34a" stroke-width="1.5"/>
<text x="390" y="190" text-anchor="middle" fill="#86efac" font-weight="bold">Grafana / Loki / Prometheus</text>
<text x="390" y="210" text-anchor="middle" fill="#4ade80" font-size="11">monitoring namespace · ClusterIP</text>
<text x="390" y="228" text-anchor="middle" fill="#4ade80" font-size="11">private · VPN only</text>
<rect x="290" y="262" width="200" height="56" rx="8" fill="#2d1e3a" stroke="#9333ea" stroke-width="1.5"/>
<text x="390" y="286" text-anchor="middle" fill="#d8b4fe" font-weight="bold">Private K8s API Server</text>
<text x="390" y="304" text-anchor="middle" fill="#c084fc" font-size="11">kubectl · VPN only</text>
<rect x="530" y="62" width="185" height="72" rx="8" fill="#3a2a1e" stroke="#ea580c" stroke-width="1.5"/>
<text x="622" y="90" text-anchor="middle" fill="#fdba74" font-weight="bold">Shared ACR</text>
<text x="622" y="110" text-anchor="middle" fill="#fb923c" font-size="11">meshweaver.azurecr.io</text>
<text x="622" y="126" text-anchor="middle" fill="#fb923c" font-size="11">portal · migration images</text>
<rect x="530" y="162" width="185" height="72" rx="8" fill="#3a2a1e" stroke="#ea580c" stroke-width="1.5"/>
<text x="622" y="190" text-anchor="middle" fill="#fdba74" font-weight="bold">Key Vault</text>
<text x="622" y="210" text-anchor="middle" fill="#fb923c" font-size="11">secrets · CSI add-on</text>
<text x="622" y="228" text-anchor="middle" fill="#fb923c" font-size="11">PG conn · master key</text>
<rect x="530" y="262" width="185" height="56" rx="8" fill="#1e2e3a" stroke="#0891b2" stroke-width="1.5"/>
<text x="622" y="286" text-anchor="middle" fill="#67e8f9" font-weight="bold">P2S VPN Gateway</text>
<text x="622" y="304" text-anchor="middle" fill="#22d3ee" font-size="11">operator admin access</text>
<line x1="150" y1="134" x2="150" y2="162" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="150" y1="218" x2="150" y2="246" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="250" y1="98" x2="290" y2="98" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="250" y1="290" x2="290" y2="290" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="490" y1="98" x2="530" y2="98" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="490" y1="196" x2="530" y2="196" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="622" y1="318" x2="622" y2="334" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5"/>
<line x1="390" y1="334" x2="622" y2="334" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5"/>
<line x1="390" y1="318" x2="390" y2="334" stroke="currentColor" stroke-opacity=".4" stroke-width="1.5" marker-end="url(#arr)"/>
<text x="380" y="375" text-anchor="middle" fill="currentColor" fill-opacity=".4" font-size="11">VPN admin path (kubectl + Grafana)</text>
</svg>

*Deployment topology: only the portal is public-facing; all data, admin, and observability stay on the private VNet, reachable only over the P2S VPN.*

| Concern | Choice |
|---|---|
| Region | Single region close to your users |
| Cluster | **Private** AKS (private API server), e.g. 2× `Standard_D4s_v3` |
| Public surface | **Portal on `:443` only.** API server, Postgres, Grafana are all private. |
| Mesh data | **Postgres Flexible Server**, VNet-injected (private IP only), password + SSL |
| Object storage / cache / keys | **Filesystem backend** on RWX **Azure Files** (`/data`, `/mnt/content`) |
| Container registry | **One shared ACR** (e.g. `meshweaver.azurecr.io`) across all solutions |
| Secrets | **One shared Key Vault** via the CSI Secrets Store add-on |
| Ingress / TLS | AKS **app routing** (managed nginx) + **cert-manager** + Let's Encrypt (HTTP-01) |
| Admin access | **P2S VPN** → `kubectl` / Grafana; nothing admin is public |
| Auth | External OIDC (Microsoft/Entra, Google, LinkedIn) — each provider opt-in |
| Orleans clustering | Single replica → `Localhost`; multi-replica requires `AzureTables`/`AdoNet` |

The ingress public IP is assigned by Azure. Retrieve it with `kubectl get svc -n app-routing-system`, then point your domain's A-record at it (see §5).

---

## 2. Images (Shared ACR)

Three images are pushed to the shared ACR. Grant the AKS kubelet `AcrPull` on the registry (cross-RG if needed).

| Image | Description |
|---|---|
| `<registry>/memex-portal-ai-base:latest` | `aspnet:10.0` + node20 + co-hosted CLIs (Claude Code + Copilot). The **one** hand-authored Dockerfile at `deploy/base-images/portal-ai`, built with `az acr build`. |
| `<registry>/memex-portal-ai:<tag>` | The portal app — an SDK container build on the base image. **Must pass `-r linux-x64`** (the Copilot SDK keys its binary off the RID). |
| `<registry>/memex-migration:<tag>` | One-shot DB migration container. |

Build and push the portal (no Dockerfile — the SDK's `PublishContainer` pushes straight to the registry):

```bash
az acr login --name <registry>
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj \
  -c Release -r linux-x64 --no-self-contained -t:PublishContainer \
  -p:ContainerRegistry=<registry>.azurecr.io -p:ContainerRepository=memex-portal-ai \
  -p:ContainerImageTag=<tag> -p:ContainerBaseImage=<registry>.azurecr.io/memex-portal-ai-base:latest
kubectl -n memex set image deployment/memex-portal-deployment memex-portal=<registry>.azurecr.io/memex-portal-ai:<tag>
```

> Use a **distinct tag** per build (not `:latest`) so the rollout is guaranteed to pull the new image.

---

## 3. Platform (Bicep)

`deploy/aks/infra/main.bicep` provisions the cluster, the VNet-injected Postgres Flexible Server, the VPN gateway, RWX storage, and (optionally) a per-deployment ACR. Set `useSharedAcr=true` to point at your shared registry instead.

Parameters live in `deploy/aks/infra/main.parameters.json`:
- **Region and node size/count** — stay within your vCPU quota.
- `postgresHighAvailability` — enable for production.
- `gatewaySku` — use an AZ SKU such as `VpnGw1AZ`.

The Postgres connection uses the **private IP + password + SSL** (`SslMode=Require;Trust Server Certificate=true`). Do not use the public FQDN form — a `database.azure.com` hostname routes the portal into its managed-identity-token branch, which does not match a password server.

---

## 4. Workload (Helm + Overlay)

`deploy/aks/scripts/deploy.sh` runs via `az aks command invoke` against the private cluster. In order, it:

1. Creates the namespace and RWX PVCs.
2. Runs `helm upgrade --install` with `deploy/helm` + `values.aks.yaml` + `values.deploy.yaml`.
3. Scales the chart's in-cluster Postgres to 0 (you use the Flexible Server).
4. Runs `kubectl set image` to the shared ACR.
5. Patches the portal to 1 replica with the Azure Files mounts.
6. **Patches the connection-string secret** to the external Postgres.

> **Known chart-generation gaps** (fix at the AddMemex generator):
> - The chart's `secrets.yaml` hardcodes the in-cluster Postgres connection string → `deploy.sh` patches it post-install.
> - The migration is rendered as a **Deployment**, not a Job, so Kubernetes reruns it after each clean exit → it can show `CrashLoopBackOff` even though every run **succeeds**. This is harmless, but should be a `Job` (see §11).

---

## 5. TLS, Ingress, and DNS

`deploy/aks/scripts/tls.sh` installs cert-manager, a Let's Encrypt `ClusterIssuer` (HTTP-01), and the portal ingress. HTTP→HTTPS redirect is automatic once TLS is active.

After the script runs:
1. Add a DNS A-record in your DNS zone pointing to the nginx LB public IP.
2. **Verify Blazor Server's sticky sessions** — the ingress sets a cookie-affinity cookie. Confirm the SignalR `/_blazor` WebSocket upgrade returns **HTTP 101** through managed nginx.

To expose another private tool publicly (e.g. Grafana), create a second `Ingress` with the same `ingressClassName` + `cert-manager.io/cluster-issuer` annotation, its own host and service, and a matching A-record at the same ingress IP. Note that this gates the tool only by its own login — weigh that against the "only the portal is public" stance.

---

## 6. External Sign-In (OAuth)

Deploy parameters flow through `AddMemex` → `MemexOptions` → portal environment variables (`Authentication__<Provider>__ClientId/Secret/TenantId`, `Social__LinkedIn__*`). A provider is offered in the sign-in UI only when its `ClientId` is set.

**Microsoft / Entra**
Register an app (`<entra-app-client-id>`) in your tenant (`<tenant-guid>`). Use single-tenant (`AzureADMyOrg`) for an internal portal. Set the redirect URI to `https://<your-domain>/signin-microsoft`. Also set `Authentication__Provider=Custom` and `Authentication__EnableDevLogin=false`.

**Google / LinkedIn**
Create the OAuth apps (redirects `/signin-google`, `/signin-linkedin`) and supply ClientId/Secret to enable each provider.

**Sign-in flow**
`/auth/login?provider=Microsoft` → the provider → `/signin-microsoft` (OIDC middleware signs the cookie) → `/auth/callback/Microsoft` (`ExternalAuthController` normalises claims; **ObjectId = email**) → `/`.

---

## 7. Onboarding and First Admin

`OnboardingMiddleware` (after `UserContextMiddleware`) intercepts an authenticated request whose email has no backing **User node** and redirects to `/onboarding`. `UserOnboardingService` then writes the partition-root User node + a User-catalog mirror, then grants self-Admin and (for the first user only) **platform-Admin** at `Admin/_Access`. All onboarding writes self-impersonate as **System** — `PostPipeline` fails closed without an identity context, and the user doesn't exist yet.

**First-admin bootstrap (operator tool)**

`BootstrapController` (`POST/GET /bootstrap/first-admin`) seeds the first admin server-side via the same `UserOnboardingService` write path. It is gated by the `Bootstrap:Secret` config value and disabled when that value is unset. Use it when the interactive `/onboarding` flow can't be driven, then **unset the secret**:

```bash
curl -sS "https://<your-domain>/bootstrap/first-admin?secret=<bootstrap-secret>&email=<admin-email>&username=<admin>"
```

---

## 8. Observability

`deploy/aks/scripts/install-observability.sh` installs the `grafana/loki-stack` chart (Grafana + Loki + Promtail + Prometheus) into the `monitoring` namespace.

- **Promtail** scrapes every pod's stdout into Loki — no portal-side configuration needed.
- **OTLP traces/metrics:** `AddMemex`'s `OtlpEndpoint` option wires `OTEL_EXPORTER_OTLP_ENDPOINT` (not needed for logs).
- **Grafana** defaults to ClusterIP (private). Reach it via the VPN (§9) + port-forward, or expose it publicly behind its own login (§5).

The observability stack is folded into the standard deploy: export `GRAFANA_PW` alongside `MEMEX_PG_CONN` and `deploy.sh` brings it up automatically.

---

## 9. Admin Access — The P2S VPN

Everything except the portal is private, so `kubectl` (private API server) and Grafana go through the **point-to-site VPN** — an AZ gateway SKU, OpenVPN + IKEv2, with a client address pool of your choice.

```bash
# A P2S root cert is uploaded to the gateway; the matching client cert lives in the operator's cert store.
az network vnet-gateway vpn-client generate -g <rg> -n <gateway> -o tsv   # download URL
# install + connect, then:
az aks get-credentials -g <rg> -n <cluster>
kubectl -n monitoring port-forward svc/loki-grafana 3000:80   # http://localhost:3000
```

> **`az` gotcha:** Recent CLI versions read `--public-cert-data` as a **file path** — pass the path to a base64 file, not the inline string and not `@file`.

---

## 10. Operations

All admin commands target the **private** API server. Without the VPN, run them via `az aks command invoke -g <rg> -n <cluster> --command "<kubectl…>"`. With the VPN active, plain `kubectl` works directly.

| Task | Command |
|---|---|
| **Logs (no VPN)** | `az aks command invoke -g <rg> -n <cluster> --command "kubectl -n memex logs deployment/memex-portal-deployment --tail=200"` |
| **Logs (Grafana)** | VPN → port-forward (§9) → `{namespace="memex"}` in Explore |
| Restart portal | `kubectl -n memex rollout restart deployment/memex-portal-deployment` |
| Pod status | `kubectl -n memex get pods` |
| Run SQL | `kubectl run … --image=postgres:17 … psql -h <pg-private-ip> -U <admin> -d memex` (password from Key Vault) |
| Reach Postgres | Private IP only (from inside the VNet or over the VPN) |

### 10.1 Update the Portal to a New Image

Build and push the image (§2), then repoint the deployment and wait for the rollout:

```bash
kubectl -n memex set image deployment/memex-portal-deployment memex-portal=<registry>.azurecr.io/memex-portal-ai:<tag>
kubectl -n memex rollout status deployment/memex-portal-deployment --timeout=220s
```

Use a **fresh tag** each build so the pull is guaranteed. Roll back by setting the previous tag.

### 10.2 View the Logs

- **Quick (no VPN):** `az aks command invoke -g <rg> -n <cluster> --command "kubectl -n memex logs deployment/memex-portal-deployment --since=10m"`
- **Dashboard (Grafana, via P2S VPN — §9):** `kubectl -n monitoring port-forward svc/loki-grafana 3000:80` → `http://localhost:3000` → Explore → `{namespace="memex"}` (add `|= "error"` to filter).

### 10.3 Enable / Configure AI Providers

Providers are gated by feature flags. Set them on the deployment once; each user then supplies their own credentials via **Settings → Models**.

```bash
kubectl -n memex set env deployment/memex-portal-deployment \
  Features__Ai__Providers__AzureFoundry=true Features__Ai__Providers__AzureOpenAI=true \
  Features__Ai__Providers__Anthropic=true   Features__Ai__Providers__OpenAI=true \
  Features__Ai__Clis__ClaudeCode=true       Features__Ai__Clis__Copilot=true \
  Ai__KeyProtection__MasterKey='<base64-32-byte-key>'    # encrypts stored provider credentials at rest
```

API providers (Anthropic, Azure OpenAI, Azure AI Foundry, OpenAI) work via **bring-your-own-key** — users add their endpoint + key per provider. The co-hosted CLIs (Claude Code, GitHub Copilot) require the **per-user Connect flow** (Phase 1 — see §11); the CLI binaries ship in the `portal-ai` image but the per-user login is not yet wired.

> The master key should live in Key Vault, not as a plaintext env var — see §10.6.

### 10.4 Restart, Scale, and Inspect

```bash
kubectl -n memex rollout restart deployment/memex-portal-deployment   # clears in-memory caches + any wedged hub
kubectl -n memex scale deployment/memex-portal-deployment --replicas=1  # >1 needs Orleans AzureTables clustering (§11)
kubectl -n memex get pods -o wide
```

### 10.5 Postgres: Query and Reset

Postgres is private (VNet IP only). Run SQL via a throwaway pod inside the cluster:

```bash
kubectl -n memex run pg --restart=Never --rm -i --image=postgres:17 \
  --env=PGPASSWORD=<pw> --env=PGSSLMODE=require --command -- \
  psql -h <pg-private-ip> -U <admin> -d memex -c "SELECT count(*) FROM auth.mesh_nodes WHERE node_type='User';"
```

To reset to the post-initialize state, drop the per-user partition schemas and truncate content (keep `admin.db_version`), then **restart the portal** — direct SQL bypasses the workspace cache.

### 10.6 Secrets via Key Vault (CSI Secrets Store)

Production secrets live in **`meshweaverkeyvault`** (access-policy mode) and are projected into the pod by the AKS **CSI Secrets Store** add-on — no plaintext env vars; the vault is the source of truth.

One-time wiring: grant the CSI add-on's identity `get/list` on the vault, store each secret, then create a `SecretProviderClass` that maps Key Vault secret names → env-var keys and syncs them into a k8s Secret the deployment mounts (CSI volume) and reads via `envFrom`:

```bash
# CSI identity object id: az aks show -g <rg> -n <cluster> --query addonProfiles.azureKeyvaultSecretsProvider.identity.objectId -o tsv
az keyvault set-policy -n meshweaverkeyvault --object-id <csi-identity-objectid> --secret-permissions get list
az keyvault secret set --vault-name meshweaverkeyvault --name ai-keyprotection-masterkey --value '<value>'   # dashes only in KV names
# SecretProviderClass `memex-kv` maps ai-keyprotection-masterkey -> Ai__KeyProtection__MasterKey (and the
# PG conn / Microsoft secret / Bootstrap secret) and syncs them into the `memex-kv-secrets` k8s Secret;
# the portal has a CSI volume for `memex-kv` + `envFrom: secretRef: memex-kv-secrets`.
```

**To rotate a secret:** `az keyvault secret set` (creates a new version) → `kubectl -n memex rollout restart deployment/memex-portal-deployment` (the CSI driver re-reads on the next mount).

The `SecretProviderClass` + the CSI volume/`envFrom` are applied post-`deploy.sh` today. Folding them into the chart is tracked in §11.

> **Windows `az` gotcha:** the CLI's console writer cannot encode non-ASCII characters in cp1252 and crashes on a raw log dump. Pipe the cluster-side command through `tr -cd '\11\12\15\40-\176'` to strip non-ASCII before `az` prints it.

---

## 11. Known Issues and Follow-ups

**Route-derived spurious partitions**
Visiting an auth-flow route (`/onboarding`, `/login`, `/welcome`) can create a same-named partition *schema*. The router then tries to activate that empty partition and its hub deadlocks (messages time out at 30s and retry). Fix: drop the spurious schemas and trace the code path that maps a route to a partition address.

**Static/seed user shadowing onboarding**
If a static node provider seeds a User for the admin email, a fresh `CreateUser` fails with "Node already exists" and the interactive form shows "user exists" even with 0 DB users. Remove the seed so real onboarding can persist the partition root.

**Secrets in Key Vault (done)**
The master key, PG connection string, Microsoft client secret, and `Bootstrap:Secret` live in `meshweaverkeyvault`; a `SecretProviderClass` + the AKS CSI Secrets Store add-on sync them into a k8s Secret the portal reads via `envFrom` (see §10.6). Remaining: the Grafana admin password (monitoring namespace), and folding the `SecretProviderClass` + deployment CSI volume/`envFrom` into the chart/AddMemex so a fresh deploy wires Key Vault automatically instead of requiring the current post-`deploy.sh` patch.

**Multi-replica HA**
Needs Orleans `AzureTables`/`AdoNet` clustering wired on the Filesystem backend.

**Migration as a Job**
The migration container is currently rendered as a Deployment (see §4). It should be a `Job`.

**Release image**
Replace any temporary debug image tag with a clean `latest`/release tag before treating the deployment as final.
