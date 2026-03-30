---
Name: Deployment
Category: Architecture
Description: Deploying MeshWeaver with .NET Aspire to Azure Container Apps, including modes, secrets, and infrastructure
Icon: Cloud
---

MeshWeaver uses **.NET Aspire** for orchestration and deployment. The Aspire AppHost (`memex/aspire/Memex.AppHost`) defines all infrastructure resources — PostgreSQL, Azure Blob Storage, Orleans clustering, Application Insights — and provisions them automatically via the Aspire CLI.

# Prerequisites

## Aspire CLI

Install the Aspire CLI as a global .NET tool:

```bash
dotnet tool install -g aspire.cli
```

Verify installation:

```bash
aspire --version
```

## Azure Login

You must be logged into Azure before deploying:

```bash
az login
azd auth login
```

## User Secrets

All parameters must be configured via `dotnet user-secrets` before deploying. Without secrets, `aspire deploy` will prompt interactively for each missing parameter and fail in non-interactive environments (CI/CD, piped shells).

Set secrets from the AppHost project directory:

```bash
cd memex/aspire/Memex.AppHost
dotnet user-secrets set "Parameters:azure-foundry-key" "<your-key>"
dotnet user-secrets set "Parameters:microsoft-client-id" "<client-id>"
dotnet user-secrets set "Parameters:microsoft-client-secret" "<secret-value>"
```

Or from the repository root using `--project`:

```bash
dotnet user-secrets set "Parameters:azure-foundry-key" "<your-key>" --project memex/aspire/Memex.AppHost
```

The three secrets above are **required** — deployment fails without them. The following are **optional** (features are disabled when absent — the AppHost skips these parameters automatically):

```bash
dotnet user-secrets set "Parameters:microsoft-tenant-id" "<tenant-guid>"
dotnet user-secrets set "Parameters:embedding-endpoint" "<endpoint>"
dotnet user-secrets set "Parameters:embedding-key" "<key>"
dotnet user-secrets set "Parameters:embedding-model" "<model>"
dotnet user-secrets set "Parameters:google-client-id" "<client-id>"
dotnet user-secrets set "Parameters:google-client-secret" "<secret>"
dotnet user-secrets set "Parameters:custom-domain" "<domain>"
dotnet user-secrets set "Parameters:certificate-name" "<cert-name>"
```

> **Important:** For `microsoft-client-secret`, use the secret **value** (the string shown once when you create the secret), not the secret **ID** (the GUID). These are different fields in the Azure portal.

Verify secrets are configured:

```bash
dotnet user-secrets list --project memex/aspire/Memex.AppHost
```

# Deployment Modes

The AppHost supports multiple modes, passed as `--mode <mode>`:

| Mode        | PostgreSQL                   | Blob Storage                  | Orleans   | Portal Name     |
|-------------|------------------------------|-------------------------------|-----------|-----------------|
| `local`     | Docker pgvector container    | Emulated (Azurite)            | Emulated  | memex-local     |
| `local-test`| Azure (memex-test)           | Azure (meshweavermemextest)   | Emulated  | memex-local     |
| `local-prod`| Azure (memex)                | Azure (meshweavermemex)       | Emulated  | memex-local     |
| `test`      | Azure (memex-test)           | Azure (meshweavermemextest)   | Azure     | memex-test      |
| `prod`      | Azure (memex)                | Azure (meshweavermemex)       | Azure     | memex-prod      |
| `monolith`  | FileSystem (standalone)      | —                             | —         | memex-monolith  |

# Deploying to Azure

All `aspire deploy` commands require `-e Development`. This tells Aspire to use the **ASP.NET Development environment**, which is what loads user secrets. Without it, Aspire defaults to the Production environment where user secrets are ignored, causing interactive prompts that fail in non-interactive terminals. The **deployment target** (test vs prod) is controlled separately by the `--mode` flag.

## Deploy to Test

```bash
aspire deploy -e Development --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode test
```

## Deploy to Production

```bash
aspire deploy -e Development --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode prod
```

## What `aspire deploy` Does

1. **Builds** the AppHost project and resolves the Aspire application model
2. **Prompts for parameters** — reads from user-secrets; prompts interactively for any missing values
3. **Generates Bicep** infrastructure templates (stored in `memex/aspire/Memex.AppHost/infra/`)
4. **Provisions Azure resources** via ARM deployment (resource group, Container App Environment, PostgreSQL, Blob Storage, etc.)
5. **Builds container images** for each project (portal, db-migration)
6. **Pushes images** to the provisioned Azure Container Registry
7. **Deploys Container Apps** with the configured environment variables, scaling rules, and custom domain

