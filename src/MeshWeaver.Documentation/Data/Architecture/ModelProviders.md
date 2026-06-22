---
nodeType: Markdown
name: Model Providers and BYO Credentials
category: Architecture
description: How AI providers and models live in the mesh — the top-level Provider catalog partition, the per-user _Memex namespace, decentralised registration, and hierarchical discovery.
icon: /static/NodeTypeIcons/sparkle.svg
---

> **The model-provider docs at a glance:** [Model Providers](/Doc/Architecture/ModelProviders) — the architectural pattern · [Provider Configuration](/Doc/AI/ProviderConfiguration) — framework config & chat-client factories · [Model Provider Setup](/Doc/AI/ModelProviderSetup) — operational setup & troubleshooting · [Model Provider Settings](/Doc/AI/ModelProviderSettings) — the settings UI. **This page: the architectural pattern.**


# Model Providers and BYO Credentials

Every AI provider and model — system default *and* user-supplied — is a MeshNode. The platform catalog is a **DB-synced [NodeType catalog](/Doc/Architecture/NodeTypeCatalogs)** under the top-level **`Provider`** partition, treated exactly like the `Agent` / `Skill` / `Harness` catalogs; a user's own providers live in their dotfile namespace `{user}/_Memex/…`. The chat-client factory resolves credentials through the same live synced query the model picker already uses.

No central registry. No `IConfiguration` sidecar. No per-user `MemoryCache` to invalidate. The built-in/config provider (`BuiltInLanguageModelProvider`) is **only a sync source**: on boot `ModelStaticRepoSource` imports the catalog into the `Provider` partition, and from then on it is served from the database.

---

<svg viewBox="0 0 760 400" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 Z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="10" y="10" width="230" height="106" rx="10" fill="#1565c0"/>
  <text x="125" y="34" text-anchor="middle" fill="#fff" font-weight="bold">System (Provider/)</text>
  <text x="125" y="54" text-anchor="middle" fill="#bbdefb" font-size="11">Provider/Anthropic  (ModelProvider)</text>
  <text x="125" y="70" text-anchor="middle" fill="#bbdefb" font-size="11">  claude-opus-4-8   (LanguageModel)</text>
  <text x="125" y="86" text-anchor="middle" fill="#bbdefb" font-size="11">  claude-sonnet-4-6  (LanguageModel)</text>
  <text x="125" y="104" text-anchor="middle" fill="#90caf9" font-size="10">DB-synced catalog · config key</text>
  <rect x="265" y="10" width="225" height="106" rx="10" fill="#2e7d32"/>
  <text x="377" y="34" text-anchor="middle" fill="#fff" font-weight="bold">User (rbuergi/_Memex/)</text>
  <text x="377" y="54" text-anchor="middle" fill="#c8e6c9" font-size="11">rbuergi/_Memex/Anthropic</text>
  <text x="377" y="70" text-anchor="middle" fill="#c8e6c9" font-size="11">  claude-opus-4-8</text>
  <text x="377" y="88" text-anchor="middle" fill="#c8e6c9" font-size="11">ApiKey = personal sk-ant-…</text>
  <text x="377" y="104" text-anchor="middle" fill="#a5d6a7" font-size="10">Permission.Api · owner only</text>
  <rect x="515" y="10" width="225" height="106" rx="10" fill="#6a1b9a"/>
  <text x="627" y="34" text-anchor="middle" fill="#fff" font-weight="bold">Org (acme/Provider/)</text>
  <text x="627" y="54" text-anchor="middle" fill="#e1bee7" font-size="11">acme/Provider/AzureFoundry</text>
  <text x="627" y="70" text-anchor="middle" fill="#e1bee7" font-size="11">  gpt-4o  (LanguageModel)</text>
  <text x="627" y="88" text-anchor="middle" fill="#e1bee7" font-size="11">  Endpoint = azure-gateway</text>
  <text x="627" y="104" text-anchor="middle" fill="#ce93d8" font-size="10">Permission.Api · org partition</text>
  <line x1="125" y1="116" x2="125" y2="160" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="377" y1="116" x2="377" y2="160" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="627" y1="116" x2="627" y2="160" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="60" y="160" width="620" height="76" rx="10" fill="#e65100"/>
  <text x="370" y="184" text-anchor="middle" fill="#fff" font-weight="bold">Picker Query Union  (AgentPickerProjection)</text>
  <text x="370" y="204" text-anchor="middle" fill="#ffe0b2" font-size="11">namespace:Provider · namespace:{currentPath}/Provider · namespace:{user}/_Memex</text>
  <text x="370" y="220" text-anchor="middle" fill="#ffe0b2" font-size="11">nodeType:LanguageModel|ModelProvider  scope:descendants   (identical filter, vary namespace)</text>
  <line x1="370" y1="236" x2="370" y2="278" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="110" y="278" width="500" height="56" rx="10" fill="#00695c"/>
  <text x="360" y="301" text-anchor="middle" fill="#fff" font-weight="bold">ChatClientCredentialResolver</text>
  <text x="360" y="320" text-anchor="middle" fill="#b2dfdb" font-size="11">Priority: explicit ProviderRef → conventional Provider/{P} → ApiKeySecretRef → IOptions fallback</text>
  <line x1="360" y1="334" x2="360" y2="372" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="160" y="372" width="400" height="22" rx="8" fill="#37474f"/>
  <text x="360" y="387" text-anchor="middle" fill="#cfd8dc" font-size="11">IChatClient  (resolved, ready to call)</text>
