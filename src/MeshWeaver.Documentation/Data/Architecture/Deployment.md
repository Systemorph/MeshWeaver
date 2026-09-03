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
| **AKS** | Shared cluster `<aks-cluster>` — the `memex` portal namespace | Build images → `az aks command invoke` `kubectl set image` + rollout | [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) |
| **Azure Container Apps** | .NET Aspire `test` / `prod` modes (ACA, Sweden Central) | `tools/deploy.sh prod\|test` (wraps `aspire deploy` + migration-exit + db-version gate) | [DeploymentContainerApps.md](/Doc/Architecture/DeploymentContainerApps) |

**Which doc do I need?**

| Scenario | Read |
|---|---|
| See every **running instance** — who it's for, its infra, database, and version — and how to create or delete one | [Instances.md](/Doc/Architecture/Instances) |
| **Database backups** & disaster recovery — managed PITR, geo-redundancy, restore | [DatabaseBackups.md](/Doc/Architecture/DatabaseBackups) |
| Understand the release model, merge gates, version channels, and **policy-driven self-update** | [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy) |
| Know what CD **guarantees** about a published image set — all-or-nothing publication, the promote ordering, the self-healing reconciler — and why you verify the IMAGE and never the green tick | [ContinuousDeliveryContract.md](/Doc/Architecture/ContinuousDeliveryContract) |
| Work out whether an install can actually **take** the newest release — the schema boundary self-update cannot cross, why the resulting stall is invisible, and the three conditions a tag must clear before it is a safe `helm upgrade` target | [SelfUpdateSchemaWall.md](/Doc/Architecture/SelfUpdateSchemaWall) |
| Ship a code update to the `memex` portal on the shared AKS cluster | [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) |
| Deploy an Aspire-orchestrated `test`/`prod` Container Apps environment | [DeploymentContainerApps.md](/Doc/Architecture/DeploymentContainerApps) |
| Understand the private-AKS-cluster architecture & operations behind the shared portal | [MemexCloudDeployment.md](/Doc/Architecture/MemexCloudDeployment) |
| Add a **new tenant environment** on the existing shared AKS platform | [OnboardingNewEnvironment.md](/Doc/Architecture/OnboardingNewEnvironment) |
| Run a **prod-like memex locally on a Mac** (Colima k3s, arm64) | [LocalColimaMac.md](/Doc/Architecture/LocalColimaMac) |
| Instance-specific configuration options (`portal.example.com`) | [DeploymentOptions.md](/Doc/Architecture/DeploymentOptions) |
| Reclaim space — delete old ACR images / prune local Docker, safely | [ImageCleanup.md](/Doc/Architecture/ImageCleanup) |
| Turn production errors into tickets automatically — deploy the red-log watcher, route incidents to repositories, or work out why nothing is being reported | [LogWatchTriage.md](/Doc/Architecture/LogWatchTriage) |

The two routes provision and run on different platforms (raw AKS deployments + Helm vs. ACA via Aspire), with different update mechanics; they are not interchangeable. The sections below (local run, Azure AD, secrets, project layout) are **shared** across both routes.

---

# How a release reaches the fleet