## Generated Infrastructure

The first `aspire deploy` generates Bicep templates in `memex/aspire/Memex.AppHost/infra/`:

```
infra/
├── main.bicep                    # Root deployment template
├── main.parameters.json          # Parameter bindings (uses ${AZURE_*} env vars)
├── memex-aca/                    # Container App Environment
├── memex-aca-acr/                # Container Registry
├── memex-test/ or memex-prod/    # Portal Container App
├── db-migration/                 # Migration Container App (run-to-completion)
├── memex-postgres/               # PostgreSQL Flexible Server
├── memexblobs/                   # Azure Storage Account
├── orleansstorage/               # Orleans Storage Account
├── appinsights/                  # Application Insights
└── *-identity/, *-roles-*/       # Managed identities and role assignments
```

These templates are committed to the repo and reused on subsequent deploys. To regenerate them:

```bash
aspire generate --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode test
```

# Running Locally

## Local Development (Docker)

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj
```

This starts in `local` mode by default, using Docker pgvector and emulated Azure services (Azurite).

## Local with Azure Databases

To run locally against Azure test or prod databases:

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode local-test
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode local-prod
```

These modes connect to Azure PostgreSQL and Blob Storage while keeping Orleans emulated locally. Requires:
- `ConnectionStrings:memex` user secret (PostgreSQL connection string)
- Active `az login` session (for Blob Storage via Azure Identity)

# Monolith Mode

For standalone development without Orleans or external infrastructure:

```bash
dotnet run --project memex/Memex.Portal.Monolith
```

Or via Aspire:

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode monolith
```

# Infrastructure

## Azure Container Apps

Deployed modes (`test`, `prod`) run on **Azure Container Apps** in Sweden Central:
- **Sticky sessions** enabled for Blazor Server (session affinity)
- **Custom domain** with managed TLS certificate
- **Scaling**: 2–6 replicas (min 2 for Orleans resilience), 2 vCPU / 4Gi per replica

## PostgreSQL

- **Local**: Docker container with pgvector extension (`pgvector/pgvector:pg17`)
- **Deployed**: Azure PostgreSQL Flexible Server with pgvector, provisioned automatically
- **local-test/local-prod**: Connects to existing Azure PostgreSQL via connection string

## Azure Blob Storage

Content files (attachments, documents) and data protection keys are stored in Azure Blob Storage.

- **Local**: Azurite emulator with persistent data bind mount (`Azurite/Data/`)
- **Deployed**: Azure Storage Account provisioned in Sweden Central
- **local-test/local-prod**: Connects via Azure Identity (`az login`), no secrets needed

## Orleans

Orleans provides distributed actor clustering for the microservices deployment.

- **Local/local-test/local-prod**: Emulated (in-process)
- **Deployed**: Azure Table Storage for clustering, Azure Blob Storage for grain state

## Application Insights

Telemetry and distributed tracing via Azure Application Insights, provisioned automatically in all modes.

# Secrets Reference

| Secret | Required | Description |
|--------|----------|-------------|
| `Parameters:azure-foundry-key` | **Yes** | Azure AI Foundry API key (LLM access) |
| `Parameters:microsoft-client-id` | **Yes** | Microsoft OAuth client ID |
| `Parameters:microsoft-client-secret` | **Yes** | Microsoft OAuth client secret (**value**, not ID) |
| `Parameters:embedding-endpoint` | No | Embedding model endpoint (embedding disabled when absent) |
| `Parameters:embedding-key` | No | Embedding model API key |
| `Parameters:embedding-model` | No | Embedding model name |
| `Parameters:microsoft-tenant-id` | No | Microsoft Entra tenant ID (defaults to `common` for multi-tenant) |
| `Parameters:google-client-id` | No | Google OAuth client ID (Google login disabled when absent) |
| `Parameters:google-client-secret` | No | Google OAuth client secret |
| `Parameters:custom-domain` | No | Custom domain for deployed portal |
| `Parameters:certificate-name` | No | TLS certificate name for custom domain |
| `ConnectionStrings:memex` | local-test/local-prod only | Azure PostgreSQL connection string |

# Project Structure

```
memex/aspire/
├── Memex.AppHost/               # Aspire orchestrator (defines all resources)
│   ├── Program.cs               # Mode matrix, resource definitions, parameters
│   ├── azure.yaml               # Azure Developer CLI service definition
│   └── infra/                   # Generated Bicep templates (committed)
├── Memex.Portal.Distributed/    # Portal with co-hosted Orleans silo
├── Memex.Portal.Orleans/        # Orleans grain interfaces
├── Memex.Portal.ServiceDefaults/# Shared service defaults (health, telemetry)
└── Memex.Database.Migration/    # Database migration project (run-to-completion)
```

# First-Time Deployment Checklist

After the first `aspire deploy` completes and Azure resources are provisioned, complete these one-time setup steps:

## 1. Register Redirect URIs in Microsoft Entra

Go to the [Azure Portal](https://portal.azure.com) > App registrations > your app > Authentication > Add a platform (Web):

- Add redirect URI: `https://<your-aca-domain>/signin-microsoft`
- The ACA domain is shown in the deploy output (e.g., `memex-test.whiteplant-79bbc284.swedencentral.azurecontainerapps.io`)
- If using a custom domain, add that redirect URI as well

