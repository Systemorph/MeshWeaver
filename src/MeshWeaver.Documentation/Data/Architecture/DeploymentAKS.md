---
Name: Deployment — AKS
Category: Architecture
Description: Deploying a code update to the shared AKS cluster (memexaks-cluster) that hosts the memex portal — build images, set image, roll out, verify
Icon: Cloud
---

# Deploying to AKS

This is **one of two deploy routes** for MeshWeaver. Use it for the shared portals on the **AKS cluster `memexaks-cluster`** (resource group `memex-aks-rg`, region swedencentral) — the `memex` namespace, backed by the Postgres Flexible Server, with container images in ACR `meshweaver.azurecr.io`. For the Azure Container Apps route (Aspire `test`/`prod` modes via `tools/deploy.sh`), see [DeploymentContainerApps.md](/Doc/Architecture/DeploymentContainerApps). These are **different routes to different targets**, not old-vs-new — pick the one that matches where you're deploying.

> **The cluster is private.** `kubectl` is not reachable directly — every command runs through `az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "…"`, which executes inside the cluster's API-server-side runner.

A **code update** is three steps: build the images, point the Deployments at the new tag, restart. It is **not** `tools/deploy.sh` and **not** `aspire deploy` — those are the Container Apps route.

> **Steady state is self-update, not this runbook.** Once an environment runs, it rolls *itself* to new images per `Admin/UpdatePolicy` (default Continuous) — the portal patches its own Deployment from inside the pod. This manual runbook is the **bootstrap / break-glass** path (first install, or to force a specific tag). See [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy), which also covers the one-time RBAC + workload-identity (AcrPull) setup the in-pod updater needs.

## 1. Build + push the images

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

Pick a `<tag>` that pins the change (e.g. `bugfix-2026-06-05`). CI also builds images on push, but it lags — check `az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5` before assuming your commit is built; if it isn't, build manually as above. If only portal code changed (no migration/schema change), you can reuse the live `memex-migration` tag and skip the migration build.

### The node-compile `#r` feed is auto-baked into the image (no manual pack step)

Node Source pulls MeshWeaver libraries in at compile time via `#r "nuget:MeshWeaver.X"` — most importantly the scope **source generator** every `IScope<,>` node needs (e.g. the PensionFund balance sheet):

```csharp
#r "nuget:MeshWeaver.BusinessRules.Generator"
```

These resolve **offline from a feed baked into the image**, never from nuget.org (they are not published there). The portal publish above runs the `BakeMeshLocalFeed` MSBuild target (in `Memex.Portal.Distributed.csproj`; **Release-only**, `BeforeTargets="ComputeFilesToPublish"`): it packs the curated set — `MeshWeaver.BusinessRules` (the `IScope<,>` surface) + `MeshWeaver.BusinessRules.Generator` (its scope generator) — into `dist/packages` at the single global `$(Version)` from `Directory.Build.props`, then copies that folder next to `nuget.config` into `/app`. At runtime `NuGetAssemblyResolver` reads `nuget.config` from the app dir and resolves `MeshWeaver.*` against that local folder.

**So the `dotnet publish -t:PublishContainer` command above is self-contained — there is no separate pack step.** Notes:

- `dist/packages` is git-ignored; the target (re)packs it on every Release publish, so the image always carries packages matching the built code.
- Node Source pins **no** version (`#r "nuget:MeshWeaver.BusinessRules.Generator"`), so it resolves whatever single version the baked feed carries — the version lives in one place (`PlatformVersion`).
- If a deployed scope node ever fails with a NuGet-resolve error, the image was built **without** this target (a `-c Debug` publish, or a `dist/packages` exclusion) — rebuild `-c Release`.
- The same curated set is packed for the test suite by `.github/workflows/dotnet-test.yml` ("Pack mesh-local #r packages"), kept in sync with this target.
- **`NETSDK1047` on `MeshWeaver.BusinessRules` during the publish** (`Assets file … doesn't have a target for 'net10.0/linux-x64'`): because `BusinessRules` is **decoupled** from the portal's project graph (pulled in only via `#r "nuget:"`), the publish's implicit restore doesn't cover it, so a stale local `obj` can lack the `-r linux-x64` RID target the `BakeMeshLocalFeed` pack needs. Restore it for the RID once, then re-publish:
  ```bash
  dotnet restore src/MeshWeaver.BusinessRules/MeshWeaver.BusinessRules.csproj -r linux-x64
  ```
  CI is immune (it restores from a clean checkout); this only bites incremental local builds.

