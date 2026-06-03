---
NodeType: Markdown
Name: "Setting Up Model Providers"
Abstract: "The operational guide to wiring AI models into a Memex deployment: providers and models are mesh nodes (ModelProvider + LanguageModel), arranged in three layers (system / space / user); which query goes where in a user's namespace; the open-weight high/medium/low tier choices; and the installation considerations that decide whether the model picker is empty or full."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#5c6bc0'/><path d='M7 7h10M7 12h10M7 17h6' stroke='white' stroke-width='2' stroke-linecap='round'/><circle cx='18' cy='17' r='2.5' fill='none' stroke='white' stroke-width='1.6'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "AI"
  - "Configuration"
  - "Providers"
  - "Installation"
---

# Setting Up Model Providers

This is the **operational** guide to getting AI models to show up in the chat picker — for an administrator standing up a space, and for an end user wiring their own key. For the *design* of the Settings → Models UI see [AI Model Provider Settings](ModelProviderSettings.md); for the credential/endpoint wiring at the framework level see [AI Provider Configuration](ProviderConfiguration.md).

> **The one-sentence model:** providers and models are **mesh nodes** — `nodeType:ModelProvider` and `nodeType:LanguageModel` — discovered by the picker through `nodeType:` queries. If the picker is empty, it is because no such nodes are visible to you, not because of a hidden config flag.

---

## Providers and models are nodes

Two companion node types carry everything a chat-client factory needs:

| Node type | Holds | Path shape |
|---|---|---|
| `ModelProvider` | credentials shared by its models — `Endpoint`, `ApiKey` (encrypted at rest), `Label`, the model-id list | `{owner}/_Provider/{Provider}` |
| `LanguageModel` | one model — `Id`, `Provider`, `ProviderRef` (→ its provider node), `ModelTier`, `Description` | `{owner}/_Provider/{Provider}/{modelId}` |

`_Provider` is the satellite-namespace segment for these nodes — the same convention as `_Access`, `_Thread`, `_Comment`. A `LanguageModel` never stores a key; it points at its `ModelProvider` via `ProviderRef`, and the resolver follows that reference to fetch `Endpoint` + `ApiKey`.

### Three layers, closest-wins

The same two node types appear at three different owners. The credential resolver and picker union them; a user-owned provider overrides a space or system one of the same name.

```text
_Provider/{Provider}                         ← SYSTEM catalog (from IConfiguration, read-only)
{space}/_Provider/{Provider}                  ← SPACE / org provider (e.g. Systemorph/_Provider/AzureFoundry)
{user}/_Provider/{Provider}                   ← USER bring-your-own-key provider
```

