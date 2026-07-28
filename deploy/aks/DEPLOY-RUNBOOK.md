# Deploy Memex to AKS — the `memex.systemorph.com` runbook

This is the **exact, verified** sequence used to bring up `https://memex.systemorph.com` on a
private AKS cluster in **swedencentral**. It is the reproducible template behind the sample in
this folder (`infra/` Bicep, `values.aks.yaml`, `manifests/`) and the image-based Aspire
AppHost at [`../aspire/Memex.Deploy.AppHost`](../aspire/Memex.Deploy.AppHost).

> **Model:** one Aspire AppHost (`Memex.Deploy.AppHost`) models the workload from published
> images; the **Kubernetes publisher** generates the Helm chart (`../helm`); this folder adds the
> AKS *platform* (Bicep) + overlay. AKS is the deploy target. All config flows from deploy
> parameters → env. See "Why a runbook and not pure `aspire up`" at the bottom.

Architecture decisions baked in (see the AGENTS memory + `../helm`):
- **Private** AKS API server + **private** Postgres Flexible Server; **only** the portal is public (`:443`).
- **One shared ACR** `meshweaver.azurecr.io` (RG `meshweaver-shared`) across all solutions.
- **Filesystem backend** with content on RWX **Azure Files** (`/mnt/content`); mesh data in Postgres.
- **Blazor sticky sessions** (cookie affinity = ACA's "bind tab to server"); **1 replica** today
  (multi-replica needs Orleans `AzureTables` clustering — a follow-up).
- TLS via **cert-manager + Let's Encrypt** (HTTP-01).

---

## 0. Prerequisites
- `az` ≥ 2.84 (logged in to the target subscription/tenant), `az bicep`, `docker`, .NET 10 SDK.
- A globally-unique shared ACR (here `meshweaver`). Create once:
  `az group create -n meshweaver-shared -l swedencentral` ;
  `az acr create -g meshweaver-shared -n meshweaver --sku Premium`.
- DNS zone for your domain in Azure DNS (here `systemorph.com`, RG `dns`).

## 1. Build + push images to the shared ACR

Images are **multi-arch** (linux/amd64 + linux/arm64) — one tag serves x86 cloud nodes AND
Apple-silicon (arm64) local k3s, so every install can pull-and-self-update natively from ACR.

```bash
# Base image (node + Claude Code + Copilot CLIs) — MULTI-ARCH manifest list.
# @anthropic-ai/claude-code is pure JS (arch-independent); @github/copilot resolves its
# per-platform binary via npm optional deps, so each arch's build bakes in the matching CLI.
az acr build --registry meshweaver --image memex-portal-ai-base:latest \
  --platform linux/amd64 --platform linux/arm64 deploy/base-images/portal-ai
# (equivalent local build: `docker buildx build --platform linux/amd64,linux/arm64 \
#   -t meshweaver.azurecr.io/memex-portal-ai-base:latest --push deploy/base-images/portal-ai`)

# App images — drop `-r linux-x64`; set RuntimeIdentifiers + ContainerRuntimeIdentifiers to both
# RIDs. With no single RID, the SDK (>= 8.0.405; we're on .NET 10) publishes per-RID and combines
# them into an OCI Image Index (manifest list). ContainerRuntimeIdentifiers MUST be a subset of
# RuntimeIdentifiers (set them equal). The arm64 leg layers on the multi-arch base above.
az acr login --name meshweaver
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj \
  -c Release --no-self-contained -t:PublishContainer -p:PublishProfile= \
  -p:RuntimeIdentifiers="linux-x64;linux-arm64" -p:ContainerRuntimeIdentifiers="linux-x64;linux-arm64" \
  -p:ContainerRegistry=meshweaver.azurecr.io -p:ContainerRepository=memex-portal-ai \
  -p:ContainerImageTag=latest -p:ContainerBaseImage=meshweaver.azurecr.io/memex-portal-ai-base:latest
dotnet publish memex/aspire/Memex.Database.Migration/Memex.Database.Migration.csproj \
  -c Release --no-self-contained -t:PublishContainer -p:PublishProfile= \
  -p:RuntimeIdentifiers="linux-x64;linux-arm64" -p:ContainerRuntimeIdentifiers="linux-x64;linux-arm64" \
  -p:ContainerRegistry=meshweaver.azurecr.io -p:ContainerRepository=memex-migration -p:ContainerImageTag=latest
```

> **First multi-arch roll:** the multi-arch base (`memex-portal-ai-base:latest`) must exist before
> the first multi-arch app build — the arm64 leg has no base layer otherwise. Rebuild the base
> multi-arch once (the `az acr build … --platform …` above), then the app builds (and the
> continuous self-update path) work for both architectures.

## 2. Provision the AKS platform (Bicep)
Edit `infra/main.parameters.json` (region, node size/count within your vCPU quota — swedencentral
defaulted to 2× `Standard_D4s_v3` under a 10-vCPU cap). Then:
```bash
PG_PW="$(openssl rand -base64 24 | tr -dc 'A-Za-z0-9' | head -c 28)Aa1"   # or your own
az deployment sub create --name memex-aks-infra-sc --location swedencentral \
  --template-file deploy/aks/infra/main.bicep \
  --parameters @deploy/aks/infra/main.parameters.json \
  --parameters postgresAdminPassword="$PG_PW"
```
Outputs: cluster name, the Postgres FQDN, the shared-ACR login server. Grant the cluster kubelet
**AcrPull** on the shared ACR (cross-RG, so done out-of-band):
```bash
KUBELET=$(az aks show -g memex-aks-rg -n memexaks-cluster --query identityProfile.kubeletidentity.objectId -o tsv)
az role assignment create --assignee-object-id $KUBELET --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -n meshweaver --query id -o tsv)
```
Same cross-RG grant for the **portal Workload Identity** (the shared UAMI the in-pod self-updater uses
to list ACR tags — provisioned by `infra/modules/portal-identity.bicep`, federated to
`system:serviceaccount:<ns>:memex-portal-sa` for every portal namespace):
```bash
PORTAL_MI=$(az identity show -g memex-aks-rg -n memexaks-portal-mi --query principalId -o tsv)
az role assignment create --assignee-object-id $PORTAL_MI --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -n meshweaver --query id -o tsv)
# Then wire its clientId into selfUpdate.azureClientId for each env (same value everywhere):
az deployment sub show --name memex-aks-infra-sc --query properties.outputs.portalIdentityClientId.value -o tsv
```
(Pure-IaC alternative to both out-of-band grants: deploy with `grantSharedAcrPull=true` for the portal
UAMI — needs User Access Administrator on `meshweaver-shared`. See [DeploymentAKS → Portal self-update](../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md).)
> Postgres connection uses the **private IP + password + SSL** (the FQDN would trip the portal's
> `database.azure.com` → Entra-token branch, which doesn't match a password server). Get it with:
> `az network private-dns record-set a list -g memex-aks-rg -z <pg-private-zone> -o table`.

## 3. External sign-in (OAuth) apps
- **Microsoft/Entra** (single-tenant home):
  ```bash
  az ad app create --display-name "Memex Portal (memex.systemorph.com)" --sign-in-audience AzureADMyOrg \
    --web-redirect-uris "https://memex.systemorph.com/signin-microsoft"
  az ad app credential reset --id <appId> --display-name aks --years 1   # => client secret
  ```
- **Google** (Cloud Console) + **LinkedIn** (Developer portal): create web OAuth clients with
  redirect URIs `https://memex.systemorph.com/signin-google` and `/signin-linkedin`.

