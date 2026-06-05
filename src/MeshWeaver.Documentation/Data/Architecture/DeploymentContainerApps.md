---
Name: Deployment — Azure Container Apps
Category: Architecture
Description: Deploying MeshWeaver to Azure Container Apps with .NET Aspire (test/prod modes) via tools/deploy.sh — the wrapper that catches silent migration failures
Icon: Cloud
---

# Deploying to Azure Container Apps

This is **one of two deploy routes**. Use it for the **.NET Aspire `test` / `prod` modes**, which provision and run on **Azure Container Apps** (Sweden Central) — the AppHost (`memex/aspire/Memex.AppHost`) is the single source of truth for every resource (PostgreSQL, Blob Storage, Orleans clustering, Application Insights). For the shared AKS-cluster portals (`atioz`, `memex` namespaces), see [DeploymentAKS.md](DeploymentAKS.md). These are **different routes to different targets** — choose by where you're deploying.

## Deployment Modes

The AppHost supports four modes, selected via `--mode <mode>`:

| Mode | PostgreSQL | Blob Storage | Orleans | Portal name |
|---|---|---|---|---|
| `local` | Docker pgvector container | Azurite emulator | Emulated (in-process) | memex-local |
| `test` | Azure (memex-test) | Azure (meshweavermemextest) | Azure | memex-test |
| `prod` | Azure (memex) | Azure (meshweavermemex) | Azure | memex-prod |
| `monolith` | FileSystem (standalone) | — | — | memex-monolith |

## Prerequisites

1. **Azure CLI** authenticated — `az login`
2. **Aspire CLI** installed — `dotnet tool install -g aspire`
3. **Docker** running (builds container images)
4. **Secrets** configured in the AppHost project (see [Deployment.md](Deployment.md) → Secrets Management)
5. **dotnet-script** installed for the post-deploy DB version check — `dotnet tool install -g dotnet-script`

## 🚨 Always use `tools/deploy.sh` — never bare `aspire deploy`

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

Expect `✅ db_version=15 (>= 15)`. After verification, open the portal URL, check the Aspire dashboard for service health, and review Application Insights for startup telemetry.

## Container Apps infrastructure

Deployed modes (`test`, `prod`) run on **Azure Container Apps** in Sweden Central with sticky sessions enabled for Blazor Server.

- **PostgreSQL** — Azure PostgreSQL Flexible Server with pgvector, provisioned by Aspire (local: `pgvector/pgvector:pg17` Docker container).
- **Azure Blob Storage** — content files (attachments, documents); local uses the Azurite emulator.
- **Orleans** — Azure Table Storage for clustering + Blob Storage for grain state (local: emulated in-process).
- **Application Insights** — telemetry + distributed tracing, provisioned in all deployed modes.
