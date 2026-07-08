---
Name: Instances
Category: Architecture
Description: Inventory of the running MeshWeaver instances — who each one is for, the infrastructure it runs on, its database, and how it is versioned, created, and deleted
Icon: Server
---

# Running Instances

An **instance** is one MeshWeaver portal: its own domain, its own database, and its own sign-in,
served by a dedicated **Kubernetes namespace** on the shared AKS cluster. All prod instances share
one cluster, one container registry, and one PostgreSQL server — only the namespace, domain, and
database differ. This page is the living inventory; keep it in sync when an instance is added or
removed.

## Inventory

| Instance | Namespace | Owner / purpose | Access | Database | Version channel |
|---|---|---|---|---|---|
| **memex.systemorph.com** | `memex` | **Systemorph company instance** — PartnerRe & client work, infra control, accounting | Private — Roland & Markus only | `memex` | Continuous self-update |
| **memex.meshweaver.cloud** | `memex-cloud` | **Public** instance — collaboration, showcase, demos | Public sign-in (Microsoft / Google / LinkedIn) | `memexcloud` | Continuous self-update |
| **atioz** *(customer domain in the env's git-ignored config)* | `atioz` | **Customer** portal (atioz) | Customer sign-in | `atioz` | Continuous self-update |
| **memex-local** | *(local k3s)* | Local dev — prod-like memex on a Mac (Colima k3s, arm64) | localhost only | local pgvector container | `autoroll` (host launchd) |

> The three cloud instances are **the same image** — a code change merged to `main` reaches all of
> them via self-update (below). They differ only in data (separate databases), branding, sign-in
> config, and who can log in.

## Shared platform (all cloud instances)

| Piece | Value |
|---|---|
| AKS cluster | `memexaks-cluster` (RG `memex-aks-rg`, **swedencentral**) — **private** cluster; `kubectl` only via `az aks command invoke` |
| Container registry | `meshweaver.azurecr.io` (ACR), multi-arch images (amd64 + arm64) |
| Database server | `memexaks-pg` — **private** Azure PG **Flexible Server 16** + pgvector, VNet-injected (one **database per instance**) |
| Workload identity | One shared UAMI `memexaks-portal-mi`, one federated credential per namespace, `AcrPull` on the ACR |
| App stack | .NET 10 · Blazor Server · Orleans · Microsoft.Extensions.AI |
| Backups | Managed PITR (14 days) + geo-redundant — see [DatabaseBackups.md](/Doc/Architecture/DatabaseBackups) |

## Versioning — how to read the live version

Each instance runs the ACR image tag its in-pod self-updater last rolled to — the CI build number
`ci.<N>`. To see what a namespace is actually running:

```bash
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
  "kubectl -n memex get deployment memex-portal-deployment \
   -o jsonpath='{.spec.template.spec.containers[0].image}'"
# → meshweaver.azurecr.io/memex-portal-ai:ci.<N>
```

## Self-update (the version channel)

Merge to `main` → CI builds a multi-arch image to ACR (`ci.<N>`) → each portal's **in-pod
self-updater** polls ACR and patches its own Deployment to the new tag → migration Job runs → portal
rolls. No manual step per instance. This is why **a red `main` blocks the rollout** for every
instance, and why the merge gate requires green CI. Full model:
[ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy).

A manual code push to one instance (bypassing self-update) is the `kubectl set image` + rollout
sequence in [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS).

## Instance lifecycle — creating and deleting instances

**There is no "create instance" / "delete instance" button in the portal today.** An instance is
provisioned with the deploy tooling, not from the running app — the company instance is where you
*run* that tooling (or drive it over MCP), not a control plane that spins up other instances.

**Create** a new instance — full runbook in
[OnboardingNewEnvironment.md](/Doc/Architecture/OnboardingNewEnvironment):

1. Add the namespace to `portalNamespaces` in `deploy/aks/infra/main.bicep` (creates its federated
   credential + AcrPull) and redeploy the identity module.
2. Create the instance's **database** on the shared `memexaks-pg` server.
3. Author `deploy/aks/envs/<env>/values.<env>.yaml` (git-ignored: host, `MEMEX_DATABASENAME`, TLS
   secret, AI + auth config, `selfUpdate.azureClientId`).
4. `deploy/aks/envs/<env>/deploy.sh` — helm install + PVCs + KV `SecretProviderClass` + ingress + TLS.
5. Wire sign-in redirect URIs + invitation/email config for the new domain.

**Delete** an instance:

1. `helm uninstall` the release in its namespace, then delete the namespace (removes pods, PVCs,
   ingress, secrets).
2. Drop (or archive-then-drop) the instance's **database** on `memexaks-pg` — this is the only place
   its data lives, so **back it up first** ([DatabaseBackups.md](/Doc/Architecture/DatabaseBackups)).
3. Remove the namespace from `portalNamespaces` (drops its federated credential) and delete its
   DNS record + TLS cert + git-ignored `envs/<env>/` config.

> Turning the company instance into a real control plane (create / tear down instances **from the
> UI**, calling the Azure + Helm APIs behind an admin gate) is a possible future feature, not a
> current capability.
