# Deploy Memex to AKS — the `portal.example.com` runbook

This is the **exact, verified** sequence used to bring up `https://portal.example.com` on a
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
KUBELET=$(az aks show -g <aks-resource-group> -n <aks-cluster> --query identityProfile.kubeletidentity.objectId -o tsv)
az role assignment create --assignee-object-id $KUBELET --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -n meshweaver --query id -o tsv)
```
Same cross-RG grant for the **portal Workload Identity** (the shared UAMI the in-pod self-updater uses
to list ACR tags — provisioned by `infra/modules/portal-identity.bicep`, federated to
`system:serviceaccount:<ns>:memex-portal-sa` for every portal namespace):
```bash
PORTAL_MI=$(az identity show -g <aks-resource-group> -n <portal-identity> --query principalId -o tsv)
az role assignment create --assignee-object-id $PORTAL_MI --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope $(az acr show -n meshweaver --query id -o tsv)
# Then wire its clientId into selfUpdate.azureClientId for each env (same value everywhere):
az deployment sub show --name memex-aks-infra-sc --query properties.outputs.portalIdentityClientId.value -o tsv
```
(Pure-IaC alternative to both out-of-band grants: deploy with `grantSharedAcrPull=true` for the portal
UAMI — needs User Access Administrator on `meshweaver-shared`. See [DeploymentAKS → Portal self-update](../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md).)
> Postgres connection uses the **private IP + password + SSL** (the FQDN would trip the portal's
> `database.azure.com` → Entra-token branch, which doesn't match a password server). Get it with:
> `az network private-dns record-set a list -g <aks-resource-group> -z <pg-private-zone> -o table`.

## 3. External sign-in (OAuth) apps
- **Microsoft/Entra** (single-tenant home):
  ```bash
  az ad app create --display-name "Memex Portal (portal.example.com)" --sign-in-audience AzureADMyOrg \
    --web-redirect-uris "https://portal.example.com/signin-microsoft"
  az ad app credential reset --id <appId> --display-name aks --years 1   # => client secret
  ```
- **Google** (Cloud Console) + **LinkedIn** (Developer portal): create web OAuth clients with
  redirect URIs `https://portal.example.com/signin-google` and `/signin-linkedin`.

## 4. Deploy the workload (private cluster → `az aks command invoke`)
Copy `scripts/values.deploy.example.yaml` → `scripts/values.deploy.yaml`, fill in the **real**
connection string, master key, and OAuth secrets (keep it OUT of git — `artifacts/`/Key Vault), then:
```bash
az aks approuting enable -g <aks-resource-group> -n <aks-cluster>          # managed nginx (public LB)
cd deploy/aks/scripts
export MEMEX_PG_CONN='Host=<PG_PRIVATE_IP>;Port=5432;Username=memexadmin;Password=<PW>;Database=memex;SslMode=Require;Trust Server Certificate=true'
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "bash deploy.sh" --file .
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
IP=$(az aks command invoke -g <aks-resource-group> -n <aks-cluster> \
  --command "kubectl get svc -n app-routing-system nginx -o jsonpath='{.status.loadBalancer.ingress[0].ip}'")
az network dns record-set a add-record -g dns -z systemorph.com -n memex --ipv4-address $IP --ttl 300
cd deploy/aks/scripts
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command "bash tls.sh" --file tls.sh   # cert-manager + Let's Encrypt + ingress
```
HTTP→HTTPS redirect is automatic once the ingress has TLS. Verify (bypassing DNS cache):
```bash
curl -sS -o /dev/null -w "%{http_code} verify=%{ssl_verify_result}\n" \
  --resolve portal.example.com:443:$IP https://portal.example.com/
```

**Default SSL certificate (cluster-wide, one-time).** Without it, any client that connects
without SNI gets the self-signed "Kubernetes Ingress Controller Fake Certificate" — and
corporate TLS-inspection / URL-categorization appliances probe exactly that way, then flag the
whole domain as insecure and block it for their users (seen 2026-08: a client's IT blocked
`memex.meshweaver.cloud` in Firefox *and* Edge over this). Point the app-routing controller's
default cert at the flagship host's cert-manager secret — patch the `NginxIngressController`
CR, **not** the nginx deployment (the addon operator reverts direct deployment edits):
```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl patch nginxingresscontroller default --type merge -p '{\"spec\":{\"defaultSSLCertificate\":{\"secret\":{\"name\":\"memexcloud-tls\",\"namespace\":\"memex-cloud\"}}}}'"
# verify from outside — must show the real cert, not "Acme Co":
echo | openssl s_client -connect <host>:443 -noservername 2>/dev/null | openssl x509 -noout -subject
```
The setting survives addon updates but is NOT re-created on a cluster rebuild — re-apply it
whenever the cluster (or the `NginxIngressController` CR) is recreated.

