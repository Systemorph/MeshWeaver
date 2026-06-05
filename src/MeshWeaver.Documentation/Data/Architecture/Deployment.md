---
Name: Deployment
Category: Architecture
Description: Deploying MeshWeaver with .NET Aspire to Azure Container Apps — modes, secrets, infrastructure, and the deploy wrapper that catches silent migration failures
Icon: Cloud
---

MeshWeaver uses **.NET Aspire** for orchestration and deployment. The AppHost project (`memex/aspire/Memex.AppHost`) is the single source of truth for every infrastructure resource — PostgreSQL, Azure Blob Storage, Orleans clustering, Application Insights — and provisions them all automatically.

---

# Deployment Modes

The AppHost supports four modes, selected via `--mode <mode>` on the command line.

| Mode | PostgreSQL | Blob Storage | Orleans | Portal name |
|---|---|---|---|---|
| `local` | Docker pgvector container | Azurite emulator | Emulated (in-process) | memex-local |
| `test` | Azure (memex-test) | Azure (meshweavermemextest) | Azure | memex-test |
| `prod` | Azure (memex) | Azure (meshweavermemex) | Azure | memex-prod |
| `monolith` | FileSystem (standalone) | — | — | memex-monolith |

---

# Deploying to Azure

## Prerequisites

Before running a deploy, confirm:

