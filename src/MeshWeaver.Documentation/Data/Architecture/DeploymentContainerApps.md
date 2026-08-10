---
Name: Deployment — Azure Container Apps
Category: Architecture
Description: Deploying MeshWeaver to Azure Container Apps with .NET Aspire (test/prod modes) via tools/deploy.sh — the wrapper that catches silent migration failures
Icon: Cloud
---

# Deploying to Azure Container Apps

This is **one of two deploy routes**. Use it for the **.NET Aspire `test` / `prod` modes**, which provision and run on **Azure Container Apps** (Sweden Central) — the AppHost (`memex/aspire/Memex.AppHost`) is the single source of truth for every resource (PostgreSQL, Blob Storage, Orleans clustering, Application Insights). For the shared AKS-cluster portal (`memex` namespace), see [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS). These are **different routes to different targets** — choose by where you're deploying.

## Deployment Modes

The AppHost's four primary modes, selected via `--mode <mode>` (default `local`):

| Mode | PostgreSQL | Blob Storage | Orleans | Portal name |
|---|---|---|---|---|
| `local` | Docker pgvector container | Azurite emulator | Emulated (in-process) | memex-local |
| `test` | Azure (memex-test) | Azure — provisioned by Aspire | Azure | memex-test |
| `prod` | Azure (memex) | Azure — provisioned by Aspire | Azure | memex-prod |
| `monolith` | FileSystem (standalone) | — | — | memex-monolith |

Two further modes exist for running the AppHost **locally against deployed Azure resources**: `local-test` and `local-prod`. They alone attach to the *existing* storage accounts `meshweavermemextest` / `meshweavermemex` via `RunAsExisting` + Azure Identity (`az login`, no secrets), and they need `ConnectionStrings:memex` set to the Azure PostgreSQL so provisioning is bypassed. Deployed `test`/`prod` do **not** use those accounts — Aspire provisions storage for them.

## Prerequisites

1. **Azure CLI** authenticated — `az login`
2. **Aspire CLI** installed — `dotnet tool install -g Aspire.Cli`
3. **Docker** running (builds container images)
4. **Secrets** configured in the AppHost project (see [Deployment.md](/Doc/Architecture/Deployment) → Secrets Management)
5. **dotnet-script** installed for the post-deploy DB version check — `dotnet tool install -g dotnet-script`
6. **`AZURE_USER_PRINCIPAL_NAME` exported** — your AAD UPN (e.g. `you@example.com`). `tools/deploy.sh` exits 64 immediately without it, and `check-db-version.csx` throws: the DB check connects to Postgres as your AAD identity, and the UPN is the Postgres username. The signed-in user must be a Postgres AAD admin (or in a group that is).

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
  <text x="480" y="76" text-anchor="middle" fill="#fff" font-size="11">Discover Postgres</text>
  <text x="480" y="89" text-anchor="middle" fill="#fff" font-size="11">FQDN via az CLI</text>
  <line x1="560" y1="66" x2="608" y2="66" stroke="currentColor" stroke-opacity=".55" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="610" y="40" width="130" height="52" rx="10" fill="#1e88e5"/>
  <text x="675" y="60" text-anchor="middle" fill="#fff" font-weight="bold">Step 3</text>
  <text x="675" y="76" text-anchor="middle" fill="#fff" font-size="11">Poll check-db-version</text>
  <text x="675" y="89" text-anchor="middle" fill="#fff" font-size="11">every 15s, 10 min cap</text>
  <rect x="400" y="115" width="160" height="42" rx="10" fill="#e53935"/>
  <text x="480" y="133" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">FQDN not resolved?</text>
  <text x="480" y="149" text-anchor="middle" fill="#fff" font-size="11">Fail (exit 2)</text>
  <line x1="480" y1="92" x2="480" y2="115" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="675" y1="92" x2="675" y2="136" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5"/>
  <rect x="610" y="115" width="130" height="42" rx="10" fill="#e53935"/>
  <text x="675" y="133" text-anchor="middle" fill="#fff" font-weight="bold" font-size="12">Deadline hit?</text>
  <text x="675" y="149" text-anchor="middle" fill="#fff" font-size="11">Fail + dump 100 log lines</text>
  <text x="380" y="195" text-anchor="middle" font-size="13" font-weight="bold" fill="currentColor" fill-opacity=".75">Portal-side safeguards (runtime)</text>
  <line x1="380" y1="200" x2="380" y2="208" stroke="currentColor" stroke-opacity=".4" stroke-width="1"/>
  <rect x="100" y="215" width="230" height="72" rx="10" fill="#43a047"/>
  <text x="215" y="235" text-anchor="middle" fill="#fff" font-weight="bold">DbVersionGate</text>
  <text x="215" y="252" text-anchor="middle" fill="#fff" font-size="11">IHostedService at startup</text>
  <text x="215" y="267" text-anchor="middle" fill="#fff" font-size="11">One-shot: db_version ≥ ExpectedDbVersion</text>
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
2. Discovers the deployed Postgres FQDN — `az postgres flexible-server list -g <rg> --query "[0].fullyQualifiedDomainName"` — because the server name carries a random suffix that changes whenever the resource group is reprovisioned.
3. **Polls the database, not the container**: loops `dotnet script tools/check-db-version.csx -- <mode> <pg-fqdn>` every 15 s against a 10-minute deadline. First success exits 0; on deadline it fails the deploy and dumps `az containerapp logs show -n db-migration --tail 100`.

