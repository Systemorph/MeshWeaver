# MeshWeaver Memex on AKS — production-grade deployment sample

A reference, operator-facing deployment of the **Memex portal** on a **private
Azure Kubernetes Service** cluster. It layers AKS-specific Azure infrastructure
(Bicep) and a Helm values overlay on top of the generic Kubernetes chart that
already lives at [`../helm`](../helm). Everything here is **infra / YAML /
markdown only** — no application code changes.

> This is a **sample**. Read it end-to-end, tune the parameters for your
> environment (regions, SKUs, CIDRs, DNS names, secrets), and treat the security
> defaults as a starting point, not a finished hardening.

---

## What you get

| Concern | This sample's choice |
|---|---|
| Cluster | **Private AKS** (`enablePrivateCluster=true`) — API server has a private IP only |
| kubectl reach | **Azure VPN Gateway (Point-to-Site)** + linked **private DNS zone** `privatelink.<region>.azmk8s.io` |
| Registry | **Azure Container Registry** (Premium) with **AcrPull** granted to the cluster's kubelet identity |
| Networking | VNet with `aks-nodes`, `GatewaySubnet`, `AzureBastionSubnet` subnets; Azure CNI overlay + Cilium |
| Portal | Blazor Server, **HA (3 replicas across 3 zones)** behind ingress with **cookie session affinity** |
| Shared storage | **Azure Files (RWX)** drives mounted at explicit paths: `/data` (caches), `/mnt/content` (content collection), `/mnt/attachments`, `/mnt/users` — via a custom `azurefile-memex` StorageClass tuned for the non-root portal (uid 1654) |
| Database | Self-managed **Postgres (pgvector) StatefulSet** on a Premium-SSD PVC |
| Backup / PITR | **pgBackRest** → **Azure Blob** (full + diff CronJobs + WAL archiving) → restore `--type=time` |
| Observability | **OpenTelemetry Collector DaemonSet** captures cluster-wide pod logs + portal OTLP → **Azure Files** log archive (`/mnt/otel-logs`) |
| Identity | **Workload Identity (OIDC)** so pgBackRest reaches Blob **keyless** |

### Topology

