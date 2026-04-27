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

## Deploy to Production

From the repository root:

```bash
aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode prod
```

This command:
1. Builds the application (Release configuration, linux-x64)
2. Pushes container images to Azure Container Registry
3. Provisions/updates Azure Container Apps, PostgreSQL, Blob Storage, Orleans clustering, and Application Insights
4. Deploys to **Sweden Central** with sticky sessions enabled for Blazor Server

## Deploy to Test

```bash
aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode test
```

Same process as prod but targets the test environment (separate PostgreSQL, Blob Storage, and Container Apps instances).

## Verify Deployment

After deployment completes, the Aspire CLI outputs the portal URL. Verify by:
- Opening the portal URL in a browser
- Checking the Aspire dashboard for service health
- Reviewing Application Insights for startup telemetry

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