> **It deliberately does NOT poll the container's exit code.** `db-migration` is deployed as a regular Container App, not a Container Apps *Job*, and Container Apps treats *any* exit — including `exit 0` — as a crash and restarts it. The replica never reaches `Terminated`, `lastTerminationState.exitCode` flickers between `null` and `0` across restarts, and a successful migration is indistinguishable from a crash loop. `db_version` in the database is the only authoritative completion signal, and polling it is an end-to-end check rather than a proxy for one.

Two additional safeguards run inside the portal itself:

- **`DbVersionGate`** (`Memex.Portal.Distributed/DbVersionGate.cs`) — an `IHostedService` that queries `admin.mesh_nodes.db_version` **once** at portal startup and calls `IHostApplicationLifetime.StopApplication()` if the version is missing or below `ExpectedDbVersion`. It does not wait or retry. Container Apps then marks the revision `Failed` and routes no traffic to it.
- **`DbVersionHealthCheck`** — a live healthcheck wrapping the same query, surfacing any drift if someone manually runs a partial migration via `psql` after startup.

> **Read the constants; don't trust a number quoted here.** The gate compares against `DbVersionGate.ExpectedDbVersion` and the script against the `ExpectedVersion` constant in `tools/check-db-version.csx`. A completed migration writes `MigrationRunner.LatestVersion` — the highest `Version` in `MigrationRegistry.All`.
>
> ⚠️ **These three are currently drifted** (`ExpectedDbVersion = 32`, `check-db-version.csx = 26`, highest registered migration `V51`), contrary to the "bump in lock-step with the highest `Vxx_*.cs`" instruction in both constants' comments. Both gates are minimums, so they pass — but a database stranded anywhere in V27–V51 clears both and neither the deploy gate nor the startup gate will notice. Verify the actual constants before relying on either as a migration check.

> **Why not gate this inside `aspire deploy` itself?** At the time the wrapper was written, Aspire exposed no first-party API for a deploy-time callback that can poll a provisioned resource and fail the pipeline (nor `PublishAsAzureContainerJob`, which would remove the crash-loop ambiguity above); the note in `tools/deploy.sh` attributes both to a later "Wave 14". The repo is now on **Aspire 13.4.6** (`Directory.Packages.props`) and no `DeployingCallbackAnnotation` appears anywhere in the tree, so the bash poller is still the mechanism. Re-check the Aspire release notes before assuming this can collapse into an AppHost annotation.

## Verifying a Deployment

`tools/deploy.sh` already runs the version gate automatically. If you ran `aspire deploy` directly, verify manually — **the Postgres FQDN is required**, as a second argument or via `PG_HOST` (the script throws without it; it is not discoverable from the mode alone because the server name carries a reprovision-random suffix):

```bash
export AZURE_USER_PRINCIPAL_NAME=you@example.com
PG_HOST=$(az postgres flexible-server list -g prod-memex \
  --query "[0].fullyQualifiedDomainName" -o tsv)
dotnet script tools/check-db-version.csx -- prod "$PG_HOST"
```

Success prints `✅ db_version=<n> (>= <ExpectedVersion>)` and exits 0. After verification, open the portal URL, check the Aspire dashboard for service health, and review Application Insights for startup telemetry.

## Container Apps infrastructure

Deployed modes (`test`, `prod`) run on **Azure Container Apps** in Sweden Central with sticky sessions enabled for Blazor Server.

- **PostgreSQL** — Azure PostgreSQL Flexible Server with pgvector, provisioned by Aspire (local: `pgvector/pgvector:pg17` Docker container).
- **Azure Blob Storage** — content files (attachments, documents); local uses the Azurite emulator.
- **Orleans** — Azure Table Storage for clustering + Blob Storage for grain state (local: emulated in-process).
- **Application Insights** — telemetry + distributed tracing, provisioned in all deployed modes.
