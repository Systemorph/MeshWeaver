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

**Every self-update roll must compile every dynamic NodeType** — the new image's framework identity
misses the whole `/data` assembly cache by design, and a lazily-compiled mesh leaves any un-visited
type hanging its pages ("no definition", 60 s `SubscribeRequest` timeouts — memex-cloud 2026-07-30).
The self-updater itself only patches the image; the compiling happens on the NEW pod at startup via
`PreWarm__DynamicTypes` (see `DEPLOY-RUNBOOK.md` §7): every new pod sweeps and compiles all dynamic
NodeTypes at start, so an un-visited type can no longer sit "no definition" until its pages hang.
Managed envs run the sweep ON — an env with it off has no deploy-time compile at all on this
channel.

**⛔ The self-update does NOT currently wait for the compile.** The companion readiness gate
(`PreWarm__GateReadiness`) is designed to do exactly that — hold the new pod's `/health` red until
every NodeType is built against ITS image, with the `startupProbe` on `/health` and
`maxUnavailable: 0` keeping the old pod serving — so a regressed type STALLS the roll instead of
surfacing as user-facing errors. It is **off**.

It was enabled on 2026-08-02 and reverted the same day. The first gated roll on memex-cloud stalled
with `7 NodeType(s) regressed on this image`, and those were **false** regressions: the pod log
contained no `CS####` compile error at all, only the sweep timing out across the roll window —
`No response received in hub cache/… within 00:01:00 for request SubscribeRequest → target
Claims / Reinsurance / SocialMedia / ClaimsDeepfield / RiskTransfer`. During a roll the baking pod
and the serving pod are two silos and the sweep cannot resolve shared sources across that boundary.
This is core #694 residue: #718 + #719 (2026-07-29) did NOT close it, so the earlier 2026-07-30
observation stands unexplained by them.

Until the sweep's cross-silo source resolution is fixed, database migration is the only part of the
roll that is gated (the unconditional `db_version` health check), and NodeType compiles fall back to
the pod-side sweep with lazy compile on failure. ⚠️ Do not re-enable the gate by widening the probe
budget — the sweep is not slow, it is erroring.

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