</svg>

*Three credential scopes (system, user, org) merge via a unioned synced query; the credential resolver follows a four-step priority chain to produce a ready IChatClient.*

## Path layout

The **platform catalog** is a top-level partition named `Provider` — a DB-synced [NodeType catalog](/Doc/Architecture/NodeTypeCatalogs). Org/context-shared providers live under `{org}/Provider/…`; a user's **own** providers live in their dotfile namespace `{user}/_Memex/…` (not a `_Provider` satellite). Each provider node holds the credential its child `LanguageModel` nodes share, nested beneath it.

```
Provider/                                 ← system catalog (DB-synced, top-level partition)
  _Policy                    PartitionAccessPolicy (PublicRead · lifted write caps)
  Anthropic                  ModelProvider (ApiKey from "Anthropic:" config)
    claude-opus-4-8          LanguageModel (ProviderRef → Provider/Anthropic)
    claude-sonnet-4-6        LanguageModel
    claude-haiku-4-5-…       LanguageModel
  OpenAI                     ModelProvider
    gpt-4o                   LanguageModel
    …

rbuergi/_Memex/                           ← user-owned (Permission.Api RLS)
  Anthropic                  ModelProvider (ApiKey = personal sk-ant-…)
    claude-opus-4-8          LanguageModel
    …
  Selection                  ModelProviderSelection (the user's picked providers)

acme/Provider/                            ← org / context-shared
  AzureFoundry               ModelProvider
    …
```

The chat picker unions the system `Provider` catalog, the current context's and NodeType's `{path}/Provider` subtrees, and the user's `{user}/_Memex` subtree, so models defined at any level are naturally available.

> ⚠️ Do not confuse `{user}/_Memex/…` (the model/provider catalog) with the unrelated GitSync user-credential namespace `{user}/_Provider/GitHub` (GitHub OAuth) — a different satellite owned by `MeshWeaver.GitSync`.

---

## Provider self-registration

Each AI provider package ships a single builder extension that wires everything for that provider: the catalog source, the `IOptions` binding, and the `IChatClientFactory`.

```csharp
builder
    .AddAI()
    .AddAnthropic()       // direct api.anthropic.com (Anthropic Messages API)
    .AddAzureFoundry()    // Azure-hosted multi-model gateway
    .AddAzureOpenAI();    // Azure OpenAI
```

`LanguageModelCatalogOptions.Sources` is populated incrementally by each `AddXxx` call — there is no central registry to edit. The Settings UI and `ModelProviderService` both read from this live list. Adding a new provider means shipping one builder extension in a new package.

---

## Picker query shape

`AgentPickerProjection.BuildModelQueries` follows the single-`nodeType`-filter pattern. Every query varies only on namespace and scope; the type filter stays identical throughout.

```csharp
"namespace:Provider nodeType:LanguageModel|ModelProvider scope:descendants"                 // system catalog
"namespace:{currentPath}/Provider nodeType:LanguageModel|ModelProvider scope:descendants"    // context-shared
"namespace:{nodeTypePath}/Provider nodeType:LanguageModel|ModelProvider scope:descendants"   // per-NodeType
"namespace:{user}/_Memex nodeType:LanguageModel|ModelProvider scope:descendants"             // the user's own
"namespace:{selectedProviderPath} nodeType:LanguageModel|ModelProvider scope:selfAndDescendants"  // a picked provider
```

> 🚨 **CRITICAL — identical type filters across queries.** The synced collection's all-Initial gating breaks when a multi-query mixes **different** `nodeType` filters. Keep the filter identical; vary only `namespace` and `scope`. See [SyncedMeshNodeQueries.md](/Doc/Architecture/SyncedMeshNodeQueries) for the full gating contract.

---

## Credential resolution