---

## 6. Observability (Grafana + Loki + Prometheus) + admin access via VPN
Everything except the portal stays private, so admin tools (Grafana, kubectl) go through the
**P2S VPN**, not a public endpoint.

**Install the stack** (`scripts/install-observability.sh` — grafana/loki-stack: Loki + Promtail +
Grafana + Prometheus, datasources auto-wired, Promtail ships every pod's logs to Loki):
```bash
export GRAFANA_PW='<strong-password>'
cd deploy/aks/scripts
az aks command invoke -g <aks-resource-group> -n <aks-cluster> \
  --command "GRAFANA_PW=$GRAFANA_PW bash install-observability.sh" --file install-observability.sh
```

**Set up the P2S VPN client** (the gateway + a root cert are provisioned by the Bicep + step 2):
```bash
# 1. Generate a P2S root+client cert (Windows) and upload the ROOT public cert to the gateway:
#    $root   = New-SelfSignedCertificate -Type Custom -KeySpec Signature -Subject "CN=MemexP2SRootCert" -KeyUsage CertSign -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm sha256 -KeyLength 2048
#    $client = New-SelfSignedCertificate -Type Custom -DnsName MemexP2SChild -KeySpec Signature -Subject "CN=MemexP2SChildCert" -Signer $root -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -HashAlgorithm sha256 -KeyLength 2048 -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.2")
#    [IO.File]::WriteAllText("root.txt",[Convert]::ToBase64String($root.RawData))
#    NOTE: this az version reads --public-cert-data as a FILE PATH, so pass the path (NOT the inline string, NOT @file):
#    az network vnet-gateway root-cert create -g <aks-resource-group> --gateway-name <vpn-gateway> --name MemexP2SRootCert --public-cert-data root.txt
# 2. Download + install the VPN client, then connect:
az network vnet-gateway vpn-client generate -g <aks-resource-group> -n <vpn-gateway> -o tsv   # -> download URL (zip)
# 3. With the VPN connected:
az aks get-credentials -g <aks-resource-group> -n <aks-cluster>
kubectl -n monitoring port-forward svc/loki-grafana 3000:80    # http://localhost:3000  (admin / $GRAFANA_PW)
```
In Grafana → Explore → Loki, the portal logs are `{namespace="memex"}` (e.g. add
`|= "error"` or `|~ "signin-microsoft"`).

### 6b. Red-log ticketing (optional) — every `fail:`/`crit:` opens exactly one issue

Once Loki is up, `mw-log-watcher` can turn production errors into GitHub issues: it reads red lines
from a persisted cursor, groups them by fault, and reports each distinct fingerprint to the portal,
which triages it with an agent and opens one ticket. A burst of ten thousand identical errors is one
issue; recurrences comment on it. **It is off until all four steps below are done** — the ingest
endpoint is not even mapped without a token.

It runs in `monitoring`, NOT in the portal's namespace, on purpose: the thing that notices the portal
is throwing errors must not be hosted by the portal.

```bash
# 1. ONE shared secret, both sides. The watcher presents it; the portal requires it.
TOKEN=$(openssl rand -hex 32)
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl -n monitoring create secret generic mw-log-watcher --from-literal=ingest-token=$TOKEN"
#    …then set LogWatch__IngestToken to the SAME value on the portal (KeyVault → its secret store).

# 2. Where tickets go. Without at least one route the control plane idles by design —
#    it will not spend agent rounds on incidents it could never file.
#      LogWatch__DefaultRepository    = Systemorph/MeshWeaver
#      LogWatch__Routes__0__Prefix    = MeshWeaver.
#      LogWatch__Routes__0__Repository = Systemorph/MeshWeaver
#      LogWatch__Routes__1__Prefix    = Memex.
#      LogWatch__Routes__1__Repository = Systemorph/Memex
#    Every key carries the LogWatch__ prefix — a bare Routes__0__Repository does not bind, and an
#    unbound route reads exactly like "no route configured": incidents pile up at New, silently.
#    Issues are opened as the GitHub App (GitHub:App), never a user's OAuth token.

# 3. Build + push the watcher image.
dotnet publish tools/MeshWeaver.LogWatcher/MeshWeaver.LogWatcher.csproj -c Release \
  -t:PublishContainer -p:ContainerRegistry=meshweaver.azurecr.io \
  -p:ContainerRepository=memex-log-watcher -p:ContainerImageTag=<tag>

# 4. Apply the Deployment + PVC (edit the image tag + watched namespaces in the manifest first).
az aks command invoke -g <aks-resource-group> -n <aks-cluster> \
  --command "kubectl apply -f log-watcher.yaml" \
  --file manifests/observability/log-watcher.yaml
```

**Verify — and know what each failure looks like:**

```bash
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl -n monitoring logs deploy/mw-log-watcher --tail=50"
```

| What the log says | Meaning |
|---|---|
| `Loki: N red line(s) …` then `Reported <fp> … — 200` | Working end to end. |
| `Log watcher is not configured` | Step 1 missed — `PortalUrl`/`IngestToken` unset. |
| `Portal REJECTED report … with 401` | The two tokens differ — the watcher's secret and the portal's `LogWatch__IngestToken` must be the same string. |
| `Portal REJECTED report … with 404` | The portal's `LogWatch__IngestToken` is unset, so `/api/log-incidents` is not mapped at all — step 1 was done on the watcher side only. |
| `Loki: 0 line(s)` forever | The namespace label is wrong, or Promtail is not shipping that namespace. |
| `Could not persist watcher state … Access to the path … is denied` | The state PVC mounted root-owned. The pod needs `securityContext.fsGroup: 1654` (the shipped manifest sets it). The watcher still reports, but the cursor stops persisting, so a restart replays the lookback window. |
| Incidents appear but stay `New` | Step 2 missed — no repository routed, so the control plane idles. |

Then browse `Admin/_LogIncident` in the portal: every incident links to the ticket it opened and to
the triage thread that wrote it.

**🚨 The state directory must be a real volume.** On an `emptyDir` a pod restart replays the lookback
window and re-reports. The manifest ships a PVC for this reason.

**To turn it off**: `kubectl -n monitoring scale deploy/mw-log-watcher --replicas=0`. Clearing the
portal's `LogWatch__IngestToken` also closes the ingest endpoint. Neither deletes existing incidents.

Full reference: [LogWatchTriage.md](../../src/MeshWeaver.Documentation/Data/Architecture/LogWatchTriage.md)

## 7. Rolling a new image — and the traps

```bash
# a) The manifest MUST carry every arch leg. A partial manifest list = ImagePullBackOff on the
#    missing arch, which presents as "the deploy hung" (memex-cloud V46 outage, 2026-07-19).
az acr manifest show -r meshweaver -n memex-portal-ai:<tag> \
  | jq -r '.manifests[]?.platform | "\(.os)/\(.architecture)"'   # expect linux/amd64 AND linux/arm64

# b) Roll.
az aks command invoke -g <aks-resource-group> -n <aks-cluster> --command \
  "kubectl set image deploy/memex-portal-deployment memex-portal=meshweaver.azurecr.io/memex-portal-ai:<tag> -n <env> \
   && kubectl rollout status deploy/memex-portal-deployment -n <env> --timeout=600s"

# c) Verify with REAL signals, looped. HTTP 200 is the Blazor shell and proves nothing.
for i in $(seq 1 10); do curl -s -o /dev/null -w "%{http_code} " https://<host>/api/content/<space>/content/<file>; done
```

### 🧱 NodeType bake — the deploy MUST compile every dynamic NodeType

Fresh pods invalidate every dynamic NodeType's cached assembly (the framework MVID changed), so
they all need a recompile. Left lazy, that compile happens on user requests after the pod is
already serving — and a type nothing happens to touch stays **"no definition"** until its pages
hang with *"No response received … `SubscribeRequest` → target X"* (SocialMedia/Post on
memex-cloud, 2026-07-30: every post page burned the 60 s timeout all morning while the portal
looked healthy). **Managed envs therefore run the bake ON — these three knobs travel together**
(`values.aks.yaml` carries them; keep them in every env overlay so a `helm upgrade` never
reverts them):

| Knob | What it does |
|---|---|
| `config.memex_portal.PreWarm__DynamicTypes: "true"` | every new pod sweeps + compiles ALL dynamic NodeTypes at start (resumes from the shared `/data` cache — warm restarts are cheap) |
| `config.memex_portal.PreWarm__GateReadiness: "true"` | `/health` stays red until the sweep is green; with `maxSurge 1 / maxUnavailable 0` a regressed type STALLS the rollout with the old image serving. **✅ ON.** It was tried 2026-08-02 and reverted the same day on 7 FALSE regressions — all cross-silo `SubscribeRequest` timeouts, not compile errors (#694 residue). The gate no longer reads "no answer" as "it broke": a `TimedOut` outcome is filed as *unevaluated* and can never gate, and that leniency now survives the cascade (a dependent of an unevaluated upstream is `UpstreamUnevaluated`, also non-gating). Only a `CompileError` — or an `UpstreamFailed` cascading from one — on a **previously-healthy** type stalls a roll |
| `probes.startup: {periodSeconds: 10, failureThreshold: 180}` (= 30 min) | ⚠️ REQUIRED with the gate: a cold bake is **~2.4 s/type**, sequential *(measured 2026-08-10, prod Loki, three portals)* — ~10 min on memex-cloud, the largest mesh — and the default 5 min budget kills the pod mid-bake forever. 30 min is that worst case plus a plain cold boot, x2. `progressDeadlineSeconds` is DERIVED from these two in the chart, so raising them can't leave it behind. **Was `1080` (3 h)** until 2026-08-10, sized from a "~90 s/type" estimate that was 37x too high — a window that long detected nothing |

**🚨 Before you trust the gate, verify the namespace actually reads it.** The gate protects a
portal through exactly two deployment facts, and on 2026-08-10 two of the three portals had drifted
away from both. A gated config in a namespace missing either is *false confidence* — it reports and
the outage happens anyway.

| Must be true | Why | Check |
|---|---|---|
| a `startupProbe` exists **on `/health`** | that probe is the ONLY reader of the gate; no startupProbe (or one on `/alive` / `/healthz`) ignores it entirely | `kubectl -n <ns> get deploy memex-portal-deployment -o jsonpath='{.spec.template.spec.containers[0].startupProbe}'` |
| `strategy.maxUnavailable: 0` | surge-first keeps the old pod serving until the new one passes; at `maxUnavailable:1` with `replicas:1` the only serving pod can be deleted BEFORE the replacement is ready | `kubectl -n <ns> get deploy memex-portal-deployment -o jsonpath='{.spec.strategy.rollingUpdate}'` |

Both are rendered correctly by the chart — a `helm upgrade` restores them. A per-env `portal-patch.json`
or a hand `kubectl patch` can still override them, which is how the drift happened.

🪦 **There is no pre-run bake Job any more, and there never can be one (#1347).** The separate
`memex-bake` image was removed after two weeks of running in zero namespaces. On its only AKS run
(memex-cloud, 2026-07-30) `memex-bake:3.0.0-ci.1565` computed a **different framework fingerprint**
than the running `portal-ai:3.0.0-ci.1565` — same version, same commit, separately published — so
its framework-stale kickoff started flipping CURRENT NodeType records to `Pending` and rebuilding
them for a framework nothing serves. That was not a bug to tune: `InformationalVersion` carries
`+build.<UtcNow.Ticks>` under `CIRun` (`Directory.Build.props`, and the stamp is load-bearing ABI
safety), so **every** `dotnet publish` mints a fresh `MeshWeaver.Graph` MVID and no second image can
ever agree with the portal's framework identity.

**The pod-side sweep is the bake.** It runs in the serving process, so its fingerprint is right by
construction, and it is fast — 76 s for 280 types (memex-cloud, batch direct-compile). The "fail
before prod" contract the Job was meant to give is given, and given better, by
`PreWarm__GateReadiness` + `maxSurge:1` / `maxUnavailable:0`: the new pod refuses readiness until
its OWN bake is green while the old image keeps serving.

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

- **A pod sitting 0/1 for a long time is the gate doing its job** while a cold bake runs; only a
  `REFUSING READINESS … Regressions:` log line means a type broke on this image — fix the type,
  never widen the timeout.
- **Retries are cheap — the sweep resumes from the shared cache.** Every type a sweep DID build is
  on `/data` for good; deleting a gate-red pod re-runs only what is missing. Progress across
  attempts is monotonic.
- **⚠️ Known flake while core #694 is open (cross-silo reply routing):** during a roll the mesh
  briefly runs TWO silos (old serving + new baking), and in that window the pod-side sweep's
  shared-source resolution can fail nondeterministically — the symptom is a `CompileError` full of
  unresolved names that live in a SIBLING type's `Source/` (`shared=@…`), on a type that compiles
  clean when triggered individually. First verify with a targeted compile (MCP `compile
  @<type>` — recycle the type's hub first if the subscribe times out; the targeted subscribe rides
  the SAME cross-silo routing, so it completes reliably only in a single-silo window — delete the
  baking pod and use the ~2 minutes before its replacement joins), then let the replacement's
  re-sweep find the fixed types `alreadyBaked`. (Observed live on the first gated roll, memex-cloud
  2026-07-30: the gate caught 2 types with stale-green records whose newest assemblies were 6 days
  old — real, invisible breakage — plus sweep failures on shared-source types that compiled clean
  when triggered individually in a single-silo window.)

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