```
                       ┌──────────────── operator laptop ───────────────┐
                       │  azure-vpn / OpenVPN client (cert auth)         │
                       └───────────────────────┬─────────────────────────┘
                                               │ P2S tunnel (172.16.201.0/24)
                  ┌────────────────────────────▼──────────────────────────┐
                  │  VNet 10.42.0.0/16                                      │
                  │  ┌── GatewaySubnet ──┐  ┌── aks-nodes 10.42.0.0/20 ──┐ │
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
├── README.md                  ← you are here
├── values.aks.yaml            ← Helm overlay for ../helm (AKS overrides)
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
    ├── portal-ha-patch.yaml   ← replicas=3, zone spread, probes, RWX volume mounts
    ├── postgres-pvc-patch.yaml← bind pg StatefulSet to its PVC
    ├── portal-ingress.yaml    ← ingress + cookie session affinity (Blazor)
    ├── observability/
    │   ├── otel-collector-config.yaml ← collector pipeline (filelog+otlp → file+debug)
    │   ├── otel-collector.yaml         ← collector DaemonSet + SA/RBAC + Service
    │   └── otel-pvc.yaml               ← RWX Azure Files PVC for the log archive
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
  `secrets.*`) with AKS-correct values (e.g. Orleans `AdoNet` clustering for HA).
- **`manifests/`** supplies the things the generic chart does not template —
  RWX PVCs, ingress with sticky sessions, the HA replica/zone patch, and the
  pgBackRest sidecar/CronJobs — as `kubectl apply` / `kubectl patch` steps you
  run right after `helm install`.

The annotated `ingress:`, `persistence:`, `replicas:`, and `pgbackrest:` blocks
at the bottom of `values.aks.yaml` double as a forward-looking contract: if the
`../helm` chart is later extended to template these, the overlay already carries
the right values.

---

## Prerequisites

- `az` CLI ≥ 2.84 (`az version`)
- `az bicep` ≥ 0.41 (`az bicep version`) — `az bicep upgrade` if older
- `kubectl` and `helm` ≥ 3.12
- A subscription where you can create resource groups + role assignments
  (Owner or User Access Administrator — the deployment grants AcrPull, DNS, and
  Blob roles)
- `openssl` (to mint the P2S VPN certificates), or Windows `New-SelfSignedCertificate`

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
> deploy time. Set `deployVpnGateway: false` if you'll use
> `az aks command invoke` or a Bastion jumpbox instead (see Step 2 alternatives).

### Key parameters

| Parameter | Default | Notes |
|---|---|---|
| `location` | `westeurope` | drives the private DNS zone name |
| `namePrefix` | `memexaks` | ≤ 12 chars, prefixes every resource |
| `systemNodeVmSize` / `systemNodeCount` | `Standard_D8s_v3` / 3 | 8 vCPU / **32 GiB** nodes, autoscales 3→6. (Pick a family with quota in your region — DSv5 was 0 in this subscription's westeurope, DSv3 had 100 vCPU.) |
| `availabilityZones` | `["1","2","3"]` | zonal spread for HA |
| `vnetAddressSpace` | `10.42.0.0/16` | must not collide with peered nets |
| `deployVpnGateway` | `true` | the P2S kubectl path |
| `vpnClientAddressPool` | `172.16.201.0/24` | **must not overlap the VNet** |
| `vpnClientRootCertData` | `""` | base64 root public cert (can add later) |
| `deployBackupStorage` | `false` | self-managed pgBackRest blob; **off** because we use the managed private Flexible Server instead |
| `deployContentFileShares` | `true` | Azure Files account + named shares for **static** PV binding (dynamic provisioning needs no shares) |
| `deployPostgresFlexible` | `true` | **PRIVATE (VNet-injected) PostgreSQL Flexible Server** with pgvector + managed PITR |
| `postgresAdminPassword` | *(required, `@secure`)* | pass at deploy time: `--parameters postgresAdminPassword=...` — never commit |
| `postgresSkuName` | `Standard_D2ds_v5` | 2 vCPU / 8 GiB GeneralPurpose; bump for more DB headroom |
| `postgresHighAvailability` | `true` | zone-redundant hot standby in a 2nd AZ |

---

## Step 2 — Reach the private API server (P2S VPN)

Because the cluster is private, `kubectl` only works from inside the VNet. The
P2S VPN attaches your laptop to the VNet; the linked private DNS zone then
resolves the API server FQDN to its private IP.

### 2a. Create the P2S certificates (cert-based auth)

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

### 2b. Download + connect the VPN client

```bash
az network vnet-gateway vpn-client generate \
  --resource-group <rg> --name <namePrefix>-vpngw --output tsv
```

Download the returned zip, install the profile (the **Azure VPN Client** or
OpenVPN; the bundle ships an OpenVPN `.ovpn` you augment with `p2sClient.crt` +
`p2sClient.key`), and connect.

### 2c. Get credentials and run kubectl

```bash
az aks get-credentials --resource-group <rg> --name <clusterName>
kubectl get nodes        # resolves the PRIVATE API server over the tunnel
```

If `kubectl` times out: confirm the VPN is connected, that the private DNS zone
`privatelink.<region>.azmk8s.io` is linked to the VNet (it is, via
`network.bicep`), and that your client gets a `172.16.201.x` address.

### Alternatives to the P2S VPN (not implemented here, but supported)

- **`az aks command invoke`** — runs a command/`kubectl`/`helm` inside a
  transient pod on the cluster; no network line-of-sight needed. Great for CI:
  `az aks command invoke -g <rg> -n <cluster> --command "kubectl get pods -A"`.
- **Jumpbox + Azure Bastion** — the `AzureBastionSubnet` is already carved out;
  deploy Bastion + a small VM in the VNet and run kubectl from there.

The P2S VPN is implemented because it gives operators a native local `kubectl`
experience without a standing VM.

---

## Step 3 — Image strategy

The chart references `ghcr.io/systemorph/memex-portal-ai` and
`ghcr.io/systemorph/memex-migration`. Two options:

**Option A — pull from GHCR directly** (simplest; needs node egress to ghcr.io,
which the default `outboundType: loadBalancer` provides). Keep
`image.registry: ghcr.io/systemorph` in `values.aks.yaml`.

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

Then set `image.registry: <acrName>.azurecr.io` in `values.aks.yaml` (and, since
the generic chart hardcodes the GHCR path in its templates, repoint the running
Deployments with `kubectl set image` or extend the chart to read
`.Values.image.registry`).

---

## Step 4 — Install the portal (Helm + manifests)

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

# 3) Bind Postgres to its PVC, then scale the portal out + go RWX
kubectl patch statefulset memex-postgres-statefulset -n $NS \
  --type strategic --patch-file manifests/postgres-pvc-patch.yaml
kubectl patch deployment memex-portal-deployment -n $NS \
  --type strategic --patch-file manifests/portal-ha-patch.yaml

# 4) Ingress with cookie session affinity (enable a controller first)
az aks approuting enable -g <rg> -n <cluster>      # managed nginx
kubectl apply -n $NS -f manifests/portal-ingress.yaml
```

