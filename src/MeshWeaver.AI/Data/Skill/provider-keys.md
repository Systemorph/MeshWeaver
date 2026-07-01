---
nodeType: Skill
name: /provider-keys
description: Administer AI provider API keys (Anthropic, OpenAI, Azure Foundry) in MeshWeaver — set/rotate a key the framework way (encrypted at rest), the "bring your own key" decouple workflow so it never silently reverts to the shared default, the master key, and the AKS Key Vault → CSI → node layering.
icon: Key
category: Skills
order: 10
---

You are administering **AI provider API keys** for a MeshWeaver deployment. Keys live in
**layers**, and the single most common failure is a customer key **silently reverting to
the shared default** after a deploy. Get the layers and the decouple step right, or it
will revert again.

# The model — where a key actually lives

A provider is the mesh node **`Provider/<Name>`** (e.g. `Provider/Anthropic`,
`Provider/OpenAI`), `nodeType: ModelProvider`, `Content` of type `ModelProviderConfiguration`:

```jsonc
{ "provider": "Anthropic",
  "apiKey":   "enc:v1:…",                              // encrypted at rest — see §the master key
  "endpoint": "https://api.anthropic.com/v1/messages", // MUST match the key's account (see below)
  "label":    "Anthropic",
  "models":   ["claude-opus-4-8", "claude-sonnet-4-6", "claude-haiku-4-5"] }
```

Three layers feed it; **the node is the runtime authority** — the agent factory
(`AzureClaudeChatClientAgentFactory`) reads the node's `apiKey`/`endpoint` first and only
falls back to config:

1. **The `Provider/<Name>` node** — what the portal actually calls the model with. Authoritative.
2. **The env-var seed** — `Anthropic:ApiKey` / `Anthropic:Endpoint` (env `Anthropic__ApiKey`).
   `BuiltInLanguageModelProvider` seeds the node from this **once** (create-if-absent), then
   the admin owns it. On a re-sync the seed re-stamps the node (see §decouple).
3. **On AKS, the seed itself comes from Key Vault** → CSI → the pod's `Anthropic__ApiKey` env
   (see §AKS layering).

**Endpoint and key type must match.** A direct Anthropic key (`sk-ant-…`) only works against
`https://api.anthropic.com/v1/messages`. An **Azure Foundry**–routed key works against
`https://<resource>.services.ai.azure.com/anthropic/`. Installing a direct key while the
endpoint still points at Foundry (or vice-versa) → 401. When you change the key, check the
endpoint in the same edit.

# Set or rotate a key — the framework way (encrypted at rest)

**GUI (preferred):** open `Provider/<Name>` → **Enter Key** → paste the plaintext key → **Save key**.
That path (`ModelProviderLayoutAreas.SaveKey`) does exactly this — replicate it if you script it:

```csharp
var protector = hub.ServiceProvider.GetService<IProviderKeyProtector>();
var stored = protector is null ? newKey : protector.Protect(newKey);   // plaintext → enc:v1:…
hub.GetWorkspace().GetMeshNodeStream("Provider/Anthropic")
   .Update(n => n.Content is ModelProviderConfiguration cfg
        ? n with { Content = cfg with { ApiKey = stored } }            // (+ Endpoint = … if changing it)
        : n)
   .Subscribe(updated => hub.Post(new SaveMeshNodeRequest(updated),
        o => o.WithTarget(new Address("Provider/Anthropic"))),
        ex => logger.LogWarning(ex, "Saving provider key failed"));
```

Notes that keep it correct, not a band-aid:
- **Always `Protect()` first.** A raw key written without encryption *functions* (the read path
  passes untagged values through) but sits **plaintext at rest** in Postgres. Don't.
- The write runs under **the caller's identity** so RLS gates it — **no `ImpersonateAsSystem`**.
- The trailing `SaveMeshNodeRequest` force-persists: sync-driven nodes don't always fire the
  per-node save subscription, so the `Update` alone can be lost on a synced partition.
- **🚨 MCP `update`/`patch` does NOT encrypt.** Writing `apiKey` via the raw MCP tools stores
  plaintext. Use the GUI **Enter Key** action or `execute_script` that calls
  `IProviderKeyProtector.Protect` — never a raw `patch` of `apiKey`.

# 🚨 Bring your own key → DECOUPLE, or it reverts

This is the workflow for any key we ship with a shared default that a customer replaces:

1. **We initialize.** `Provider/<Name>` ships **Synced**, seeded with the shared default key
   (e.g. the Azure Foundry key + Foundry endpoint). Works out of the box.
2. **You bring your own.** Enter the customer's key (and matching endpoint) via the GUI.
3. **You decouple.** Set the **`Provider` partition to "Not synced"** — *this step is what makes
   the key permanent.* **Skip it and the next deploy/import re-seeds the node back to our
   default** — the exact incident where `Provider/Anthropic` reverted to the shared Azure key.

Decouple sets the partition root's `SyncBehavior` to `ExcludeThisAndChildren` (importer skips
root + every child):

- **GUI:** Admin → Partitions → `Provider` → Sync status → **Not synced** (or per-node **Stop Sync**
  on `Provider/Anthropic` for finer scope — `StopSyncLayoutArea`).
