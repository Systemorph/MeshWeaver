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

```bash
# Tap this repo and install the HEAD formula:
brew tap systemorph/memex https://github.com/Systemorph/MeshWeaver.git
brew install --HEAD systemorph/memex/memex-local

# …or install straight from a local checkout of this repo:
brew install --HEAD ./deploy/homebrew/Formula/memex-local.rb
```

The formula declares the **exact toolchain `LocalColimaMac.md` §1 installs**:
`colima`, `kubernetes-cli` (kubectl), `helm`, `mkcert`, `ollama`, `socket_vmnet`,
plus the **`dotnet-sdk` cask** (needed only for the local-build image path,
Option B). It vendors a snapshot of `deploy/helm` into the bottle so a standalone
install works; a live `MEMEX_REPO`/`MEMEX_CHART_DIR` always overrides it.

## Use

```bash
memex-local up                 # full stack, idempotent (default: local arm64 build, Option B)
memex-local up --from-acr      # pull the multi-arch image from ACR instead (Option A)
memex-local status             # colima / pods / ingress / port-forward / health
memex-local logs               # tail the portal logs (verbose; see "Full logging")
memex-local logs --migration   # tail the migration job
memex-local update             # roll portal+migration to a newer image, refresh forward
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
| Self-update (ReleaseStrategy) | in-pod patch per `Admin/UpdatePolicy` (Continuous default) | `cmd_update()` is the manual path; chart ships the RBAC + self-updater |

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