## 4. Deploy the workload (private cluster → `az aks command invoke`)
Copy `scripts/values.deploy.example.yaml` → `scripts/values.deploy.yaml`, fill in the **real**
connection string, master key, and OAuth secrets (keep it OUT of git — `artifacts/`/Key Vault), then:
```bash
az aks approuting enable -g memex-aks-rg -n memexaks-cluster          # managed nginx (public LB)
cd deploy/aks/scripts
export MEMEX_PG_CONN='Host=<PG_PRIVATE_IP>;Port=5432;Username=memexadmin;Password=<PW>;Database=memex;SslMode=Require;Trust Server Certificate=true'
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "bash deploy.sh" --file .
```
`deploy.sh` does: namespace + RWX PVCs → `helm upgrade --install` (chart + `values.aks.yaml` +
`values.deploy.yaml`) → scale the chart's in-cluster pg to 0 (we use the Flexible Server) →
`kubectl set image` to the shared ACR → patch the portal to 1 replica + the Azure Files mounts →
**patch the connection-string secret** (the generated chart hardcodes the in-cluster pg — known
chart-gen gap). **Observability is folded in:** export `GRAFANA_PW=...` alongside `MEMEX_PG_CONN`
and `deploy.sh` also brings up Grafana + Loki + Prometheus (see §6); omit it to skip monitoring.
At the model level, `AddMemex`'s `OtlpEndpoint` option wires `OTEL_EXPORTER_OTLP_ENDPOINT` for
OTLP traces/metrics (not needed for log shipping — Promtail scrapes stdout).