## 2. Roll out (NS = `memex`)

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "\
  kubectl -n <NS> set image deployment/memex-portal-deployment memex-portal=meshweaver.azurecr.io/memex-portal-ai:<tag>; \
  kubectl -n <NS> set image deployment/memex-migration-deployment memex-migration=meshweaver.azurecr.io/memex-migration:<tag>; \
  kubectl -n <NS> rollout restart deployment/memex-migration-deployment deployment/memex-portal-deployment; \
  kubectl -n <NS> rollout status deployment/memex-portal-deployment --timeout=300s"
```

Container names are `memex-portal` and `memex-migration`; deployments are `memex-portal-deployment` and `memex-migration-deployment`.

## 3. Verify

- **Migration ran:** `az aks command invoke … --command "kubectl -n <NS> logs deployment/memex-migration-deployment --tail=40"` → expect `Database migration completed. Version: N`. The migration pod exits 0 and the Deployment restarts it, so a *benign* `CrashLoopBackOff` on `memex-migration` is normal — read the log, don't panic on the status.
- **Portal serves:** `curl -sS -o /dev/null -w '%{http_code}' https://<NS>.meshweaver.cloud/` → `200`.
- **Schema/index applied** (when the change was a migration): spot-check via `az aks command invoke … "kubectl -n <NS> exec deployment/memex-portal-deployment -- …"` or an MCP query.

## Portal self-update — Workload Identity for ACR polling

Steady state is **self-update** (see [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy)): the portal polls ACR and patches its own Deployment to a newer image. The in-cluster PATCH uses the `memex-portal-sa` service-account token (RBAC ships in the Helm chart and works everywhere). **Listing the ACR tags** to discover a newer image needs an Azure credential — that is wired with **AKS Workload Identity**, mirroring the existing pgBackRest wiring (`infra/modules/storage.bicep`).

**What the Helm chart already does** (no edits needed): when `selfUpdate.azureClientId` is set it annotates `memex-portal-sa` with `azure.workload.identity/client-id`, labels the pod `azure.workload.identity/use: "true"`, and sets `AZURE_CLIENT_ID`. The self-updater (`AcrTagLister`) then uses `ManagedIdentityCredential(AZURE_CLIENT_ID)` → AAD token → ACR token.

**What the Azure side provides** (`infra/modules/portal-identity.bicep`, wired from `infra/main.bicep`): a **single shared** user-assigned managed identity (`<namePrefix>-portal-mi`) with **one federated credential per portal namespace** — subject `system:serviceaccount:<ns>:memex-portal-sa`, issuer = the cluster OIDC issuer, audience `api://AzureADTokenExchange` — for every namespace in the `portalNamespaces` param (`memex`, `memex-cloud`, and any customer portal namespaces). The UAMI gets **AcrPull** on `meshweaver.azurecr.io` (AcrPull includes the `metadata_read` the tag-list call needs). One UAMI → one AcrPull grant → the **same** `portalIdentityClientId` wired into `selfUpdate.azureClientId` for every namespace.

### One-time setup

1. **Provision the UAMI + federated credentials** — included in the infra deploy (`deployPortalIdentity` defaults `true`). Read the client id back:
   ```bash
   az deployment sub show --name memex-aks-infra \
     --query "properties.outputs.{clientId:portalIdentityClientId.value, principalId:portalIdentityPrincipalId.value}" -o jsonc
   ```
