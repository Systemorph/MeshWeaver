---
NodeType: Markdown
Name: "Deployment Options (AKS)"
Abstract: "What can actually be done to configure and ship the memex.systemorph.com AKS deployment: live mesh-node config (no redeploy), the default static catalog via Helm, Key Vault + CSI secrets, code changes via CI-built images, and how to reach a PRIVATE AKS API server. Includes the master-key caveat that can silently break encrypted keys."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#0d47a1'/><path d='M12 3l7 4v6c0 4-3 6-7 8-4-2-7-4-7-8V7z' fill='none' stroke='white' stroke-width='1.8'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Deployment"
  - "AKS"
  - "Key Vault"
  - "AI"
---

# Deployment Options (AKS)

How to change what runs at **memex.systemorph.com**, from "instant, no redeploy" to "full image release". Each option below is independent — pick the lightest one that does the job. This page exists because several of the options have non-obvious constraints (a *private* API server, CI-only image builds, and a master key that must not be overwritten).

## The environment, in one table

| Fact | Value | Consequence |
|---|---|---|
| Cluster | `memexaks-cluster` / rg `memex-aks-rg` (Sweden Central) | — |
| API server | **private** (`…privatelink…azmk8s.io`) | `kubectl`/`helm` from outside the VNet **cannot reach it** — use `az aks command invoke` or an in-VNet runner/VPN |
| Key Vault CSI | `azureKeyvaultSecretsProvider` add-on **enabled** (identity clientId `6c9dcc8d-…`) | secrets can come from Key Vault, keyless |
| Key Vault | `Systemorph` (`https://systemorph.vault.azure.net`) | holds `AzureFoundry-ApiKey` (set) |
| Images | `ghcr.io/systemorph/memex-portal-ai:latest` (+ lean `memex-portal`, `memex-migration`) | built **only by CI** (`release-images.yml`) on a `v*.*.*` **tag push** |
| portal-ai base | bakes `@anthropic-ai/claude-code` + `@github/copilot` CLIs | the `claude` CLI is present in the running pod |
| AI model picker | fed by `ModelProvider` / `LanguageModel` **mesh nodes** | see [Setting Up Model Providers](/Doc/AI/ModelProviderSetup) |

## Option A — Live config via mesh nodes (no redeploy) ✅ used now

The model picker reads `ModelProvider` + `LanguageModel` mesh nodes. Create/patch them through the portal (MCP / Settings) and they take effect **immediately** — the data lives in Postgres, the running pod serves it, no deploy.

- The shared DeepSeek tiers live at `Systemorph/Provider/AzureFoundry` (+ children); the key is stored **encrypted** (`enc:v1:…`, AES-256-GCM) on that node, decrypted in-process at request time.
- Per-user providers (e.g. Claude Code) live at `{user}/_Memex/…` and merge into the picker via the user's `{user}/_Memex/Selection`.