- **`SyncBehavior`** values: `Include` (synced, default) · `ExcludeThisOnly` · `ExcludeThisAndChildren`
  ("Not synced").
- The importer reads this claim **authoritatively** (`StaticRepoImporter.ReadClaimedRoots`,
  `GetMeshNodeStream` not a query snapshot) so a just-set decouple is honoured before the next pass.
- **Never "remove the source" to stop sync** — that orphans the partition and **deletes it
  entirely** (keys and all). "Not synced" is the safe, reversible control.

> A still-**Synced** `Provider` partition + a deploy/restart = key silently back to our default.
> If you've checked everything and the key keeps reverting, the partition is not decoupled.

# Validate BEFORE you install (don't trust a stale key)

A revoked/rotated key fails closed with **401**, breaking every chat round. Check it first —
cheap, no token spend:

```bash
curl -sS -o /dev/null -w "%{http_code}\n" https://api.anthropic.com/v1/models \
  -H "x-api-key: $KEY" -H "anthropic-version: 2023-06-01"   # 200 = live, 401 = revoked/rotated
```

If 401, the key is dead — do **not** install it (it would replace a working key with a broken
one). Get a fresh key from the customer. The same applies to recovering an old key from a Key
Vault version: an earlier version may be the original customer key, but **validate it** — it may
have been rotated on the provider side since.

# The master key (encryption at rest)

`apiKey` is stored as `enc:v1:{base64(nonce|ciphertext|tag)}` — AES-256-GCM via
`IProviderKeyProtector`. The master key comes from config **`Ai:KeyProtection:MasterKey`**
(env `Ai__KeyProtection__MasterKey`); a **null** master key disables encryption (passthrough —
keys stored plaintext). A fresh random nonce per encrypt means the same key encrypts to
different ciphertext each time — **the stored blob is not a fingerprint** of the key; don't
compare blobs to tell two keys apart.

- **On AKS** the master key is KV secret `<env>-Ai-KeyProtection-MasterKey`.
- **🚨 On a DB migration/restore, REUSE the source env's master key.** A fresh master key over a
  database that already holds `enc:v1:` values makes every stored provider key **undecryptable**
  (decrypt returns null → providers silently keyless). Only mint a fresh master key for an
  **empty** database.

# AKS — Key Vault → CSI → env layering

On AKS the env-var seed is sourced from Key Vault `Systemorph`, synced by a CSI
`SecretProviderClass` (`<env>-portal-ai-secrets`) into a k8s secret, then loaded via `envFrom`:

```
KV  Systemorph/<env>-Anthropic-ApiKey  ──CSI──▶  secret <env>-portal-ai-secrets[Anthropic__ApiKey]  ──envFrom──▶  Anthropic__ApiKey
```

- **The CSI secret must be LAST in the container's `envFrom`** — later sources win, so it
  overrides the chart's `memex-portal-config` / `memex-portal-secrets` defaults.
- **🚨 Never bake a provider key into `values.<env>.yaml`'s `config.*` section** — that lands it
  **plaintext in the `memex-portal-config` ConfigMap** (not even a Secret). Keep keys in the KV/CSI
  secret only.
- **Rotate the KV-sourced key:** `az keyvault secret set --vault-name Systemorph --name
  <env>-Anthropic-ApiKey --value <key>` (creates a new version) → `kubectl -n <env> rollout
  restart deployment/memex-portal-deployment` (CSI re-reads on the next mount). This only changes
  the **seed**; if the `Provider` partition is decoupled, also update the **node** (Enter Key) —
  the decoupled node won't pick up the new seed.
- **When restaging an env, the Anthropic secret is easy to get wrong** — `OnboardingNewEnvironment`
  doesn't list it, so a re-run can paste the shared Foundry key over the customer's. Set
  `<env>-Anthropic-ApiKey` to the **customer's** key deliberately.

# Troubleshooting: "the key reverted to our own"

1. **Read the node** — `get @Provider/Anthropic`. If `endpoint` is the Foundry URL
   (`…services.ai.azure.com/anthropic/`), it's on our shared key, not the customer's.
2. **Is the partition decoupled?** `get @Provider` → if `lastModifiedBy: system-security` /
   `SyncBehavior: Include`, it's still synced and will keep re-seeding. **Decouple it.**
3. **Check the seed.** On AKS compare the KV `<env>-Anthropic-ApiKey` value against
   `AzureFoundry-ApiKey` — if equal, the seed was overwritten with our shared key; reset KV to the
   customer's key (validate first).
4. **Fix order:** validate the customer key → Enter Key on the node (+ endpoint) → decouple the
   `Provider` partition → restore the KV seed → scrub any plaintext key from the ConfigMap.

# Related

- [Managing Partition Sync](/Doc/Architecture/PartitionSyncGuide) · [Static-Repo Import](/Doc/Architecture/StaticRepoImport)
- [Onboarding a New Environment](/Doc/Architecture/OnboardingNewEnvironment) · [Memex Cloud Deployment](/Doc/Architecture/MemexCloudDeployment)
- [Access Control](/Doc/Architecture/AccessControl) (platform admin = who may administer keys)