`ChatClientCredentialResolver` is a top-level singleton on the mesh hub. Each `Resolve` reads live from the same `workspace.GetQuery` cache the picker uses (no materialised dictionary of node content), follows `ModelDefinition.ProviderRef` to the parent `ModelProvider`, and returns the credential.

Resolution proceeds in this order:

| Priority | Source | When it applies |
|---|---|---|
| 1 | **Explicit `ProviderRef`** → `ModelProvider` content at that path | Normal path for catalog entries and user-supplied providers (e.g. `Provider/Anthropic`) |
| 2 | **Conventional fallback** at `Provider/{Provider}` | Catalog entries without a stamped `ProviderRef` |
| 3 | **`ModelDefinition.ApiKeySecretRef` / `Endpoint`** | Legacy layouts that put the key directly on the `LanguageModel` node |
| 4 | **`CredentialResolution.Missing`** | Factory falls back to its own `IOptions<XxxConfiguration>` binding |

`ChatClientCredentialResolver.WatchPartition(userPath)` widens the live read to include a specific user or org partition's provider subtree (`{user}/_Memex` / `{org}/Provider`). The fixture and chat pipeline call this once per active partition (idempotent; records the path only — no node content is cached).

---

## Hierarchical discovery

`ModelDiscoveryService` exposes three layers, all anchored on the top-level mesh hub — never on a per-thread or `_Exec` hub that could block.

```csharp
// (a) one node's Provider subtree
service.GetModelsAtNode("rbuergi/Underwriting");

// (b) walks UP the path: node + parent + grandparent + … + root
service.GetModelsForNodeHierarchy("rbuergi/Underwriting/Case-1234");

// (c) (b) on the node-path + (b) on the NodeType-path, unioned
service.GetEffectiveModels(
    nodePath: "rbuergi/Underwriting/Case-1234",
    nodeTypePath: "acme/Underwriting/Project");
```

Each layer is rebuilt on demand over the workspace's per-id `GetQuery` cache (`Replay(1).RefCount` upstream), so every call reflects live state — no materialised observable cache, hence nothing to invalidate. RLS is per-subscription — denied callers see an empty result without affecting other callers.

---

## Key protection

| Where the key lives | Read access |
|---|---|
| `Provider/Anthropic.ApiKey` (system) | `Permission.Api` on the `Provider` partition |
| `{user}/_Memex/Anthropic.ApiKey` (user) | `Permission.Api` on user partition — owner only |
| `Provider/Anthropic/claude-opus-4-8.ApiKeySecretRef` (LanguageModel) | `null` — LanguageModel nodes never carry the key (`WithPublicRead`); the `Provider/_Policy` opens public Read of the key-less children |

`CreateNodePermissionAttribute.GetPermissionForNodeType` maps `"ModelProvider"` → `Permission.Api`, the same gate as `"ApiToken"`. Cross-partition reads are blocked by the standard RLS path. An owner can view their own key through authenticated MCP or the Settings UI — the same access model as API tokens.

---

## Settings UI

A user manages their own keys in the Models tab of the user-settings page; **platform admins** manage the shared `Provider` catalog through the **standard mesh catalog (mesh search UI)** — which has a permission-gated delete (trash) affordance plus keyboard shortcuts — not a bespoke settings form.

- The provider dropdown is populated from the live `LanguageModelCatalogOptions.Sources` list.
- On save, `ModelProviderService` writes a `ModelProvider` node plus one `LanguageModel` per default model ID. A user's own keys land under `{ownerPath}/_Memex/{provider}`; a platform provider (when `targetNamespace` is the `Provider` partition) lands under `Provider/{provider}` and is sync-excluded so admin edits survive redeploys.

`ModelProviderService.GetProvidersForOwner(path)` powers the listed view — backed by a per-owner `Replay(1).RefCount` stream with a one-hour TTL. Writes invalidate the cache entry immediately.

---

## See also

- [NodeType Catalogs](/Doc/Architecture/NodeTypeCatalogs) — how the `Provider` partition is rooted + the `IsDefinitionOnly` type-def dissociation
- [Static-Repo Import](/Doc/Architecture/StaticRepoImport) — how the built-in catalog is materialized into the `Provider` partition on boot
- [SyncedMeshNodeQueries.md](/Doc/Architecture/SyncedMeshNodeQueries) — the synced collection's all-Initial gating contract
- [ExtensibleDefaults.md](/Doc/Architecture/ExtensibleDefaults) — system defaults + mesh extensions pattern
- [CqrsAndContentAccess.md](/Doc/Architecture/CqrsAndContentAccess) — when to use synced queries vs `GetMeshNodeStream` vs `QueryAsync`
- [AccessControl.md](/Doc/Architecture/AccessControl) — `Permission.Api` and per-partition RLS
