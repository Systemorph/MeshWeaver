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

## First-time environment setup ≠ code update

`deploy/aks/envs/<env>/deploy.sh` provisions a **new** environment: `helm install` of the chart, PVCs, the Key Vault `SecretProviderClass`, ingress, and the connection-string patch. **Do not run it for a code update** — it re-applies the whole chart and can reset live ConfigMaps (e.g. the email config). Use it only when standing up a brand-new namespace.

## Diagnostics (private cluster)

- Logs: `az aks command invoke … --command "kubectl -n <NS> logs deployment/memex-portal-deployment --tail=120"`. Note: the Azure CLI can crash on non-ASCII (`→`) in log output on Windows (cp1252) — pipe through `tr -cd '\11\12\15\40-\176'` **inside** the `--command` so az only receives printable text.
- A `MESHWEAVER_MSG_TRACE=1` env var on the portal Deployment turns on the message-flow trace (`/tmp/meshweaver-msg-trace.log` in the pod). Toggling it restarts the pod; remove it (`kubectl set env … MESHWEAVER_MSG_TRACE-`) when done — it writes per-message and adds lock/IO overhead.

---

For Azure AD app registration and secrets (shared across both routes), see [Deployment.md](/Doc/Architecture/Deployment).
