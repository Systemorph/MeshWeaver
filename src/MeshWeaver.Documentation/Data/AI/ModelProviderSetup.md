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

> **The model-provider docs at a glance:** [Model Providers](/Doc/Architecture/ModelProviders) — the architectural pattern · [Provider Configuration](/Doc/AI/ProviderConfiguration) — framework config & chat-client factories · [Model Provider Setup](/Doc/AI/ModelProviderSetup) — operational setup & troubleshooting · [Model Provider Settings](/Doc/AI/ModelProviderSettings) — the settings UI. **This page: operational setup & troubleshooting.**


# Setting Up Model Providers

This is the **operational** guide to getting AI models to show up in the chat picker — for an administrator standing up a space, and for an end user wiring their own key. For the *design* of the Settings → Models UI see [AI Model Provider Settings](/Doc/AI/ModelProviderSettings); for the credential/endpoint wiring at the framework level see [AI Provider Configuration](/Doc/AI/ProviderConfiguration).

> **The one-sentence model:** providers and models are **mesh nodes** — `nodeType:ModelProvider` and `nodeType:LanguageModel` — discovered by the picker through `nodeType:` queries. If the picker is empty, it is because no such nodes are visible to you, not because of a hidden config flag.

---

## Providers and models are nodes

Two companion node types carry everything a chat-client factory needs:

| Node type | Holds | Path shape |
|---|---|---|
| `ModelProvider` | credentials shared by its models — `Endpoint`, `ApiKey` (encrypted at rest), `Label`, the model-id list | `Provider/{Provider}` (platform) · `{user}/_Memex/{Provider}` (user) |
| `LanguageModel` | one model — `Id`, `Provider`, `ProviderRef` (→ its provider node), `ModelTier`, `Description` | `{providerPath}/{modelId}` (nested under its provider) |

The platform catalog lives in the top-level **`Provider`** partition — a DB-synced [NodeType catalog](/Doc/Architecture/NodeTypeCatalogs), exactly like `Agent` / `Skill` / `Harness`. A user's **own** providers live in their dotfile namespace `{user}/_Memex/…`. A `LanguageModel` is always **nested under its provider** and never stores a key; it points at its `ModelProvider` via `ProviderRef`, and the resolver follows that reference to fetch `Endpoint` + `ApiKey`.

### Three layers, closest-wins

The same two node types appear at three different owners. The credential resolver and picker union them; a user-owned provider overrides a space or system one of the same name.

```text
Provider/{Provider}                          ← SYSTEM catalog (DB-synced top-level partition, read-only to non-admins)
{space}/Provider/{Provider}                   ← SPACE / org provider (e.g. Systemorph/Provider/AzureFoundry)
{user}/_Memex/{Provider}                      ← USER bring-your-own-key provider
```

