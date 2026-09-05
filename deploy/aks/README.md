# MeshWeaver Memex on AKS — a public, self-contained deployment sample

A reference, operator-facing deployment of the **Memex portal** on a **private
Azure Kubernetes Service** cluster. It layers AKS-specific Azure infrastructure
(Bicep) and a Helm values overlay on top of the generic Kubernetes chart that
already lives at [`../helm`](../helm). Everything here is **infra / YAML /
markdown only** — no application code changes.

> This is a **sample**, and it is **self-contained**: this one file is the whole
> AKS story — bring-up, ingress/TLS, the roll-out procedure, and the traps that
> have actually bitten production. Read it end-to-end, tune the parameters for
> your environment (regions, SKUs, CIDRs, DNS names, secrets), and treat the
> security defaults as a starting point, not a finished hardening.

Architecture decisions baked in:

- **Private** AKS API server + **private** Postgres Flexible Server; **only** the
  portal is public (`:443`).
- One shared **Azure Container Registry**, reachable from every environment.
- Content on RWX **Azure Files** (`/mnt/content`); mesh data in Postgres.
- **Blazor sticky sessions** (cookie affinity), HA across replicas with Orleans
  **AdoNet** clustering.
- TLS via **cert-manager + Let's Encrypt** (HTTP-01).

---

## What you get