> **Secrets**: never commit a real password. The CSI Secrets Store add-on is
> enabled in `aks.bicep` — wire `secrets.memex_*` to Key Vault via a
> `SecretProviderClass` for production rather than `--set`.

> **Blazor sticky sessions**: the ingress affinity cookie is mandatory. Without
> it, SignalR circuit reconnects can land on the wrong replica and users see
> "Attempting to reconnect…" loops. The annotations are in
> `manifests/portal-ingress.yaml` (nginx today, AGIC commented).

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
cluster and archives it to a mounted **Azure Files** share — no per-GB App
Insights ingest. (This repo bills App Insights per ingest, so an in-cluster file
archive is the cost-conscious default for a self-hosted deployment.)

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
- **Cost**: a flat Azure Files share (≈€0.06/GB-month Standard) vs. per-GB App
  Insights ingest — the archive is the cheap default for self-hosting.
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
| **Microsoft / Entra (HOME)** | `TenantId` (Systemorph tenant GUID), `ClientId`, `ClientSecret` | `https://memex.systemorph.com/signin-microsoft` |
| **Google** | `ClientId`, `ClientSecret` | `https://memex.systemorph.com/signin-google` |
| **LinkedIn** | `ClientId`, `ClientSecret` | `https://memex.systemorph.com/signin-linkedin` |

- Setting `Authentication__Microsoft__TenantId` to a **real tenant GUID** (not
  `common`) makes that AAD the **home** directory. This subscription's tenant is
  `3a01d7ac-3330-444d-942d-975eb491b5d6` (Systemorph) and is pre-filled.
- Any provider with a `ClientId` set is offered on the login page; the presence
  of external providers flips the portal into multi-provider mode and dev login
  is off in the Distributed image.
- **You still must create the app registrations / OAuth clients** and fill the
  `CHANGE_ME_*` `ClientId`s (config) + `ClientSecret`s (secrets) — those are real
  credentials this repo does not contain. Register each redirect URI above.
- Host is `memex.systemorph.com` (ingress + TLS). Point a DNS A/CNAME at the
  ingress controller's IP and issue the `memex-tls` cert (cert-manager or a
  pre-created secret).

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
- **API server**: private-only; `enablePrivateClusterPublicFQDN=false`. Local
  accounts stay enabled so the VPN kubeconfig works — consider Entra-only
  (`disableLocalAccounts=true` + AKS-managed Entra RBAC) for prod.
- **VPN auth**: this sample uses certificate auth for simplicity. Entra ID
  authentication on the P2S is stronger (revocation, conditional access).
- **ACR**: `publicNetworkAccess` is Enabled for first-run `az acr import`. For a
  fully private cluster, switch ACR to Premium + Private Endpoint and disable
  public access once images are imported.
- **Egress**: default `outboundType: loadBalancer` allows node egress to GHCR /
  Azure. For a locked-down network use `userDefinedRouting` + Azure Firewall and
  Option B (ACR import) so no pull traffic leaves the VNet.
```
