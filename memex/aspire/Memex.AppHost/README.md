# Memex.AppHost

## Overview
Memex.AppHost is the .NET Aspire orchestrator for the Memex platform. It defines the full distributed topology — PostgreSQL, Azure Blob Storage, Orleans clustering, Application Insights, and the portal — with a mode matrix that scales from local Docker development to Azure Container Apps production.

## Usage
```bash
dotnet run --project memex/aspire/Memex.AppHost
# Default mode: "local" (Docker pgvector + emulated storage)

dotnet run --project memex/aspire/Memex.AppHost -- --mode test
# Deploys to Azure with test-tier resources
```

## Deployment Modes
| Mode | PostgreSQL | Blob Storage | Orleans | Portal |
|---|---|---|---|---|
| `local` | Docker pgvector | Emulated | Emulated | memex-local |
| `local-test` | Azure (test) | Azure (test) | Emulated | memex-local |
| `local-prod` | Azure (prod) | Azure (prod) | Emulated | memex-local |
| `test` | Azure (test) | Azure (test) | Azure | memex-test |
| `prod` | Azure (prod) | Azure (prod) | Azure | memex-prod |
| `monolith` | FileSystem | -- | -- | memex-monolith |

## Integration
- Orchestrates [Memex.Portal.Distributed](../Memex.Portal.Distributed/), [Memex.Database.Migration](../Memex.Database.Migration/), and [Memex.Portal.Monolith](../../Memex.Portal.Monolith/)
- Provisions Azure Container App Environment, Key Vault, Application Insights, and storage in Sweden Central
- Secrets managed via `dotnet user-secrets` (UserSecretsId: `memex-apphost`)