- **System** — imported into the `Provider` partition on boot by `ModelStaticRepoSource` from the `BuiltInLanguageModelProvider` catalog (config-derived; see [Installation considerations](#installation-considerations)), then served from the DB. No nodes are authored by hand; platform admins curate them through the mesh catalog UI.
- **Space / org** — an admin authors a `ModelProvider` node in a space (e.g. `Systemorph`) so every user with read access to that space can select it. `ModelProvider` is a creatable type (gated by `Permission.Api`), so it can be authored in the UI or via MCP — see [Set up a space provider](#set-up-a-space-provider).
- **User** — a user pastes their own key in Settings → Models, which writes `{user}/_Memex/{Provider}` with their encrypted key.

---

## Which query goes where in a user's namespace

The chat picker is **provider-first**: it lists providers, and selecting one loads *that provider's* models. There is no flat "all models everywhere" query. Two query shapes do all the work (both from `AgentPickerProjection.BuildModelQueries`):

**1. Discover providers + the system catalog** — always run, no per-user state:

```text
namespace:Provider nodeType:LanguageModel|ModelProvider scope:descendants
```

**2. Load a selected provider's models** — one query per entry in the user's selection node `{user}/_Memex/Selection`:

```text
namespace:{providerPath} nodeType:LanguageModel|ModelProvider scope:selfAndDescendants
```

So to give a user the Systemorph models, you do **not** copy nodes into their namespace. You put the *selection* in their namespace:

| Node in the user's namespace | Content | Effect |
|---|---|---|
| `{user}/_Memex/Selection` | `ModelProviderSelection { SelectedProviderPaths: ["Systemorph/Provider/AzureFoundry"] }` | the picker runs `namespace:Systemorph/Provider/AzureFoundry nodeType:LanguageModel scope:selfAndDescendants` and shows those three models |

The `Selection` node is seeded empty at onboarding. Selecting a provider in Settings → Models appends its path to `SelectedProviderPaths`; you can also set it directly (admin/MCP) to pre-configure a user.

> **Common empty-picker cause:** a `Selection` that points at a provider path that doesn't exist (e.g. `{user}/_Memex/AzureFoundry` when the user never created a personal provider). The query returns nothing and the dropdown is empty even though the system/space catalog is full. Fix: point the selection at a provider that exists, or create the provider it names.

### Grouping models by provider

The discovery query returns **both** the `ModelProvider` nodes and their `LanguageModel` children, and every `ModelInfo` carries its `Provider`. That is the data needed to render a provider menu with a model submenu (e.g. *Azure Foundry ▸ DeepSeek V3 / Llama 3.3 70B / Phi-4 mini*). Group the projected models by `Provider`, using the matching `ModelProvider` node's `Label` as the group header.

---

## Set up a space provider

This is exactly how the shared **Azure AI Foundry (Systemorph)** provider was created. Author one `ModelProvider` node plus one `LanguageModel` child per model. Via MCP (or the equivalent `IMeshService.CreateNode`):

```jsonc
// Systemorph/Provider/AzureFoundry  — the provider node (holds the key + endpoint)
{
  "id": "AzureFoundry",
  "namespace": "Systemorph/Provider",
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
// Systemorph/Provider/AzureFoundry/DeepSeek-V3-0324  — one child per model
{
  "id": "DeepSeek-V3-0324",
  "namespace": "Systemorph/Provider/AzureFoundry",
  "name": "DeepSeek V3 — High",
  "nodeType": "LanguageModel",
  "content": {
    "$type": "ModelDefinition",
    "id": "DeepSeek-V3-0324",
    "displayName": "DeepSeek V3 (High)",
    "provider": "AzureFoundry",
    "providerRef": "Systemorph/Provider/AzureFoundry",
    "modelTier": "heavy",
    "order": 1
  }
}
```

The `model` ids must match models actually **deployed** in that Azure AI Foundry resource (serverless / standard deployments), or chat fails with a 404 at request time. The `Provider` string (`AzureFoundry`) selects the chat-client factory; for non-`claude-*` ids that is `AzureFoundryChatClientAgentFactory` (the catch-all). See [model-to-factory routing](/Doc/AI/ProviderConfiguration#model-to-factory-routing).

---

## Keys: where the credential lives

A `LanguageModel` node never carries a key. The resolver ([`ChatClientCredentialResolver`](/Doc/AI/ProviderConfiguration)) walks, top wins:

1. the model's `ProviderRef` → that `ModelProvider` node's `ApiKey` / `Endpoint`;
2. the conventional `Provider/{Provider}` node;
3. legacy fields stamped on the model node;
4. otherwise the factory's `IOptions<…Configuration>` binding (the system-default key from config).

So there are two clean ways to supply the key:

- **Shared org key** — set `ApiKey` + `Endpoint` on the space `ModelProvider` node (e.g. `Systemorph/Provider/AzureFoundry`). The key is `enc:`-encrypted at rest and is read under a system identity for any user who has **Read** on the subtree (use-without-see) — the raw key never leaves the server. One key, every user in the space.
- **Per-user key** — the user pastes their own key in **Settings → Models → Azure AI Foundry**, which writes `{user}/_Memex/{Provider}` with their encrypted key. A per-user provider overrides the org one of the same name.

> Never put a literal key in a `LanguageModel` node or in a doc/commit. The only sanctioned homes for the secret are the `ModelProvider.ApiKey` field (encrypted) and the deployment's secret store.

### Encryption at rest

`ModelProvider.ApiKey` is encrypted with `Ai:KeyProtection:MasterKey` (`ConfigMasterKeyProvider` → `IProviderKeyProtector`): a stored key is `enc:`-tagged and decrypted only at the moment it is handed to a factory. **Encryption is applied by the write path, not the storage** — set the key through **Settings → Models** or `ModelProviderService.CreateProvider`/`RotateKey` (both call `Protect()`), which produces the `enc:` value. A *raw* node write (e.g. a plain MCP `patch` of `content.apiKey`) stores the string verbatim — it still works (decryption is a passthrough for un-tagged values) but it is **not encrypted at rest**. So always set keys through the protected path. If no master key is configured, `Protect()` is a passthrough (plaintext) — verify `Ai:KeyProtection:MasterKey` is set in the deployment before relying on encryption.

---

## Installation considerations

Whether the picker is empty or full on a fresh deployment is decided **here**, at install time.

### The system catalog comes from configuration

`BuiltInLanguageModelProvider` reads, per registered provider, `{Section}:Models`, `{Section}:Endpoint`, `{Section}:ApiKey` from `IConfiguration` and emits a `ModelProvider` node (plus a `LanguageModel` per model id) under the top-level `Provider` partition — these are then imported into the DB on boot by `ModelStaticRepoSource` and served from there. **It emits a node only when at least one of those has a value** ("any signal"). No config signal ⇒ no system nodes ⇒ empty picker.

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

All three are deployed on the `s-meshweaver` Azure AI Foundry resource (verify with `az cognitiveservices account deployment list -n s-meshweaver -g rg-meshweaverai`). The tier → model mapping lives in `ModelTier:Heavy/Standard/Light/Utility` config (wired via the Helm overlay for the AKS deployment).

> **Claude (Anthropic) works very well — noticeably stronger on the hardest agentic and coding tasks — but it comes at a price.** It is intentionally **not** wired as a shared org key. Each user connects **Claude Code** (the co-hosted CLI, `Features:Ai:Clis:ClaudeCode`) under their own account in **Settings → Models → Connect**, which stores a per-user `{user}/_Memex/ClaudeCode` provider and injects Claude into their picker on their own subscription. To turn this on for a deployment, see [Enabling per-user Claude Code Connect](#enabling-per-user-claude-code-connect).

`modelTier:` frontmatter is a strictly **optional hint** declared only by the built-in background micro-agents (notification triage, description/icon writing, thread naming) — it kicks in solely when no model was selected in the chat composer (the composer selection always wins), resolving through the `ModelTier__*` config. With no tier config the hint is inert. Keep the deployed tier mapping and the `Models[]` catalog in sync where you do configure it.

---

## Enabling per-user Claude Code Connect

**Claude is intentionally not wired as a shared org key.** Instead each user connects **Claude Code** — the co-hosted CLI — under their **own** Claude subscription in **Settings → Models → Connect**, so their Claude usage is billed to their personal account. This is the *Connect* flow (a subscription / CLI provider), **not** a per-user Anthropic API key: the login captures the user's subscription token, never an `sk-ant-…` key. It is gated behind one deploy-time flag — `Features:Ai:Clis:ClaudeCode` (env `Features__Ai__Clis__ClaudeCode`) — and **coexists** with the shared providers: turning it on **adds** the per-user "Claude Code" card and does not touch the shared org Anthropic key (`Features:Ai:Providers:Anthropic`, a *separate* flag).

### What Connect is — and is not

- **Per-user, own subscription.** The card runs the CLI's native login (`claude setup-token`, `ClaudeConnectStrategy`), captures the user's token, and stores it **encrypted** as a `ModelProvider` node at `{user}/_Memex/ClaudeCode` — via `ConnectTokenSink` → `ModelProviderService.CreateProvider` / `RotateKey`, which `Protect()`-encrypt it. Each user self-connects; there is **no** per-person admin activation.
- **Resolved by the per-user token, not by model id.** When a user runs a Claude Code round, the CLI harness (`ClaudeCodeHarness`) resolves *that user's own* token through `ChatClientCredentialResolver.ResolveConnectToken` / `ResolveConnectCredential` (reading `{user}/_Memex/ClaudeCode`). It never forwards the composer's selected-model API key, so it does not collide with the shared model catalog.
- **Independent of the shared org key.** `Features:Ai:Providers:Anthropic` (shared org Anthropic key) and `Features:Ai:Clis:ClaudeCode` (per-user Connect) are two flags gated separately in `MemexConfiguration`. Enabling Connect neither removes nor changes the shared key; users who have not connected keep using the shared providers.

### Prerequisites and enablement

Connect is a **deploy-time capability**, and the packaged deployment paths ship it **off** (opt-in): `deploy/.env.example` sets `Features__Ai__Clis__ClaudeCode=false`, and the Azure Marketplace offer's *"Bundle co-hosted Claude Code + GitHub Copilot CLIs"* checkbox is unchecked by default. (In code the flag defaults to `true` — an absent `Features` section preserves the all-on, no-regression behaviour — but it is **inert unless the portal image actually bundles the `claude` CLI**.) To turn it on:

1. **Run the CLI-enabled portal image.** The co-hosted CLIs ship only in the `portal-ai` image (`deploy/base-images/portal-ai/Dockerfile`, which `npm install -g @anthropic-ai/claude-code`). The lean `portal` image has no `claude` binary, so Connect cannot run there. On AKS the `memex` portal is built on this base (`memex-portal-ai`).
2. **Set the flag.** `Features__Ai__Clis__ClaudeCode=true` in the portal config / env. This gates three registrations in `MemexConfiguration.cs`: the chat-client factory + config (`services.AddClaudeCode(...)`), the Connect backend (`ConnectSessionManager` + the `IConnectStrategy` `ClaudeConnectStrategy`), and the mesh catalog source (`mb.AddClaudeCode()`). Independent of `Features__Ai__Providers__Anthropic`.
3. **Point each user at their own `.claude` dir.** Set `ClaudeCode:ConfigDirRoot` (env `ClaudeCode__ConfigDirRoot`) to a **writable, per-user-persistent mount**. The co-hosted image defaults it to `/mnt/users`, which on AKS / HA is an Azure Files (RWX) share so every replica sees the same per-user credentials. Each spawn (login and round) runs with `CLAUDE_CONFIG_DIR = {ConfigDirRoot}/{userId}/.claude`, so each user logs in under their own directory (`ClaudeCodeConfiguration.ConfigDirRoot`, mirrored to `ClaudeConnectOptions.ConfigDirRoot`). On the Linux portal the login also needs a PTY; `ClaudeConnect:UsePseudoTerminal` defaults on there so `claude setup-token`'s terminal UI is scrapeable.
4. **Redeploy / roll out** so the flag and the mount take effect.

### Verifying it

1. **Card appears.** With the flag set, open **Settings → Models**: a **"Claude Code"** CLI card with a **Connect / Log in** button is shown. When the flag is unset the card is **absent** — CLI providers render no UI at all.
2. **Connect uses the user's subscription.** Complete Connect as a user, then run a Claude model: the round runs on **that user's** Claude subscription, and a per-user encrypted-token `ModelProvider` node is created at `{user}/_Memex/ClaudeCode`.
3. **Shared path unchanged.** The shared org Anthropic key path is untouched and still serves users who have not connected.

For the credential-encryption master key and the general "bring your own key" layering, see the [/provider-keys](/Skill/provider-keys) skill; for the Settings → Models UI design (API vs CLI cards, the inline login state machine, and the `ConnectSessionManager` lifecycle), see [AI Model Provider Settings](/Doc/AI/ModelProviderSettings).

---

## Troubleshooting an empty picker

Work top-down — the first hit is usually the cause:

1. **Are there any provider/model nodes?** `search nodeType:ModelProvider scope:descendants`. Empty ⇒ no system config signal (see [installation](#installation-considerations)) and no space/user provider authored.
2. **Does the user's `Selection` point at a provider that exists?** Read `{user}/_Memex/Selection`; a path to a non-existent `ModelProvider` yields no models.
3. **Can the user read the space provider?** Org providers are visible only to users with Read on the space subtree.
4. **Do the model ids exist in the resource?** A model node whose `Id` isn't deployed in the Azure resource shows in the picker but 404s at chat time — that is a *credential/deployment* problem, not a picker problem.
5. **Logs** — grep the `MeshWeaver.AI.AgentPickerProjection` channel for `[AgentPicker]`: it logs the raw snapshot count and type breakdown, telling you whether the query returned 0 nodes or the projection dropped them.

---

## Related

- [Enabling per-user Claude Code Connect](#enabling-per-user-claude-code-connect) — turn on per-user Claude Code Connect (`Features__Ai__Clis__ClaudeCode`) so each user runs Claude on their own subscription
- [AI Model Provider Settings](/Doc/AI/ModelProviderSettings) — the Settings → Models UI design (API vs CLI providers, inline CLI login)
- [/provider-keys](/Skill/provider-keys) — administering AI provider keys the framework way (encryption at rest, the "bring your own key" decouple)
- [AI Provider Configuration](/Doc/AI/ProviderConfiguration) — framework credential/endpoint wiring and model-to-factory routing
- [Agentic AI](/Doc/AI/AgenticAI) — how agents are composed and select models
