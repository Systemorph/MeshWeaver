# memex-local — Homebrew installer for local memex on Colima k3s (Mac)

A `brew`-installable CLI that stands up the **prod-like memex stack on Colima k3s**,
**1:1 with [`LocalColimaMac.md`](../../src/MeshWeaver.Documentation/Data/Architecture/LocalColimaMac.md)**
— same defaults, same topology, no substitutions. It *orchestrates*
`colima` / `helm` / `kubectl` / `mkcert` / `ollama`; it does **not** reimplement
the chart. The [`deploy/helm`](../helm) chart + values stay the single source of
truth.

> Why: the manual doc has ~14 steps across colima, image build, ingress, mkcert,
> Helm, Ollama, and a launchd port-forward. This packages all of it behind
> `memex-local up` (idempotent) and `memex-local down`.

## Install

The default (Option B) image path builds the portal locally, so **you need a
checkout anyway** — and the CLI is designed to run straight from it. That's the
supported route today:

```bash
# Run straight from the checkout (resolves the chart + share assets from the repo):
./deploy/homebrew/bin/memex-local up

# …or put it on PATH via a symlink (a `git pull` then updates CLI + chart together):
ln -s "$PWD/deploy/homebrew/bin/memex-local" ~/.local/bin/memex-local   # (~/.local/bin on PATH)
memex-local up
```

**Brew route (needs a local tap for now).** Current Homebrew discovers formulae
only at a tap's root or top-level `Formula/`; this formula lives at the nested
`deploy/homebrew/Formula/`, so `brew install ./…/memex-local.rb` and a two-arg tap
of the monorepo are both rejected, and `systemorph/memex` isn't a published tap
yet. Until `Systemorph/homebrew-memex` exists, build a local tap from the checkout:

```bash
brew tap-new systemorph/memex
cp deploy/homebrew/Formula/memex-local.rb "$(brew --repo systemorph/memex)/Formula/"
brew install --HEAD systemorph/memex/memex-local
```

The formula declares the **exact brew toolchain `LocalColimaMac.md` §1 installs**:
`colima`, `kubernetes-cli` (kubectl), `helm`, `mkcert`, `ollama`, `socket_vmnet`.
The **.NET SDK** (10.0) is *not* a formula dependency (`depends_on cask:` is rejected
by current Homebrew, and only the local-build path needs it) — install it separately
for Option B (`brew install --cask dotnet-sdk` or the standalone installer); the
`--from-acr` path (Option A) needs no SDK. The formula vendors a snapshot of
`deploy/helm` into the install so a brew install works standalone; that vendored
snapshot is refreshed by `brew reinstall`. In **run-from-checkout** mode the live
`deploy/helm` (or `MEMEX_REPO`/`MEMEX_CHART_DIR`) is used directly.

## Use

```bash
memex-local up                 # full stack, idempotent (default: local arm64 build, Option B)
memex-local up --from-acr      # pull the multi-arch image from ACR instead (Option A)
memex-local status             # colima / pods / ingress / port-forward / health
memex-local logs               # tail the portal logs (verbose; see "Full logging")
memex-local logs --migration   # tail the migration job
memex-local update             # roll portal+migration to a newer image, refresh forward
memex-local autoroll up        # AUTO-redeploy portal+migration whenever a fresh image is built
memex-local autoroll status    # show the watcher + last-rolled portal/migration digests
memex-local autoroll down      # stop auto-rolling
memex-local observability      # Grafana/Loki/Promtail/Prometheus (§11)
memex-local doctor             # preflight: tools, chart/assets, cluster
memex-local down               # uninstall release (KEEPS the Postgres PVC)
memex-local down --purge       # also delete namespace + PVC (data loss) + ingress-nginx
```

Open **https://memex.localhost:8443** once `up` finishes.

The **default image path is Option B** (build native arm64 straight into Colima's
Docker store — the *no-cloud-creds* path the doc leads with for un-pushed source),
which needs the MeshWeaver source: set `MEMEX_REPO` to your checkout, or run from
a checkout. **Option A** (`--from-acr`) pulls the now-multi-arch image from
`meshweaver.azurecr.io` (run `az acr login -n meshweaver` first).

### First-run secrets & OAuth

On first `up`, the CLI generates `~/.memex-local/values.local.yaml` from the
template in [`share/values.local.yaml`](share/values.local.yaml), substituting a
freshly generated Postgres password and AI master key. **Edit that file** to fill
in your Microsoft Entra `ClientId` / `TenantId` / `ClientSecret` (`LocalColimaMac.md`
§9), or uncomment `Authentication__EnableDevLogin` for a no-Azure login. Re-run
`memex-local up` to apply.