| Concern | This sample's choice |
|---|---|
| Cluster | **Private AKS** (`enablePrivateCluster=true`) — API server has a private IP only |
| kubectl reach | **`az aks command invoke`** — runs kubectl/helm inside the cluster, no network line-of-sight. An **optional** Azure VPN Gateway (Point-to-Site) + linked private DNS zone `privatelink.<region>.azmk8s.io` gives interactive tooling a route into the VNet ([Appendix A](#appendix-a--point-to-site-vpn-optional)) |
| Registry | **Azure Container Registry** (Premium) with **AcrPull** granted to the cluster's kubelet identity |
| Networking | VNet with `aks-nodes`, `GatewaySubnet`, `AzureBastionSubnet` subnets; Azure CNI overlay + Cilium |
| Portal | Blazor Server, **HA** behind ingress with **cookie session affinity**; replicas owned by a KEDA `ScaledObject` (2 → 8 in this overlay), never by a `kubectl patch` |
| Shared storage | **Azure Files (RWX)** drives mounted at explicit paths: `/data` (caches), `/mnt/content` (content collection), `/mnt/attachments`, `/mnt/users` — via a custom `azurefile-memex` StorageClass tuned for the non-root portal (uid 1654) |
| Database | **Azure Database for PostgreSQL Flexible Server**, VNet-injected + private (default). A self-managed **Postgres (pgvector) StatefulSet** on a Premium-SSD PVC is the in-cluster alternative |
| Backup / PITR | **pgBackRest** → **Azure Blob** (full + diff CronJobs + WAL archiving) → restore `--type=time` |
| Observability | **OpenTelemetry Collector DaemonSet** captures cluster-wide pod logs + portal OTLP → **Azure Files** log archive (`/mnt/otel-logs`); **Grafana + Loki + Promtail + Prometheus** in `monitoring` for search and dashboards |
| Error ticketing | **`mw-log-watcher`** (optional, `monitoring`) reads red logs from Loki and opens one triaged GitHub issue per distinct fault — off until configured |
| Identity | **Workload Identity (OIDC)** so pgBackRest reaches Blob **keyless** |
| Instance lifecycle | **provision → suspend → tear down** driven from the mesh, with a verified `pg_dump` before anything is destroyed and an ingress-level paywall while suspended — see [INSTANCE-LIFECYCLE.md](INSTANCE-LIFECYCLE.md) |

### Topology

```
                       ┌──────────────── operator laptop ───────────────┐
                       │  az aks command invoke  (primary — no tunnel)   │
                       │  azure-vpn / OpenVPN client (OPTIONAL, Appx A)  │
                       └───────────────────────┬─────────────────────────┘
                                               │ P2S tunnel (172.16.201.0/24)
                                               │ — optional; Grafana/psql only
                  ┌────────────────────────────▼──────────────────────────┐
                  │  VNet 10.0.0.0/16                                      │
                  │  ┌── GatewaySubnet ──┐  ┌── aks-nodes 10.0.0.0/20 ──┐ │
                  │  │  VPN Gateway      │  │  AKS node pool (3x, zonal)  │ │
                  │  └───────────────────┘  │   ├ memex-portal x3 (RWX)   │ │
                  │  privatelink.<rgn>.      │   ├ memex-postgres (PVC)    │ │
                  │  azmk8s.io  ◄── private  │   │   └ pgbackrest sidecar  │ │
                  │  API server A record     │   └ pgbackrest CronJobs     │ │
                  │                          └─────────────┬───────────────┘ │
                  └────────────────────────────────────────┼─────────────────┘
                                                            │ Workload Identity
                          ACR (AcrPull)        Azure Blob ◄─┘ (WAL + backups, keyless)
```

---

## Repository layout

```
deploy/aks/
├── README.md                  ← you are here — the whole AKS story
├── SELF-UPDATE.md             ← how a running portal pulls its own new image
├── INSTANCE-LIFECYCLE.md      ← provision → suspend → tear down, driven from the mesh
├── values.aks.yaml            ← Helm overlay for ../helm (AKS overrides)
├── scripts/
│   ├── deploy.sh              ← namespace + PVCs + helm upgrade, run via `command invoke`
│   ├── tls.sh                 ← cert-manager + Let's Encrypt issuer + ingress TLS
│   ├── install-observability.sh ← Grafana + Loki + Promtail + Prometheus (ns monitoring)
│   ├── import-dashboards.sh   ← generic Grafana dashboard importer
│   ├── check-chart-invariants.sh ← PR gate: does the CHART describe a workable shape?
│   ├── check-chart-drift.sh   ← does the CLUSTER run what the chart describes?
│   ├── aks-extras.yaml        ← StorageClass + RWX PVCs, applied by deploy.sh
│   └── values.deploy.example.yaml ← template for your secrets file (never commit the real one)
├── infra/
│   ├── main.bicep             ← subscription-scoped orchestrator (creates RG)
│   ├── main.parameters.json   ← edit these
│   └── modules/
│       ├── network.bicep      ← VNet + subnets + private DNS zone + VNet link
│       ├── acr.bicep          ← Azure Container Registry
│       ├── aks.bicep          ← PRIVATE AKS + identities + AcrPull + CSI + OIDC
│       ├── vpn.bicep          ← P2S VPN Gateway (cert auth, OpenVPN/IKEv2)
│       ├── storage.bicep      ← Blob storage + Workload Identity for pgBackRest
│       └── files.bicep        ← Azure Files account + named shares for STATIC PV binding
└── manifests/                 ← applied alongside the Helm release
    ├── storageclass-azurefile.yaml ← custom azurefile-memex SC (uid 1654, nobrl)
    ├── portal-pvcs.yaml       ← RWX drives: data/content/attachments/users + pg PVC
    ├── portal-ha-patch.yaml   ← LEGACY zone spread + probes; the chart now renders the
    │                            RWX volumes and owns the replica count — see Step 4
    ├── postgres-pvc-patch.yaml← LEGACY; the chart's StatefulSet has its own volumeClaimTemplate
    ├── portal-ingress.yaml    ← ingress + cookie session affinity (Blazor)
    ├── observability/
    │   ├── otel-collector-config.yaml ← collector pipeline (filelog+otlp → file+debug)
    │   ├── otel-collector.yaml         ← collector DaemonSet + SA/RBAC + Service
    │   ├── otel-pvc.yaml               ← RWX Azure Files PVC for the log archive
    │   └── log-watcher.yaml            ← mw-log-watcher Deployment + state PVC (optional)
    └── pgbackrest/
        ├── serviceaccount.yaml← Workload-Identity SA for keyless Blob
        ├── configmap.yaml     ← pgbackrest.conf (Azure repo) + WAL archive conf
        ├── sidecar-patch.yaml ← pgBackRest sidecar + WAL archiving wiring
        └── cronjobs.yaml      ← stanza-create Job + full/diff backup CronJobs
```

### Why a Helm overlay **and** extra manifests?

The chart at `../helm` is generated from the Aspire model and is intentionally
**generic** (Azure-free, single replica, `emptyDir` volumes, no ingress). We do
**not** fork or regenerate it. Instead:

- **`values.aks.yaml`** sets the keys the chart already consumes (`config.*`,
  `secrets.*`, `persistence.*`, `keda.*`, `portal.image`) with AKS-correct values
  (e.g. Orleans `AdoNet` clustering for HA).
- **`manifests/`** supplies the things the generic chart does not template —
  the custom StorageClass, the RWX PVCs themselves, ingress with sticky sessions,
  and the pgBackRest sidecar/CronJobs — as `kubectl apply` steps around
  `helm install`.

🚨 **The overlay has been catching up to the chart, and the direction is one-way:
work keeps MOVING from `manifests/` into the chart.** `persistence:` and
`replicas:`/`keda:` used to be bolt-on `kubectl patch` steps and are now rendered
by `../helm` itself, which is why `manifests/portal-ha-patch.yaml` and
`manifests/postgres-pvc-patch.yaml` are marked LEGACY above and are **not** part
of the install (Step 4 says why re-applying them is actively harmful). When you
find a patch file here, check whether the chart already does it before running it.

---

## Prerequisites

- `az` CLI ≥ 2.84 (`az version`), logged in to the target subscription/tenant
- `az bicep` ≥ 0.41 (`az bicep version`) — `az bicep upgrade` if older
- `kubectl` and `helm` ≥ 3.12
- A subscription where you can create resource groups + role assignments
  (Owner or User Access Administrator — the deployment grants AcrPull, DNS, and
  Blob roles)
- `docker` and the **.NET 10 SDK** — only if you build the images yourself
  (Step 3, Option C) rather than pulling published ones
- A **DNS zone** for your domain in Azure DNS (Step 4b creates the A record in it)
- `openssl` — only for the **optional** P2S VPN certificates
  ([Appendix A](#appendix-a--point-to-site-vpn-optional)); Windows
  `New-SelfSignedCertificate` works too

A **globally-unique registry** shared across environments, created once (skip if
you already have one, or if `infra/modules/acr.bicep` provisions a
per-deployment ACR for you):

```bash
az group create -n meshweaver-shared -l swedencentral
az acr create -g meshweaver-shared -n <acrName> --sku Premium
```

Validate the Bicep before deploying:

```bash
az bicep build --file infra/main.bicep --stdout > /dev/null   # compiles clean
```

---

## Step 1 — Provision the infrastructure

Edit `infra/main.parameters.json` (region, `namePrefix`, node SKU/count, CIDRs,
toggles). Then deploy at **subscription** scope (the template creates the
resource group):

```bash
az deployment sub create \
  --name memex-aks-infra \
  --location westeurope \
  --template-file infra/main.bicep \
  --parameters @infra/main.parameters.json \
  --parameters postgresAdminPassword="$PG_ADMIN_PW"   # required: deployPostgresFlexible=true
```

> `postgresAdminPassword` is a `@secure()` parameter — it is NOT in
> `main.parameters.json` (never commit a DB password). Pass it at deploy time.
> If you set `deployPostgresFlexible: false` (use the in-cluster StatefulSet
> instead), you can omit it.

This is **infra only** — it does not install the portal. Capture the outputs you
need for later steps:

```bash
az deployment sub show --name memex-aks-infra \
  --query "properties.outputs.{rg:resourceGroupName.value, cluster:clusterName.value, acr:acrLoginServer.value, vpn:vpnGatewayName.value, pgFqdn:postgresFqdn.value, pgDb:postgresDatabaseName.value, pgUser:postgresAdminLogin.value, filesAccount:contentFilesAccount.value, oidc:oidcIssuerUrl.value}" -o jsonc
```

> The VPN Gateway takes **20–45 minutes** to provision — this dominates the
> deploy time, and **you do not need it to deploy**. Step 2's
> `az aks command invoke` is the primary path and needs no gateway, so set
> `deployVpnGateway: false` unless you specifically want the interactive VNet
> route described in [Appendix A](#appendix-a--point-to-site-vpn-optional)
> (Grafana port-forward, `psql`). It defaults to `true`
> (`infra/main.bicep`, `param deployVpnGateway bool = true`).

### Key parameters

| Parameter | Default | Notes |
|---|---|---|
| `location` | `westeurope` | drives the private DNS zone name |
| `namePrefix` | `<aks>` | ≤ 12 chars, prefixes every resource |
| `systemNodeVmSize` / `systemNodeCount` | `Standard_D8s_v3` / 3 | 8 vCPU / **32 GiB** nodes, autoscales 3→6. (Pick a family with quota in your region — DSv5 was 0 in this subscription's westeurope, DSv3 had 100 vCPU.) |
| `availabilityZones` | `["1","2","3"]` | zonal spread for HA |
| `vnetAddressSpace` | `10.0.0.0/16` | must not collide with peered nets |
| `deployVpnGateway` | `true` | the P2S kubectl path |
| `vpnClientAddressPool` | `172.16.201.0/24` | **must not overlap the VNet** |
| `vpnClientRootCertData` | `""` | base64 root public cert (can add later) |
| `deployBackupStorage` | `false` | self-managed pgBackRest blob; **off** because we use the managed private Flexible Server instead |
| `deployPortalIdentity` | `true` | portal Workload Identity (UAMI + one federated credential per `portalNamespaces` entry) for the in-pod self-updater's **ACR polling**. Output `portalIdentityClientId` → `selfUpdate.azureClientId`. |
| `portalNamespaces` | *(required — no default)* | namespaces that run the portal; one federated credential each (subject `system:serviceaccount:<ns>:memex-portal-sa`). Set it in `main.parameters.json`. Deliberately has no default: a default of `[]` would create zero credentials and still report success, leaving every portal pod unable to reach ACR with nothing to read. |
| `grantSharedAcrPull` | `false` | author the portal UAMI's AcrPull on the **shared** cross-RG ACR in-bicep (needs UAA on `meshweaver-shared`); default = grant out-of-band like the kubelet. A per-deployment ACR is granted in-bicep regardless. |
| `sharedAcrResourceGroup` | `meshweaver-shared` | RG of the shared ACR; used only when `grantSharedAcrPull=true`. |
| `deployContentFileShares` | `true` | Azure Files account + named shares for **static** PV binding (dynamic provisioning needs no shares) |
| `deployPostgresFlexible` | `true` | **PRIVATE (VNet-injected) PostgreSQL Flexible Server** with pgvector + managed PITR |
| `postgresAdminPassword` | *(required, `@secure`)* | pass at deploy time: `--parameters postgresAdminPassword=...` — never commit |
| `postgresSkuName` | `Standard_D2ds_v5` | 2 vCPU / 8 GiB GeneralPurpose; bump for more DB headroom |
| `postgresHighAvailability` | `true` | zone-redundant hot standby in a 2nd AZ |

### Grant AcrPull on a SHARED registry (cross-RG, out-of-band)

A per-deployment ACR (`infra/modules/acr.bicep`) is granted in-bicep and needs
nothing here. A **shared** registry in another resource group cannot be granted
from a deployment scoped to this one, so two role assignments are made
out-of-band — and **both** are needed, for different readers:

```bash
# 1. The cluster KUBELET — this is what pulls the container images.
KUBELET=$(az aks show -g <rg> -n <cluster> \
  --query identityProfile.kubeletidentity.objectId -o tsv)
az role assignment create --assignee-object-id $KUBELET --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -n <acrName> --query id -o tsv)

# 2. The portal WORKLOAD IDENTITY — this is what the in-pod self-updater uses to
#    LIST tags on the registry. Provisioned by infra/modules/portal-identity.bicep and
#    federated to system:serviceaccount:<ns>:memex-portal-sa for every portalNamespaces entry.
PORTAL_MI=$(az identity show -g <rg> -n <portal-identity> --query principalId -o tsv)
az role assignment create --assignee-object-id $PORTAL_MI --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -n <acrName> --query id -o tsv)

# Wire its clientId into selfUpdate.azureClientId for each env (the same value everywhere):
az deployment sub show --name memex-aks-infra \
  --query properties.outputs.portalIdentityClientId.value -o tsv
```

> Pure-IaC alternative to both grants: deploy with `grantSharedAcrPull=true` for
> the portal UAMI — it needs *User Access Administrator* on the shared registry's
> resource group. See
> [DeploymentAKS → Portal self-update](../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md)
> and [SELF-UPDATE.md](SELF-UPDATE.md).

### The Postgres connection string — use the FQDN, keep the password

Take the server's **fully-qualified name** from the `postgresFqdn` output, not a
private IP:

```bash
MEMEX_PG_CONN='Host=<pg-server>.postgres.database.azure.com;Port=5432;Username=memexadmin;Password=<PW>;Database=memex;SslMode=Require;Trust Server Certificate=true'
```

🚨 A private IP used to be recommended here to dodge the portal's Entra-token
branch. That was a workaround for a bug that no longer exists, and it is now
simply wrong — a VNet-injected Flexible Server resolves its FQDN to the private
IP through the linked private DNS zone anyway. The rule lives in code
(`src/MeshWeaver.Hosting.PostgreSql/AzurePostgres.cs`): the token path is taken
only when the **host** ends in `.postgres.database.azure.com` **AND the string
carries no password**. FQDN + password therefore takes the plain-Npgsql path, by
design and pinned by
`test/MeshWeaver.Hosting.PostgreSql.Test/AzurePostgresAuthSelectionTests.cs`
(`AzureHostWithPassword_DoesNotUseManagedIdentity`). Leave the password out and
you get managed-identity auth; that is the switch, not the host spelling.

---

## Step 2 — Reach the private API server (`az aks command invoke`)

The API server has a private IP only, so `kubectl` cannot reach it from outside
the VNet. **`az aks command invoke` is the primary path and the only one any
automation here uses**: it runs a command inside a transient pod on the cluster,
so no tunnel, no jumpbox, and no network line-of-sight are required.

```bash
az aks command invoke -g <rg> -n <cluster> --command "kubectl get pods -A"

# --file ships local files into that pod — this is how every script here runs:
az aks command invoke -g <rg> -n <cluster> --command "bash deploy.sh" --file .
```

It needs the `Azure Kubernetes Service Cluster User` role plus
`Microsoft.ContainerService/managedClusters/runcommand/action`, and nothing on
the network side.

**When to use which**

| You want to… | Use |
|---|---|
| Apply manifests, run helm, read logs, patch a deployment | **`az aks command invoke`** — works from anywhere, and is what CI does |
| Run the scripts in `scripts/` | **`az aks command invoke … --file`** (they are written for it) |
| `kubectl port-forward` to Grafana, or `psql` straight at the private Postgres | a **route into the VNet**: the optional [P2S VPN](#appendix-a--point-to-site-vpn-optional), or a Bastion jumpbox |
| A native local `kubectl` experience with tab-completion and long-running watches | the optional [P2S VPN](#appendix-a--point-to-site-vpn-optional) |

Both are supported; the VPN is for *interactive* tooling that needs to open a
socket into the VNet, and nothing automated depends on it. `check-chart-drift.sh`
makes the split explicit — it takes `--via kubectl|aks-invoke`, and its only CI
caller (`.github/workflows/chart-drift.yml`) passes `aks-invoke`.

> **Jumpbox + Azure Bastion** is the third option: the `AzureBastionSubnet` is
> already carved out by `network.bicep`; deploy Bastion + a small VM in the VNet
> and run kubectl from there. Not implemented in this sample.

---

## Step 3 — Image strategy

The chart references `ghcr.io/systemorph/memex-portal-ai` and
`ghcr.io/systemorph/memex-migration`. Both are **multi-arch** (linux/amd64 +
linux/arm64): the release path builds the base with
`docker buildx --platform linux/amd64,linux/arm64 --push` and the app images with
`-p:RuntimeIdentifiers="linux-x64;linux-arm64" -p:ContainerRuntimeIdentifiers="linux-x64;linux-arm64"`
(no `-r linux-x64`), so one tag is an OCI image index that serves both architectures.

### The keys that actually set the image

🚨 **`values.aks.yaml` carries an `image:` block (`registry` / `portal` /
`migration` / `tag` / `pullPolicy`) that NOTHING READS.** No template under
`../helm/templates/**` references `.Values.image` — `grep -rn '\.Values\.image'
deploy/helm/` returns nothing. Setting it changes exactly zero bytes of rendered
YAML, which is why `scripts/deploy.sh` still repoints the running Deployments
with `kubectl set image` after `helm upgrade`. The keys the chart *does* consume:

| Key | Consumed by | Chart default (`../helm/values.yaml`) |
|---|---|---|
| `portal.image` | `../helm/templates/memex-portal/deployment.yaml` | `ghcr.io/systemorph/memex-portal-ai:latest` |
| `migration.image` | `../helm/templates/memex-migration/job.yaml` | `ghcr.io/systemorph/memex-migration:latest` |
| `portalNext.image` | `../helm/templates/memex-portal/portal-next.yaml` | *(only when `portalNext.enabled`)* |

Each is a **full reference including the tag** — registry, repository and tag in
one string, not a registry prefix that gets composed with something else.

⚠️ Prefer setting these over `kubectl set image`: a later `helm upgrade`
re-renders the Deployment from `portal.image` and silently reverts anything
`kubectl set image` had pointed it at.

Three options:

**Option A — pull from GHCR directly** (simplest; needs node egress to ghcr.io,
which the default `outboundType: loadBalancer` provides). The chart defaults
already point there — nothing to set.

**Option B — import into the private ACR** (recommended for a locked-down
cluster; AcrPull is already granted to the kubelet identity):

```bash
ACR=<acrName>   # from outputs (without .azurecr.io)
az acr import --name $ACR --source ghcr.io/systemorph/memex-portal-ai:latest      --image memex-portal-ai:latest
az acr import --name $ACR --source ghcr.io/systemorph/memex-migration:latest      --image memex-migration:latest
# optional lean / base variants:
az acr import --name $ACR --source ghcr.io/systemorph/memex-portal:latest         --image memex-portal:latest
az acr import --name $ACR --source ghcr.io/systemorph/memex-portal-ai-base:latest --image memex-portal-ai-base:latest
```

> `az acr import` **preserves the manifest list** — it copies the whole multi-arch
> OCI image index (all per-platform child manifests) referenced by the source tag,
> so the ACR copy stays multi-arch (amd64 + arm64); it does not flatten to one
> architecture. (This is the same server-side copy `release-images.yml` uses to
> mirror released images from GHCR into ACR.)

Then point the **real** keys at the ACR — on the `helm` command line, or (better,
so it survives every upgrade) in your values overlay:

```bash
helm upgrade --install memex ../helm -f ../helm/values.yaml -f values.aks.yaml -n $NS \
  --set portal.image=<acrName>.azurecr.io/memex-portal-ai:latest \
  --set migration.image=<acrName>.azurecr.io/memex-migration:latest
```

**Option C — build the images yourself** (what the release path does; needs
`docker` + the .NET 10 SDK). Images are **multi-arch** so one tag serves x86
cloud nodes AND Apple-silicon local k3s, and every install can pull-and-self-update
natively. ⚠️ Unlike the `kubectl`/`helm` commands elsewhere in this file, these
build paths are **repository-root-relative** — run them from the repo root, not
from `deploy/aks`:

```bash
# Base image (node + Claude Code + Copilot CLIs) — MULTI-ARCH manifest list.
# @anthropic-ai/claude-code is pure JS (arch-independent); @github/copilot resolves its
# per-platform binary via npm optional deps, so each arch's build bakes in the matching CLI.
az acr build --registry <acrName> --image memex-portal-ai-base:latest \
  --platform linux/amd64 --platform linux/arm64 deploy/base-images/portal-ai
# (equivalent local build: `docker buildx build --platform linux/amd64,linux/arm64 \
#   -t <acrName>.azurecr.io/memex-portal-ai-base:latest --push deploy/base-images/portal-ai`)

# App images — drop `-r linux-x64`; set RuntimeIdentifiers + ContainerRuntimeIdentifiers to both
# RIDs. With no single RID, the SDK (>= 8.0.405; we're on .NET 10) publishes per-RID and combines
# them into an OCI Image Index (manifest list). ContainerRuntimeIdentifiers MUST be a subset of
# RuntimeIdentifiers (set them equal). The arm64 leg layers on the multi-arch base above.
az acr login --name <acrName>
dotnet publish ../MeshWeaver.Plugins/src/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj \
  -c Release --no-self-contained -t:PublishContainer -p:PublishProfile= \
  -p:RuntimeIdentifiers="linux-x64;linux-arm64" -p:ContainerRuntimeIdentifiers="linux-x64;linux-arm64" \
  -p:ContainerRegistry=<acrName>.azurecr.io -p:ContainerRepository=memex-portal-ai \
  -p:ContainerImageTag=latest -p:ContainerBaseImage=<acrName>.azurecr.io/memex-portal-ai-base:latest
dotnet publish MeshWeaver.Plugins/src/Memex.Database.Migration/Memex.Database.Migration.csproj \
  -c Release --no-self-contained -t:PublishContainer -p:PublishProfile= \
  -p:RuntimeIdentifiers="linux-x64;linux-arm64" -p:ContainerRuntimeIdentifiers="linux-x64;linux-arm64" \
  -p:ContainerRegistry=<acrName>.azurecr.io -p:ContainerRepository=memex-migration -p:ContainerImageTag=latest
```

> **First multi-arch roll — order matters:** the multi-arch base
> (`memex-portal-ai-base:latest`) must exist **before** the first multi-arch app
> build, or the arm64 leg has no base layer. Rebuild the base multi-arch once
> (the `az acr build … --platform …` above), then the app builds — and the
> continuous self-update path — work for both architectures.

---

## Step 4 — Install the portal (Helm + manifests)

Everything below runs **inside the cluster** via `az aks command invoke` (Step 2).
`scripts/deploy.sh` is the packaged form of exactly these steps — see
["Or run `scripts/deploy.sh`"](#or-run-scriptsdeploysh) at the end of this step.

```bash
NS=memex
kubectl create namespace $NS

# 0) Custom StorageClass for the non-root portal (uid 1654) — cluster-scoped,
#    so no namespace. Must exist before the RWX PVCs that reference it.
kubectl apply -f manifests/storageclass-azurefile.yaml

# 1) Real RWX + DB PVCs (must exist before the workloads mount them)
kubectl apply -n $NS -f manifests/portal-pvcs.yaml

# 2) Install the chart with the AKS overlay (set a real PG password!)
helm install memex ../helm \
  -f ../helm/values.yaml \
  -f values.aks.yaml \
  --namespace $NS \
  --set secrets.memex_postgres.memex_postgres_password='<strong-password>' \
  --set secrets.memex_migration.memex_postgres_password='<strong-password>' \
  --set secrets.memex_portal.memex_postgres_password='<strong-password>'

# 3) Ingress with cookie session affinity (enable a controller first)
az aks approuting enable -g <rg> -n <cluster>      # managed nginx
kubectl apply -n $NS -f manifests/portal-ingress.yaml
```

### 🚨 Do NOT apply `portal-ha-patch.yaml` — the chart owns both halves now

Older revisions of this document had a step 3 that ran

```bash
kubectl patch deployment memex-portal-deployment -n $NS \
  --type strategic --patch-file manifests/portal-ha-patch.yaml     # ← DO NOT
kubectl patch statefulset memex-postgres-statefulset -n $NS \
  --type strategic --patch-file manifests/postgres-pvc-patch.yaml  # ← DO NOT
```

Both are now the **chart's** job, and re-applying them is not merely redundant:

- **The RWX volumes are rendered by the chart.** `persistence:` in
  `values.aks.yaml` supplies a `claimName` per drive and
  `../helm/templates/memex-portal/deployment.yaml` mounts each one directly, so a
  plain `helm upgrade` gets `/data`, `/mnt/content`, `/mnt/users` right. That
  replaced the bolt-on patch precisely because **any unpatched apply silently
  reverted `/data` to `emptyDir`**, wiping the assembly / NuGet / DataProtection
  caches on every restart in the gap (`values.aks.yaml`, the `persistence:`
  comment; the same warning heads `scripts/deploy.sh`).
- **`replicas` belongs to the autoscaler.** `portal-ha-patch.yaml` writes
  `spec.replicas: 2`. KEDA is enabled in this overlay (`keda.enabled: true`,
  `minReplicas: 2`), and the chart therefore **omits `spec.replicas` entirely**
  under KEDA so the HPA is the single writer. Patching it back in means helm and
  the HPA fight over one field, and every `helm upgrade` yanks a scaled-out
  deployment back down until the HPA pushes it out again. This is invariant #1 of
  `scripts/check-chart-invariants.sh` — *"spec.replicas is ABSENT when a
  ScaledObject exists"* — a gate that exists because the chart described exactly
  this contradiction in git for a month and produced a **production 503 on
  2026-08-14**. Writing the patch re-creates the bug the gate was built to catch.
- **Postgres has its own `volumeClaimTemplate`.** `postgres-pvc-patch.yaml` was
  written when the chart used `emptyDir` for the data dir; the StatefulSet now
  declares `volumeClaimTemplates: memex-pgdata` (RWO, 10Gi). And on the default
  path here the in-cluster Postgres is not used at all — see the Database section.

What the patch file still holds that the chart does not is the zone-spread
`topologySpreadConstraints`. If you want that, put it in your values overlay or a
chart change — do not hand-patch the Deployment.

> **Secrets**: never commit a real password. The CSI Secrets Store add-on is
> enabled in `aks.bicep` — wire `secrets.memex_*` to Key Vault via a
> `SecretProviderClass` for production rather than `--set`.

> **Blazor sticky sessions**: the ingress affinity cookie is mandatory. Without
> it, SignalR circuit reconnects can land on the wrong replica and users see
> "Attempting to reconnect…" loops. The annotations are in
> `manifests/portal-ingress.yaml` (nginx today, AGIC commented).

### Or run `scripts/deploy.sh`

The same sequence, packaged for the private cluster. Run it from a staging
directory holding the script, `aks-extras.yaml` (StorageClass + PVCs), your
`values.deploy.yaml` secrets (from `values.deploy.example.yaml` — keep it OUT of
git), a copy of the chart as `./helm`, and a copy of `values.aks.yaml`:

```bash
az aks approuting enable -g <rg> -n <cluster>          # managed nginx (public LB)
cd deploy/aks/scripts
export MEMEX_PG_CONN='Host=<pg-server>.postgres.database.azure.com;Port=5432;Username=memexadmin;Password=<PW>;Database=memex;SslMode=Require;Trust Server Certificate=true'
az aks command invoke -g <rg> -n <cluster> \
  --command "MEMEX_PG_CONN='$MEMEX_PG_CONN' bash deploy.sh" --file .
```

It creates the namespace, applies the StorageClass + RWX PVCs, runs
`helm upgrade --install` with the chart + `values.aks.yaml` + `values.deploy.yaml`,
scales the chart's in-cluster Postgres to 0 (this deployment uses the Flexible
Server), repoints both images at the shared ACR, and **patches the
connection-string secret** — a chart-gen gap: the generated secret template
hardcodes the in-cluster Postgres host. It does **not** patch volumes any more,
for the reason above.

**Observability is folded in**: export `GRAFANA_PW=…` alongside `MEMEX_PG_CONN`
and `deploy.sh` also brings up Grafana + Loki + Prometheus (see Observability);
omit it to skip monitoring. At the model level, `AddMemex`'s `OtlpEndpoint`
option wires `OTEL_EXPORTER_OTLP_ENDPOINT` for OTLP traces/metrics — not needed
for log shipping, since Promtail scrapes stdout.

---

## Step 4b — Public ingress + TLS + DNS

Point DNS at the ingress controller's public IP, then let cert-manager issue the
certificate over HTTP-01:

```bash
IP=$(az aks command invoke -g <rg> -n <cluster> \
  --command "kubectl get svc -n app-routing-system nginx -o jsonpath='{.status.loadBalancer.ingress[0].ip}'")
az network dns record-set a add-record -g <dns-rg> -z <your-zone> -n <record> --ipv4-address $IP --ttl 300
cd deploy/aks/scripts
az aks command invoke -g <rg> -n <cluster> --command "bash tls.sh" --file tls.sh   # cert-manager + Let's Encrypt + ingress
```

HTTP→HTTPS redirect is automatic once the ingress has TLS. Verify while bypassing
the DNS cache:

```bash
curl -sS -o /dev/null -w "%{http_code} verify=%{ssl_verify_result}\n" \
  --resolve <host>:443:$IP https://<host>/
```

### Default SSL certificate (cluster-wide, one-time)

Without it, any client that connects **without SNI** gets the self-signed
"Kubernetes Ingress Controller Fake Certificate" — and corporate TLS-inspection /
URL-categorization appliances probe exactly that way, then flag the whole domain
as insecure and block it for their users (seen 2026-08: a client's IT blocked a
portal host in Firefox *and* Edge over this).

Point the app-routing controller's default cert at the flagship host's
cert-manager secret — patch the `NginxIngressController` **CR**, *not* the nginx
deployment (the addon operator reverts direct deployment edits):

```bash
az aks command invoke -g <rg> -n <cluster> --command \
  "kubectl patch nginxingresscontroller default --type merge -p '{\"spec\":{\"defaultSSLCertificate\":{\"secret\":{\"name\":\"<tls-secret>\",\"namespace\":\"<ns>\"}}}}'"
# verify from outside — must show the real cert, not "Acme Co":
echo | openssl s_client -connect <host>:443 -noservername 2>/dev/null | openssl x509 -noout -subject
```

The setting survives addon updates but is **NOT** re-created on a cluster
rebuild — re-apply it whenever the cluster (or the `NginxIngressController` CR)
is recreated.

---

## Storage drives — mountable Azure Files at explicit `/mnt` paths

The portal's persistent data is split across **dedicated Azure Files (RWX)
drives**, one per concern, each mounted at an explicit path. This keeps user
content off the small/churny framework-cache volume and lets you size, expand,
and (optionally) back up each drive independently.

| Drive (PVC) | Mount path | Holds | Repointed by |
|---|---|---|---|
| `memex-data` | `/data` | Framework caches only: DataProtection keys (`/data/dataprotection-keys`), NodeType assembly-cache, NuGet package-cache | `Deployment__DataRoot=/data` |
| `memex-content` | `/mnt/content` | **The content collection** — uploaded files / media / per-node-hub content (`{BasePath}/content/{nodePath}`) | `Storage__BasePath=/mnt/content` |
| `memex-attachments` | `/mnt/attachments` | Attachments drive (see note) | *(forward-looking — no env knob today)* |
| `memex-users` | `/mnt/users` | Co-hosted CLI configs | *(unchanged)* |
| `memex-pgdata` | Postgres data dir | Database files (RWO, managed-csi) | — |

### Why the custom `azurefile-memex` StorageClass (uid 1654)

The portal image runs as the .NET **`app` user — uid 1654 / gid 1654** (the
non-root uid baked into the chiseled `dotnet/aspnet` images). The default
`azurefile-csi` class mounts shares `uid=0,gid=0` (root-owned, mode 0777). That
*usually* works, but a non-root process on a root-owned share is brittle — it's
exactly the failure mode that produced
`UnauthorizedAccessException: Access to the path '/data/dataprotection-keys' is denied`
on the Docker-Compose deploy. `manifests/storageclass-azurefile.yaml` pins
`uid=1654,gid=1654` so **every inode on the share is owned by the portal user**,
plus `mfsymlinks` (DataProtection writes symlinks), `cache=strict`, `actimeo=30`,
and `nobrl` (Azure Files SMB rejects the POSIX byte-range locks that SQLite and
other file-lock-y libraries take; `nobrl` makes them no-ops). `reclaimPolicy:
Retain` keeps the share + its keys/content if a PVC is accidentally deleted.

### How the portal reads these paths (no app code change)

- **Content** — `MemexConfiguration.ConfigureMemexMesh` reads `Storage:BasePath`
  as the FileSystem content-collection root and gives each node hub a
  `content/{nodePath}` subdirectory under it. The overlay sets
  `Storage__BasePath=/mnt/content`. **This is the real functional change.**
- **Attachments** — the portal also maps an `attachments` collection
  (`MapContentCollection("attachments", "storage", "attachments/{nodePath}")`).
  ⚠️ In the **Distributed / Filesystem** backend (the image this sample runs) the
  `storage` *source* collection is **not** separately registered — only the
  Monolith registers it — so **attachments has no independently env-repointable
  base path today** (there is no `Storage__Attachments__BasePath` setting). We
  mount `/mnt/attachments` anyway so the drive exists and is ready: if the app
  later registers a filesystem `storage` source rooted there, no manifest change
  is needed. (If you run the **Monolith** image instead, it *does* register the
  `storage` source from the `Storage` section, so attachments follows
  `Storage__BasePath` — but then content + attachments share one drive.)

### Option A — dynamic provisioning (default, simplest)

`manifests/portal-pvcs.yaml` requests the drives against the `azurefile-memex`
StorageClass; the CSI driver **creates a share per PVC automatically**. Nothing
else to do — this is what Step 4 applies.

### Option B — static PV binding to pre-created named shares

If you'd rather pre-create named shares in one account (to size/quota/firewall/
back them up centrally), set `deployContentFileShares: true` (default) so
`infra/modules/files.bicep` provisions a `StorageV2 / Standard_ZRS /
largeFileSharesState=Enabled` account with shares `content`, `attachments`,
`data`, `users`, `otel-logs`. Then bind a **static PV** per share. Grab the
account name + key:

```bash
SA=$(az deployment sub show --name memex-aks-infra \
  --query "properties.outputs.contentFilesAccount.value" -o tsv)
RG=$(az deployment sub show --name memex-aks-infra \
  --query "properties.outputs.resourceGroupName.value" -o tsv)
KEY=$(az storage account keys list -g $RG -n $SA --query "[0].value" -o tsv)
kubectl create secret generic azure-files-creds -n memex \
  --from-literal=azurestorageaccountname=$SA \
  --from-literal=azurestorageaccountkey=$KEY
```

Then a PV/PVC pair per share (content shown; repeat for attachments/data/users):

```yaml
apiVersion: v1
kind: PersistentVolume
metadata:
  name: memex-content-pv
spec:
  capacity: { storage: 128Gi }
  accessModes: [ReadWriteMany]
  storageClassName: ""            # static — no dynamic provisioner
  persistentVolumeReclaimPolicy: Retain
  mountOptions: [dir_mode=0777, file_mode=0777, uid=1654, gid=1654, mfsymlinks, cache=strict, actimeo=30, nobrl]
  csi:
    driver: file.csi.azure.com
    volumeHandle: memex-content   # any cluster-unique id
    volumeAttributes:
      resourceGroup: <RG>
      storageAccount: <SA>
      shareName: content          # the pre-created share from files.bicep
    nodeStageSecretRef:
      name: azure-files-creds
      namespace: memex
---
apiVersion: v1
kind: PersistentVolumeClaim
metadata: { name: memex-content, namespace: memex }
spec:
  accessModes: [ReadWriteMany]
  storageClassName: ""
  volumeName: memex-content-pv
  resources: { requests: { storage: 128Gi } }
```

> Keyless alternative: instead of the account-key secret, federate a Workload
> Identity with the portal's ServiceAccount and grant it *Storage File Data SMB
> Share Contributor* — same pattern as the pgBackRest identity in `storage.bicep`.

Keep **dynamic (Option A) as the default**; reach for static binding only when
you need named, centrally-managed shares.

---

## Observability — OpenTelemetry across the cluster → Azure Files archive

A single **OpenTelemetry Collector DaemonSet** captures telemetry for the whole
cluster and archives it to a mounted **Azure Files** share — no per-GB
managed-telemetry ingest cost. (A self-hosted in-cluster file archive is the
cost-conscious default for a self-hosted deployment, avoiding the per-GB ingest
charges of a managed PaaS observability service.)

```
  every node:                                            Azure Files (RWX)
  ┌─ pod stdout/stderr ─┐   filelog (hostPath /var/log/pods)   /mnt/otel-logs
  │  (ALL namespaces)   ├────────────────┐                     ├ logs-<node>.json
  └─────────────────────┘                ▼                     ├ traces-<node>.json
                              ┌─ otel-collector (DaemonSet) ─┐  └ metrics-<node>.json
  memex-portal x3 ─ OTLP ────►│ k8sattributes + resourcedetect│──► file exporter (rotated)
   (:4317 grpc via Service)   │ + batch                       │──► debug exporter (kubectl logs)
                              └───────────────────────────────┘
```

- **Sources**: `filelog` tails `/var/log/pods/**/*.log` on every node (so **all**
  pod logs cluster-wide are captured, not just the portal), and `otlp`
  (gRPC :4317 / HTTP :4318) receives the portal's traces/logs/metrics.
- **Enrichment**: `k8sattributes` (pod/namespace/node/deployment) +
  `resourcedetection` + `batch`.
- **Sink**: the `file` exporter writes rotated JSON to `/mnt/otel-logs`
  (`max_megabytes: 100`, `max_backups: 10`). Each DaemonSet pod namespaces its
  output by node name (`logs-<node>.json`) via the downward-API `NODE_NAME` env,
  so replicas don't clobber each other on the shared share. A `debug` exporter
  (verbosity `basic`) mirrors a summary to the collector's own stdout for
  `kubectl logs ds/otel-collector`.

### How the portal emits OTLP (verified wiring — no code change)

`Memex.Portal.ServiceDefaults/ServiceDefaults.cs` →
`AddOpenTelemetryExporters()` does:

```csharp
var useOtlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
if (useOtlp)      builder.Services.AddOpenTelemetry().UseOtlpExporter();
```

So setting **`OTEL_EXPORTER_OTLP_ENDPOINT`** turns on the OTLP exporter — the only
telemetry path (Azure Application Insights has been discontinued; observability is
the Prometheus / Grafana / Loki stack). `values.aks.yaml` sets
`OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317` and
`OTEL_EXPORTER_OTLP_PROTOCOL=grpc`, so the portal exports metrics/traces to the
in-cluster collector. Application logs reach Loki out-of-band via Promtail
scraping pod stdout (no app wiring).

### Red-log ticketing — `mw-log-watcher` (optional, off by default)

Loki makes errors *findable*; this makes them *reported*. `mw-log-watcher` is a small Deployment in
`monitoring` that reads `fail:`/`crit:` lines from Loki, groups them by fault, and POSTs each
distinct fingerprint to the portal — which triages it with an agent and opens **one** GitHub issue in
the repository that owns the code. Ten thousand identical errors are one ticket; recurrences comment
on it and reopen it if it was closed.

```
Loki ──query_range from a persisted cursor──▶ mw-log-watcher ──POST /api/log-incidents──▶ portal
        (ns monitoring, own PVC)                                                            │
                                                          Admin/_LogIncident/{fingerprint} ◀┘
                                                          agent triage ─▶ GitHub App ─▶ issue
```

Three properties worth knowing before you operate it:

- **It is not in the portal's namespace, deliberately.** The component that notices the portal is
  throwing errors must survive the portal being the thing that is broken. When the portal is wedged
  the watcher keeps reading Loki and queues to its PVC; delivery is delayed, nothing is lost.
- **Its state directory must be a persistent volume.** The cursor and the undelivered queue live
  there. On an `emptyDir` a pod restart replays the lookback window and re-reports.
- **It is off until configured, in two independent ways.** No `LogWatch:IngestToken` on the portal ⇒
  the ingest endpoint is not mapped at all (reaching it spends model budget and opens issues, so
  absence means off, never open). No repository routed ⇒ the control plane idles rather than running
  agent rounds that could never end in a ticket.

Design and configuration reference:
**[LogWatchTriage.md](../../src/MeshWeaver.Documentation/Data/Architecture/LogWatchTriage.md)**.

#### Deploy it — four steps, and it stays off until all four are done

The ingest endpoint is not even mapped without a token, so a half-configured
watcher is inert rather than half-working.

```bash
# 1. ONE shared secret, both sides. The watcher presents it; the portal requires it.
TOKEN=$(openssl rand -hex 32)
az aks command invoke -g <rg> -n <cluster> --command \
  "kubectl -n monitoring create secret generic mw-log-watcher --from-literal=ingest-token=$TOKEN"
#    …then set LogWatch__IngestToken to the SAME value on the portal (KeyVault → its secret store).

# 2. Where tickets go. Without at least one route the control plane idles by design —
#    it will not spend agent rounds on incidents it could never file.
#      LogWatch__DefaultRepository     = <owner>/<repo>
#      LogWatch__Routes__0__Prefix     = MeshWeaver.
#      LogWatch__Routes__0__Repository = <owner>/<repo>
#      LogWatch__Routes__1__Prefix     = Memex.
#      LogWatch__Routes__1__Repository = <owner>/<other-repo>
#    Every key carries the LogWatch__ prefix — a bare Routes__0__Repository does not bind, and an
#    unbound route reads exactly like "no route configured": incidents pile up at New, silently.
#    Issues are opened as the GitHub App (GitHub:App), never a user's OAuth token.

# 3. Build + push the watcher image.
dotnet publish tools/MeshWeaver.LogWatcher/MeshWeaver.LogWatcher.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=<acrName>.azurecr.io \
  -p:ContainerRepository=memex-log-watcher -p:ContainerImageTag=<tag>

# 4. Apply the Deployment + PVC (edit the image tag + watched namespaces in the manifest first).
az aks command invoke -g <rg> -n <cluster> \
  --command "kubectl apply -f log-watcher.yaml" \
  --file manifests/observability/log-watcher.yaml
```

**Verify — and know what each failure looks like:**

```bash
az aks command invoke -g <rg> -n <cluster> --command \
  "kubectl -n monitoring logs deploy/mw-log-watcher --tail=50"
```

| What the log says | Meaning |
|---|---|
| `Loki: N red line(s) …` then `Reported <fp> … — 200` | Working end to end. |
| `Log watcher is not configured` | Deploy step 1 missed — `PortalUrl`/`IngestToken` unset. |
| `Portal REJECTED report … with 401` | The two tokens differ — the watcher's secret and the portal's `LogWatch__IngestToken` must be the same string. |
| `Portal REJECTED report … with 404` | The portal's `LogWatch__IngestToken` is unset, so `/api/log-incidents` is not mapped at all — deploy step 1 was done on the watcher side only. |
| `Loki: 0 line(s)` forever | The namespace label is wrong, or Promtail is not shipping that namespace. |
| `Could not persist watcher state … Access to the path … is denied` | The state PVC mounted root-owned. The pod needs `securityContext.fsGroup: 1654` (the shipped manifest sets it). The watcher still reports, but the cursor stops persisting, so a restart replays the lookback window. |
| Incidents appear but stay `New` | Deploy step 2 missed — no repository routed, so the control plane idles. |

Then browse `Admin/_LogIncident` in the portal: every incident links to the ticket
it opened and to the triage thread that wrote it.

**🚨 The state directory must be a real volume.** On an `emptyDir` a pod restart
replays the lookback window and re-reports. The manifest ships a PVC for this reason.

**To turn it off**: `kubectl -n monitoring scale deploy/mw-log-watcher --replicas=0`.
Clearing the portal's `LogWatch__IngestToken` also closes the ingest endpoint.
Neither deletes existing incidents.

A provisioned Grafana alert rule covers the human-facing half — it shows red-log volume in Grafana
but deliberately does **not** open tickets. Ticketing hangs off the watcher's cursor instead, because
an alert notification that fires while its receiver is down is simply lost: acceptable for a nudge,
not for "every distinct error gets a ticket".

> The rule and the Grafana dashboards used to live here as `deploy/aks/dashboards/`. They are bound
> to one Grafana's datasource UIDs and to specific namespaces — elsewhere they render empty panels
> rather than failing — so they moved to the deployment repo alongside the environments they
> describe. `scripts/import-dashboards.sh` is the generic importer and stays here; point it at
> whatever dashboard JSON you have.

### Apply it

```bash
NS=memex
kubectl apply -f manifests/storageclass-azurefile.yaml         # if not already applied
kubectl apply -n $NS -f manifests/observability/otel-pvc.yaml
kubectl apply -n $NS -f manifests/observability/otel-collector-config.yaml
kubectl apply -n $NS -f manifests/observability/otel-collector.yaml
# portal already points at the collector via values.aks.yaml — restart to pick up env if needed
kubectl rollout restart deployment memex-portal-deployment -n $NS
```

### Grafana + Loki + Prometheus (log search and dashboards)

The file archive above is the durable, cheap sink. For *searching* logs there is a
`grafana/loki-stack` install (Loki + Promtail + Grafana + Prometheus, datasources
auto-wired, Promtail shipping every pod's stdout into Loki) in the `monitoring`
namespace:

```bash
export GRAFANA_PW='<strong-password>'
cd deploy/aks/scripts
az aks command invoke -g <rg> -n <cluster> \
  --command "GRAFANA_PW=$GRAFANA_PW bash install-observability.sh" --file install-observability.sh
```

Everything except the portal stays private, so Grafana has **no public endpoint**.
Reach it over a route into the VNet — the optional
[P2S VPN](#appendix-a--point-to-site-vpn-optional) or a Bastion jumpbox — and
port-forward:

```bash
az aks get-credentials -g <rg> -n <cluster>                      # VPN connected
kubectl -n monitoring port-forward svc/loki-grafana 3000:80      # http://localhost:3000 (admin / $GRAFANA_PW)
```

In Grafana → Explore → Loki, the portal logs are `{namespace="memex"}` (add
`|= "error"` or `|~ "signin-microsoft"` to narrow).

### Read / download the archived logs

The archive lives on the `otel-logs` Azure Files share. Inspect from a pod:

```bash
kubectl exec -n memex ds/otel-collector -- ls -lh /mnt/otel-logs
kubectl exec -n memex ds/otel-collector -- tail -n 50 /mnt/otel-logs/logs-<node>.json
```

…or download straight from the Files share with the account key (Option B account,
or the dynamically-created share — find it under the cluster's node resource group):

```bash
az storage file download-batch \
  --account-name <filesAccount> --account-key <key> \
  --source otel-logs --destination ./otel-archive
```

### Retention / rotation, cost, and scale-up

- **Rotation** is per-node-file: 100 MB × 10 backups ⇒ ~1 GB/node retained, then
  oldest rolls off. Bump `max_backups` / the `otel-logs` PVC size for longer
  retention; add an Azure Files lifecycle/cleanup CronJob for time-based pruning.
- **Cost**: a flat Azure Files share (≈€0.06/GB-month Standard) vs. per-GB
  managed-telemetry ingest — the archive is the cheap default for self-hosting.
- **Azure Table storage has NO native OTel Collector exporter** — Azure **Files**
  (the `file` exporter over a mounted SMB share) is the chosen sink. For richer
  query/alerting at scale, swap the `file` exporter for either:
  - **Grafana Loki backed by Azure Blob** (the `loki` exporter → Loki → Blob
    object store) for label-indexed log search, or
  - the **`azuremonitor` exporter** to ship into Azure Monitor / Log Analytics
    (KQL, alerts) — accepting the per-GB ingest cost.

  Both are drop-in exporter swaps in `otel-collector-config.yaml`; neither is
  implemented here to keep the default zero-PaaS and cheap.

---

## Database — PRIVATE PostgreSQL Flexible Server (default)

This sample defaults to a **managed, private** database:
`infra/modules/postgres.bicep` provisions an **Azure Database for PostgreSQL
Flexible Server** injected into the delegated `postgres` subnet — no public
endpoint. It resolves only inside the VNet (and over the P2S VPN) via the
`*.private.postgres.database.azure.com` private DNS zone that `network.bicep`
links. This matches the private-everything posture: private API server, private
drives (Azure Files), private DB.

- **pgvector** is allowlisted (`azure.extensions = VECTOR,UUID-OSSP`) for the
  portal's embeddings + HNSW vector search; the `memex` database is created.
- **Managed PITR** — automatic backups + WAL; restore to any second in the
  retention window with `az postgres flexible-server restore`. No in-cluster
  backup machinery, so `deployBackupStorage: false` and the
  `postgres-pvc-patch` / `pgbackrest` manifests are **not** applied.
- **HA** — `postgresHighAvailability: true` runs a zone-redundant hot standby.

Point the portal at the server's private FQDN (from the `postgresFqdn` output)
in `values.aks.yaml` — set `MEMEX_HOST` and the connection-string secret to the
Flexible Server endpoint, user `memexadmin`, db `memex`. The portal's
Postgres path auto-detects Azure-managed-identity vs basic auth from the
connection string (see `Memex.Portal.Distributed/Program.cs`); basic auth with
the admin password works out of the box.

> **In-cluster alternative**: set `deployPostgresFlexible: false` (+ revert
> `deployBackupStorage: true`) to use the self-managed Postgres StatefulSet +
> pgBackRest PITR instead — see Step 5 below. The two are mutually exclusive;
> pick one.

---

## Documentation search — full-text + optional vector

The built-in MeshWeaver platform documentation ships **inside the images** (embedded
resources) and is served from memory at runtime. So that it also shows up in the portal's
**main search bar**, the one-shot **migration mirrors every doc page into a Postgres `doc`
schema** on each deploy:

- **Full-text search** (always on, no external dependency). Each doc's title + one-line
  description is indexed; the search bar finds docs by keyword out of the box.
- **Semantic / vector search** (opt-in). When an embeddings endpoint is configured, the
  migration also computes an embedding per doc (title + description + body) and stores it in
  the pgvector **HNSW** index, so natural-language queries (“how do I cancel a running job”)
  rank the right page. The portal embeds the search query the same way, so both sides must use
  the same model. (`pgvector` is already allowlisted on the Flexible Server — see the Database
  section.)

The mirror is a **full replace + incremental embed**: every deploy upserts the current doc set
and prunes rows whose source page no longer ships, and the (paid) embedding call only fires for
pages whose content actually changed since the last run. Reads/navigation still come from the
in-memory copy — the `doc` schema is purely a search index.

### Configure it

The embeddings provider is **optional**. Leave it unset and docs are full-text-searchable with
no external AI dependency; set it to enable vector ranking. The deploy AppHost
(`deploy/aspire/Memex.Deploy.AppHost`) reads three parameters and flows them to **both** the
migration and the portal:

| Deploy parameter | Container env (migration **and** portal) | Notes |
|---|---|---|
| `Parameters:embedding-endpoint` | `Embedding__Endpoint` | Azure AI Foundry embeddings endpoint (Cohere embed-v4). Empty ⇒ full-text only. |
| `Parameters:embedding-key` | `Embedding__ApiKey` | Secret — only emitted when set (ACA/compose reject empty secrets). |
| `Parameters:embedding-model` | `Embedding__Model` | Defaults to `embed-v-4-0` (the Cohere embed-v4 Azure AI Foundry deployment name). Migration + portal must agree (sizes the vector column). |

Set them via `dotnet user-secrets` / env / GitHub secrets at publish time, e.g.:

```bash
aspire publish --apphost deploy/aspire/Memex.Deploy.AppHost/Memex.Deploy.AppHost.csproj \
  -o deploy/helm -- --mode kubernetes \
  -- --Parameters:embedding-endpoint=https://<foundry>.services.ai.azure.com/... \
     --Parameters:embedding-key=<key>
```

For the **AKS / Helm** path these surface as `config.Embedding__*` (and the key as a
`secrets.*` entry) on the regenerated chart's migration Job and portal Deployment — set them in
`values.aks.yaml` (or `--set`) alongside the other secrets, and wire the key through the CSI
Secrets Store add-on for production rather than committing it.

---

## Orleans clustering — Postgres-backed (never Localhost in prod)

HA runs the portal as **multiple silos**, which must form one cluster via a shared membership
store. This deployment uses **Postgres-backed ADO.NET clustering** on the **same Postgres server
in a separate `orleans` database** (so silo membership never shares tables or locks with mesh
data). It works for a single silo too, so the self-host AppHosts use it in every mode — Localhost
clustering is never used in a deployment.

How it's wired (all DB config flows through Aspire):

- The `AddMemex` integration declares the `orleans` database on the same Postgres server and
  references it on both the portal and the migration, so Aspire injects `ConnectionStrings:orleans`.
- The portal silo selects the provider from the **feature flag `Features:Orleans:Clustering`**
  (set to `AdoNet` by the self-host AppHosts; legacy `Deployment:Orleans:Clustering` still works)
  and calls `UseAdoNetClustering(Invariant=Npgsql)` against that injected connection string.
- The **db-migration creates the Orleans membership tables** (`OrleansQuery`,
  `OrleansMembershipTable`, …) in the `orleans` database from the verbatim Orleans 10 PostgreSQL
  scripts — idempotent, and it auto-creates the database on self-managed Postgres. The Orleans
  provider does *not* self-create these tables, so the migration must run before the silos start
  (the portal already `WaitForCompletion(migration)`).

> Aspire's Orleans integration only wires Redis / Azure-Table clustering — not ADO.NET — so the
> `orleans` database lives in Aspire while the silo wiring and the membership DDL live in the
> portal and the migration. (The Azure/ACA path instead uses Azure Table Storage clustering via
> the Aspire Orleans integration and doesn't need any of this.)

**AKS / Flexible Server note:** on the managed-Postgres path, ensure the `orleans` database exists
on the server (the chart's migration Job creates the tables but the managed server must allow the
DB; `azure.extensions` already includes pgvector for the mesh DB). The regenerated chart carries
`Features__Orleans__Clustering=AdoNet` and the `orleans` connection string from the Aspire model;
set the connection-string secret in `values.aks.yaml` alongside the `memex` one. HA needs **≥2
replicas** (the `portal-ha-patch.yaml` already sets 3).

---

## Authentication — Systemorph AAD (home) + Google + LinkedIn

`values.aks.yaml` wires the login providers the portal's auth pipeline
(`AuthenticationBuilderExtensions`) reads from `Authentication:*`:

| Provider | Config keys (env: `Authentication__<P>__*`) | Redirect URI to register |
|---|---|---|
| **Microsoft / Entra (HOME)** | `TenantId` (Systemorph tenant GUID), `ClientId`, `ClientSecret` | `https://portal.example.com/signin-microsoft` |
| **Google** | `ClientId`, `ClientSecret` | `https://portal.example.com/signin-google` |
| **LinkedIn** | `ClientId`, `ClientSecret` | `https://portal.example.com/signin-linkedin` |

- Setting `Authentication__Microsoft__TenantId` to a **real tenant GUID** (not
  `common`) makes that AAD the **home** directory. `values.aks.yaml` ships the
  `CHANGE_ME_aad_tenant_id` placeholder — fill it from
  `az account show --query tenantId -o tsv`.
- Any provider with a `ClientId` set is offered on the login page; the presence
  of external providers flips the portal into multi-provider mode and dev login
  is off in the Distributed image.
- **You still must create the app registrations / OAuth clients** and fill the
  `CHANGE_ME_*` `ClientId`s (config) + `ClientSecret`s (secrets) — those are real
  credentials this repo does not contain. Register each redirect URI above.
- Host is `portal.example.com` (ingress + TLS) — Step 4b points DNS at the
  ingress controller's IP and issues the certificate.

### Create the app registrations / OAuth clients

**Microsoft / Entra** (single-tenant home directory):

```bash
az ad app create --display-name "Memex Portal (portal.example.com)" \
  --sign-in-audience AzureADMyOrg \
  --web-redirect-uris "https://portal.example.com/signin-microsoft"
az ad app credential reset --id <appId> --display-name aks --years 1   # => client secret
```

**Google** (Cloud Console) and **LinkedIn** (Developer portal): create web OAuth
clients with redirect URIs `https://portal.example.com/signin-google` and
`https://portal.example.com/signin-linkedin`.

---

## Step 5 — PITR backups with pgBackRest → Azure Blob (in-cluster alternative)

> **Recommended for turnkey prod: use Azure Database for PostgreSQL Flexible
> Server instead** (managed PITR, automatic WAL, restore to any point in the
> retention window with one CLI call, no in-cluster moving parts). See the
> [Flexible Server](#alternative-azure-database-for-postgresql-flexible-server)
> section. pgBackRest is the **all-in-cluster, self-managed** option for when you
> want the database to live next to the workload.

### Wire it up

```bash
NS=memex
# Workload-Identity SA — put the pgBackRestIdentityClientId output in the SA
kubectl apply -n $NS -f manifests/pgbackrest/serviceaccount.yaml   # edit the client-id first
kubectl apply -n $NS -f manifests/pgbackrest/configmap.yaml

# Add the WAL-archive wiring + sidecar to the Postgres StatefulSet
kubectl patch statefulset memex-postgres-statefulset -n $NS \
  --type strategic --patch-file manifests/pgbackrest/sidecar-patch.yaml

# Wait for the DB pod to roll, then create the stanza + scheduled backups
kubectl apply -n $NS -f manifests/pgbackrest/cronjobs.yaml
```

Before applying, substitute your storage account + container into the manifests
(they carry `__AZURE_ACCOUNT__` / `pgbackrest` placeholders): the
`backupStorageAccount` and `backupContainerName` infra outputs, and the
`pgBackRestIdentityClientId` into the service account annotation.

How it works:

- **WAL archiving**: the init container appends `archive_command = pgbackrest …
  archive-push` to `postgresql.conf`, so every completed WAL segment is pushed to
  Blob continuously. This is what makes PITR (replay to an arbitrary timestamp)
  possible.
- **Scheduled backups**: `pgbackrest-full` (weekly) + `pgbackrest-diff` (daily)
  CronJobs write full/differential base backups to the same Blob repo.
- **Keyless auth**: the SA is federated (Workload Identity) with the managed
  identity that holds *Storage Blob Data Contributor* — no account key on disk.
  (To use a key instead, set `repo1-azure-key` in the ConfigMap and drop the
  workload-identity annotation.)

### Backup runbook

```bash
NS=memex; POD=memex-postgres-statefulset-0
# Ad-hoc full backup (zero contention — runs in the sidecar):
kubectl exec -n $NS $POD -c pgbackrest -- \
  pgbackrest --config=/etc/pgbackrest/pgbackrest.conf --stanza=memex --type=full backup

# Verify repo health + list backups:
kubectl exec -n $NS $POD -c pgbackrest -- \
  pgbackrest --config=/etc/pgbackrest/pgbackrest.conf --stanza=memex check
kubectl exec -n $NS $POD -c pgbackrest -- \
  pgbackrest --config=/etc/pgbackrest/pgbackrest.conf --stanza=memex info
```

### Restore runbook (Point-In-Time)

Restore is **destructive** to the live data dir — it replaces cluster files,
so the database must be stopped during the restore.

```bash
NS=memex
# 1) Scale the portal down (no writers) and stop Postgres.
kubectl scale deployment memex-portal-deployment -n $NS --replicas=0
kubectl scale statefulset memex-postgres-statefulset -n $NS --replicas=0

# 2) Run pgBackRest restore against the PVC from a one-off pod that mounts
#    memex-pgdata + the pgbackrest config. PITR to a timestamp:
kubectl run pgbackrest-restore -n $NS --rm -i --restart=Never \
  --image=docker.io/woblerr/pgbackrest:2.54.2 \
  --overrides='{
    "spec": {
      "serviceAccountName": "pgbackrest-sa",
      "containers": [{
        "name": "restore",
        "image": "docker.io/woblerr/pgbackrest:2.54.2",
        "command": ["pgbackrest","--config=/etc/pgbackrest/pgbackrest.conf","--stanza=memex",
                    "--type=time","--target=2026-05-30 14:30:00+00","--delta","restore"],
        "volumeMounts": [
          {"name":"memex-pgdata","mountPath":"/var/lib/postgresql/data"},
          {"name":"pgbackrest-conf","mountPath":"/etc/pgbackrest"}
        ]
      }],
      "volumes": [
        {"name":"memex-pgdata","persistentVolumeClaim":{"claimName":"memex-pgdata"}},
        {"name":"pgbackrest-conf","configMap":{"name":"pgbackrest-config","items":[{"key":"pgbackrest.conf","path":"pgbackrest.conf"}]}}
      ],
      "metadata": {"labels": {"azure.workload.identity/use": "true"}}
    }
  }'

# 3) Bring Postgres back; it replays WAL up to the target time, then promotes.
kubectl scale statefulset memex-postgres-statefulset -n $NS --replicas=1
kubectl logs -n $NS memex-postgres-statefulset-0 -f   # watch recovery complete
kubectl scale deployment memex-portal-deployment -n $NS --replicas=3
```

> Replace the `--target` timestamp (and remember the ConfigMap's account/container
> placeholders must be rendered). For "latest possible" recovery drop
> `--type=time --target=…` and pgBackRest replays all archived WAL.

### Alternative: Azure Database for PostgreSQL Flexible Server

For most production deployments, prefer the managed database:

- **Built-in PITR** — automatic backups + WAL; restore to any second in the
  retention window (7–35 days) via `az postgres flexible-server restore`.
- No StatefulSet, no PVC, no pgBackRest sidecar/CronJobs to operate.
- pgvector is supported (`azure.extensions`).

To switch: set `deployBackupStorage: false`, do **not** apply the
`postgres-pvc-patch` / `pgbackrest` manifests, scale the chart's Postgres
StatefulSet to 0, provision a Flexible Server (private-access / VNet-injected
into a delegated subnet), and point the portal's `MEMEX_HOST` /
`MEMEX_JDBCCONNECTIONSTRING` / connection-string secret at it in
`values.aks.yaml`. The portal is unchanged — it just talks to a different
Postgres endpoint.

---

## Operating it — rolling a new image, and the traps

```bash
# a) The manifest MUST carry every arch leg. A partial manifest list = ImagePullBackOff on the
#    missing arch, which presents as "the deploy hung" (memex-cloud V46 outage, 2026-07-19).
az acr manifest show -r <acrName> -n memex-portal-ai:<tag> \
  | jq -r '.manifests[]?.platform | "\(.os)/\(.architecture)"'   # expect linux/amd64 AND linux/arm64

# b) Roll.
az aks command invoke -g <rg> -n <cluster> --command \
  "kubectl set image deploy/memex-portal-deployment memex-portal=<acrName>.azurecr.io/memex-portal-ai:<tag> -n <env> \
   && kubectl rollout status deploy/memex-portal-deployment -n <env> --timeout=600s"

# c) Verify with REAL signals, looped. HTTP 200 is the Blazor shell and proves nothing.
for i in $(seq 1 10); do curl -s -o /dev/null -w "%{http_code} " https://<host>/api/content/<space>/content/<file>; done
```

### 🧱 NodeType bake — the deploy MUST compile every dynamic NodeType

Fresh pods invalidate every dynamic NodeType's cached assembly (the framework MVID
changed), so they all need a recompile. Left lazy, that compile happens on user
requests after the pod is already serving — and a type nothing happens to touch
stays **"no definition"** until its pages hang with *"No response received …
`SubscribeRequest` → target X"* (SocialMedia/Post on memex-cloud, 2026-07-30:
every post page burned the 60 s timeout all morning while the portal looked
healthy). **Managed envs therefore run the bake ON — these three knobs travel
together** (`values.aks.yaml` carries them; keep them in every env overlay so a
`helm upgrade` never reverts them):

| Knob | What it does |
|---|---|
| `config.memex_portal.PreWarm__DynamicTypes: "true"` | every new pod sweeps + compiles ALL dynamic NodeTypes at start (resumes from the shared `/data` cache — warm restarts are cheap) |
| `config.memex_portal.PreWarm__GateReadiness: "true"` | `/health` stays red until the sweep is green; with `maxSurge 1 / maxUnavailable 0` a regressed type STALLS the rollout with the old image serving. **⚠️ Depends on the sweep, and `values.aks.yaml` currently sets BOTH to `"false"` — read the namespace, not this table (#1981).** The two keys are ONE setting: the gate reads state only the sweep writes, so gate-without-sweep is disarmed at startup (it used to register and stay permanently green) and logged at Critical. Turning the gate on means turning `PreWarm__DynamicTypes` on in the same change. It was tried 2026-08-02 and reverted the same day on 7 FALSE regressions — all cross-silo `SubscribeRequest` timeouts, not compile errors (#694 residue). The gate no longer reads "no answer" as "it broke": a `TimedOut` outcome is filed as *unevaluated* and can never gate, and that leniency now survives the cascade (a dependent of an unevaluated upstream is `UpstreamUnevaluated`, also non-gating). Only a `CompileError` — or an `UpstreamFailed` cascading from one — on a **previously-healthy** type stalls a roll |
| `config.memex_portal.PreWarm__AllowUnprovenBake: "false"` | **✅ OFF (strict).** The gate also refuses readiness when the sweep *errored* — enumeration threw or timed out — because such a pod verified **nothing** and a gate that certifies "I verified nothing" is worse than no gate. That guard used to live in the pre-run bake Job (*"FINDING NOTHING IS NOT PASSING"*, exit 3, `Bake__AllowEmpty`) and was lost when #1357 retired the Job; it is now enforced on the surviving path as `BakePhase.Faulted`. ⚠️ **"Empty" is not "unproven"** — a mesh that genuinely has no dynamic NodeTypes enumerates fine, completes and serves; only the *inability to get an answer* gates. Set `"true"` only to roll forward past an environment that cannot answer the enumeration, accepting lazy compilation. It can never waive a real regression, and `/health` keeps reporting the bake as unproven |
| `probes.startup: {periodSeconds: 10, failureThreshold: 180}` (= 30 min) | ⚠️ REQUIRED with the gate: a cold bake is **~2.4 s/type**, sequential *(measured 2026-08-10, prod Loki, three portals)* — ~10 min on memex-cloud, the largest mesh — and the default 5 min budget kills the pod mid-bake forever. 30 min is that worst case plus a plain cold boot, x2. `progressDeadlineSeconds` is DERIVED from these two in the chart, so raising them can't leave it behind. **Was `1080` (3 h)** until 2026-08-10, sized from a "~90 s/type" estimate that was 37x too high — a window that long detected nothing |

**🚨 Before you trust the gate, verify the namespace actually reads it.** The gate
protects a portal through exactly two deployment facts, and on 2026-08-10 two of
the three portals had drifted away from both. A gated config in a namespace
missing either is *false confidence* — it reports and the outage happens anyway.

| Must be true | Why | Check |
|---|---|---|
| a `startupProbe` exists **on `/health`** | that probe is the ONLY reader of the gate; no startupProbe (or one on `/alive` / `/ready` / `/healthz`) ignores it entirely | `kubectl -n <ns> get deploy memex-portal-deployment -o jsonpath='{.spec.template.spec.containers[0].startupProbe}'` |
| `strategy.maxUnavailable: 0` | surge-first keeps the old pod serving until the new one passes; at `maxUnavailable:1` with `replicas:1` the only serving pod can be deleted BEFORE the replacement is ready | `kubectl -n <ns> get deploy memex-portal-deployment -o jsonpath='{.spec.strategy.rollingUpdate}'` |

Both are rendered correctly by the chart — a `helm upgrade` restores them. A
per-env `portal-patch.json` or a hand `kubectl patch` can still override them,
which is how the drift happened.

🪦 **There is no pre-run bake Job any more (#1347) — the CI bake replaced it
(#1660 WS3).** The separate `memex-bake` image was removed after two weeks of
running in zero namespaces. On its only AKS run (memex-cloud, 2026-07-30)
`memex-bake:3.0.0-ci.1565` computed a **different framework fingerprint** than the
running `portal-ai:3.0.0-ci.1565` — same version, same commit, separately
published — so its framework-stale kickoff started flipping CURRENT NodeType
records to `Pending` and rebuilding them for a framework nothing serves. The cause
was the per-build `+build.<UtcNow.Ticks>` stamp `InformationalVersion` then carried
under `CIRun`: every `dotnet publish` minted a fresh `MeshWeaver.Graph` MVID.
#1660 WS3 removed that stamp and made CI builds deterministic — the framework
identity is now the **API-surface hash** (`s<hash>`, `FrameworkBuildIdentity`;
identical across CI builds AND across internal-only merges, moved only by breaking
surface changes or Graph changes) — so the Build-and-Test run's bake artifact IS
adoptable, and usually already published: `main-cd`'s `publish-bake` job copies it
onto the portals' `/data` share (`prebuilt-bundles/<identity>/<source>/`), and each
booting pod seeds its own identity's bundles (`PreWarm__PrebuiltBundleRoot`) before
its sweep.

**The pod-side sweep remains the enforcement.** It runs in the serving process,
adopts whatever CI published, and compiles the remainder — 76 s for 280 types cold
(memex-cloud, batch direct-compile), seconds when the CI bake covered the shipped
content. The "fail before prod" contract is given by `PreWarm__GateReadiness` +
`maxSurge:1` / `maxUnavailable:0`: the new pod refuses readiness until its OWN bake
is green while the old image keeps serving.

**Verify after every roll** — the bake completing is the deploy signal, not HTTP 200:

```bash
# The sweep's verdict (compiled=N alreadyBaked=M compileErrors=0 …) — read the NEWEST pod
# explicitly: `kubectl logs deploy/…` picks an arbitrary pod and mid-rollout that is often the OLD one.
NEW=$(kubectl -n <env> get pods -l app.kubernetes.io/component=memex-portal \
  --sort-by=.metadata.creationTimestamp -o jsonpath='{.items[-1:].metadata.name}')
kubectl -n <env> logs "$NEW" | grep "DynamicTypePreWarmer: warm-up complete"
# The gate's verdict (Healthy "baked in …" / Unhealthy "regressed" — rollout stalled on purpose):
kubectl -n <env> get pods   # new pod 0/1 while baking is CORRECT; investigate only a Regressed log line
```

Live-env equivalent without a helm apply (what enabled memex-cloud on 2026-07-30):

```bash
kubectl -n <env> patch configmap memex-portal-config --type merge \
  -p '{"data":{"PreWarm__DynamicTypes":"true","PreWarm__GateReadiness":"false"}}'
kubectl -n <env> patch deployment memex-portal-deployment --type json -p \
  '[{"op":"replace","path":"/spec/template/spec/containers/0/startupProbe/periodSeconds","value":10},
    {"op":"replace","path":"/spec/template/spec/containers/0/startupProbe/failureThreshold","value":180}]'
# the probe patch rolls the deployment; the new pod bakes behind the gate while the old serves
```

- **A pod sitting 0/1 for a long time is the gate doing its job** while a cold bake
  runs; only a `REFUSING READINESS … Regressions:` log line means a type broke on
  this image — fix the type, never widen the timeout.
- **Retries are cheap — the sweep resumes from the shared cache.** Every type a
  sweep DID build is on `/data` for good; deleting a gate-red pod re-runs only what
  is missing. Progress across attempts is monotonic.
- **⚠️ Known flake while core #694 is open (cross-silo reply routing):** during a
  roll the mesh briefly runs TWO silos (old serving + new baking), and in that
  window the pod-side sweep's shared-source resolution can fail
  nondeterministically — the symptom is a `CompileError` full of unresolved names
  that live in a SIBLING type's `Source/` (`shared=@…`), on a type that compiles
  clean when triggered individually. First verify with a targeted compile (MCP
  `compile @<type>` — recycle the type's hub first if the subscribe times out; the
  targeted subscribe rides the SAME cross-silo routing, so it completes reliably
  only in a single-silo window — delete the baking pod and use the ~2 minutes before
  its replacement joins), then let the replacement's re-sweep find the fixed types
  `alreadyBaked`. (Observed live on the first gated roll, memex-cloud 2026-07-30:
  the gate caught 2 types with stale-green records whose newest assemblies were 6
  days old — real, invisible breakage — plus sweep failures on shared-source types
  that compiled clean when triggered individually in a single-silo window.)

Mechanism:
[NodeTypeCompilation.md](../../src/MeshWeaver.Documentation/Data/Architecture/NodeTypeCompilation.md).

### Scaling: KEDA wins

`kubectl scale --replicas=0` (the documented heal for a wedged mesh) is **silently
reverted** by a `ScaledObject` with `minReplicaCount: 2` — you conclude the heal
failed when it never ran. `kubectl get scaledobject -n <env>` first, and check
`PAUSED`.

#### 🚧 A PAUSED scaler is a FENCE — find out why before you remove it

`autoscaling.keda.sh/paused-replicas: "<n>"` pins the deployment at `n`, **deletes
the HPA**, and makes `minReplicaCount` inert. Nothing in any chart sets it,
`kubectl get deploy` does not show it, and it survives every roll — so it reads as
"this deployment is just single-replica" long after whoever applied it has moved on.

**Nobody applies it for fun.** It is the most direct way to say "stop making new
silos", so assume it is suppressing a multi-silo defect until you have evidence
otherwise. `memex-cloud` carried one for **16 days** (2026-07-29 → 2026-08-14): it
was applied 87 minutes after the second fix for **#694 — *cross-silo posts lose
AccessContext: static content 500s on ~50% of requests with 2 replicas*** — and 29
seconds after KEDA scaled the namespace back up. Removing it without reading that
history is re-running the incident.

Establish provenance before unpausing — the same way you would for any hand-applied
field:

```bash
kubectl -n <env> get scaledobject <name> -o jsonpath='{range .metadata.managedFields[*]}{.manager} {.operation} {.time}{"\n"}{end}'
kubectl -n <env> get scaledobject <name> -o jsonpath='{.status.lastActiveTime}{"\n"}'   # when it last scaled
```

`managedFields` gives you the writer (`kubectl-annotate`) and the timestamp;
`lastActiveTime` immediately before it is the tell that someone watched it scale and
stopped it. Then find what shipped or broke that day (`git log --since=<date>
--until=<date+1>`, closed issues), and only unpause once you can name the defect and
point at its fix. Treat the unpause as a **monitored experiment** with a rollback
trigger, not a config tidy-up — a fix merged while the namespace was pinned has only
ever been exercised in the transient two-silo window of a rollout, never in steady
state. `scripts/check-chart-drift.sh` reports the annotation as CLUSTER-ONLY drift so
it stops being invisible.

### Crash dumps: verify the MOUNT

`DOTNET_DbgEnableMiniDump` + `DOTNET_DbgMiniDumpName=/data/dumps/…` do nothing
without a volume at that path — `createdump` does not create directories, so the
crash destroys its own evidence. The chart mounts a dedicated `memex-dumps`
emptyDir; verify the LIVE pod actually has it:

```bash
kubectl get deploy memex-portal-deployment -n <env> \
  -o jsonpath='{range .spec.template.spec.containers[0].volumeMounts[*]}{.name} -> {.mountPath}{"\n"}{end}' \
  | grep dumps || echo "NO DUMP MOUNT — every crash will produce nothing"
```

⚠️ A per-env `portal-patch.json` replaces volumes **by index**, so adding or
reordering a chart volume silently misaligns an environment and a cluster can run an
older volume set while the chart in git looks right. Diff live mounts against the
chart after every deploy.

### Degraded-but-Ready replica — intermittent hangs, `kubectl top` divergence

Symptom: some requests hang or serve garbled state while most succeed, typically
after a burst of content syncs or a scheduled bake. Cause class (2026-08-25,
MeshWeaver#2194): each NodeType publication mints a new AssemblyLoadContext and
serving instances stay on the OLD build behind the Recycle banner, so every
superseded ALC stays rooted — the type-hosting silos climb to tens of GB and go
GC-bound while still answering `/ready` (the readiness probe's path since MeshWeaver#3330), so
Kubernetes never pulls them from rotation — deliberately: eviction hands the traffic to siblings
converging on the same ceiling. A progress-aware `/alive` restarts such a pod instead.

```bash
az aks command invoke -g <rg> -n <cluster> --command "kubectl top pods -n <ns> --no-headers"
```

One or two pods far above their siblings in BOTH memory and CPU = this. Remedy:
`kubectl delete pod` the outliers (grace-drain; the Deployment replaces them).
Prevention: enable `Modules:AutoRecycleOnStaleBuild` (MeshWeaver#2192) so instances
converge onto each newly published build and old ALCs collect — mechanism in
[NodeTypeCompilation.md](../../src/MeshWeaver.Documentation/Data/Architecture/NodeTypeCompilation.md).

---

## Generating this from Aspire

The repo already models the deployment in
[`deploy/aspire/Memex.Deploy.AppHost`](../aspire). Running
`aspire publish` (or `azd`) against that model is what produced the generic
[`../helm`](../helm) chart and the [`../aca`](../aca) Container Apps Bicep.

This AKS sample is **complementary**, not generated: the Aspire publishers emit a
portable Helm chart and an ACA topology, but they do **not** emit a private-AKS +
P2S-VPN + pgBackRest-PITR stack. So the relationship is:

- **Aspire owns the app model** → it generates `../helm` (Deployments, Service,
  StatefulSet, migration Job, config/secret templates). Keep regenerating that
  from Aspire when the app composition changes.
- **This sample owns the AKS platform** → `infra/*.bicep` (private cluster, VPN,
  ACR, backup storage) + `values.aks.yaml` overlay + `manifests/` for the pieces
  the generic chart doesn't template. These are hand-authored Azure platform
  concerns that don't belong in the app model.

If you want the Aspire AppHost to drive the **infra** too, you can call this
Bicep from a `Memex.Deploy.AppHost` publisher: add it as an
`AddBicepTemplate("aks-infra", "infra/main.bicep")` resource (or invoke
`az deployment sub create` from a publish hook) and pass the cluster/ACR/storage
outputs into the Helm release step. That keeps a single `aspire`/`azd`-driven
entry point while this directory remains the source of truth for the AKS-specific
Azure resources. The chart stays Aspire-generated; only the platform Bicep and
the overlay are added here.

To keep the chart in sync after an app-model change, regenerate `../helm` from
Aspire and re-run Step 4 — `values.aks.yaml` and `manifests/` continue to apply
on top unchanged (they only reference stable resource names like
`memex-portal-deployment` / `memex-postgres-statefulset`).

### Why a written procedure and not pure `aspire up`

Aspire's Kubernetes publisher generates the **workload** chart, but it does not
provision an AKS **cluster**, a private Postgres Flexible Server, a VPN, or
Let's Encrypt. Those platform pieces are the Bicep + the steps above. The AppHost
([`../aspire/Memex.Deploy.AppHost`](../aspire/Memex.Deploy.AppHost)) owns the app
model **and the deploy parameters** — including the OAuth providers; this document
stitches the platform around it. All config flows from deploy parameters → env.

---

## Known gaps / follow-ups

- **Dump mount not applied to the live clusters** (2026-07-28): all three
  namespaces carry the dump env vars while no pod mounts `/data/dumps`, so every
  production `exit=139` so far produced no dump. Applying the current chart fixes
  it; until then a `mkdir /data/dumps` on the `/data` PVC is a stopgap that puts
  heap-sized dumps on a shared 16Gi share instead of the size-bounded emptyDir.
- ~~**Multi-replica HA**~~ (chart side done 2026-08-14): Orleans **AdoNet**
  clustering is the HA provider (`Features:Orleans:Clustering`, legacy key
  `Deployment:Orleans:Clustering`), backed by a dedicated `orleans` database the
  migration's `OrleansClusteringSetup` creates. The chart now templates the replica
  count and omits `spec.replicas` entirely under KEDA so the HPA owns it;
  `scripts/check-chart-invariants.sh` asserts the prerequisites at render time.
  What remains is per-environment and per-cluster, not chart work: each env's
  overlay must set `keda.enabled`, and a paused `ScaledObject` still pins the count
  regardless (see "Scaling: KEDA wins" above — check `PAUSED` before believing a
  replica number).
- ~~**Chart connection string**~~ (fixed 2026-08-14):
  `../helm/templates/memex-portal/secrets.yaml` and its migration counterpart now
  take `secrets.memex_{portal,migration}.ConnectionStrings__memex` / `__orleans`
  from values, falling back to the in-cluster host only when unset. An env that
  supplies them no longer needs the post-`helm` secret patch in `scripts/deploy.sh`.
  🚨 Until an env's values file DOES supply them, `helm upgrade` still re-renders
  the in-cluster host — and for `__orleans` nothing patches it back, so an AdoNet
  namespace loses cluster membership. The chart `fail`s the render if an external
  `ConnectionStrings__memex` is supplied without a matching `__orleans`, so the
  half-configured case cannot ship silently.
- **Secrets → Key Vault**: move the PG password, master key, and OAuth secrets into
  Key Vault via the CSI Secrets Store add-on (enabled in `infra/modules/aks.bicep`).

---

## Teardown

```bash
helm uninstall memex -n memex
kubectl delete namespace memex                 # also deletes the PVCs (Azure Files/Disk)
az group delete --name <rg> --yes --no-wait    # cluster, VPN, ACR, storage, VNet
```

> Deleting the namespace deletes the PVCs and their backing Azure Files shares /
> managed disks. The pgBackRest **Blob** repo lives in the separate backup
> storage account and survives the cluster — delete the resource group (or just
> the storage account) to remove backups, mindful of the 30-day soft-delete.

---

## Security notes (read before prod)

- **Secrets**: move `memex_postgres_password` out of `--set`/values into Key
  Vault via the CSI Secrets Store add-on (already enabled).
- **API server**: private-only; `enablePrivateClusterPublicFQDN=false`.
  `infra/modules/aks.bicep` sets `disableLocalAccounts: false` with the comment
  *"keep admin kubeconfig usable over the VPN"* — that is the **only** thing local
  accounts are for here, since `az aks command invoke` does not need them. If you
  skip the VPN (`deployVpnGateway: false`), consider Entra-only
  (`disableLocalAccounts=true` + AKS-managed Entra RBAC) for prod.
- **VPN auth**: the optional P2S uses certificate auth for simplicity. Entra ID
  authentication on the P2S is stronger (revocation, conditional access).
- **ACR**: `publicNetworkAccess` is Enabled for first-run `az acr import`. For a
  fully private cluster, switch ACR to Premium + Private Endpoint and disable
  public access once images are imported.
- **Egress**: default `outboundType: loadBalancer` allows node egress to GHCR /
  Azure. For a locked-down network use `userDefinedRouting` + Azure Firewall and
  Option B (ACR import) so no pull traffic leaves the VNet.

---

## Appendix A — Point-to-Site VPN (optional)

**You do not need this to deploy.** Step 2's `az aks command invoke` covers every
apply / helm / logs / patch operation, and it is the only path anything automated
here uses. Provision the gateway when you want a **route into the VNet** for
interactive tooling that opens its own socket:

- `kubectl port-forward` to in-cluster Grafana (it has no public endpoint);
- `psql` straight at the private Postgres Flexible Server;
- a native local `kubectl` — tab-completion, long-running `watch`/`logs -f` — without
  a standing jumpbox VM.

The gateway is still provisioned by default (`infra/main.bicep`,
`param deployVpnGateway bool = true`, module `vpn.bicep`) and it costs 20–45 minutes
of deploy time; `deployVpnGateway: false` is supported and documented on that
parameter. Because the cluster is private, the tunnel is what lets `kubectl` resolve
the API server: the linked private DNS zone `privatelink.<region>.azmk8s.io` maps
the API server FQDN to its private IP once your laptop is attached to the VNet.

### A1. Create the P2S certificates (cert-based auth)

```bash
# Root CA
openssl genrsa -out p2sRoot.key 2048
openssl req -x509 -new -nodes -key p2sRoot.key -subj "/CN=Memex-P2S-Root" -days 3650 -out p2sRoot.crt

# Client cert signed by the root
openssl genrsa -out p2sClient.key 2048
openssl req -new -key p2sClient.key -subj "/CN=Memex-P2S-Client" -out p2sClient.csr
openssl x509 -req -in p2sClient.csr -CA p2sRoot.crt -CAkey p2sRoot.key -CAcreateserial -days 365 -out p2sClient.crt

# Base64 of the ROOT public cert (single line, no PEM headers) — feed to Bicep
openssl x509 -in p2sRoot.crt -outform der | base64 -w0 ; echo
```

You can either:

- paste that base64 string into `vpnClientRootCertData` and redeploy infra, **or**
- upload it after the fact without redeploying:

```bash
az network vnet-gateway root-cert create \
  --resource-group <rg> --gateway-name <namePrefix>-vpngw \
  --name P2SRootCert --public-cert-data "<base64-root-cert>"
```

⚠️ **`--public-cert-data` is read as a FILE PATH by current `az` versions** — pass
the path, NOT the inline base64 string and NOT `@file`. On Windows the equivalent
certificate mint is:

```powershell
$root   = New-SelfSignedCertificate -Type Custom -KeySpec Signature -Subject "CN=MemexP2SRootCert" -KeyUsage CertSign -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm sha256 -KeyLength 2048
$client = New-SelfSignedCertificate -Type Custom -DnsName MemexP2SChild -KeySpec Signature -Subject "CN=MemexP2SChildCert" -Signer $root -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm sha256 -KeyLength 2048 -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.2")
[IO.File]::WriteAllText("root.txt",[Convert]::ToBase64String($root.RawData))
# then: az network vnet-gateway root-cert create -g <rg> --gateway-name <vpn-gateway> \
#         --name MemexP2SRootCert --public-cert-data root.txt
```

### A2. Download + connect the VPN client

```bash
az network vnet-gateway vpn-client generate \
  --resource-group <rg> --name <namePrefix>-vpngw --output tsv   # -> download URL (zip)
```

Download the returned zip, install the profile (the **Azure VPN Client** or
OpenVPN; the bundle ships an OpenVPN `.ovpn` you augment with `p2sClient.crt` +
`p2sClient.key`), and connect.

### A3. Get credentials and run kubectl

```bash
az aks get-credentials --resource-group <rg> --name <clusterName>
kubectl get nodes        # resolves the PRIVATE API server over the tunnel
kubectl -n monitoring port-forward svc/loki-grafana 3000:80   # what the tunnel is actually for
```

If `kubectl` times out: confirm the VPN is connected, that the private DNS zone
`privatelink.<region>.azmk8s.io` is linked to the VNet (it is, via `network.bicep`),
and that your client gets a `172.16.201.x` address.


## Silo memory growth on recompile waves (#2194)

> Carried across from `DEPLOY-RUNBOOK.md` when #2144 consolidated the deployment docs.
> The runbook was deleted in that consolidation; this analysis was written against it and
> would otherwise have been lost to a modify/delete conflict.

volume silently misaligns an environment and a cluster can run an older volume set while the chart
in git looks right. Diff live mounts against the chart after every deploy.

### Degraded-but-Ready replica — intermittent hangs, `kubectl top` divergence

Symptom: some requests hang or serve garbled state while most succeed, typically after a burst of
content syncs or a scheduled bake. Cause class (2026-08-25, MeshWeaver#2194): each NodeType
publication mints a new AssemblyLoadContext and serving instances stay on the OLD build behind the
Recycle banner, so every superseded ALC stays rooted — the type-hosting silos climb to tens of GB
and go GC-bound while still answering `/ready` (the readiness probe's path since MeshWeaver#3330),
so Kubernetes never pulls them from rotation — deliberately, because eviction hands the traffic to
siblings converging on the same ceiling. A progress-aware `/alive` restarts such a pod instead.

```bash
az aks command invoke -g <rg> -n <cluster> --command "kubectl top pods -n <ns> --no-headers"
```

One or two pods far above their siblings in BOTH memory and CPU = this. Remedy: `kubectl delete
pod` the outliers (grace-drain; the Deployment replaces them). Prevention: enable
`Modules:AutoRecycleOnStaleBuild` (MeshWeaver#2192) so instances converge onto each newly
published build and old ALCs collect — mechanism in
`src/MeshWeaver.Documentation/Data/Architecture/NodeTypeCompilation.md`.

**You no longer have to be watching.** `PortalReplicaWorkingSetDiverged`
(`deploy/aks/scripts/values.observability.yaml`, group `portal-degraded`) fires when one
`memex-portal` container's working set is >3× the lightest in the same namespace AND above 8 GB for
15 minutes — the exact 2026-08-25 shape, where nothing alerted and the two sick pods served hung
pages for three and a half hours. It is DETECTION only; the fix is still convergence. It needs the
observability stack installed (`install-observability.sh`), and it can never fire on a
single-replica namespace by construction (`max == min`).

✅ Closed on #2194: a **progress-aware `/alive`** ships in `Memex.Portal.Distributed`
(`ProcessProgressHealthCheck`, MeshWeaver.Plugins#1234), and readiness has been moved OFF that path
onto `/ready` so the progress verdict restarts a pod instead of evicting it 60 s earlier onto
siblings at the same ceiling (MeshWeaver#3330). Do NOT "fix" anything here by pointing readiness
back at `/health` OR at `/alive` — the first re-creates the 2026-07-21 probe death-spiral, the
second rebuilt it out of the containment. See `Doc/Architecture/ProbeSemantics`.

## Known gaps / follow-ups