- **System** — materialised from `IConfiguration` by `BuiltInLanguageModelProvider` (see [Installation considerations](#installation-considerations)). No nodes are authored by hand.
- **Space / org** — an admin authors a `ModelProvider` node in a space (e.g. `Systemorph`) so every user with read access to that space can select it. `ModelProvider` is a creatable type (gated by `Permission.Api`), so it can be authored in the UI or via MCP — see [Set up a space provider](#set-up-a-space-provider).
- **User** — a user pastes their own key in Settings → Models, which writes `{user}/_Provider/{Provider}` with their encrypted key.

---

## Which query goes where in a user's namespace

The chat picker is **provider-first**: it lists providers, and selecting one loads *that provider's* models. There is no flat "all models everywhere" query. Two query shapes do all the work (both from `AgentPickerProjection.BuildModelQueries`):

**1. Discover providers + the system catalog** — always run, no per-user state:

```text
namespace:_Provider nodeType:LanguageModel|ModelProvider scope:descendants
```

**2. Load a selected provider's models** — one query per entry in the user's selection node `{user}/_Provider/_Selection`:

```text
namespace:{providerPath} nodeType:LanguageModel|ModelProvider scope:selfAndDescendants
```

So to give a user the Systemorph models, you do **not** copy nodes into their namespace. You put the *selection* in their namespace:

| Node in the user's namespace | Content | Effect |
|---|---|---|
| `{user}/_Provider/_Selection` | `ModelProviderSelection { SelectedProviderPaths: ["Systemorph/_Provider/AzureFoundry"] }` | the picker runs `namespace:Systemorph/_Provider/AzureFoundry nodeType:LanguageModel scope:selfAndDescendants` and shows those three models |

The `_Selection` node is seeded empty at onboarding. Selecting a provider in Settings → Models appends its path to `SelectedProviderPaths`; you can also set it directly (admin/MCP) to pre-configure a user.

> **Common empty-picker cause:** a `_Selection` that points at a provider path that doesn't exist (e.g. `{user}/_Provider/AzureFoundry` when the user never created a personal provider). The query returns nothing and the dropdown is empty even though the system/space catalog is full. Fix: point the selection at a provider that exists, or create the provider it names.

### Grouping models by provider

The discovery query returns **both** the `ModelProvider` nodes and their `LanguageModel` children, and every `ModelInfo` carries its `Provider`. That is the data needed to render a provider menu with a model submenu (e.g. *Azure Foundry ▸ DeepSeek V3 / Llama 3.3 70B / Phi-4 mini*). Group the projected models by `Provider`, using the matching `ModelProvider` node's `Label` as the group header.

---

## Set up a space provider

This is exactly how the shared **Azure AI Foundry (Systemorph)** provider was created. Author one `ModelProvider` node plus one `LanguageModel` child per model. Via MCP (or the equivalent `IMeshService.CreateNode`):

```jsonc
// Systemorph/_Provider/AzureFoundry  — the provider node (holds the key + endpoint)
{
  "id": "AzureFoundry",
  "namespace": "Systemorph/_Provider",
  "name": "Azure AI Foundry (Systemorph)",
  "nodeType": "ModelProvider",
  "content": {
    "$type": "ModelProviderConfiguration",
    "provider": "AzureFoundry",
    "label": "Azure AI Foundry (Systemorph)",
    "endpoint": "https://<resource>.services.ai.azure.com/models",
    "models": ["DeepSeek-V3-0324", "Llama-3.3-70B-Instruct", "Phi-4-mini-instruct"]
  }
}
```

```jsonc
// Systemorph/_Provider/AzureFoundry/DeepSeek-V3-0324  — one child per model
{
  "id": "DeepSeek-V3-0324",
  "namespace": "Systemorph/_Provider/AzureFoundry",
  "name": "DeepSeek V3 — High",
  "nodeType": "LanguageModel",
  "content": {
    "$type": "ModelDefinition",
    "id": "DeepSeek-V3-0324",
    "displayName": "DeepSeek V3 (High)",
    "provider": "AzureFoundry",
    "providerRef": "Systemorph/_Provider/AzureFoundry",
    "modelTier": "heavy",
    "order": 1
  }
}
```

The `model` ids must match models actually **deployed** in that Azure AI Foundry resource (serverless / standard deployments), or chat fails with a 404 at request time. The `Provider` string (`AzureFoundry`) selects the chat-client factory; for non-`claude-*` ids that is `AzureFoundryChatClientAgentFactory` (the catch-all). See [model-to-factory routing](ProviderConfiguration.md#model-to-factory-routing).

---

## Keys: where the credential lives

A `LanguageModel` node never carries a key. The resolver ([`ChatClientCredentialResolver`](ProviderConfiguration.md)) walks, top wins:

1. the model's `ProviderRef` → that `ModelProvider` node's `ApiKey` / `Endpoint`;
2. the conventional `_Provider/{Provider}` node;
3. legacy fields stamped on the model node;
4. otherwise the factory's `IOptions<…Configuration>` binding (the system-default key from config).

So there are two clean ways to supply the key:

- **Shared org key** — set `ApiKey` + `Endpoint` on the space `ModelProvider` node (e.g. `Systemorph/_Provider/AzureFoundry`). The key is `enc:`-encrypted at rest and is read under a system identity for any user who has **Read** on the subtree (use-without-see) — the raw key never leaves the server. One key, every user in the space.
- **Per-user key** — the user pastes their own key in **Settings → Models → Azure AI Foundry**, which writes `{user}/_Provider/{Provider}` with their encrypted key. A per-user provider overrides the org one of the same name.

> Never put a literal key in a `LanguageModel` node or in a doc/commit. The only sanctioned homes for the secret are the `ModelProvider.ApiKey` field (encrypted) and the deployment's secret store.

### Encryption at rest

`ModelProvider.ApiKey` is encrypted with `Ai:KeyProtection:MasterKey` (`ConfigMasterKeyProvider` → `IProviderKeyProtector`): a stored key is `enc:`-tagged and decrypted only at the moment it is handed to a factory. **Encryption is applied by the write path, not the storage** — set the key through **Settings → Models** or `ModelProviderService.CreateProvider`/`RotateKey` (both call `Protect()`), which produces the `enc:` value. A *raw* node write (e.g. a plain MCP `patch` of `content.apiKey`) stores the string verbatim — it still works (decryption is a passthrough for un-tagged values) but it is **not encrypted at rest**. So always set keys through the protected path. If no master key is configured, `Protect()` is a passthrough (plaintext) — verify `Ai:KeyProtection:MasterKey` is set in the deployment before relying on encryption.

---

## Installation considerations

Whether the picker is empty or full on a fresh deployment is decided **here**, at install time.

### The system catalog comes from configuration

`BuiltInLanguageModelProvider` reads, per registered provider, `{Section}:Models`, `{Section}:Endpoint`, `{Section}:ApiKey` from `IConfiguration` and emits a read-only `ModelProvider` node (plus a `LanguageModel` per model id) under the root `_Provider` namespace. **It emits a node only when at least one of those has a value** ("any signal"). No config signal ⇒ no system nodes ⇒ empty picker.

Each provider package self-registers its config section via `AddLanguageModelCatalogSource` inside its builder extension (`AddAnthropic`, `AddAzureFoundry`, `AddAzureOpenAI`, `AddOpenAI`, `AddClaudeCode`, `AddCopilot`), gated by the `Features:Ai:Providers:*` / `Features:Ai:Clis:*` flags. Registering the source is necessary but not sufficient — the matching config section must also carry values.

### Aspire deploy auto-seeds; the Helm/AKS chart does not

A stock **Aspire** deploy (`memex/aspire/Memex.AppHost`) sets the catalog env vars, so the system catalog populates out of the box:

```text
Anthropic__Endpoint, Anthropic__ApiKey, Anthropic__Models__0..2   (= ModelTier heavy/standard/light)
AzureFoundry__Endpoint, AzureFoundry__ApiKey                       (open-weight /models gateway)
ModelTier__Heavy / __Standard / __Light / __Utility
```

> The AppHost currently emits these multi-model keys as `AzureAIS__*`, which **nothing binds** (the code reads the `AzureFoundry:` section) — a latent bug. Use `AzureFoundry__*`, as the Helm chart does.

The **Helm / AKS** chart (`deploy/helm`, `deploy/aks/values.aks.yaml`) is a *different* path and currently templated only `MEMEX_*`, `Deployment__*`, `Storage__*`, `Graph__*`, `Mcp__BaseUrl` in its portal ConfigMap — **no `Anthropic__*` / `AzureFoundry__*` / `ModelTier__*` keys at all.** (This guide's deployment shipped that fix — the chart now templates them; older checkouts may not.) A portal deployed this way boots with zero AI provider config, so `BuiltInLanguageModelProvider` finds no signal and the picker is empty. This is the most common "no models in production" cause.

Two ways to fix an AKS deployment:

1. **Config in the chart** — `deploy/helm/templates/memex-portal/config.yaml` templates the `Anthropic__*` / `AzureFoundry__*` / `ModelTier__*` keys and `secrets.yaml` the `Anthropic__ApiKey` / `AzureFoundry__ApiKey` / `Ai__KeyProtection__MasterKey` keys; `deploy/aks/values.aks.yaml` carries the AKS-correct values (key from Key Vault via the CSI Secrets Store add-on). Set the endpoint + key there and a fresh deploy self-populates the system catalog, matching Aspire.
2. **Author space/user provider nodes** — create `ModelProvider` + `LanguageModel` nodes directly (see [Set up a space provider](#set-up-a-space-provider)). This needs no redeploy and is how the Systemorph shared provider was set up; the key still has to be supplied (org key on the node, or per-user keys).

### Choosing models: open-weight high / medium / low

The Systemorph space ships three tiers, chosen to be **powerful but inexpensive and open-weight** — strong at programming and at producing structured data — sourced from the Azure AI Foundry catalog:

| Tier (`ModelTier`) | Model id | Why |
|---|---|---|
| **High** (`heavy`) | `DeepSeek-V4-Pro` | flagship open-weight (2026), best open-source coding + reasoning. The pick when answer quality matters most. |
| **Medium** (`standard`) | `DeepSeek-V3-0324` | proven open-weight workhorse, large deployed quota; balanced coding + general use at low cost. |
| **Low** (`light` / `utility`) | `DeepSeek-V4-Flash` | fast + very cheap open-weight variant, ideal for high-volume classification, extraction, and JSON/structured output. |

All three are deployed on the `s-meshweaver` Azure AI Foundry resource (verify with `az cognitiveservices account deployment list -n s-meshweaver -g rg-meshweaverai`). The tier → model mapping lives in `ModelTier:Heavy/Standard/Light/Utility` config (wired via the Helm overlay for the AKS deployment), so agents tagged `modelTier:` bind to these.

> **Claude (Anthropic) works very well — noticeably stronger on the hardest agentic and coding tasks — but it comes at a price.** It is intentionally **not** wired as a shared org key. Each user connects **Claude Code** (the co-hosted CLI, `Features:Ai:Clis:ClaudeCode`) under their own account in **Settings → Models → Connect**, which stores a per-user `{user}/_Provider/ClaudeCode` provider and injects Claude into their picker on their own subscription. Pin Claude per-agent via `PreferredModel` where the quality justifies the spend.

Agents pick a tier with `ModelTier` frontmatter; the tier names (`heavy` / `standard` / `light`) resolve through `ModelTier__*` config, so keep the deployed tier mapping and the `Models[]` catalog in sync.

---

## Troubleshooting an empty picker

Work top-down — the first hit is usually the cause:

1. **Are there any provider/model nodes?** `search nodeType:ModelProvider scope:descendants`. Empty ⇒ no system config signal (see [installation](#installation-considerations)) and no space/user provider authored.
2. **Does the user's `_Selection` point at a provider that exists?** Read `{user}/_Provider/_Selection`; a path to a non-existent `ModelProvider` yields no models.
3. **Can the user read the space provider?** Org providers are visible only to users with Read on the space subtree.
4. **Do the model ids exist in the resource?** A model node whose `Id` isn't deployed in the Azure resource shows in the picker but 404s at chat time — that is a *credential/deployment* problem, not a picker problem.
5. **Logs** — grep the `MeshWeaver.AI.AgentPickerProjection` channel for `[AgentPicker]`: it logs the raw snapshot count and type breakdown, telling you whether the query returned 0 nodes or the projection dropped them.

---

## Related

- [AI Model Provider Settings](ModelProviderSettings.md) — the Settings → Models UI design (API vs CLI providers, inline CLI login)
- [AI Provider Configuration](ProviderConfiguration.md) — framework credential/endpoint wiring and model-to-factory routing
- [Agentic AI](AgenticAI.md) — how agents are composed and select models