1. **Azure CLI** is authenticated — `az login`
2. **Aspire CLI** is installed — `dotnet tool install -g aspire`
3. **Docker** is running (required to build container images)
4. **Secrets** are configured in the AppHost project (see [Secrets Management](#secrets-management) below)
5. **dotnet-script** is installed for the post-deploy DB version check — `dotnet tool install -g dotnet-script`

## 🚀 Canonical: deploy a code update to AKS

The portals now run on the shared **AKS cluster** `memexaks-cluster` (resource group `memex-aks-rg`, region swedencentral). Each environment is a namespace (`atioz`, `memex`), backed by the Postgres Flexible Server; container images live in ACR `meshweaver.azurecr.io`.

> **The cluster is private.** `kubectl` is not reachable directly — every command runs through `az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "…"`, which executes inside the cluster's API-server-side runner.

A **code update** is three steps — build the images, point the Deployments at the new tag, restart. It is **not** `tools/deploy.sh` and **not** `aspire deploy` (both are legacy ACA, see below).

### 1. Build + push the images

```bash
az acr login -n meshweaver

# Portal — needs the prebuilt custom base image:
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-portal-ai -p:ContainerImageTag=<tag> \
  -p:ContainerBaseImage=meshweaver.azurecr.io/memex-portal-ai-base:latest

# Migration — this is what creates the schema, the partition_access table, AND the
# public.top_level_index materialized view. A schema/index change ships in THIS image:
dotnet publish memex/aspire/Memex.Database.Migration/Memex.Database.Migration.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-migration -p:ContainerImageTag=<tag>
```

Pick a `<tag>` that pins the change (e.g. `bugfix-2026-06-05`). CI also builds images on push, but it lags — check `az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5` before assuming your commit is built; if it isn't, build manually as above.

### 2. Roll out (NS = `atioz` | `memex`)

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "\
  kubectl -n <NS> set image deployment/memex-portal-deployment memex-portal=meshweaver.azurecr.io/memex-portal-ai:<tag>; \
  kubectl -n <NS> set image deployment/memex-migration-deployment memex-migration=meshweaver.azurecr.io/memex-migration:<tag>; \
  kubectl -n <NS> rollout restart deployment/memex-migration-deployment deployment/memex-portal-deployment; \
  kubectl -n <NS> rollout status deployment/memex-portal-deployment --timeout=300s"
```

### 3. Verify

- Migration ran: `az aks command invoke … --command "kubectl -n <NS> logs deployment/memex-migration-deployment --tail=40"` → expect `Database migration completed. Version: N`. The migration pod exits 0 and the Deployment restarts it, so a *benign* `CrashLoopBackOff` on `memex-migration` is normal — read the log, don't panic on the status.
- Portal serves: `curl -sS -o /dev/null -w '%{http_code}' https://<NS>.meshweaver.cloud/` → `200`.
- Schema/index applied (when the change was a migration): spot-check via `az aks command invoke … "kubectl -n <NS> exec deployment/memex-portal-deployment -- …"` or an MCP query.

### First-time environment setup ≠ code update

`deploy/aks/envs/<env>/deploy.sh` provisions a **new** environment: `helm install` of the chart, PVCs, the Key Vault `SecretProviderClass`, ingress, and the connection-string patch. **Do not run it for a code update** — it re-applies the whole chart and can reset live ConfigMaps (e.g. the email config). Use it only when standing up a brand-new namespace.

---

## Legacy: Azure Container Apps (`tools/deploy.sh` / `aspire deploy`)

> The sections below describe the **old Azure Container Apps** deployment. They are kept for historical reference. New deploys go to **AKS** via the method above. Do **not** run bare `aspire deploy` against the current infrastructure.

<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="320" rx="12" fill="none"/>
  <text x="380" y="22" text-anchor="middle" font-size="14" font-weight="bold" fill="currentColor" fill-opacity=".85">tools/deploy.sh — Three-step safe deploy</text>
  <rect x="20" y="40" width="130" height="52" rx="10" fill="#5c6bc0"/>
  <text x="85" y="62" text-anchor="middle" fill="#fff" font-weight="bold">tools/deploy.sh</text>
  <text x="85" y="80" text-anchor="middle" fill="#fff" font-size="11">prod | test</text>
  <line x1="150" y1="66" x2="198" y2="66" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="200" y="40" width="150" height="52" rx="10" fill="#1e88e5"/>
  <text x="275" y="60" text-anchor="middle" fill="#fff" font-weight="bold">Step 1</text>
  <text x="275" y="76" text-anchor="middle" fill="#fff" font-size="11">aspire deploy</text>
  <text x="275" y="89" text-anchor="middle" fill="#fff" font-size="11">(AppHost + mode)</text>
  <line x1="350" y1="66" x2="398" y2="66" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="400" y="40" width="160" height="52" rx="10" fill="#1e88e5"/>
  <text x="480" y="60" text-anchor="middle" fill="#fff" font-weight="bold">Step 2</text>
  <text x="480" y="76" text-anchor="middle" fill="#fff" font-size="11">Poll db-migration</text>
  <text x="480" y="89" text-anchor="middle" fill="#fff" font-size="11">exit code via az CLI</text>
  <line x1="560" y1="66" x2="608" y2="66" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="610" y="40" width="130" height="52" rx="10" fill="#1e88e5"/>
  <text x="675" y="60" text-anchor="middle" fill="#fff" font-weight="bold">Step 3</text>
  <text x="675" y="76" text-anchor="middle" fill="#fff" font-size="11">check-db-version</text>
  <text x="675" y="89" text-anchor="middle" fill="#fff" font-size="11">db_version ≥ 15</text>
  <rect x="400" y="115" width="160" height="42" rx="10" fill="#e53935"/>
  <text x="480" y="133" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Non-zero exit code?</text>
  <text x="480" y="149" text-anchor="middle" fill="#fff" font-size="11">Fail + dump 100 log lines</text>
  <line x1="480" y1="92" x2="480" y2="115" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="675" y1="92" x2="675" y2="136" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5"/>
  <rect x="610" y="115" width="130" height="42" rx="10" fill="#e53935"/>
  <text x="675" y="133" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Version mismatch?</text>
  <text x="675" y="149" text-anchor="middle" fill="#fff" font-size="11">Fail deploy</text>
  <text x="380" y="195" text-anchor="middle" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".75">Portal-side safeguards (runtime)</text>
  <line x1="380" y1="200" x2="380" y2="208" stroke="currentColor" stroke-opacity=".4" stroke-width="1"/>
  <rect x="100" y="215" width="230" height="72" rx="10" fill="#43a047"/>
  <text x="215" y="235" text-anchor="middle" fill="#fff" font-weight="bold">DbVersionGate</text>
  <text x="215" y="252" text-anchor="middle" fill="#fff" font-size="11">IHostedService at startup</text>
  <text x="215" y="267" text-anchor="middle" fill="#fff" font-size="11">Checks db_version ≥ 15</text>
  <text x="215" y="281" text-anchor="middle" fill="#fff" font-size="11">Stops app if below → revision Failed</text>
  <rect x="430" y="215" width="230" height="72" rx="10" fill="#43a047"/>
  <text x="545" y="235" text-anchor="middle" fill="#fff" font-weight="bold">DbVersionHealthCheck</text>
  <text x="545" y="252" text-anchor="middle" fill="#fff" font-size="11">Live healthcheck</text>
  <text x="545" y="267" text-anchor="middle" fill="#fff" font-size="11">Wraps same db_version query</text>
  <text x="545" y="281" text-anchor="middle" fill="#fff" font-size="11">Catches post-deploy manual drift</text>
</svg>

*The deploy wrapper closes the silent-failure gap in `aspire deploy` with two poller steps, backed by two runtime safeguards inside the portal.*

```bash
tools/deploy.sh prod    # or: tools/deploy.sh test
```

Running `aspire deploy` on its own **silently passes when the db-migration container crashes**. Aspire's pipeline reports `✓ provision-db-migration-containerapp completed successfully` as soon as the Container App *definition* provisions — it does not watch the migration container's actual exit code. The result is a half-migrated database, an exit-0 deploy, and a portal that comes up against broken data with 401 errors for every user.

The wrapper script closes that gap in three steps:

1. Runs `aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode <prod|test>` (the command Aspire docs sanction).
2. After Aspire returns, polls `az containerapp replica list -n db-migration -g <rg>` until the replica reaches `Terminated`, then reads `lastTerminationState.exitCode`. A non-zero exit fails the deploy and dumps the last 100 log lines.
3. Runs `tools/check-db-version.csx` to assert `admin.mesh_nodes.db_version >= 15` against the deployed DB via AAD-authenticated psql — catching the edge case where the migration container exited 0 but crashed inside a `try/catch` that swallowed the exception.

Two additional safeguards run inside the portal itself:

- **`DbVersionGate`** (`Memex.Portal.Distributed/DbVersionGate.cs`) — an `IHostedService` that queries `admin.mesh_nodes.db_version` at portal startup and calls `IHostApplicationLifetime.StopApplication()` if the version is missing or below `ExpectedDbVersion = 15`. Container Apps then marks the revision `Failed` and routes no traffic to it.
- **`DbVersionHealthCheck`** — a live healthcheck wrapping the same query, surfacing any drift if someone manually runs a partial migration via `psql` after startup.

> **Keep these in sync.** Bump `DbVersionGate.ExpectedDbVersion` and the `ExpectedVersion` constant in `tools/check-db-version.csx` in lock-step with the highest `Vxx_*.cs` migration file in `memex/aspire/Memex.Database.Migration/Migrations/`.

> **Why not gate this inside `aspire deploy` itself?** Aspire 13.2.x has no first-party API for a deploy-time callback that can poll a provisioned resource and fail the pipeline. The required `DeployingCallbackAnnotation` + `IReportingTask.FailAsync` surface ships in **Wave 14**. When the project upgrades to 14.x, the bash poller can move into `Memex.AppHost/Program.cs` as an annotation on the `db-migration` resource, and `tools/deploy.sh` can collapse back to a thin alias.

## Verifying a Deployment

`tools/deploy.sh` already runs the version gate automatically. If you ran `aspire deploy` directly, verify manually:

```bash
dotnet script tools/check-db-version.csx -- prod
```

Expect `✅ db_version=15 (>= 15)`.

After verification, open the portal URL in a browser, check the Aspire dashboard for service health, and review Application Insights for startup telemetry.

---

# Running Locally

## Aspire (local mode)

For full local development with Docker containers:

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode local
```

This starts PostgreSQL (pgvector) and Azurite in Docker containers, with Orleans running in-process.

## Monolith (standalone, no Docker)

For a lighter setup without Orleans or external infrastructure:

```bash
dotnet run --project memex/Memex.Portal.Monolith
```

Or via the AppHost:

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode monolith
```

---

# Infrastructure

## Azure Container Apps

Deployed modes (`test`, `prod`) run on **Azure Container Apps** in Sweden Central with sticky sessions enabled for Blazor Server.

## PostgreSQL

- **Local** — Docker container with pgvector extension (`pgvector/pgvector:pg17`)
- **Deployed** — Azure PostgreSQL Flexible Server with pgvector, provisioned automatically by Aspire

## Azure Blob Storage

Content files (attachments, documents) live in Azure Blob Storage.

- **Local** — Azurite emulator with a persistent data bind mount
- **Deployed** — Azure Storage Account provisioned in Sweden Central

## Orleans

Orleans provides distributed actor clustering for the microservices deployment.

- **Local** — Emulated in-process
- **Deployed** — Azure Table Storage for clustering, Azure Blob Storage for grain state

## Application Insights

Telemetry and distributed tracing via Azure Application Insights are provisioned automatically in all deployed modes.

---

# Azure AD App Registration

Microsoft authentication requires an app registration in Microsoft Entra ID (Azure AD).

1. **Azure Portal** → **App registrations** → select your app (or create one)
2. Under **Authentication** → **Platform configurations** → **Web**, add redirect URIs:
   - `http://localhost:5000/signin-microsoft` (local development)
   - `https://<your-deployed-domain>/signin-microsoft` (deployed environments)
3. Note the **Application (client) ID** and **Directory (tenant) ID** from the **Overview** page
4. Under **Certificates & secrets**, create a client secret

For single-tenant apps, configure the tenant ID explicitly — the default `/common` endpoint is not supported.

---

# Secrets Management

Secrets are stored in `dotnet user-secrets` for local development and in GitHub secrets for CI/CD.

Required secrets for distributed modes:

| Secret | Description |
|---|---|
| `Parameters:azure-foundry-key` | Azure AI Foundry API key (LLM access) |
| `Parameters:embedding-endpoint` | Embedding model endpoint |
| `Parameters:embedding-key` | Embedding model API key |
| `Parameters:embedding-model` | Embedding model name |
| `Parameters:microsoft-client-id` | Microsoft OAuth client ID |
| `Parameters:microsoft-client-secret` | Microsoft OAuth client secret |
| `Parameters:microsoft-tenant-id` | Microsoft Entra tenant ID (single-tenant apps) |
| `Parameters:google-client-id` | Google OAuth client ID |
| `Parameters:google-client-secret` | Google OAuth client secret |
| `Parameters:custom-domain` | Custom domain for the deployed portal |
| `Parameters:certificate-name` | TLS certificate name for the custom domain |

Set a secret with:

```bash
cd memex/aspire/Memex.AppHost
dotnet user-secrets set "Parameters:azure-foundry-key" "<your-key>"
```

---

# Project Structure

```
memex/aspire/
├── Memex.AppHost/                  # Aspire orchestrator — defines all resources
├── Memex.Portal.Distributed/       # Portal with co-hosted Orleans silo
├── Memex.Portal.Orleans/           # Orleans grain interfaces
├── Memex.Portal.ServiceDefaults/   # Shared service defaults (health, telemetry)
└── Memex.Database.Migration/       # Database migration project
```
