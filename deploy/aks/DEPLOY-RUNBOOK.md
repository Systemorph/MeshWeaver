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
```bash
# Base image (node + Claude Code + Copilot CLIs) — Linux builder => correct copilot-linux-x64
az acr build --registry meshweaver --image memex-portal-ai-base:latest deploy/base-images/portal-ai
# App image — MUST pass -r linux-x64 (the Copilot SDK keys the CLI binary off the RID)
az acr login --name meshweaver
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj \
  -c Release -r linux-x64 --no-self-contained -t:PublishContainer -p:PublishProfile= \
  -p:ContainerRegistry=meshweaver.azurecr.io -p:ContainerRepository=memex-portal-ai \
  -p:ContainerImageTag=latest -p:ContainerBaseImage=meshweaver.azurecr.io/memex-portal-ai-base:latest
dotnet publish memex/aspire/Memex.Database.Migration/Memex.Database.Migration.csproj \
  -c Release -r linux-x64 --no-self-contained -t:PublishContainer -p:PublishProfile= \
  -p:ContainerRegistry=meshweaver.azurecr.io -p:ContainerRepository=memex-migration -p:ContainerImageTag=latest
```

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
chart-gen gap).

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

## Known gaps / follow-ups
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