**The contract (maintainer, 2026-09-03: *"end of github pipeline must call memex, which must
register release and publish event"*) is three sentences:**

1. **Every publishing pipeline ENDS with one call to memex.** Core's CD, after the image set is
   promoted, POSTs the signed platform build (`event: platform-build`) into the control instance's
   `Hosting/PlatformBuilds` inbox (`notify-platform-update`). Every node repository's
   `node-repo-publish-bake.yml` run, after its bundles are sealed for an identity, POSTs the signed
   publication record (`event: bundle-publication` — source, identity, commit, tester + portal image)
   into the same inbox (`register-publication`, its last job). Nothing runs after that call, and no
   pipeline sends a `repository_dispatch` to another repository.
2. **memex REGISTERS the release** as a durable node — `Hosting/PlatformBuilds/<version>` for a
   platform build, `Hosting/Publications/<identity>/<source>` for a bundle publication — the source
   of truth for "what is published for which identity" (what the self-update availability check reads).
3. **memex PUBLISHES the event** from that registration: `FrameworkReleaseBroadcaster` sends
   `meshweaver-framework-released` (platform) or `meshweaver-upstream-published` (bundle publication,
   `client_payload.version` = the identity) to the subscribed repositories — the repositories the
   control instance's `Hosting/Deployment` records name as registry sources. The subscribers' CI
   receives it, resolves both images from the version, builds and publishes for that identity — and
   ends by calling memex (1).

```
 pipeline (core CD | a node repo's publish-bake)        memex (control instance)              subscriber CI
 ───────────────────────────────────────────────        ────────────────────────              ─────────────
 promote / seal ✅                                       WebhookInbox Hosting/PlatformBuilds
   └─ ONE signed POST ──(platform-build |──────────────▶│ verify HMAC
      bundle-publication)… and FINISH                    ├─ REGISTER  Hosting/PlatformBuilds/<version>
                                                         │            Hosting/Publications/<identity>/<source>
                                                         ├─ subscribers = Hosting/Deployment records'
                                                         │              pluginRepos[].isRegistrySource
                                                         └─ PUBLISH   repository_dispatch ─────────────▶ on: repository_dispatch:
                                                            meshweaver-framework-released |               types: [meshweaver-framework-released,
                                                            meshweaver-upstream-published                        meshweaver-upstream-published]
                                                                                                          → bake for the version → seal → POST memex
```

Where the pieces are: the POST steps in `main-cd.yml` and `node-repo-publish-bake.yml` (this repo);
the inbox watcher, registration and broadcast in the Hosting module's `PlatformBuildInboxWatcher`
(MeshWeaver.Plugins, `Hosting/Deployment/Source`); the broadcaster in `src/MeshWeaver.GitSync`.
`PlatformReleaseNotifyGuard.CoreDispatchesToNoRepository` refuses a dispatch SENDER in any workflow
under `.github/workflows` — there is no ledger — and
`UpstreamBuildGateGuard.TheLaneEndsByRegisteringWithMemex_AndDispatchesToNobody` pins the lane's call.

Operator view: after a promote, the control instance's log carries `[PlatformBuilds] verified build …`,
then `[PlatformBuilds] release broadcast for <version>: N subscriber(s) dispatched.`; each subscribed
repository shows a `repository_dispatch` run whose payload carries `source: memex`; the node repos'
pin-bump PRs follow. A 2xx on the pipeline's POST proves only that memex STORED the record.

# Running Locally

## Aspire (local mode)

Full local development with Docker containers (PostgreSQL pgvector + Azurite, Orleans in-process):

```bash
aspire run --project ../MeshWeaver.Plugins/src/Memex.AppHost/Memex.AppHost.csproj -- --mode local
```

## Monolith (standalone, no Docker)

Lighter setup without Orleans or external infrastructure:

```bash
dotnet run --project ../MeshWeaver.Plugins/src/Memex.Portal.Monolith
# or via the AppHost:
aspire run --project ../MeshWeaver.Plugins/src/Memex.AppHost/Memex.AppHost.csproj -- --mode monolith
```

---

# Azure AD App Registration

Microsoft authentication requires an app registration in Microsoft Entra ID (Azure AD).

1. **Azure Portal** → **App registrations** → select your app (or create one)
2. Under **Authentication** → **Platform configurations** → **Web**, add redirect URIs:
   - `https://localhost:7122/signin-microsoft` (local Monolith — HTTP fallback port 5022)
   - `https://localhost:7202/signin-microsoft` (local Aspire portal — HTTP fallback port 5202)
   - `https://<your-deployed-domain>/signin-microsoft` (deployed environments)
3. Note the **Application (client) ID** and **Directory (tenant) ID** from the **Overview** page
4. Under **Certificates & secrets**, create a client secret

For single-tenant apps, configure the tenant ID explicitly — the default `/common` endpoint is not supported.

---

# Secrets Management

Secrets are stored in `dotnet user-secrets` for local development and in GitHub secrets for CI/CD. (On AKS, secrets come from Key Vault through a `SecretProviderClass` the chart renders from the `keyVaultSecrets` values block — names only, never values; see [DeploymentAKS](/Doc/Architecture/DeploymentAKS) → "Key Vault secrets are DECLARED in values".)

Parameters for distributed modes (the authoritative list is the `builder.AddParameter(...)` calls in `../MeshWeaver.Plugins/src/Memex.AppHost/Program.cs`):

| Parameter | Description | If unset |
|---|---|---|
| `Parameters:azure-foundry-key` | Azure AI Foundry API key (LLM access) | **Required** |
| `Parameters:azure-foundry-endpoint` | Azure AI Foundry `/models` endpoint | Optional — defaulted in `appsettings.json` |
| `Parameters:anthropic-endpoint` | Anthropic-compatible endpoint | **Required** — blank yields `Endpoint is missing for model 'X'` |
| `Parameters:anthropic-model-0/1/2` | Model catalog offered in the composer's model picker | **Required** — blank yields an empty model dropdown |
| `Parameters:embedding-endpoint` | Embedding model endpoint | Optional (defaults to empty) |
| `Parameters:embedding-key` | Embedding model API key | Optional (defaults to empty) |
| `Parameters:embedding-model` | Embedding model name | Optional (defaults to empty) |
| `Parameters:key-protection-master-key` | Encrypts `ModelProvider` API keys at rest | Falls back to a **dev default that is not secret** — `test`/`prod` MUST override |
| `Parameters:microsoft-client-id` | Microsoft OAuth client ID | **Required** |
| `Parameters:microsoft-client-secret` | Microsoft OAuth client secret | **Required** |
| `Parameters:microsoft-tenant-id` | Microsoft Entra tenant ID (single-tenant apps) | Optional — omitted when empty |
| `Parameters:google-client-id` | Google OAuth client ID | Optional (defaults to empty) |
| `Parameters:google-client-secret` | Google OAuth client secret | Optional — omitted when empty |
| `Parameters:linkedin-client-secret` | LinkedIn publishing (client id is inlined in the AppHost) | Optional |
| `Parameters:custom-domain` | Custom domain for the deployed portal | Optional — omitted when empty |
| `Parameters:certificate-name` | TLS certificate name for the custom domain | Optional — omitted when empty |

> 🚨 Several of these deliberately carry **no `value:` default** in the AppHost. That is not an oversight: passing `value: ""` makes Aspire resolve the parameter to the empty string and skip the user-secrets/config lookup entirely, so the setting silently stays blank even when user-secrets has it. Don't "tidy" a default onto them.

Set a secret with:

```bash
cd ../MeshWeaver.Plugins/src/Memex.AppHost
dotnet user-secrets set "Parameters:azure-foundry-key" "<your-key>"
```

---

# Project Structure

```
memex/aspire/
├── Memex.AppHost/                  # Aspire orchestrator — defines all resources
├── Memex.Aspire.Hosting/           # Shared Aspire hosting extensions
├── Memex.Portal.Distributed/       # Portal with co-hosted Orleans silo
├── Memex.Portal.ServiceDefaults/   # Shared service defaults (health, telemetry)
└── Memex.Database.Migration/       # Database migration project (runs MigrationRegistry.All)
```

Outside `aspire/`, `memex/` also holds `Memex.Portal.Monolith` (the standalone dev portal), `Memex.Portal.Shared` (shared portal code, including the self-update poller), `Memex.Client`, and `Memex.LocalMesh`.