2. **Grant AcrPull on the shared registry.** The ACR (`meshweaver.azurecr.io`, RG `meshweaver-shared`) is **cross-RG** from `memex-aks-rg`, so — exactly like the cluster kubelet's AcrPull — grant it out-of-band:
   ```bash
   PORTAL_MI_OID=$(az identity show -g memex-aks-rg -n memexaks-portal-mi --query principalId -o tsv)
   az role assignment create --assignee-object-id "$PORTAL_MI_OID" --assignee-principal-type ServicePrincipal \
     --role AcrPull --scope $(az acr show -n meshweaver --query id -o tsv)
   ```
   (IaC alternative: deploy with `grantSharedAcrPull=true` — authors this via `infra/modules/acr-role-assignment.bicep` in the registry's RG; needs User Access Administrator on `meshweaver-shared`. A *per-deployment* ACR instead of the shared one is granted in-bicep automatically.)
3. **Set `selfUpdate.azureClientId`** to `portalIdentityClientId` for each environment (the in-pod patch works without it; this only authenticates the tag-list). Same value everywhere:
   - `memex` → the git-ignored `scripts/values.deploy.yaml` (see `values.deploy.example.yaml`), or `helm upgrade --set selfUpdate.azureClientId=<clientId>`.
   - `memex-cloud` / customer portals → the git-ignored `envs/<env>/values.<env>.yaml`.

> Adding a **new** portal namespace? It needs its own federated credential on the shared UAMI — add the namespace to `portalNamespaces` and re-run the infra deploy (idempotent), or `az identity federated-credential create` (see [OnboardingNewEnvironment.md](/Doc/Architecture/OnboardingNewEnvironment)). The subject must be exactly `system:serviceaccount:<ns>:memex-portal-sa`.

## Migration under self-update

A self-update is **not portal-only.** When an install rolls itself to a new tag (per `Admin/UpdatePolicy`), the in-pod updater patches **both** Deployments in one pass — `memex-portal-deployment` (container `memex-portal`) **and** `memex-migration-deployment` (container `memex-migration`) — exactly as the manual §2 roll-out does. **No operator action is required**; Kubernetes performs the rolling update on both.

- **The migration re-runs on every roll.** `memex-migration-deployment` runs the DB migrations, applies any new schema, bumps `admin.mesh_nodes.db_version`, then exits 0. The Deployment restarts the exited pod, so a *benign* `CrashLoopBackOff` on `memex-migration` is expected (same as §3) — read the log (`Database migration completed. Version: N`); don't treat the status as a failure.
- **Ordering is migration-before-portal.** The portal must never serve a schema it hasn't migrated to, so two gates enforce the order even though both Deployments roll together:
  - the portal pod's `wait-for-postgres` **initContainer** blocks startup until the database is reachable, and
  - the portal's **`DbVersionGate`** hosted service holds the app from serving until `db_version` matches the version the running code expects.

  So the new portal goes live only after the new migration has bumped `db_version` — the roll is safe regardless of which pod's image is pulled first.

## First-time environment setup ≠ code update

`deploy/aks/envs/<env>/deploy.sh` provisions a **new** environment: `helm install` of the chart, PVCs, the Key Vault `SecretProviderClass`, ingress, and the connection-string patch. **Do not run it for a code update** — it re-applies the whole chart and can reset live ConfigMaps (e.g. the email config). Use it only when standing up a brand-new namespace.

## Diagnostics (private cluster)

- Logs: `az aks command invoke … --command "kubectl -n <NS> logs deployment/memex-portal-deployment --tail=120"`. Note: the Azure CLI can crash on non-ASCII (`→`) in log output on Windows (cp1252) — pipe through `tr -cd '\11\12\15\40-\176'` **inside** the `--command` so az only receives printable text.
- A `MESHWEAVER_MSG_TRACE=1` env var on the portal Deployment turns on the message-flow trace (`/tmp/meshweaver-msg-trace.log` in the pod). Toggling it restarts the pod; remove it (`kubectl set env … MESHWEAVER_MSG_TRACE-`) when done — it writes per-message and adds lock/IO overhead.

---

For Azure AD app registration and secrets (shared across both routes), see [Deployment.md](/Doc/Architecture/Deployment).
