---
Name: Instances
Category: Architecture
Description: What a MeshWeaver instance is, how its running version is read, how self-update works, and how instances are created and deleted
Icon: Server
---

# Running Instances

An **instance** is one MeshWeaver portal: its own domain, its own database, and its own sign-in,
served by a dedicated **Kubernetes namespace**. Instances typically share one cluster, one container
registry and one PostgreSQL server — only the namespace, domain and database differ, so a code change
merged to `main` reaches all of them via self-update. They differ in data, branding, sign-in
configuration, and who may log in.

## Where the inventory lives

**Not here.** Which instances exist, who each is for, what they are named and which database each
uses is operational detail about live services — it belongs with whoever runs them.

- **Running an instance of your own?** Nothing on this page needs changing. Read it for the model
  (below): how versions are read, how self-update works, and how instances are created and deleted.
- **Operating an installation Systemorph runs?** The inventory is in the private
  [Systemorph/Memex](https://github.com/Systemorph/Memex) repo — `docs/deployments.md` for what runs
  where, `deployments/aks/` for each one's configuration.

The rest of this page is the mechanism, and applies to any installation.

## Shared platform (all cloud instances)

| Piece | Value |
|---|---|
| AKS cluster | `<aks-cluster>` (RG `<aks-resource-group>`, **swedencentral**) — **private** cluster; `kubectl` only via `az aks command invoke` |
| Container registry | `meshweaver.azurecr.io` (ACR), multi-arch images (amd64 + arm64) |
| Database server | `<pg-server>` — **private** Azure PG **Flexible Server 16** + pgvector, VNet-injected (one **database per instance**) |
| Workload identity | One shared UAMI `<portal-identity>`, one federated credential per namespace, `AcrPull` on the ACR |
| App stack | .NET 10 · Blazor Server · Orleans · Microsoft.Extensions.AI |
| Backups | Managed PITR (14 days) + geo-redundant — see [DatabaseBackups.md](/Doc/Architecture/DatabaseBackups) |

## Versioning — how to read the live version

Each instance runs the ACR image tag its in-pod self-updater last rolled to — the CI build number
`ci.<N>`. To see what a namespace is actually running:

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
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
2. Create the instance's **database** on the shared `<pg-server>` server.
3. Author `deploy/aks/envs/<env>/values.<env>.yaml` (git-ignored: host, `MEMEX_DATABASENAME`, TLS
   secret, AI + auth config, `selfUpdate.azureClientId`).
4. `deploy/aks/envs/<env>/deploy.sh` — helm install + PVCs + KV `SecretProviderClass` + ingress + TLS.
5. Wire sign-in redirect URIs + invitation/email config for the new domain.

**Delete** an instance:

1. `helm uninstall` the release in its namespace, then delete the namespace (removes pods, PVCs,
   ingress, secrets).
2. Drop (or archive-then-drop) the instance's **database** on `<pg-server>` — this is the only place
   its data lives, so **back it up first** ([DatabaseBackups.md](/Doc/Architecture/DatabaseBackups)).
3. Remove the namespace from `portalNamespaces` (drops its federated credential) and delete its
   DNS record + TLS cert + git-ignored `envs/<env>/` config.

> Turning the company instance into a real control plane (create / tear down instances **from the
> UI**, calling the Azure + Helm APIs behind an admin gate) is a possible future feature, not a
> current capability.

## The admin Instances tab (company instance only)

**Settings ▸ Administration ▸ Instances** lists every portal on the cluster live from the k8s API —
domain, namespace, running version (image tag), replica health — with per-instance Grafana/Loki log
deep links and a guided create-instance **plan** generator (commands only; nothing deploys itself).

The tab exists **only on portal.example.com**, doubly gated:

- **`Instances:Enabled`** (config, default `false`) — the Settings menu item is not even created on
  an install that doesn't set it. In the Helm chart, `instancesAdmin.clusterRead` drives BOTH this
  flag and the RBAC below; `deploy/aks/values.aks.yaml` (the `memex` env overlay) sets it `true`,
  customer/public envs inherit the default.
- **Cluster-read RBAC** (`instancesAdmin.clusterRead`) — reading deployments/ingresses across
  namespaces needs a cluster-scoped grant, given only to the company instance (a tenant pod must not
  enumerate the cluster). Without it the tab (where enabled) shows "Cluster query unavailable".

Log links need `Instances:GrafanaBaseUrl` (Helm: `instancesAdmin.grafanaBaseUrl`) — the Grafana
Explore (Loki) deep link is built per namespace. Empty until the in-cluster Grafana
(`monitoring` ns, `loki-grafana` service) is exposed on a host.
