---
Name: Local memex on Colima k3s (Mac)
Category: Architecture
Description: Run a prod-like memex portal locally on Colima k3s on an arm64 Mac — native image build, Helm deploy, ingress-nginx + mkcert TLS, Entra OAuth, local Qwen via Ollama, durable access
Icon: Cloud
---

# Running memex locally on Colima k3s (Mac)

This page is a step-by-step guide for standing up a **prod-like memex portal** on a Mac, on a real Kubernetes cluster (k3s) inside [Colima](https://github.com/abiosoft/colima). It exercises the same Helm chart, the same Postgres + ingress + OAuth path that the cloud deployments use — but entirely on your laptop, with a **local LLM** and **trusted local TLS**, no cloud dependency.

## When to use this (vs. the other routes)

| You want | Use | Doc |
|---|---|---|
| Fastest inner loop — edit code, hit a browser, no Docker/k8s | **Monolith** (`dotnet run`) or Aspire local mode | [Deployment.md](/Doc/Architecture/Deployment) → Running Locally · [LocalDevWorkflow.md](/Doc/Architecture/LocalDevWorkflow) |
| A **prod-like** stack on your Mac — real k8s, Helm chart, ingress/TLS, Postgres PVC, OAuth, local LLM | **This page** (Colima k3s) | — |
| Ship a code update to the shared `memex` portal | AKS | [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) |
| Deploy an Aspire `test`/`prod` environment | Azure Container Apps | [DeploymentContainerApps.md](/Doc/Architecture/DeploymentContainerApps) |
| Understand how an install updates itself (policy-driven) | Self-update | [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy) |

> Because this is the **same Helm chart** as AKS, the in-pod self-updater applies here too: a `Continuous` install patches its own deployment to a newer ACR tag (see [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy)). **The ACR images are now multi-arch (linux/amd64 + linux/arm64)** — built that way by CI (see §3) — so on this arm64 VM the self-updater pulls the **native arm64** variant of each new tag and it Just Works. A pure local-build loop (images built straight into Colima's Docker store, never pushed to ACR) has nothing to poll — treat it as `None`.

The Colima k3s route is the closest thing to "prod on your laptop": it runs the **`deploy/helm` chart** (the same chart the AKS environments use), terminates TLS at an ingress controller, authenticates through Microsoft Entra, persists Postgres on a PVC, and survives reboots. The trade-off is build time and a one-time setup. For everyday code iteration, prefer the Monolith / Aspire workflow — reach for Colima k3s when you need to validate the *deployment* shape, ingress/TLS, OAuth redirects, or the self-hosted-LLM path.

> Everything below was set up and verified on an arm64 Mac (Apple Silicon). The defaults — hostname `memex.localhost`, port `8443`, a host-native Ollama — are chosen so the whole thing runs **without sudo** and survives a reboot.

---

## 1. Prerequisites

Install the toolchain via Homebrew:

```bash
brew install colima kubectl helm mkcert
brew install ollama                     # local LLM runtime (runs on the host — see §11)
# socket_vmnet enables Colima's vmnet networking (host-gateway reachability):
brew install socket_vmnet
```

You also need the **.NET SDK** (10.0) to build the portal image — install from [dotnet.microsoft.com](https://dotnet.microsoft.com/download) or `brew install --cask dotnet-sdk`.

> **Prefer one command?** `deploy/homebrew/` ships a Homebrew formula + a `memex-local` CLI that automates **every step on this page**, idempotently — `brew install` the tap, then `memex-local up` (and `down` / `status` / `logs` / `update`). See `deploy/homebrew/README.md`. The rest of this page is the manual reference the CLI follows 1:1.

The work splits across three areas, which the rest of this page walks through in order:

<svg viewBox="0 0 760 250" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L0,7 L8,3.5 Z" fill="#90a4ae"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="250" rx="10" fill="#1a1e2e" opacity="0.7"/>
  <text x="380" y="26" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="12">Browser  →  https://memex.localhost:8443</text>
  <rect x="40" y="44" width="220" height="50" rx="10" fill="#1b3a4b" stroke="#0288d1" stroke-width="1.5"/>
  <text x="150" y="65" text-anchor="middle" fill="#81d4fa" font-size="12" font-weight="bold">launchd port-forward</text>
  <text x="150" y="83" text-anchor="middle" fill="#4fc3f7" font-size="11">8443 → ingress :443</text>
  <line x1="260" y1="69" x2="298" y2="69" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="300" y="44" width="180" height="50" rx="10" fill="#1b2e1b" stroke="#66bb6a" stroke-width="1.5"/>
  <text x="390" y="65" text-anchor="middle" fill="#a5d6a7" font-size="12" font-weight="bold">ingress-nginx</text>
  <text x="390" y="83" text-anchor="middle" fill="#81c784" font-size="11">TLS (mkcert) · k3s</text>
  <line x1="480" y1="69" x2="518" y2="69" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="520" y="44" width="200" height="50" rx="10" fill="#1b2e1b" stroke="#66bb6a" stroke-width="2"/>
  <text x="620" y="65" text-anchor="middle" fill="#a5d6a7" font-size="12" font-weight="bold">memex-portal pod</text>
  <text x="620" y="83" text-anchor="middle" fill="#81c784" font-size="11">arm64 image · Helm</text>
  <rect x="300" y="120" width="180" height="46" rx="10" fill="#2a2440" stroke="#7e57c2" stroke-width="1.5"/>
  <text x="390" y="140" text-anchor="middle" fill="#b39ddb" font-size="12" font-weight="bold">Postgres (PVC)</text>
  <text x="390" y="157" text-anchor="middle" fill="#9575cd" font-size="11">pgvector · persists</text>
  <line x1="620" y1="94" x2="470" y2="120" stroke="#455a64" stroke-opacity="0.6" stroke-width="1" stroke-dasharray="4,3" marker-end="url(#arr)"/>
  <rect x="520" y="120" width="200" height="46" rx="10" fill="#2a1f1a" stroke="#f57c00" stroke-width="1.5"/>
  <text x="620" y="140" text-anchor="middle" fill="#ffb74d" font-size="12" font-weight="bold">Ollama (host)</text>
  <text x="620" y="157" text-anchor="middle" fill="#ffa726" font-size="11">Metal GPU · qwen3.6</text>
  <line x1="620" y1="94" x2="620" y2="120" stroke="#455a64" stroke-opacity="0.6" stroke-width="1" stroke-dasharray="4,3" marker-end="url(#arr)"/>
  <rect x="40" y="120" width="220" height="46" rx="10" fill="#263238" stroke="#455a64" stroke-width="1.5"/>
  <text x="150" y="140" text-anchor="middle" fill="#b0bec5" font-size="12" font-weight="bold">Microsoft Entra</text>
  <text x="150" y="157" text-anchor="middle" fill="#78909c" font-size="11">OAuth · /signin-microsoft</text>
  <text x="380" y="200" text-anchor="middle" fill="currentColor" fill-opacity="0.45" font-size="11">All in-cluster components run on Colima's k3s VM (arm64, 8 CPU / 16 GiB)</text>
  <text x="380" y="222" text-anchor="middle" fill="currentColor" fill-opacity="0.45" font-size="11">Ollama runs on the host so it keeps the Metal GPU (the VM has no GPU passthrough)</text>
</svg>

---

## 2. Start Colima with k3s

Start Colima with the Docker runtime **and** Kubernetes (k3s) enabled, sized for the portal + Postgres + observability stack:

```bash
colima start --kubernetes --cpu 8 --memory 16
```

This brings up a single arm64 VM running both a Docker daemon and a k3s cluster. The key reason to enable k3s *with the Docker runtime* (rather than containerd) is that **k3s then shares Colima's Docker image store** — so an image you build and `docker tag` locally is immediately visible to the cluster's `IfNotPresent` pull policy, with **no registry push**. That is what makes the build loop in §4 fast.

`colima start` writes a kubeconfig context; verify:

```bash
kubectl config current-context     # → colima
kubectl get nodes                  # → one Ready node
```

> k3s here ships **without Traefik** (we install ingress-nginx instead — §6). If you ever recreate the profile, that's expected.

---

## 3. Get the portal image (arm64)

You have two ways to get an arm64 image onto the VM. **CI now publishes multi-arch images**, so for an unmodified portal you can just pull from ACR; build locally only when you're iterating on un-pushed source changes.

### Option A — pull the multi-arch image from ACR (no local build)

The CONTINUOUS (`main-cd.yml`) and RELEASE (`release-images.yml`) pipelines build `memex-portal-ai`, `memex-portal`, and `memex-migration` as **multi-arch manifest lists** (`linux/amd64` + `linux/arm64`) via the .NET SDK's `ContainerRuntimeIdentifiers="linux-x64;linux-arm64"` (an OCI image index — supported since SDK 8.0.405, and we build on .NET 10). The hand-authored base `memex-portal-ai-base` is likewise built multi-arch with `docker buildx --platform linux/amd64,linux/arm64`. So on this arm64 VM, Docker/k3s pulls the **native arm64** variant automatically — no emulation, and the in-pod self-updater (the blockquote in the intro) can roll forward to new ACR tags on its own.

> **🚨 This only holds for genuinely multi-arch tags.** Tags built before the multi-arch CI change (and any tag hand-built single-arch) are **amd64-only**; run emulated on the arm64 VM they make .NET's `ConfigurationBinder` throw a spurious `NullReferenceException` (`InvokeStub_GraphStorageConfig.get_ConnectionString`) that crashes the portal on startup. Verify a tag is multi-arch before relying on it:
> ```bash
> docker manifest inspect meshweaver.azurecr.io/memex-portal-ai:latest \
>   | grep -A1 '"architecture"'        # expect both "amd64" and "arm64"
> ```
> **One-time operator step:** the very first multi-arch roll needs the base rebuilt multi-arch *before* the app build (the app's arm64 leg has no base layer otherwise). `release-images.yml` does this on the next `v*.*.*` tag; to do it by hand into ACR: `az acr build --registry meshweaver --image memex-portal-ai-base:latest --platform linux/amd64 --platform linux/arm64 deploy/base-images/portal-ai` (or `docker buildx ... --push`).

### Option B — build natively (fast inner loop for un-pushed edits)

When you've changed source that isn't in any pushed tag, build straight into Colima's Docker store. The verified rebuild loop (a few minutes on an M-series Mac):

```bash
# 1. Publish a native arm64 container image straight into Colima's Docker store.
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj -c Release \
  -t:PublishContainer -p:ContainerRepository=memex-portal-ai-local

# 2. Tag it to the name the chart expects (IfNotPresent then finds it locally).
docker tag memex-portal-ai-local:latest ghcr.io/systemorph/memex-portal-ai:latest

# 3. Roll the deployment so the pod picks up the new image.
kubectl rollout restart deploy/memex-portal-deployment -n memex

# 4. Restart the port-forward (a rollout invalidates the old pod binding — see §8/Troubleshooting).
```

Do the same for the migration image if you changed schema/migrations:

```bash
dotnet publish memex/aspire/Memex.Database.Migration/Memex.Database.Migration.csproj -c Release \
  -t:PublishContainer -p:ContainerRepository=memex-migration-local
docker tag memex-migration-local:latest ghcr.io/systemorph/memex-migration:latest
```

Notes:

- The local build uses the **default** `mcr.microsoft.com/dotnet/aspnet:10.0` base image, **not** the custom `memex-portal-ai-base` (that base bundles Node / Claude Code / Copilot, is now multi-arch in ACR, and is unneeded for the local inner loop).
- Because k3s runs the Docker runtime (`docker://`), the retag is visible to the cluster immediately — there is no `docker push` / registry step.
- This is the only step that is slow. Once the image exists, config-only changes (§5) don't need a rebuild.

---

## 4. Deploy via Helm

The chart lives at **`deploy/helm`**. It is *neutral by default* — every self-host-only feature (ingress, external Ollama, instance identity) is **off** unless an overlay turns it on. Keep your machine-specific secrets and toggles in a **local overlay outside the repo** so nothing sensitive is ever committed:

```bash
mkdir -p ~/.memex-local
# Generate strong secrets once and write them into the overlay (do this with your
# own values — examples shown as placeholders).
```

A minimal `~/.memex-local/values.local.yaml` looks like this (placeholders — fill in your own generated secrets and OAuth values):

```yaml
secrets:
  memex_postgres:
    memex_postgres_password: "<generated-pg-password>"
  memex_migration:
    ConnectionStrings__memex: "Host=memex-postgres-service;Port=5432;Database=memex;Username=postgres;Password=<generated-pg-password>"
    memex_postgres_password: "<generated-pg-password>"
  memex_portal:
    ConnectionStrings__memex: "Host=memex-postgres-service;Port=5432;Database=memex;Username=postgres;Password=<generated-pg-password>"
    memex_postgres_password: "<generated-pg-password>"
    Ai__KeyProtection__MasterKey: "<generated-ai-master-key>"
    Authentication__Microsoft__ClientSecret: "<entra-client-secret>"   # see §10

config:
  memex_portal:
    Authentication__Provider: "Microsoft"
    Authentication__Microsoft__ClientId: "<entra-client-id>"
    Authentication__Microsoft__TenantId: "<entra-tenant-id>"
    Portal__InstanceName: "Local"           # tab title + favicon badge — see §13
    Portal__InstanceColor: "#f59e0b"
    OpenAICompatible__Endpoint: "http://ollama:11434/v1"   # local LLM — see §11
    OpenAICompatible__Models__0: "qwen3.6-code"
    OpenAICompatible__ApiKey: "ollama"

ingress:
  enabled: true                  # see §6
  host: "memex.localhost"
  tlsSecret: "memex-portal-tls"

ollama:
  external:
    enabled: true                # see §11
    host: "192.168.5.2"          # Colima's host-gateway IP
    port: 11434
```

Install (or upgrade) the release into the `memex` namespace, layering the local overlay on top of the chart defaults:

```bash
helm upgrade --install memex deploy/helm \
  -f deploy/helm/values.yaml \
  -f ~/.memex-local/values.local.yaml \
  -n memex --create-namespace
```

The chart wires up everything the portal needs to come up cleanly:

- **Postgres** runs as a StatefulSet (`pgvector/pgvector:pg17`) with a **10 Gi PVC** (`volumeClaimTemplates`), so your data survives pod restarts and reboots.
- A **`wait-for-postgres` initContainer** on the portal pod (`busybox` + `nc -z memex-postgres-service 5432`) gates portal startup on Postgres TCP readiness — fixing the portal-vs-Postgres startup race on a fresh install.
- The **migration** job runs the schema migrations; the portal then boots against the migrated DB.

> **ConfigMap changes don't restart pods.** After a `helm upgrade` that only changed `config` values, run `kubectl rollout restart deploy/memex-portal-deployment -n memex` for the new env to take effect. Secret/image changes that the chart templates as pod-spec changes do trigger a rollout on their own.

---

## 5. HTTPS via ingress-nginx + mkcert

k3s here has no Traefik, so install **ingress-nginx** once:

```bash
helm repo add ingress-nginx https://kubernetes.github.io/ingress-nginx
helm upgrade --install ingress-nginx ingress-nginx/ingress-nginx \
  -n ingress-nginx --create-namespace \
  --set controller.service.type=ClusterIP \
  --set controller.admissionWebhooks.enabled=false
```

This registers the `nginx` IngressClass. The chart's `deploy/helm/templates/memex-portal/ingress.yaml` (gated on `ingress.enabled`) then creates a standard `Ingress` that terminates TLS for `ingress.host` using `ingress.tlsSecret` and proxies HTTP to `memex-portal-service`. The `proxy-read-timeout` / `proxy-send-timeout` annotations (3600 s) keep the Blazor Server SignalR WebSocket circuit alive. nginx sets `X-Forwarded-Proto: https` / `X-Forwarded-Host`, and the portal clears `KnownProxies`/`KnownIPNetworks` so it trusts them and builds the correct `https://memex.localhost:8443/signin-microsoft` redirect URI.

Mint a locally-trusted certificate with **mkcert** and load it as the TLS secret the ingress references:

```bash
mkcert -install                                    # one-time: trust the mkcert CA in the system keychain
mkdir -p ~/.memex-local
mkcert -cert-file ~/.memex-local/memex.localhost.pem \
       -key-file  ~/.memex-local/memex.localhost-key.pem \
       "memex.localhost" "*.memex.localhost"

# (Re)create the secret in the memex namespace — idempotent apply:
kubectl create secret tls memex-portal-tls -n memex \
  --cert="$HOME/.memex-local/memex.localhost.pem" \
  --key="$HOME/.memex-local/memex.localhost-key.pem" \
  --dry-run=client -o yaml | kubectl apply -f -
```

`mkcert -install` adds the mkcert root CA to the macOS System keychain, so Safari/Chrome show a trusted padlock with no warning. Re-run the `create secret … | kubectl apply` command any time the certificate changes.

---

## 6. Hostname & access

The default hostname is **`memex.localhost`**, accessed at **`https://memex.localhost:8443`**.

- **`*.localhost` auto-resolves to loopback on macOS** — `dscacheutil -q host -a name memex.localhost` returns `127.0.0.1`, so **no `/etc/hosts` entry is needed**. This is why `memex.localhost` is the default.
- A **custom single-label name** (e.g. just `memex`) does **not** auto-resolve and needs an explicit hosts entry:
  ```bash
  echo "127.0.0.1 memex" | sudo tee -a /etc/hosts
  ```
- The mkcert certificate above covers `memex.localhost` (and `*.memex.localhost`), **not** plain `localhost` — so prefer `https://memex.localhost` for a green padlock.

Why **8443** and not 443: port 443 is privileged on macOS, so a `kubectl port-forward … 443:443` needs `sudo` — which breaks the no-sudo launchd auto-start (§8). 8443 is unprivileged, so the durable login agent can bring it up automatically. (See §9 for an optional clean `:443` setup.)

---

## 7. Durable access (survives reboot)

Because 8443 is unprivileged, a **launchd login agent** can keep `https://memex.localhost:8443` available with no sudo, automatically after every reboot.

`~/.memex-local/port-forward.sh` does three things in order:

```bash
#!/bin/bash
# 1. Start Colima if it isn't running (brings the k3s cluster + portal back).
colima status >/dev/null 2>&1 || colima start
# 2. Wait until the ingress-nginx namespace/controller is ready.
until kubectl get ns ingress-nginx >/dev/null 2>&1; do sleep 2; done
# 3. Forward 8443 on the host to the ingress controller's :443.
exec kubectl port-forward -n ingress-nginx svc/ingress-nginx-controller 8443:443
```

`~/Library/LaunchAgents/com.memex.local.plist` runs that script with `RunAtLoad` + `KeepAlive` (it restarts the forward if it ever drops), logging to `~/.memex-local/port-forward.log`. Manage it with:

```bash
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.memex.local.plist   # load + start
launchctl bootout   gui/$(id -u) ~/Library/LaunchAgents/com.memex.local.plist   # stop + unload
```

After a reboot, logging in starts Colima (the portal `replicas=1` + the Postgres PVC persist on the VM disk) and re-establishes the port-forward — so `https://memex.localhost:8443` comes back on its own.

---

## 8. Optional: a clean `:443`

If you want the URL without the `:8443` suffix, forward the privileged port 443. Because that needs root, use a **root LaunchDaemon** in `/Library/LaunchDaemons/` (not a user LaunchAgent) running essentially the same `kubectl port-forward -n ingress-nginx svc/ingress-nginx-controller 443:443`. Installing a LaunchDaemon requires `sudo` (it runs as root at boot, before login). This does **not** require restarting Colima. Prefer `https://memex.localhost` (no port) here — both the mkcert cert and the Entra redirect URI are valid for it.

> This is optional convenience only. The 8443 path (§7) is the recommended default because it needs no sudo.

---

## 9. Auth — Microsoft Entra OAuth

The Distributed portal authenticates via **Microsoft Entra OAuth** (the callback path is `/signin-microsoft`, set in `AuthenticationBuilderExtensions.cs`). Create a **dedicated app registration** for local dev (so its redirect URIs don't collide with the cloud apps):

1. Azure Portal → **App registrations** → **New registration** (or reuse a dedicated local-dev app).
2. Under **Authentication → Web → Redirect URIs**, register the local callbacks. Register both the no-port (443) and `:8443` forms so either port works:

   | Redirect URI |
   |---|
   | `https://memex.localhost:8443/signin-microsoft` |
   | `https://memex.localhost/signin-microsoft` |

3. Note the **Application (client) ID** and **Directory (tenant) ID** from the Overview page → put them in your overlay (`Authentication__Microsoft__ClientId` / `__TenantId`).
4. Under **Certificates & secrets**, create a client secret → put it in the overlay's `secrets.memex_portal.Authentication__Microsoft__ClientSecret`.

### The SameSite=None → Secure cookie fix

OAuth over `http://localhost` originally failed with `/login?error=auth_failed` and a portal log line `AuthenticationFailureException: Correlation failed` (`.AspNetCore.Correlation.* cookie not found`). Root cause: the OIDC **correlation + nonce cookies are `SameSite=None`** (the Microsoft callback is a cross-site `form_post`), and browsers **drop a `SameSite=None` cookie that isn't also `Secure`**. The handler's default `SecurePolicy = SameAsRequest` left them non-Secure over plain HTTP, so they were never stored.

The fix in `src/MeshWeaver.Blazor.Portal/Authentication/AuthenticationBuilderExtensions.cs` (`AddMicrosoftAuthentication`) forces them Secure:

```csharp
options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;
options.NonceCookie.SecurePolicy = CookieSecurePolicy.Always;
```

Browsers make a **localhost exception** (they accept `Secure` cookies over `http://localhost`), so login works even without TLS, and it is a no-op in prod (already HTTPS). This is the security-correct setting everywhere.

---

## 10. Local LLM — Qwen via Ollama

The local LLM is wired through the chart's **`OpenAICompatible`** provider. The wrinkle on macOS is the **GPU**: Colima's arm64 VM has no Metal passthrough, so a containerized Ollama would run CPU-only. Therefore **Ollama runs on the host** (keeping the Metal GPU) and is exposed to the cluster by a stable in-cluster name.

The chart's `deploy/helm/templates/ollama/service.yaml` (gated on `ollama.external.enabled`) creates a **selector-less `Service ollama` + a manual `Endpoints`** object pointing at the host gateway (`ollama.external.host`, e.g. Colima's `192.168.5.2:11434`). The portal then addresses Ollama by stable name instead of a brittle hardcoded IP:

```text
OpenAICompatible__Endpoint = http://ollama:11434/v1
OpenAICompatible__Models__0 = qwen3.6-code
OpenAICompatible__ApiKey    = ollama
```

Start Ollama on the host, bound to all interfaces so the VM's host-gateway can reach it, and alias the model to the id the config expects:

```bash
OLLAMA_HOST=0.0.0.0:11434 ollama serve            # bind to 0.0.0.0 so the cluster can reach it
ollama pull qwen3.6                                # the Ollama-library model
ollama cp qwen3.6 qwen3.6-code                     # alias to the id used by OpenAICompatible__Models__0
```

> k3s v1.35 logs a deprecation warning for `Endpoints` (in favor of `EndpointSlice`), but the mirroring controller auto-creates the slice, so routing works.

---

## 11. Observability

Install the Grafana Loki stack into a `monitoring` namespace (Grafana + Loki + Promtail + Prometheus) — the same stack `deploy/aks/scripts/install-observability.sh` uses:

```bash
helm repo add grafana https://grafana.github.io/helm-charts
helm upgrade --install loki grafana/loki-stack \
  -n monitoring --create-namespace \
  --set grafana.enabled=true,prometheus.enabled=true,promtail.enabled=true
```

Port-forward Grafana to view dashboards/logs; the admin password is in `~/.memex-local/grafana-password.txt`.

---

## 12. Instance identity (tab title + favicon badge)

So a "Local" tab is unmistakable next to Test/Prod, the portal supports two optional config keys, set in the local overlay and allow-listed in the chart's `config.yaml`:

| Key | Effect |
|---|---|
| `Portal__InstanceName` | Browser-tab **title** becomes this name, and the favicon becomes a distinct **colored badge** showing the name's initial. Empty (prod) = default Memex branding. |
| `Portal__InstanceColor` | Badge fill color (hex, e.g. `#f59e0b`). Empty = amber default. |

These are implemented in `memex/Memex.Portal.Shared/App.razor`: when `Portal:InstanceName` is set it emits an inline-SVG data-URI favicon (a rounded-square badge with the initial) and a small `MutationObserver` that pins the tab title to the instance name. Unset → the standard `favicon.ico` and `Memex Portal` title. Set `Portal__InstanceName: "Local"` in your overlay so your local tab is obvious at a glance.

---

## 13. Optional (advanced): home-wide access with a real cheap domain

This is an **optional, advanced** recipe — skip it unless you want every device on your home network (phones, other laptops) to reach the portal over **trusted HTTPS with no per-device CA install**. The mkcert approach (§5) only trusts the Mac that ran `mkcert -install`; a real Let's Encrypt certificate is trusted everywhere automatically.

The shape (a plan, not exact secrets):

1. **Buy a cheap domain** and create an `A` record `memex.<yourdomain>` → your Mac's **LAN IP**. Pin that IP with a **DHCP reservation** on your router so it doesn't change.
2. **Install cert-manager** into k3s (`helm upgrade --install cert-manager jetstack/cert-manager --set crds.enabled=true`).
3. **Issue a real wildcard cert via DNS-01.** Create a `ClusterIssuer` (Let's Encrypt ACME) with a **DNS-01 solver** for your DNS provider (e.g. Cloudflare API token in a secret), then a `Certificate` requesting `*.<yourdomain>` / `memex.<yourdomain>`. DNS-01 only needs cert-manager to write a TXT record — it works **without ever exposing the cluster to the internet**.
4. **Point the ingress at the issued secret** — set `ingress.host` to `memex.<yourdomain>` and `ingress.tlsSecret` to the Certificate's secret. Bind the ingress port-forward / service to the **LAN** interface so other devices can reach it.

The result: every device trusts the cert out of the box (no mkcert CA install), and the portal is reachable on the home network by a real name — all without any inbound internet exposure. Treat the provider tokens and the ClusterIssuer email as secrets kept outside the repo.

---

## 14. Troubleshooting

| Symptom | Cause & fix |
|---|---|
| Portal crashes on startup with `NullReferenceException` in `ConfigurationBinder` / `get_ConnectionString` | You're running an **amd64-only image emulated** on the arm64 VM — i.e. a pre-multi-arch (or hand-built single-arch) tag. Confirm with `docker manifest inspect …` (§3 Option A) that the tag carries an `arm64` entry; if not, pull a multi-arch tag or build natively (§3 Option B). |
| `/login?error=auth_failed`; log shows `Correlation failed` / correlation cookie not found | The `SameSite=None` correlation/nonce cookies were dropped because they weren't `Secure`. Ensure you're on the build with the `SecurePolicy = Always` fix (§9). Verify: `curl -i http://localhost:8080/auth/login?provider=Microsoft` shows `secure; samesite=none` on both Set-Cookie lines. |
| Page returns empty reply / HTTP 000 after a portal rollout | The `kubectl port-forward` binds **one pod**; a `rollout restart` replaces it and the old forward goes stale. **Restart the port-forward** (or let the launchd agent's `KeepAlive` do it). |
| A route 404s for ~1 second right after a Helm upgrade or ingress patch | ingress-nginx **reload lag** — the controller is reloading its config. Retry; it clears within a second. |
| OAuth redirect URI mismatch | The redirect URI the portal built must exactly match one registered on the app (§9). Check you're hitting the host/port whose `/signin-microsoft` is registered. |
| Ollama unreachable from the portal | Ollama must be started with `OLLAMA_HOST=0.0.0.0:11434` (not the default loopback bind), and `ollama.external.host` must be Colima's host-gateway IP (`192.168.5.2`). |

**Verify end-to-end** that TLS + routing + the app are all working:

```bash
curl --cacert "$(mkcert -CAROOT)/rootCA.pem" \
  -sS -o /dev/null -w 'http=%{http_code} ssl_verify=%{ssl_verify_result}\n' \
  https://memex.localhost:8443/
# Expect: http=200  (ssl_verify_result 0 once `mkcert -install` has trusted the CA)
```

---

## 15. Playwright E2E test env (`memex-local e2e`)

Browser E2E (Playwright) needs a portal it can **log into without Entra** (DevLogin) that **also has a real language model**. The dev Monolith has DevLogin but no model; this `memex` stack has the model but uses Entra OAuth and holds your real data. `memex-local e2e` stands up a **throwaway, DevLogin portal** built from the **current working tree**, in the **same namespace**, reusing this stack's Postgres, host Ollama and ingress/TLS — but against its **own** database (`memex_e2e`) with DevLogin on, behind the ingress (reverse proxy) at `https://e2e.memex.localhost:8444`. It is **additive**: it never touches the `memex` release, DB, or config.

```bash
memex-local up                              # the base stack (PG, Ollama, ingress, TLS) — once
memex-local e2e up                          # build working tree → create memex_e2e → migrate → deploy → reverse-proxy
memex-local e2e test HomeChatExecuteTest    # run the Playwright E2E (E2E_BASE_URL + DevLogin preset)
memex-local e2e down                        # delete the e2e objects + drop the e2e DB (--keep-db to keep it)
```

What `e2e up` does, and why:

| Step | Why |
|---|---|
| Build portal + migration image (native arm64) from the working tree | Test the code you're holding, not a stale image. `--skip-build` reuses the last build. |
| `CREATE DATABASE memex_e2e` in the existing `memex-postgres` | Your own data — never the `memex` DB. |
| Run the migration `Job` against it | The portal's `DbVersionGate` refuses to start against an un-migrated DB. |
| Deploy `memex-e2e-portal` (Deployment + Service + Ingress) reusing `memex-portal-config`/`-secrets` via `envFrom` | Proven config; override **only** `ConnectionStrings__memex` → `memex_e2e` and `Authentication__EnableDevLogin=true`. Clustering stays `Localhost` (own in-process silo). `/data` is an ephemeral `emptyDir` (mesh data is in PG). |
| Ingress for `e2e.memex.localhost` (covered by the `*.memex.localhost` mkcert cert) + a `:443→:8444` port-forward | The reverse proxy Playwright drives. |

`PortalFixture` authenticates via `POST /dev/signin?personId=Roland` and sets `IgnoreHTTPSErrors=true`, so the self-signed cert is fine. The repeatable flow is captured as the `/playwright` skill. **Always deploy on Colima and drive THAT — never run a model E2E against the Monolith (no model) or the `memex` portal (Entra + real data).**

---

## Related

- [Deployment.md](/Doc/Architecture/Deployment) — the deploy-route index (AKS vs Container Apps) and shared Azure AD / secrets setup.
- [DeploymentAKS.md](/Doc/Architecture/DeploymentAKS) — the cloud counterpart that uses the same `deploy/helm` chart shape.
- [LocalDevWorkflow.md](/Doc/Architecture/LocalDevWorkflow) — the faster Aspire/Monolith inner loop for everyday code iteration.
- [ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling) — why all the portal's I/O (including Postgres) goes through the I/O pool, never `Observable.FromAsync`.