**Best for:** adding/curating models, fixing an empty picker, per-user keys. **Limitation:** these are instance nodes in a space/user partition — not the *default* catalog served to every partition (that's Option B). The encrypted key depends on the master key staying constant (see [the caveat](#-the-master-key-caveat)).

## Option B — Default static catalog via Helm config (needs redeploy)

`BuiltInLanguageModelProvider` materialises a **default** catalog at `Provider/{provider}` + nested model children from config (`{Section}:Models` / `:Endpoint`) — imported into the top-level `Provider` partition on boot and served from the DB. The tier→model map (`ModelTier:Heavy/Standard/Light/Utility`) also comes from config and is what agents resolve. The AKS overlay (`deploy/aks/values.aks.yaml`) sets:

```text
AzureFoundry__Endpoint = https://s-meshweaver.services.ai.azure.com/models
AzureFoundry__Models__0/1/2 = DeepSeek-V4-Pro, DeepSeek-V3-0324, DeepSeek-V4-Flash
ModelTier__Heavy/Standard/Light/Utility = V4-Pro / V3-0324 / V4-Flash / V4-Flash
ClaudeCode__ConfigDirRoot = /mnt/users
```

The chart templates these in `deploy/helm/templates/memex-portal/config.yaml` (+ `secrets.yaml`); base `values.yaml` defaults them empty (neutral chart). **Takes effect on the next `helm upgrade`** (Option E), not before.

**Best for:** the org-wide default catalog + agent tier mapping. Anthropic/Claude is intentionally **not** wired here — Claude is per-user Claude Code (Option D).

## Option C — Secrets via Key Vault + CSI (keyless, no committed keys)

The real `AzureFoundry` key lives in the `Systemorph` Key Vault, mounted by the CSI add-on and synced into a K8s Secret the portal reads via `envFrom`.

1. Secret is stored: `az keyvault secret set --vault-name Systemorph --name AzureFoundry-ApiKey --value <key>` (done).
2. Grant the add-on identity read access (once): `az role assignment create --assignee 6c9dcc8d-d5b3-4545-afa1-209b33e8a1ba --role "Key Vault Secrets User" --scope <Systemorph KV resourceId>` (or an access policy with secret get/list).
3. Apply `deploy/aks/manifests/secretproviderclass.yaml` (the `SecretProviderClass`).
4. Patch the portal Deployment to mount it + read the synced secret:

```yaml
# strategic-merge patch on the portal container/pod
spec:
  template:
    spec:
      containers:
        - name: memex-portal
          envFrom:
            - secretRef: { name: memex-portal-ai-secrets }   # synced by the SPC
          volumeMounts:
            - name: kv-ai-secrets
              mountPath: /mnt/secrets-store
              readOnly: true
      volumes:
        - name: kv-ai-secrets
          csi:
            driver: secrets-store.csi.k8s.io
            readOnly: true
            volumeAttributes:
              secretProviderClass: memex-portal-ai-secrets
```

**Best for:** keeping the key out of git/values. Pairs with Option B (B says *which* models; C supplies the key).

## Option D — Claude Code (per-user, Claude on your own subscription)

Claude is **not** a shared org key. Each user connects the co-hosted **Claude Code** CLI under their own account in **Settings → Models → Connect**: the portal runs `claude setup-token` under `{ClaudeCode:ConfigDirRoot}/{userId}/.claude`, captures the token, and stores an encrypted `{user}/_Memex/ClaudeCode` provider that injects Claude into that user's picker.

- `claude setup-token` renders an Ink (terminal) UI that **needs a real PTY**; the login now runs under a pseudo-terminal (`script -qfc "claude setup-token" /dev/null`) when `ClaudeConnect:UsePseudoTerminal` is on (defaulted on for the Linux portal). This is a **code change** → ships via a new image (Option E).
- Requires `ClaudeCode__ConfigDirRoot=/mnt/users` (set in the AKS overlay) and the `/mnt/users` RWX share (already mounted).

## Option E — Code changes → new image → deploy

Anything in `.cs` (e.g. the Claude Code PTY fix, the static-catalog behaviour) only ships in a **new image**:

1. **Build + push (CI only):** push a `v*.*.*` tag → `release-images.yml` builds `memex-portal-ai:<version>` + `:latest` and pushes to GHCR. There is no supported local build path for the prod images.
2. **Roll the cluster to it.** Because the API server is private, run server-side via `az aks command invoke`:

```bash
# config/manifest changes (works against the CURRENT image):
az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command "kubectl apply -f secretproviderclass.yaml && kubectl rollout restart deploy/memex-portal-deployment -n memex" \
  --file deploy/aks/manifests/secretproviderclass.yaml

# helm upgrade (uploads the chart + values to the in-cluster run pod, which has helm):
az aks command invoke -g memex-aks-rg -n memexaks-cluster \
  --command "helm upgrade memex ./helm -f ./helm/values.yaml -f values.aks.yaml -n memex" \
  --file deploy/helm --file deploy/aks/values.aks.yaml
```

`az aks command invoke` runs the command from a pod inside the cluster (kubectl + helm preinstalled) and attaches the `--file` paths — the standard way to operate a private AKS cluster without VNet line-of-sight.

## 🚨 The master-key caveat

`ModelProvider.ApiKey` values are encrypted with `Ai:KeyProtection:MasterKey`. The running deployment already has a master key set **out-of-band** (not in the chart, not in the KVs above). Therefore:

- The chart emits each AI secret key **only when non-empty** (`secrets.yaml` guards), so an empty value never overrides what's set out-of-band.
- **Never** deploy a *different* `Ai:KeyProtection:MasterKey` — doing so makes every stored `enc:` provider key (including the live `Systemorph/Provider/AzureFoundry` key) undecryptable. If you ever manage it via the chart/Key Vault, point it at the exact same value the deployment already uses.

## Quick chooser

| You want to… | Option | Redeploy? |
|---|---|---|
| Add/curate models or fix an empty picker now | A (mesh nodes) | no |
| Make a model the org-wide default + agent tier | B (Helm config) | yes |
| Keep the key out of git | C (Key Vault + CSI) | yes |
| Let users use Claude on their own subscription | D (Claude Code) | code → E |
| Ship a `.cs` change | E (tag → CI image → command invoke) | yes |

## Related

- [Setting Up Model Providers](/Doc/AI/ModelProviderSetup) — the node model + which query goes where
- [AI Provider Configuration](/Doc/AI/ProviderConfiguration) — credential/endpoint wiring + factory routing
