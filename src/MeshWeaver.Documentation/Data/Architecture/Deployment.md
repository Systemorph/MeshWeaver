---
Name: Deployment
Category: Architecture
Description: How MeshWeaver is deployed — the two deploy routes (AKS cluster and Azure Container Apps), plus shared local-run, Azure AD, and secrets setup
Icon: Cloud
---

# Deployment

MeshWeaver has **two distinct deploy routes**. They target different infrastructure — pick the one that matches where you're deploying. Neither is deprecated.

| Route | Target | How | Doc |
|---|---|---|---|
| **AKS** | Shared cluster `memexaks-cluster` — the `memex` portal namespace | Build images → `az aks command invoke` `kubectl set image` + rollout | [DeploymentAKS.md](DeploymentAKS.md) |
| **Azure Container Apps** | .NET Aspire `test` / `prod` modes (ACA, Sweden Central) | `tools/deploy.sh prod\|test` (wraps `aspire deploy` + migration-exit + db-version gate) | [DeploymentContainerApps.md](DeploymentContainerApps.md) |

**Which one?** If you're shipping a code update to the `memex` portal on the shared AKS cluster → AKS doc. If you're deploying an Aspire-orchestrated `test`/`prod` Container Apps environment → Container Apps doc. The two routes provision and run on different platforms (raw AKS deployments + Helm vs. ACA via Aspire), with different update mechanics; they are not interchangeable.

The sections below (local run, Azure AD, secrets, project layout) are **shared** across both routes.

---

# Running Locally

## Aspire (local mode)

Full local development with Docker containers (PostgreSQL pgvector + Azurite, Orleans in-process):

```bash
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode local
```

## Monolith (standalone, no Docker)

Lighter setup without Orleans or external infrastructure:

```bash
dotnet run --project memex/Memex.Portal.Monolith
# or via the AppHost:
aspire run --project memex/aspire/Memex.AppHost/Memex.AppHost.csproj -- --mode monolith
```

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

Secrets are stored in `dotnet user-secrets` for local development and in GitHub secrets for CI/CD. (On AKS, secrets come from the Key Vault `SecretProviderClass` wired by `deploy/aks/envs/<env>/deploy.sh`.)

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
