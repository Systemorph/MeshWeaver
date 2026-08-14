---
Name: Deployment — AKS
Category: Architecture
Description: Deploying a code update to the shared AKS cluster (<aks-cluster>) that hosts the memex portal — build images, set image, roll out, verify
Icon: Cloud
---

# Deploying to AKS

This is **one of two deploy routes** for MeshWeaver. Use it for the shared portals on the **AKS cluster `<aks-cluster>`** (resource group `<aks-resource-group>`, region swedencentral) — the `memex` namespace, backed by the Postgres Flexible Server, with container images in ACR `meshweaver.azurecr.io`. For the Azure Container Apps route (Aspire `test`/`prod` modes via `tools/deploy.sh`), see [DeploymentContainerApps.md](/Doc/Architecture/DeploymentContainerApps). These are **different routes to different targets**, not old-vs-new — pick the one that matches where you're deploying.

> **The cluster is private.** `kubectl` is not reachable directly — every command runs through `az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "…"`, which executes inside the cluster's API-server-side runner.

A **code update** is three steps: build the images, point the Deployments at the new tag, restart. It is **not** `tools/deploy.sh` and **not** `aspire deploy` — those are the Container Apps route.

> **Steady state is self-update, not this runbook.** Once an environment runs, it rolls *itself* to new images per `Admin/UpdatePolicy` (default Continuous) — the portal patches its own Deployment from inside the pod. This manual runbook is the **bootstrap / break-glass** path (first install, or to force a specific tag). See [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy), which also covers the one-time RBAC + workload-identity (AcrPull) setup the in-pod updater needs.

## 1. Build + push the images

```bash
az acr login -n meshweaver

# Portal — needs the prebuilt custom base image. MULTI-ARCH: never pass `-r linux-x64`.
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj -c Release \
  --no-self-contained -t:PublishContainer -p:PublishProfile= \
  -p:ContainerRuntimeIdentifiers='"linux-x64;linux-arm64"' \
  -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-portal-ai -p:ContainerImageTag=<tag> \
  -p:ContainerBaseImage=meshweaver.azurecr.io/memex-portal-ai-base:latest

# Migration — this is what creates the schema, the partition_access table, AND the
# public.top_level_index materialized view. A schema/index change ships in THIS image:
dotnet publish memex/aspire/Memex.Database.Migration/Memex.Database.Migration.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-migration -p:ContainerImageTag=<tag>
```

**🚨 The image must be MULTI-ARCH.** `memex-portal-ai-base:latest` is built for `linux/amd64` **and**
`linux/arm64` (x86 cloud nodes and Apple-silicon local k3s both pull it), so the app image layered on it must
be too. Do **not** pass `-r linux-x64` — a single RID produces a single-arch image, and a node of the other
architecture gets an `ImagePullBackOff` that reads as "the deploy hung". Three details make the flags above
non-obvious, all of them silent when wrong:
- `-p:PublishProfile=` (empty) is required to override the csproj's `<PublishProfile>DefaultContainer</PublishProfile>`.
- `RuntimeIdentifiers` is declared **project-local** in `Memex.Portal.Distributed.csproj`, never as a global
  `-p:` — a global one propagates to every `ProjectReference` and fails them with `NETSDK1083`.
- The `'"a;b"'` quoting (single-quoted double-quotes around a **real** `;`) is load-bearing. Writing `%3B`
  instead gives MSBuild an *escaped* semicolon, so the value stays the single bogus RID `linux-x64;linux-arm64`
  and the build fails with `MSB4115` / `NETSDK1083`.

**🚨 `<tag>` must be dotted SemVer (`3.0.0`, `3.0.0-ci.749`) — a descriptive tag gets reverted.** The
in-pod self-updater only recognises tags matching `^\d+\.\d+\.\d+([-+].*)?$` (`VersionSelect.PlatformVersionTag`);
a hand-picked `bugfix-2026-06-05` is not a candidate, so the poller picks the newest CI tag instead and
patches the Deployment straight back off your image — and it polls once immediately on pod start
(`StartWith(-1L)`), so the revert lands within moments of the roll you just did. A hand-built image is
therefore only usable with the updater paused (see "Hard pause" below); the normal way to ship code is a
merged PR whose CI build produces a `ci.<N>` tag.

CI also builds images on push, but it lags — check `az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5` before assuming your commit is built. If only portal code changed (no migration/schema change), you can reuse the live `memex-migration` tag and skip the migration build.

### Business rules / scopes are NOT in the image — they ship as a plugin