## 5. Public ingress + TLS + DNS
```bash
IP=$(az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command "kubectl get svc -n app-routing-system nginx -o jsonpath='{.status.loadBalancer.ingress[0].ip}'")
az network dns record-set a add-record -g dns -z systemorph.com -n memex --ipv4-address $IP --ttl 300
cd deploy/aks/scripts
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command "bash tls.sh" --file tls.sh   # cert-manager + Let's Encrypt + ingress
```
HTTP→HTTPS redirect is automatic once the ingress has TLS. Verify (bypassing DNS cache):
```bash
curl -sS -o /dev/null -w "%{http_code} verify=%{ssl_verify_result}\n" \
  --resolve memex.systemorph.com:443:$IP https://memex.systemorph.com/
```

---

## 6. Observability (Grafana + Loki + Prometheus) + admin access via VPN
Everything except the portal stays private, so admin tools (Grafana, kubectl) go through the
**P2S VPN**, not a public endpoint.

**Install the stack** (`scripts/install-observability.sh` — grafana/loki-stack: Loki + Promtail +
Grafana + Prometheus, datasources auto-wired, Promtail ships every pod's logs to Loki):
```bash
export GRAFANA_PW='<strong-password>'
cd deploy/aks/scripts
az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command "GRAFANA_PW=$GRAFANA_PW bash install-observability.sh" --file install-observability.sh
```

**Set up the P2S VPN client** (the gateway + a root cert are provisioned by the Bicep + step 2):
```bash
# 1. Generate a P2S root+client cert (Windows) and upload the ROOT public cert to the gateway:
#    $root   = New-SelfSignedCertificate -Type Custom -KeySpec Signature -Subject "CN=MemexP2SRootCert" -KeyUsage CertSign -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm sha256 -KeyLength 2048
#    $client = New-SelfSignedCertificate -Type Custom -DnsName MemexP2SChild -KeySpec Signature -Subject "CN=MemexP2SChildCert" -Signer $root -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm sha256 -KeyLength 2048 -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.2")
#    [IO.File]::WriteAllText("root.txt",[Convert]::ToBase64String($root.RawData))
#    NOTE: this az version reads --public-cert-data as a FILE PATH, so pass the path (NOT the inline string, NOT @file):
#    az network vnet-gateway root-cert create -g memex-aks-rg --gateway-name memexaks-vpngw --name MemexP2SRootCert --public-cert-data root.txt
# 2. Download + install the VPN client, then connect:
az network vnet-gateway vpn-client generate -g memex-aks-rg -n memexaks-vpngw -o tsv   # -> download URL (zip)
# 3. With the VPN connected:
az aks get-credentials -g memex-aks-rg -n memexaks-cluster
kubectl -n monitoring port-forward svc/loki-grafana 3000:80    # http://localhost:3000  (admin / $GRAFANA_PW)
```
In Grafana → Explore → Loki, the portal logs are `{namespace="memex"}` (e.g. add
`|= "error"` or `|~ "signin-microsoft"`).

## 7. Rolling a new image — and the traps

```bash
# a) The manifest MUST carry every arch leg. A partial manifest list = ImagePullBackOff on the
#    missing arch, which presents as "the deploy hung" (memex-cloud V46 outage, 2026-07-19).
az acr manifest show -r meshweaver -n memex-portal-ai:<tag> \
  | jq -r '.manifests[]?.platform | "\(.os)/\(.architecture)"'   # expect linux/amd64 AND linux/arm64

# b) Roll.
az aks command invoke -g memex-aks-rg -n memexaks-cluster --command \
  "kubectl set image deploy/memex-portal-deployment memex-portal=meshweaver.azurecr.io/memex-portal-ai:<tag> -n <env> \
   && kubectl rollout status deploy/memex-portal-deployment -n <env> --timeout=600s"

# c) Verify with REAL signals, looped. HTTP 200 is the Blazor shell and proves nothing.
for i in $(seq 1 10); do curl -s -o /dev/null -w "%{http_code} " https://<host>/static/<space>/content/<file>; done
```

**Expect a cold-compile window.** Fresh pods invalidate every dynamic NodeType's cached assembly
(the framework MVID changed), so they all recompile. During it, pages and `/static/…` can fail with
*"No response received … `GetDataRequest`/`SubscribeRequest` → target X"*. This is normal; it is not
a bad image.

- **Do not cycle pods while it warms** — that restarts the compile work from cold.
- **Converging vs. stuck:** warm-up trends toward 100%. A *stable* ~50% over many minutes means one
  replica is consistently failing — investigate that replica instead of waiting.
- **Probes:** `startupProbe` gives 300s and gates liveness/readiness, so a slow boot is safe; a hang
  or crash after startup is killed by liveness in 90s.

### Scaling: KEDA wins
`kubectl scale --replicas=0` (the documented heal for a wedged mesh) is **silently reverted** by a
`ScaledObject` with `minReplicaCount: 2` — you conclude the heal failed when it never ran.
`kubectl get scaledobject -n <env>` first, and check `PAUSED`.

### Crash dumps: verify the MOUNT
`DOTNET_DbgEnableMiniDump` + `DOTNET_DbgMiniDumpName=/data/dumps/…` do nothing without a volume at
that path — `createdump` does not create directories, so the crash destroys its own evidence. The
chart mounts a dedicated `memex-dumps` emptyDir; verify the LIVE pod actually has it:

```bash
kubectl get deploy memex-portal-deployment -n <env> \
  -o jsonpath='{range .spec.template.spec.containers[0].volumeMounts[*]}{.name} -> {.mountPath}{"\n"}{end}' \
  | grep dumps || echo "NO DUMP MOUNT — every crash will produce nothing"
```

⚠️ `envs/<env>/portal-patch.json` replaces volumes **by index**, so adding or reordering a chart
volume silently misaligns an environment and a cluster can run an older volume set while the chart
in git looks right. Diff live mounts against the chart after every deploy.

## Known gaps / follow-ups
- **Dump mount not applied to the live clusters** (2026-07-28): all three namespaces carry the dump
  env vars while no pod mounts `/data/dumps`, so every production `exit=139` so far produced no
  dump. Applying the current chart fixes it; until then a `mkdir /data/dumps` on the `/data` PVC is
  a stopgap that puts heap-sized dumps on a shared 16Gi share instead of the size-bounded emptyDir.
- **Multi-replica HA**: needs Orleans `AzureTables` clustering wired on the Filesystem backend
  (the portal currently registers the clustering table client only in the Azure-backend branch).
- **Chart connection string**: `../helm/templates/memex-portal/secrets.yaml` hardcodes the
  in-cluster pg host/user — hence the post-install secret patch in `deploy.sh`. Fix at the
  chart-generator (AddMemex) so an external connection string flows from values.
- **Secrets → Key Vault**: move the PG password, master key, and OAuth secrets into
  `meshweaverkeyvault` via the CSI Secrets Store add-on (enabled in `infra/modules/aks.bicep`).

## Why a runbook and not pure `aspire up`
Aspire's Kubernetes publisher generates the **workload** chart, but it does not provision an AKS
**cluster**, a private Postgres Flexible Server, a VPN, or Let's Encrypt. Those platform pieces are
the Bicep + these steps. The AppHost (`../aspire/Memex.Deploy.AppHost`) owns the app model + the
deploy parameters (including the OAuth providers); this runbook stitches the platform around it.
