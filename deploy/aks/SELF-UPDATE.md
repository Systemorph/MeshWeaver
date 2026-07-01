# Pull-based self-update (the deploy model)

Deployment is **pull, not push**. CI's responsibility ends at *publishing the image to ACR*
(`.github/workflows/main-cd.yml` → the `images` job). There is **no deploy job** and CI holds **no
cluster credentials**. Each installation updates *itself*:

```
merge to main ─▶ "Build and Test" (green) ─▶ images job builds+pushes  meshweaver.azurecr.io/memex-portal-ai:<version>
                                                                              │
        ┌─────────────────────────────────────────────────────────────────────┼──────────────── … every install in the world
        ▼                                   ▼                                   ▼
   memex install                       atioz install                      external install
   SelfUpdateHostedService polls ACR (its OWN workload identity) every 6h, per its OWN Admin/UpdatePolicy,
   and PATCHes its OWN portal+migration Deployments via its OWN in-cluster ServiceAccount token.
```

**Why** — prod must not know about the (potentially many) installs, and a central CI service principal
with per-cluster Azure RBAC cannot scale to clusters prod doesn't manage. (It was also the concrete
break on 2026-06-29: the CD SP lacked `Microsoft.ContainerService/managedClusters/commandResults/read`,
so every `az aks command invoke` deploy leg failed.) Pull-based removes that failure class entirely.

Code: `memex/Memex.Portal.Shared/SelfUpdate/` — `SelfUpdateHostedService` (poller), `AcrTagLister`
(ACR via AAD→ACR token exchange), `VersionSelect` (which tag), `KubernetesDeploymentUpdater` (in-cluster
PATCH). Wired by `AddSelfUpdate()` in `MemexConfiguration.cs`.

## Update policy (Admin → Platform updates)

`Admin/UpdatePolicy` (`UpdatePolicyContent`), editable in the **Platform updates** settings tab:

| Field | Meaning |
|---|---|
| **Update strategy** | `Continuous` (newest build incl. `-ci.N`, default) · `Stable` (clean releases only) · `None` (manual) |
| **Only update to CI-verified (green) builds** | default **on**. The `images` job publishes ONLY when "Build and Test" is green, so every published tag is green by construction; this flag stays correct if an unverified **edge** channel is added (it excludes `-edge.N` tags). Off = also accept edge builds. |

## Enabling self-update on an AKS environment

Most of it is already in the chart (`deploy/helm/templates/memex-portal/`): the `memex-portal-sa`
ServiceAccount, a namespaced Role/RoleBinding granting `get,patch` on the portal+migration Deployments,
`serviceAccountName: memex-portal-sa` on the Deployment, and the conditional workload-identity
annotation/label/env. The gaps are operational:

1. **Azure (once):** ensure the portal UAMI + per-namespace federated credentials exist
   (`deploy/aks/infra/modules/portal-identity.bicep`, default-on via `main.bicep`), and grant it
   **AcrPull** on the registry (cross-RG, out-of-band — see `DEPLOY-RUNBOOK.md`):
   ```bash
   PORTAL_MI=$(az identity show -g memex-aks-rg -n memexaks-portal-mi --query principalId -o tsv)
   az role assignment create --assignee-object-id "$PORTAL_MI" --assignee-principal-type ServicePrincipal \
     --role AcrPull --scope "$(az acr show -n meshweaver --query id -o tsv)"
   ```
2. **Set `selfUpdate.azureClientId`** to the UAMI client id in each env's (git-ignored) values overlay,
   then **`helm upgrade`** the env. This both wires workload identity AND (for envs whose live
   Deployment predates the chart's SA — e.g. **atioz**, currently on the `default` SA) creates the SA +
   RBAC and sets `serviceAccountName`. Manual fallback without re-helm:
   ```bash
   kubectl -n <ns> apply -f <SA + Role + RoleBinding from the chart>
   kubectl -n <ns> patch deployment memex-portal-deployment --type=merge -p \
     '{"spec":{"template":{"metadata":{"labels":{"azure.workload.identity/use":"true"}},"spec":{"serviceAccountName":"memex-portal-sa"}}}}'
   ```
3. **Verify:** the portal logs `[SelfUpdate] starting … canPatch=True`, and a newer ACR tag triggers
   `[SelfUpdate] applying update <tag>`. A `403` on PATCH = missing RBAC (step 2); a token/ACR error =
   missing workload identity or AcrPull (steps 1–2).

> ⚠️ The chart's migration is a **Job**, but the updater/RBAC target `memex-migration-deployment`. Live
> AKS clusters still have that Deployment (scaled to 0), so the migration PATCH is a harmless no-op
> there; a chart-only env logs a 404 and skips it.

## Follow-up: the "edge" (any / unverified) channel

`RequireCiGreen = false` only does something once an **edge channel** publishes images on *every* build
(not just green), tagged with an `edge` SemVer label (e.g. `3.0.0-edge.<run>`). Add a workflow that
builds+pushes on `push: main` (independent of the test gate) to those tags; `VersionSelect.IsEdge` already
recognizes and (in green-only mode) skips them.
