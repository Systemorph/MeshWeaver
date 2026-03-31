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
| `local-test`| Azure (memex-test)           | Azure (meshweavermemextest)   | Emulated  | memex-local     |
| `local-prod`| Azure (memex)                | Azure (meshweavermemex)       | Emulated  | memex-local     |
| `test`      | Azure (memex-test)           | Azure (meshweavermemextest)   | Azure     | memex-test      |
| `prod`      | Azure (memex)                | Azure (meshweavermemex)       | Azure     | memex-prod      |
| `monolith`  | FileSystem (standalone)      | —                             | —         | memex-monolith  |

# Deploying to Production

Deploy using the Aspire CLI:

```bash
aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode prod
```

For test environment:

```bash
aspire deploy --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode test
```

The `aspire deploy` command builds the application, pushes container images, and provisions/updates Azure resources as defined in the AppHost.

# Running Locally

For local development with Docker containers:

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj
```

This starts in `local` mode by default, using Docker pgvector and emulated Azure services.

To run locally against Azure test or prod databases:

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode local-test
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode local-prod
```

These modes connect to Azure PostgreSQL and Blob Storage while keeping Orleans emulated locally.

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
- **local-test/local-prod**: Connects to existing Azure PostgreSQL via connection string

## Azure Blob Storage

Content files (attachments, documents) are stored in Azure Blob Storage.

- **Local**: Azurite emulator with persistent data bind mount
- **Deployed**: Azure Storage Account provisioned in Sweden Central

## Orleans

Orleans provides distributed actor clustering for the microservices deployment.

- **Local/local-test/local-prod**: Emulated (in-process)
- **Deployed**: Azure Table Storage for clustering, Azure Blob Storage for grain state

## Application Insights

Telemetry and distributed tracing via Azure Application Insights, provisioned automatically in all deployed modes.

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
| `Parameters:google-client-id` | Google OAuth client ID |
| `Parameters:google-client-secret` | Google OAuth client secret |
| `Parameters:custom-domain` | Custom domain for deployed portal |
| `Parameters:certificate-name` | TLS certificate name for custom domain |

For `local-test` and `local-prod` modes, also set:

| Secret | Description |
|--------|-------------|
| `ConnectionStrings:memex` | Azure PostgreSQL connection string |

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