## Auto-update (auto-roll) — the local equivalent of AKS self-update

On AKS the portal self-updates by polling ACR and patching its own Deployment
(`SelfUpdateHostedService`, per [`ReleaseStrategy.md`](../../src/MeshWeaver.Documentation/Data/Architecture/ReleaseStrategy.md)).
That in-pod poll **cannot work locally** — the local images are built straight into
Colima's Docker store, never pushed to a registry the pod can list (the in-pod
`AcrTagLister` would fail the Azure→ACR token exchange). So locally the same
*outcome* is driven from the **host** instead:

```bash
memex-local autoroll up        # arm it (once)
```

This installs a launchd agent (`com.memex.local.autoroll`) that, every 30s:

1. **Pins the portal Deployment to the moving tag** `…/memex-portal-ai:latest`
   (`imagePullPolicy: IfNotPresent` — k3s reuses the Docker-store image, no pull).
2. Watches the **portal** *and* **migration** `…-local:latest` image digests.
   When a build changes one, it first **retags** that fresh `…-local:latest` onto
   the chart ref the cluster actually runs (`…/memex-portal-ai:latest`,
   `…/memex-migration:latest`) — so even a bare `dotnet publish` (which only writes
   `…-local:latest`) goes live — then:
   - **migration first** — re-runs the migration as a one-shot Job against the
     main DB and **waits for it** (the portal's `DbVersionGate` blocks boot until
     the schema is current; if the migration fails it leaves everything on the
     current images and retries — it never rolls the portal onto a schema it
     can't run);
   - then **`rollout restart`** the portal so the rebuilt image goes live.

So after `autoroll up`, **any rebuild** — `memex-local update`, `memex-local up
--build`, or a bare `dotnet publish -t:PublishContainer` of the portal/migration
— auto-deploys within ~30s, with **no manual `kubectl set image`** (the retag in
step 2 is what makes the bare-`publish` case work — without it the roll would
re-run the old chart-ref image).

> **Do you need to install anything first?** No. `autoroll` uses **launchd**
> (built into macOS) plus `docker` + `kubectl`, which are already `memex-local`
> prerequisites (`LocalColimaMac.md` §1). Nothing new to install.

`autoroll status` shows whether the watcher is running and the last-rolled portal
and migration digests; `autoroll down` stops it (the portal stays on its current
image). The log is `~/.memex-local/autoroll.log`. Only the **portal** Deployment is
rolled in place; the **migration** is re-run as a Job (it has no Deployment).

## How each `LocalColimaMac.md` step maps into the CLI

| Doc section | Manual step | CLI implementation (function in `bin/memex-local`) |
|---|---|---|
| §1 Prerequisites | `brew install colima kubectl helm mkcert ollama socket_vmnet` + .NET SDK | Formula `depends_on`; `preflight()` re-checks at runtime |
| §2 Colima k3s | `colima start --kubernetes --cpu 8 --memory 16`; verify context/node | `start_colima()` |
| §3 Image (A) | pull multi-arch from ACR, verify `arm64` in manifest, retag | `image_pull_acr()` (`up --from-acr`) |
| §3 Image (B) | `dotnet publish -t:PublishContainer` portal+migration, `docker tag` to chart name | `image_build_local()` (default) |
| §4 Helm | `helm upgrade --install memex deploy/helm -f values.yaml -f overlay -n memex` | `helm_deploy()` + `ensure_overlay()` |
| §4 Postgres PVC | shipped by the chart (StatefulSet, 10Gi PVC, pgvector pg17) | unchanged — chart is source of truth |
| §5 ingress-nginx | `helm upgrade --install ingress-nginx … ClusterIP, no admission webhooks` | `install_ingress_nginx()` |
| §5 mkcert TLS | `mkcert -install` + mint cert + `kubectl create secret tls memex-portal-tls` | `setup_tls()` |
| §6 Hostname | `memex.localhost` (auto-resolves), port `8443` | constants `HOSTNAME_LOCAL` / `HOST_PORT` |
| §7 Durable access | `port-forward.sh` + `com.memex.local.plist`, `launchctl bootstrap` | `install_launchd()` (assets in `share/`) |
| §10 Local LLM | `OLLAMA_HOST=0.0.0.0:11434 ollama serve`; `ollama pull qwen3.6`; `ollama cp … qwen3.6-code` | `setup_ollama()` + overlay `ollama.external` + `OpenAICompatible__*` |
| §11 Observability | `helm upgrade --install loki grafana/loki-stack …` | `cmd_observability()` |
| §12 Instance identity | `Portal__InstanceName/Color` | overlay `config.memex_portal` |
| §14 Verify | `curl --cacert …/rootCA.pem https://memex.localhost:8443/` | `verify_endpoint()` |
| §14 Troubleshoot (stale forward) | restart the port-forward after a rollout | `cmd_port_forward()`; `update` auto-refreshes it |
| Self-update (ReleaseStrategy) | in-pod patch per `Admin/UpdatePolicy` (Continuous default) — can't see local-built images | `cmd_update()` = manual roll; `cmd_autoroll()` = host-side auto-roll on a new local build (the local stand-in for the in-pod poll; see "Auto-update" above) |

`down` reverses §7 (launchd) and §4 (Helm release) while **retaining the Postgres
PVC** so data survives — exactly the "survives reboot" property the doc emphasises.
`--purge` is the explicit data-wipe.

## Full logging (verbose) — and what is **not** touched

`LocalColimaMac.md` wants logs turned up locally. The chart's portal `ConfigMap`
is a **fixed allow-list** (it does not pass arbitrary `config.memex_portal` keys),
and the chart templates are source-of-truth — we never edit them. So the verbose
level is applied **two complementary ways**:

1. **Declared in the overlay** (`config.memex_portal.Logging__LogLevel__Default:
   "Debug"`) for visibility / intent.
2. **Applied as a deployment-config override** by `apply_logging()` after every
   Helm upgrade: `kubectl set env deployment/memex-portal-deployment
   Logging__LogLevel__Default=$MEMEX_LOG_LEVEL`. A container `env` entry **wins
   over `envFrom`**, and it's idempotent (same value → no rollout).

Set `MEMEX_LOG_LEVEL=Trace` for maximum verbosity. Logs are one command away:
`memex-local logs` (portal), `--migration`, `--postgres`.

> 🚨 This installer **never edits any committed `src/**/appsettings*.json`** — per
> `AGENTS.md` those are production contract. Verbose logging is purely a runtime
> deployment-config override on your local cluster.

## Postgres (durable)

Postgres is the chart's in-cluster `StatefulSet` (`pgvector/pgvector:pg17`) on a
**10Gi PVC** via `volumeClaimTemplates` — unchanged. `down` keeps it; only
`down --purge` deletes it. The password is generated once into the overlay and
reused across the migration/portal secret groups (the chart builds the connection
strings from it).

## Files in this directory

| Path | What |
|---|---|
| `Formula/memex-local.rb` | Homebrew formula (deps + install + caveats + test) |
| `bin/memex-local` | the orchestration CLI (all subcommands) |
| `share/values.local.yaml` | local Helm overlay template (full logging + pg + ingress + ollama + instance id) |
| `share/port-forward.sh` | the §7 port-forward script (started by launchd) |
| `share/com.memex.local.plist` | the §7 launchd login-agent template |

State lives in `~/.memex-local/` (overlay, mkcert cert/key, port-forward script +
log, ollama log, grafana password) — exactly the paths the doc uses.

## Environment overrides

| Var | Default | Purpose |
|---|---|---|
| `MEMEX_REPO` | auto-detected from checkout | repo root for chart + local build |
| `MEMEX_CHART_DIR` | `$MEMEX_REPO/deploy/helm` or vendored copy | explicit chart dir |
| `MEMEX_LOG_LEVEL` | `Debug` | verbose log level (`Trace` for max) |
| `MEMEX_COLIMA_CPU` / `MEMEX_COLIMA_MEM` | `8` / `16` | VM sizing (§2) |
| `MEMEX_PORT` | `8443` | host port for the forward (§6/§7) |
| `MEMEX_LOCAL_HOME` | `~/.memex-local` | state directory |

## Validation & what must be verified on a real Mac

Validated here (syntax / correct-by-construction):

- `ruby -c Formula/memex-local.rb` — formula parses.
- `bash -n bin/memex-local share/port-forward.sh` — scripts parse.
- `shellcheck` clean (if installed).
- Every `LocalColimaMac.md` manual step traces to a CLI function (table above).

Cannot be run in this environment — verify on a real Apple-Silicon Mac:

- `brew install` of the formula (needs Homebrew + network).
- `colima start --kubernetes` (needs virtualization + `socket_vmnet`).
- `mkcert -install` (writes the CA to the **System keychain** — interactive auth).
- `ollama serve` + `ollama pull qwen3.6` (downloads the model; needs the Metal GPU
  for performance).
- The local-build image path (`dotnet publish -t:PublishContainer`) and/or the
  ACR pull (`az acr login`).
- The end-to-end `curl https://memex.localhost:8443/` returning `http=200`.