## 2. Allow-list pgvector Extension on Azure PostgreSQL

Azure PostgreSQL Flexible Server requires explicit extension allow-listing. Without this, the database migration fails silently on vector-related operations:

```bash
az postgres flexible-server parameter set \
  --resource-group <your-rg> \
  --server-name <your-postgres-server> \
  --name azure.extensions \
  --value "vector"
```

Find the server name in the Azure portal or from the Aspire deployment output.

## 3. Restart the Database Migration

After allow-listing pgvector, restart the `db-migration` container app so it re-runs schema initialization:

```bash
az containerapp revision restart \
  --name db-migration \
  --resource-group <your-rg> \
  --revision <latest-revision>
```

Or redeploy via `aspire deploy` which rebuilds and redeploys all container apps.

# Troubleshooting

## "Failed to read input in non-interactive mode"
All parameters must be set via `dotnet user-secrets` before deploying. Also ensure you pass `-e Development` to `aspire deploy` — without it, user secrets are not loaded. Run `dotnet user-secrets list --project memex/aspire/Memex.AppHost` to check which are missing.

## Aspire deployment state cache
`aspire deploy` caches parameter values in `~/.aspire/deployments/<hash>/development.json`. If you update a secret via `dotnet user-secrets`, the cached value is **not** automatically refreshed. To force a refresh, either:
- Delete the cache file and redeploy (Aspire will re-read from user secrets)
- Manually edit the `Parameters` section in the cached JSON file

## Container App not reachable after deploy
Check that `UseForwardedHeaders()` is enabled in `MemexConfiguration.cs` — Azure Container Apps uses a reverse proxy that sets `X-Forwarded-*` headers. Without forwarded headers, HTTPS redirects and OAuth callbacks fail.

## Microsoft login: AADSTS50194 (not configured as multi-tenant)
The app registration is single-tenant but the OIDC authority URL defaults to `/common`. Set the `microsoft-tenant-id` parameter to your Entra tenant GUID (see Secrets Reference). Without it, the AppHost defaults to `common` (multi-tenant).

## Microsoft login: AADSTS50011 (redirect URI mismatch)
The ACA URL must be registered as a redirect URI in the Microsoft Entra app registration. Add `https://<your-aca-domain>/signin-microsoft` under Authentication > Web > Redirect URIs.

## Microsoft login: AADSTS7000215 (invalid client secret)
Two common causes:
1. **Secret ID vs secret value**: The `microsoft-client-secret` parameter must be the secret **value** (shown once at creation), not the secret **ID** (a GUID).
2. **Stale deployment cache**: Even after updating `dotnet user-secrets`, the old value may persist in `~/.aspire/deployments/`. See "Aspire deployment state cache" above.

## Microsoft login redirects to blank page
Ensure the redirect URI `https://<your-domain>/signin-microsoft` is registered in the Microsoft Entra app registration. The portal constructs this from the forwarded host header.

## "function rebuild_user_effective_permissions() does not exist"
The database migration (`db-migration` container app) failed before creating schema functions. Common cause: pgvector extension was not allow-listed on Azure PostgreSQL (see First-Time Deployment Checklist above). Check db-migration logs, fix the root cause, and redeploy.

## Sample data (ACME, Northwind, etc.) missing in deployed environment
Sample data nodes are loaded from `samples/Graph/Data/` via `AddGraph()`. Verify the container image includes these files and that the portal starts successfully (check Application Insights logs).