**The published image contains no `MeshWeaver.BusinessRules` assembly and no scope source generator.**
Business rules and scopes were removed from the platform (commit `f7a8c086c`); they ship as the
**`MeshWeaver.Plugins/BusinessRules` plugin**, which carries the scope runtime as a shared-source library
node (pulled into a consumer's compilation via `shared=@BusinessRules/Scope/Source`) and carries the
`ScopeCodeGenerator` source for the generator-injection seam. The platform and the Doc partition start with
**zero** business-rules dependency — stated in the comment blocks in `MeshWeaver.Graph.csproj` and
`Memex.Portal.Distributed.csproj`, and asserted against the built image by `PortalImageFacility`.

Consequences for a deploy:

- **An `IScope<,>` node needs the BusinessRules plugin installed** on the target environment. It does not
  compile "just by declaring the interface" — neither the `IScope<,>` surface nor the generator is in the
  image's reference set.
- **`MeshNodeCompilationService.BuiltInGeneratorPaths` is EMPTY on a deployed image.** It only fills when a
  `MeshWeaver.BusinessRules.Generator.dll` happens to sit next to the app (a dev/self-host tree that put one
  there) — kept as graceful degradation, not as the shipping story.
- **The legacy-`#r` strip is keyed off that same list, so on a deployed image it does nothing.**
  `StripBuiltInScopeGeneratorRef` removes a legacy `#r "nuget:MeshWeaver.BusinessRules.Generator"` only when
  `builtInPresent` is true. On an image where it is false the `#r` survives and is resolved through NuGet.
  ⚠️ **Unresolved:** the XML doc on that method still describes the built-in generator as shipping with the
  platform, and warns that resolving the legacy `#r` hard-fails on a deployed image once the mesh-local feed
  was gone. Those two statements can no longer both hold. Treat a deployed node still carrying that `#r` as
  suspect and verify it compiles on the target environment rather than assuming it is filtered.

**The `dotnet publish -t:PublishContainer` command above is self-contained — there is no pack step and no
mesh-local feed.** The former `BakeMeshLocalFeed` target and the CI "Pack mesh-local #r packages" step were
both removed in #395; nothing resolves `MeshWeaver.*` from `dist/packages` during a deploy. `nuget.config` is
still copied into the image (so a Code node's third-party `#r "nuget:…"` can resolve), and it still declares
the `mesh-local` source plus a packageSourceMapping pinning `MeshWeaver.*`/`Memex.*` to it — the mapping is
what stops a typo'd `#r "nuget:MeshWeaver.X"` pulling a same-named package from another publisher on
nuget.org. The directory that source points at does not exist in the image.

## 2. Roll out (NS = `memex`)

Portal-only code update (no schema change):

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "\
  kubectl -n <NS> set image deployment/memex-portal-deployment memex-portal=meshweaver.azurecr.io/memex-portal-ai:<tag>; \
  kubectl -n <NS> rollout restart deployment/memex-portal-deployment; \
  kubectl -n <NS> rollout status deployment/memex-portal-deployment --timeout=300s"
```

The portal container is `memex-portal` in Deployment `memex-portal-deployment`.

> **🚨 The migration is a run-once Job in the chart, NOT a Deployment — `set image` cannot roll it.** Older
> copies of this runbook also passed `deployment/memex-migration-deployment` to `set image` and
> `rollout restart`; that step is removed above because it does not run a migration.
> `deploy/helm/templates/memex-migration/job.yaml` renders a `batch/v1` Job named
> `memex-migration-<Release.Revision>` with `restartPolicy: Never`; a fresh one is created by **`helm
> upgrade`**, and `ttlSecondsAfterFinished` cleans it up. `memex-migration-deployment` is a **legacy**
> resource: per `deploy/aks/SELF-UPDATE.md`, live AKS clusters still carry it **scaled to 0**, so
> `set image` / `rollout restart` against it changes a pod spec that never runs, and on a chart-only
> environment the command simply 404s. Either way it is **silent** — the commands appear to succeed and no
> migration happens. **A schema change therefore has to go out via `helm upgrade` (which mints a new Job), not
> via this `set image` roll-out.** Confirm which of the two shapes the target namespace actually has before
> you rely on either (`kubectl -n <NS> get deploy,job | grep migration`).

## 3. Verify

- **Migration ran:** find the Job first — `kubectl -n <NS> get jobs -l app.kubernetes.io/component=memex-migration` — then `kubectl -n <NS> logs job/memex-migration-<revision> --tail=40` → expect `Database migration completed. Version: N`. A Job that reports `Complete` with that line is the success signal.
  - **A `CrashLoopBackOff` on a migration *Deployment* is NOT benign.** That is the legacy shape, and it is exactly the failure the Job replaced: the process exits 0, the Deployment restarts it, and every run rebuilds `public.top_level_index` across every partition schema. The chart records 310 restarts in a day pegging a full core. If you see it, the namespace is still on the legacy Deployment — do not wave it through.
- **Portal serves:** `curl -sS -o /dev/null -w '%{http_code}' https://<portal-host>/` → `200`. **The host is not derivable from the namespace** — namespace `memex` serves `memex.systemorph.com` (the DNS record `deploy/aks/DEPLOY-RUNBOOK.md` creates), while `memex.meshweaver.cloud` is the *`memex-cloud`* namespace. Read the host off the namespace's own Ingress (`kubectl -n <NS> get ingress -o wide`) rather than templating it, or you will happily verify a portal you did not deploy to.
- **Schema/index applied** (when the change was a migration): spot-check via `az aks command invoke … "kubectl -n <NS> exec deployment/memex-portal-deployment -- …"` or an MCP query.

### The cluster runs what the chart describes — check it, don't assume it

A green rollout says the pods came up. It says nothing about whether they came up with the
configuration the chart declares. **An image-tag roll does not apply a chart change — only a
`helm upgrade` does** — so a fix committed to the chart can sit unapplied indefinitely, and a
setting applied by hand (`kubectl set env`) runs until the next `helm upgrade` silently deletes
it. Four such divergences surfaced on one day, none of them detected by anything:

| what diverged | how it went unnoticed |
|---|---|
| the drain `preStop` hook | in the chart, never applied — every roll severed live circuits |
| a `wget` probe | in the chart, contradicted by the image (`curl` present, `wget` absent) |
| the GitHub App identity | hand-applied to one cluster, never in the chart — other envs never got it |
| a portal's `PluginCatalog__*` | inline `env:` on the Deployment, not in the ConfigMap |

```bash
deploy/aks/scripts/check-chart-drift.sh -n <NS> -r <release> \
  -f <values.yaml> [-f <values.env.yaml>] \
  --via aks-invoke -g <aks-resource-group> --aks <aks-cluster> \
  --expect-patch deploy/aks/envs/<env>/portal-patch.json
```

It renders the chart, reads the live `memex-portal-config` and `memex-portal-deployment`, and
classifies every difference as **CLUSTER-ONLY** (hand-applied — the next `helm upgrade` deletes
it), **CHART-ONLY** (described but never applied — nobody is getting it), or **DIFFERS**. Secret
values are never printed; inline `env` is compared by name only.

Two things to know before you trust a green run:

- **Render with the SAME `-f` list the deploy uses.** Values files are git-ignored and live
  outside this repo (see "First-time environment setup"). A different `-f` list diffs against a
  chart nobody deployed, and every unset key reads as drift.
- **`--expect-patch` is how a post-`helm` patch is *declared*.** An env's `deploy.sh` applies
  `portal-patch.json` after `helm upgrade` (the CSI `envFrom`, extra volumes); pass it so those
  additions read as intentional. Anything cluster-only and *not* in that file is undeclared drift.

It is a script and not a CI job on purpose: it needs cluster credentials, and a CI gate that
skips when credentials are absent renders the same tick as one that passed — that would rebuild
the very defect it is meant to catch. It fails RED when it cannot compare — an unreachable
cluster, a failed render, or a rendered ConfigMap with no keys — rather than reporting "no
drift" on no evidence.

## Self-update ops — pausing, pinning, and the rules that bite

Operational facts about the in-pod updater (learned the hard way — each cost a debugging session):

- **Tags must be dotted SemVer** (`3.0.0` / `3.0.0-ci.749`). `VersionSelect.PickTarget` keeps only tags
  matching `^\d+\.\d+\.\d+([-+].*)?$` (and drops per-RID suffixes like `-linux-arm64`), then picks the
  highest. It never inspects the tag you deployed — it compares its pick against the **running build's
  stamped version** (`ShippedReleaseSeed.InstalledPlatformVersion`). So a hand-built `myfix-<sha>` image
  whose build stamped an older version is simply overtaken: the poller finds the newest `ci.<N>` tag,
  judges it newer, and **patches the Deployment off your image**. Manual rolls therefore only stick with
  CI-built `ci.<N>` tags — ship code via a merged PR, or pause the updater first.
- **Pause switch** = the `Admin/UpdatePolicy` node: patch `content.policy` to `None`
  (`Continuous`/`Stable`/`None`). BUT a **freshly booted pod races the policy read**: `CreatePolicySource`
  emits the configured default (`Continuous`) via `StartWith` *before* the node's live value arrives, and
  the poll timer fires immediately (`StartWith(-1L)`). The live `None` then switches the poller off, but a
  check may already have fired. `None` alone therefore does not reliably protect a roll that restarts the
  pod.
- **Hard pause** (break-glass, e.g. pinning a diagnostic image): delete the RoleBinding
  `memex-portal-self-update` (namespace-local; role + SA are both named per chart) — the updater's
  Deployment PATCH then fails closed. Recreate the RoleBinding to resume. Always restore promptly.
- **KeyVault CSI env timing**: a new/changed KV secret needs **two rollout restarts** — the first
  pod's mount populates the synced k8s Secret, but that pod's `envFrom` snapshot predates it; the
  second restart reads the populated Secret. Verify with `printenv <key> | md5sum` in the NEWEST
  pod (sort by `creationTimestamp`).
- **🚨 Namespace ↔ instance mapping**: this cluster hosts several instances whose Deployments all
  share names (`memex-portal-deployment`): namespace `memex` = the systemorph.com company portal,
  `memex-cloud` = **memex.meshweaver.cloud** (SPC `<database>-portal-ai-secrets`, KeyVault
  `Systemorph`, `<database>-`-prefixed secret names), `prod` = the customer portal. Before ANY
  kubectl change, confirm the namespace matches the instance you mean — e.g. run a diagnostic on
  the target portal that prints its pod hostname and `kubectl get pods -A | grep <hostname>`.

## Portal self-update — Workload Identity for ACR polling

Steady state is **self-update** (see [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy)): the portal polls ACR and patches its own Deployment to a newer image. The in-cluster PATCH uses the `memex-portal-sa` service-account token (RBAC ships in the Helm chart and works everywhere). **Listing the ACR tags** to discover a newer image needs an Azure credential — that is wired with **AKS Workload Identity**, mirroring the existing pgBackRest wiring (`deploy/aks/infra/modules/storage.bicep`).

**What the Helm chart already does** (no edits needed): when `selfUpdate.azureClientId` is set it annotates `memex-portal-sa` with `azure.workload.identity/client-id`, labels the pod `azure.workload.identity/use: "true"`, and sets `AZURE_CLIENT_ID`. The self-updater (`AcrTagLister`) then uses `ManagedIdentityCredential(AZURE_CLIENT_ID)` → AAD token → ACR token.

**What the Azure side provides** (`deploy/aks/infra/modules/portal-identity.bicep`, wired from `deploy/aks/infra/main.bicep`): a **single shared** user-assigned managed identity (`<namePrefix>-portal-mi`) with **one federated credential per portal namespace** — subject `system:serviceaccount:<ns>:memex-portal-sa`, issuer = the cluster OIDC issuer, audience `api://AzureADTokenExchange` — for every namespace in the `portalNamespaces` param (`memex`, `memex-cloud`, and any customer portal namespaces). The UAMI gets **AcrPull** on `meshweaver.azurecr.io` (AcrPull includes the `metadata_read` the tag-list call needs). One UAMI → one AcrPull grant → the **same** `portalIdentityClientId` wired into `selfUpdate.azureClientId` for every namespace.

### One-time setup

1. **Provision the UAMI + federated credentials** — included in the infra deploy (`deployPortalIdentity` defaults `true`). Read the client id back:
   ```bash
   # --name is whatever the infra deploy was created with; deploy/aks/DEPLOY-RUNBOOK.md uses memex-aks-infra-sc.
   az deployment sub show --name memex-aks-infra-sc \
     --query "properties.outputs.{clientId:portalIdentityClientId.value, principalId:portalIdentityPrincipalId.value}" -o jsonc
   ```
2. **Grant AcrPull on the shared registry.** The ACR (`meshweaver.azurecr.io`, RG `meshweaver-shared`) is **cross-RG** from `<aks-resource-group>`, so — exactly like the cluster kubelet's AcrPull — grant it out-of-band:
   ```bash
   PORTAL_MI_OID=$(az identity show -g <aks-resource-group> -n <portal-identity> --query principalId -o tsv)
   az role assignment create --assignee-object-id "$PORTAL_MI_OID" --assignee-principal-type ServicePrincipal \
     --role AcrPull --scope $(az acr show -n meshweaver --query id -o tsv)
   ```
   (IaC alternative: deploy with `grantSharedAcrPull=true` — authors this via `infra/modules/acr-role-assignment.bicep` in the registry's RG; needs User Access Administrator on `meshweaver-shared`. A *per-deployment* ACR instead of the shared one is granted in-bicep automatically.)
3. **Set `selfUpdate.azureClientId`** to `portalIdentityClientId` for each environment (the in-pod patch works without it; this only authenticates the tag-list). Same value everywhere:
   - `memex` → the git-ignored `values.deploy.yaml` in the staging dir (template: `deploy/aks/scripts/values.deploy.example.yaml`), or `helm upgrade --set selfUpdate.azureClientId=<clientId>`.
   - `memex-cloud` / customer portals → the git-ignored `deploy/aks/envs/<env>/values.<env>.yaml`.

> Adding a **new** portal namespace? It needs its own federated credential on the shared UAMI — add the namespace to `portalNamespaces` and re-run the infra deploy (idempotent), or `az identity federated-credential create` (see [OnboardingNewEnvironment.md](/Doc/Architecture/OnboardingNewEnvironment)). The subject must be exactly `system:serviceaccount:<ns>:memex-portal-sa`.

## Migration under self-update

When an install rolls itself to a new tag (per `Admin/UpdatePolicy`), the in-pod updater patches **two
Deployments** in one pass — `memex-portal-deployment` (container `memex-portal`) and
`memex-migration-deployment` (container `memex-migration`), the names in `SelfUpdateOptions`.

**🚨 That second patch does not currently run a migration, and this is a known gap, not a working
mechanism.** `deploy/aks/SELF-UPDATE.md` states it outright: the chart's migration is a Job, but the updater
and its RBAC target `memex-migration-deployment` — live AKS clusters have that Deployment scaled to 0, so
the PATCH is a no-op there, and a chart-only environment logs a 404 and skips it. **So a self-update rolls
the portal image while the schema stays where it was.** A release that carries a migration needs the Job to
run (`helm upgrade`) — do not assume self-update covers it. Verify `admin.mesh_nodes.db_version` after any
roll that included a schema change.

**Startup ordering — what actually happens if the portal outruns the schema.** Two mechanisms exist, and
only the first is a wait:

- the portal pod's `wait-for-postgres` **initContainer** genuinely blocks startup until Postgres accepts TCP
  connections; and
- the portal's **`DbVersionGate`** hosted service does a **one-shot check at startup, and does not wait**. It
  reads `admin.mesh_nodes.db_version` once and, if it is below the `ExpectedDbVersion` constant compiled into
  the build, logs `Critical` and calls `lifetime.StopApplication()`.

So a portal that starts against an un-migrated database **fails closed and exits** — it does not hold and
then go live. Kubernetes restarts it, and it recovers only once something else has bumped `db_version`; until
then the namespace has no serving portal. The gate protects the database from a half-migrated portal; it does
**not** make the ordering safe on its own. Treat "the migration ran" as a precondition you verify, not one
the roll guarantees.

## First-time environment setup ≠ code update

`deploy/aks/envs/<env>/deploy.sh` provisions a **new** environment: `helm upgrade --install` of the chart, PVCs, the Key Vault `SecretProviderClass`, ingress, and the connection-string patch. **Do not run it for a code update** — it re-applies the whole chart and can reset live ConfigMaps (e.g. the email config). Use it only when standing up a brand-new namespace.

Only the reference env `deploy/aks/envs/example/` is in this repo; per-tenant env directories are git-ignored (`.gitignore`: "directory names are tenant identities and must not enter this public repo"), so the real ones live outside it. Copy `example/` as the template.

Note the tension with §2: because the chart's migration is a Job created per Helm revision, `helm upgrade` is also the *only* in-repo path that runs a migration. A schema change consequently needs this script (or a bare `helm upgrade`) even though a plain code update must not use it.

## Diagnostics (private cluster)

- Logs: `az aks command invoke … --command "kubectl -n <NS> logs deployment/memex-portal-deployment --tail=120"`. Note: the Azure CLI can crash on non-ASCII (`→`) in log output on Windows (cp1252) — pipe through `tr -cd '\11\12\15\40-\176'` **inside** the `--command` so az only receives printable text.
- A `MESHWEAVER_MSG_TRACE=1` env var on the portal Deployment turns on the message-flow trace (`/tmp/meshweaver-msg-trace.log` in the pod). Toggling it restarts the pod; remove it (`kubectl set env … MESHWEAVER_MSG_TRACE-`) when done — it writes per-message and adds lock/IO overhead.

---

For Azure AD app registration and secrets (shared across both routes), see [Deployment.md](/Doc/Architecture/Deployment).
