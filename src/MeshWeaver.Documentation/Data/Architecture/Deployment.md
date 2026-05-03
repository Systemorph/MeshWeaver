---
Name: Deployment
Category: Architecture
Description: Deploying MeshWeaver with .NET Aspire to Azure Container Apps, including modes, secrets, and infrastructure
Icon: Cloud
---

MeshWeaver uses **.NET Aspire** for orchestration and deployment. The Aspire AppHost (`memex/aspire/Memex.AppHost`) defines all infrastructure resources — PostgreSQL, Azure Blob Storage, Orleans clustering, Application Insights — and provisions them automatically via the Aspire CLI.

# Deployment Modes

The AppHost supports multiple modes, passed as `--mode <mode>`:

| Mode        | PostgreSQL                   | Blob Storage                  | Orleans   | Portal Name     |
|-------------|------------------------------|-------------------------------|-----------|-----------------|
| `local`     | Docker pgvector container    | Emulated (Azurite)            | Emulated  | memex-local     |
| `test`      | Azure (memex-test)           | Azure (meshweavermemextest)   | Azure     | memex-test      |
| `prod`      | Azure (memex)                | Azure (meshweavermemex)       | Azure     | memex-prod      |
| `monolith`  | FileSystem (standalone)      | —                             | —         | memex-monolith  |

# How to Deploy

## Prerequisites

