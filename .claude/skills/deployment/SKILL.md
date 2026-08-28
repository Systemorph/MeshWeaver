---
name: deploy
description: 'Deploy MeshWeaver — the two routes (shared AKS cluster, and Azure Container Apps via the Aspire test/prod modes) and the traps that make a deploy look done when it is not. Use when rolling a code change onto a portal, standing up or patching an environment, or verifying that what is running is what you built. Covers the AKS build-image / set-image / restart sequence on a PRIVATE cluster (kubectl only through az aks command invoke), why an env deploy.sh is first-time setup and not a code-update path, why the database migration is a run-once Job that a helm upgrade runs — never a Deployment to roll — and the DbVersionGate that makes a portal refuse to serve ahead of its schema.'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /deploy — pick the route by TARGET, then verify what is actually running

**Two deploy routes, different targets — neither deprecated. Don't mix them.**

- **AKS** — the shared cluster `memex` portal. Full ref:
  [DeploymentAKS.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md).
- **Azure Container Apps** — the Aspire `test`/`prod` modes, via `tools/deploy.sh prod|test`. Full
  ref:
  [DeploymentContainerApps.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentContainerApps.md).

Index: [Deployment.md](../../../src/MeshWeaver.Documentation/Data/Architecture/Deployment.md).

🚨 **Before any AKS deploy, read DeploymentAKS.md end-to-end** — it is the source of truth for
build → roll-out → verify. 🚨 It no longer describes an auto-baked mesh-local `#r` package feed: the
`BakeMeshLocalFeed` target was **REMOVED (#395)**, `dist/packages` is gone, and a legacy
`#r "nuget:MeshWeaver.BusinessRules.Generator"` now hard-fails on a deployed image (`"The local
source '/app/dist/packages' doesn't exist"` — the prod BalanceSheet failure). The generator ships
built-in and `GeneratorPipeline` strips that `#r` instead. Nothing bakes at BUILD time on any
machine, dev Macs included: `--bake-output` exists only in CI scripts, never in a
`.targets`/`.props`/`.csproj`. The commands below are a quick reference, not a substitute for the
doc.

## The AKS route

The `memex` portal runs on the shared **AKS cluster** `<aks-cluster>` (RG `<aks-resource-group>`,
swedencentral) — namespace `memex` — against the Postgres Flexible Server, images in ACR
`meshweaver.azurecr.io`. **Private cluster: `kubectl` ONLY via
`az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "…"`.**

**On AKS a code update = build image → set image → restart** (the AKS route does NOT use
`tools/deploy.sh` or `aspire deploy` — those are the Container Apps route):

```bash
az acr login -n meshweaver
# Portal (custom base) AND migration (the migration is what creates schema + the matview):
dotnet publish ../MeshWeaver.Plugins/src/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-portal-ai -p:ContainerImageTag=<tag> \
  -p:ContainerBaseImage=meshweaver.azurecr.io/memex-portal-ai-base:latest
dotnet publish memex/aspire/Memex.Database.Migration/Memex.Database.Migration.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-migration -p:ContainerImageTag=<tag>
# Roll out (NS = memex). 🚨 The MIGRATION is a Job, not a Deployment — see below.
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "\
  kubectl -n <NS> set image deployment/memex-portal-deployment memex-portal=meshweaver.azurecr.io/memex-portal-ai:<tag>; \
  kubectl -n <NS> rollout restart deployment/memex-portal-deployment; \
  kubectl -n <NS> rollout status deployment/memex-portal-deployment --timeout=300s"
```

- **An env's `deploy.sh` is first-time ENV SETUP only** (helm install + PVCs + KV
  SecretProviderClass + ingress + connection-string patch). Do NOT use it for a code update — it
  re-runs the whole chart and can reset live config. 🚨 **Env folders live in the PRIVATE
  `Systemorph/Memex` repo**, not `deploy/aks/envs/<env>/` — they moved out 2026-08-08/09
  (`a69959165`) because their directory names are tenant identities; `deploy/aks/envs/example/` in
  this repo is the reference template only.
- **Don't run `tools/deploy.sh` or `aspire deploy` against the AKS cluster** — those are the
  *Container Apps* route (a different target), not a code-update path for AKS.

## 🚨 The migration is a run-once `Job`, NOT a Deployment

`deploy/helm/templates/memex-migration/job.yaml`: `kind: Job`, `restartPolicy: Never`, the name
embeds `.Release.Revision`, `ttlSecondsAfterFinished`. It runs on **`helm upgrade`**, not on an
image-tag roll — so a `kubectl set image` / `rollout restart` aimed at a
`memex-migration-deployment` is a command with no object: the chart does not define one, and any
that exists in a live namespace is a cluster-only orphan of a chart revision that predates the Job
(#1788). Do not resurrect it. A `MigrationWorkloadModelGuard`
(test/MeshWeaver.Documentation.Test) fails the build if such a command returns to AGENTS.md, a
skill under `.claude/skills/`, a doc, or a deploy script.

**A crash-looping migration pod is a FAILURE, not noise.** It used to be modelled as a Deployment,
which restarted it forever after each clean exit — three prod namespaces sat at 50/53/38 restarts,
each rebuilding `public.top_level_index` across every partition schema. Documenting that as
"benign" is what made a real migration failure unreadable. With the Job form the pod runs once and
stops, so `CrashLoopBackOff` on it now means exactly what it says.

**Before declaring a deploy successful**, confirm the Job's log shows
`Database migration completed. Version: N` AND the portal serves (HTTP 200):

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl -n <NS> get jobs -l app.kubernetes.io/component=memex-migration"
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl -n <NS> logs job/memex-migration-<revision>"
```

**The schema has a hard gate.** `DbVersionGate` (a hosted service in `Memex.Portal.Distributed`)
reads `admin.mesh_nodes.db_version` at startup and `StopApplication()`s with a `LogCritical` when it
is below `DbVersion.Latest`. So a portal rolled ahead of its schema refuses to serve rather than
serving a half-migrated database — loud, and the reason the ordering between the two workloads
cannot silently invert.

## Verify the IMAGE, never the green tick

A merged, green, CD-published change is not a deployed change until the running pod says so. The
delivery chain, the publish batching window and the reconciler are in
[/release](../release/SKILL.md); the assertions are:

```bash
az acr repository show-tags -n meshweaver --repository memex-portal-ai --orderby time_desc --top 5 -o tsv
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl get deploy -A -o custom-columns=NS:.metadata.namespace,IMAGE:.spec.template.spec.containers[0].image --no-headers | grep memex-portal-ai"
.github/scripts/check-image-set.sh <short-sha>   # the exact assertion CD itself makes
```

## Running the portal locally (not a deploy route)

`dotnet watch --project ../MeshWeaver.Plugins/src/Memex.AppHost` restarts only the affected Aspire
resource on save; the dashboard's Resources → ⋯ → **Restart** is the fallback; a process kill is the
last resort. Don't kill the whole `aspire` / `Memex.AppHost` process unless you changed AppHost
wiring itself — a full restart costs 30–60 s and loses the dashboard auth token. Full reference:
[LocalDevWorkflow.md](../../../src/MeshWeaver.Documentation/Data/Architecture/LocalDevWorkflow.md).

## Checklist

- [ ] Route chosen by target (AKS vs Container Apps) — no `tools/deploy.sh`/`aspire deploy` against
      AKS.
- [ ] `kubectl` reached only through `az aks command invoke`.
- [ ] No `deploy.sh` re-run for a code update.
- [ ] The migration Job's log shows `Database migration completed. Version: N`.
- [ ] The RUNNING image tag was read back off the deployment — not inferred from a green CI tick.