1. **Azure CLI** authenticated (`az login`)
2. **Aspire CLI** installed (`dotnet tool install -g aspire`)
3. **Docker** running (required for building container images)
4. **Secrets configured** in the AppHost project (see [Secrets Management](#secrets-management) below)
5. **dotnet-script** for the post-deploy DB version check (`dotnet tool install -g dotnet-script`)

## 🚨 Always use `tools/deploy.sh` — never bare `aspire deploy`

```bash
tools/deploy.sh prod    # or: tools/deploy.sh test
```

`aspire deploy` on its own **silently passes when the db-migration container crashes**. Aspire's pipeline reports `✓ provision-db-migration-containerapp completed successfully` as soon as the Container App *definition* provisions — it doesn't watch the actual migration container's exit code. Result: a half-migrated DB, an exit-0 deploy, and a portal that comes up against broken data and 401s every user.

The wrapper script closes that gap:

1. Runs `aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode <prod|test>` (the same command Aspire docs sanction).
2. After Aspire returns, polls `az containerapp replica list -n db-migration -g <rg>` until the replica reaches `Terminated` and reads `lastTerminationState.exitCode`. Non-zero → fails the deploy with the last 100 log lines.
3. Runs `tools/check-db-version.csx` to assert `admin.mesh_nodes.db_version >= 15` against the deployed DB via AAD-authenticated psql. End-to-end check — catches the case where the migration container terminated 0 but didn't finish (e.g., crashed inside a try/catch that swallowed the exception).

Two layers of belt-and-braces backstop the wrapper inside the portal itself:

- **`DbVersionGate` (`Memex.Portal.Distributed/DbVersionGate.cs`)**: `IHostedService` that runs at portal startup, queries `admin.mesh_nodes.db_version`, and calls `IHostApplicationLifetime.StopApplication()` if it's missing or below `ExpectedDbVersion = 15`. Container Apps then marks the revision `Failed` and routes no traffic to it.
- **`DbVersionHealthCheck`**: Live healthcheck wrapping the same query — surfaces drift if someone manually rolls a partial migration via `psql` after startup.

Bump `DbVersionGate.ExpectedDbVersion` and `tools/check-db-version.csx`'s `ExpectedVersion` constant in lock-step with the highest `Vxx_*.cs` migration in `memex/aspire/Memex.Database.Migration/Migrations/`.

> **Why not gate this inside `aspire deploy` itself?** Aspire 13.2.x has no first-party API for a deploy-time callback that can poll a provisioned resource and fail the pipeline. The required `DeployingCallbackAnnotation` + `IReportingTask.FailAsync` surface ships in **Wave 14**. When we bump to 14.x, the bash poller can move into `Memex.AppHost/Program.cs` as an annotation on the `db-migration` resource, and `tools/deploy.sh` can collapse back to a thin alias.

## Verify Deployment

`tools/deploy.sh` already runs the version gate. If you ran `aspire deploy` directly, verify manually:

```bash
dotnet script tools/check-db-version.csx -- prod
```

Expect `✅ db_version=15 (>= 15)`.

After verification:
- Open the portal URL in a browser
- Check the Aspire dashboard for service health
- Review Application Insights for startup telemetry

# Running Locally

For local development with Docker containers:

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode local
```

This starts in `local` mode by default, using Docker pgvector and emulated Azure services.

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

Deployed modes (`test`, `prod`) run on **Azure Container Apps** in Sweden Central with sticky sessions enabled for Blazor Server.

## PostgreSQL

- **Local**: Docker container with pgvector extension (`pgvector/pgvector:pg17`)
- **Deployed**: Azure PostgreSQL Flexible Server with pgvector, provisioned automatically

## Azure Blob Storage

Content files (attachments, documents) are stored in Azure Blob Storage.

- **Local**: Azurite emulator with persistent data bind mount
- **Deployed**: Azure Storage Account provisioned in Sweden Central

## Orleans

Orleans provides distributed actor clustering for the microservices deployment.

- **Local**: Emulated (in-process)
- **Deployed**: Azure Table Storage for clustering, Azure Blob Storage for grain state

## Application Insights

Telemetry and distributed tracing via Azure Application Insights, provisioned automatically in all deployed modes.

# Azure AD App Registration

Microsoft authentication requires an app registration in Microsoft Entra ID (Azure AD):

1. **Azure Portal** → **App registrations** → select your app (or create one)
2. Under **Authentication** → **Platform configurations** → **Web**, add redirect URIs:
   - `http://localhost:5000/signin-microsoft` (local development)
   - `https://<your-deployed-domain>/signin-microsoft` (deployed environments)
3. Note the **Application (client) ID** and **Directory (tenant) ID** from the **Overview** page
4. Under **Certificates & secrets**, create a client secret

For single-tenant apps, the tenant ID must be configured — the default `/common` endpoint is not supported.

# Secrets Management

Secrets are managed via `dotnet user-secrets` locally and GitHub secrets in CI/CD.

Required secrets for distributed modes:

| Secret | Description |
|--------|-------------|
| `Parameters:azure-foundry-key` | Azure AI Foundry API key (LLM access) |
| `Parameters:embedding-endpoint` | Embedding model endpoint |
| `Parameters:embedding-key` | Embedding model API key |
| `Parameters:embedding-model` | Embedding model name |
| `Parameters:microsoft-client-id` | Microsoft OAuth client ID |
| `Parameters:microsoft-client-secret` | Microsoft OAuth client secret |
| `Parameters:microsoft-tenant-id` | Microsoft Entra tenant ID (single-tenant apps) |
| `Parameters:google-client-id` | Google OAuth client ID |
| `Parameters:google-client-secret` | Google OAuth client secret |
| `Parameters:custom-domain` | Custom domain for deployed portal |
| `Parameters:certificate-name` | TLS certificate name for custom domain |

Set secrets using:

```bash
cd memex/aspire/Memex.AppHost
dotnet user-secrets set "Parameters:azure-foundry-key" "<your-key>"
```

# Project Structure

```
memex/aspire/
├── Memex.AppHost/           # Aspire orchestrator (defines all resources)
├── Memex.Portal.Distributed/  # Portal with co-hosted Orleans silo
├── Memex.Portal.Orleans/      # Orleans grain interfaces
├── Memex.Portal.ServiceDefaults/ # Shared service defaults (health, telemetry)
└── Memex.Database.Migration/  # Database migration project
```
