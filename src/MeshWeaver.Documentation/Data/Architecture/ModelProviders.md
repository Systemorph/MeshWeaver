---
nodeType: Markdown
name: Model Providers and BYO Credentials
category: Architecture
description: How AI provider credentials live in the mesh — _Provider satellite layout, decentralised registration, and hierarchical discovery.
icon: /static/NodeTypeIcons/sparkle.svg
---

# Model Providers and BYO Credentials

Every AI provider credential — system default *and* user-supplied — is a MeshNode stored under the `_Provider` satellite. The chat-client factory resolves credentials through the same live synced query the model picker already uses.

No central registry. No `IConfiguration` sidecar. No per-user `MemoryCache` to invalidate.

---

<svg viewBox="0 0 760 400" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 Z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="10" y="10" width="230" height="106" rx="10" fill="#1565c0"/>
  <text x="125" y="34" text-anchor="middle" fill="#fff" font-weight="bold">System (_Provider/)</text>
  <text x="125" y="54" text-anchor="middle" fill="#bbdefb" font-size="11">_Provider/Anthropic  (ModelProvider)</text>
  <text x="125" y="70" text-anchor="middle" fill="#bbdefb" font-size="11">  claude-opus-4-7   (LanguageModel)</text>
  <text x="125" y="86" text-anchor="middle" fill="#bbdefb" font-size="11">  claude-sonnet-4-6  (LanguageModel)</text>
  <text x="125" y="104" text-anchor="middle" fill="#90caf9" font-size="10">read-only · IConfiguration key</text>
  <rect x="265" y="10" width="225" height="106" rx="10" fill="#2e7d32"/>
  <text x="377" y="34" text-anchor="middle" fill="#fff" font-weight="bold">User (rbuergi/_Provider/)</text>
  <text x="377" y="54" text-anchor="middle" fill="#c8e6c9" font-size="11">rbuergi/_Provider/Anthropic</text>
  <text x="377" y="70" text-anchor="middle" fill="#c8e6c9" font-size="11">  claude-opus-4-7</text>
  <text x="377" y="88" text-anchor="middle" fill="#c8e6c9" font-size="11">ApiKey = personal sk-ant-…</text>
  <text x="377" y="104" text-anchor="middle" fill="#a5d6a7" font-size="10">Permission.Api · owner only</text>
  <rect x="515" y="10" width="225" height="106" rx="10" fill="#6a1b9a"/>
  <text x="627" y="34" text-anchor="middle" fill="#fff" font-weight="bold">Org (acme/_Provider/)</text>
  <text x="627" y="54" text-anchor="middle" fill="#e1bee7" font-size="11">acme/_Provider/AzureFoundry</text>
  <text x="627" y="70" text-anchor="middle" fill="#e1bee7" font-size="11">  gpt-4o  (LanguageModel)</text>
  <text x="627" y="88" text-anchor="middle" fill="#e1bee7" font-size="11">  Endpoint = azure-gateway</text>
  <text x="627" y="104" text-anchor="middle" fill="#ce93d8" font-size="10">Permission.Api · org partition</text>
  <line x1="125" y1="116" x2="125" y2="160" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="377" y1="116" x2="377" y2="160" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="627" y1="116" x2="627" y2="160" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="60" y="160" width="620" height="76" rx="10" fill="#e65100"/>
  <text x="370" y="184" text-anchor="middle" fill="#fff" font-weight="bold">Picker Query Union  (AgentPickerProjection)</text>
  <text x="370" y="204" text-anchor="middle" fill="#ffe0b2" font-size="11">namespace:_Provider · namespace:{currentPath}/_Provider · namespace:{nodeTypePath}/_Provider</text>
  <text x="370" y="220" text-anchor="middle" fill="#ffe0b2" font-size="11">nodeType:LanguageModel|ModelProvider  scope:descendants   (identical filter, vary namespace)</text>
  <line x1="370" y1="236" x2="370" y2="278" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="110" y="278" width="500" height="56" rx="10" fill="#00695c"/>
  <text x="360" y="301" text-anchor="middle" fill="#fff" font-weight="bold">ChatClientCredentialResolver</text>
  <text x="360" y="320" text-anchor="middle" fill="#b2dfdb" font-size="11">Priority: explicit ProviderRef → conventional _Provider/{P} → ApiKeySecretRef → IOptions fallback</text>
  <line x1="360" y1="334" x2="360" y2="372" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="160" y="372" width="400" height="22" rx="8" fill="#37474f"/>
  <text x="360" y="387" text-anchor="middle" fill="#cfd8dc" font-size="11">IChatClient  (resolved, ready to call)</text>
</svg>

*Three credential scopes (system, user, org) merge via a unioned synced query; the credential resolver follows a four-step priority chain to produce a ready IChatClient.*

## Path layout

The `_Provider` satellite follows the same convention as `_Access`, `_Thread`, and `_Comment`. Wherever a MeshNode can live, that node can have a `_Provider` subtree alongside it.

```
_Provider/                                ← system catalog (read-only static)
  Anthropic                  ModelProvider (ApiKey from "Anthropic:" IConfiguration)
    claude-opus-4-7          LanguageModel (ProviderRef → _Provider/Anthropic)
    claude-sonnet-4-6        LanguageModel
    claude-haiku-4-5-…       LanguageModel
  OpenAI                     ModelProvider
    gpt-4o                   LanguageModel
    …

rbuergi/_Provider/                        ← user-owned (Permission.Api RLS)
  Anthropic                  ModelProvider (ApiKey = personal sk-ant-…)
    claude-opus-4-7          LanguageModel
    …

acme/_Provider/                           ← org-shared
  AzureFoundry               ModelProvider
    …
```

The chat picker walks **both** the namespace hierarchy and the NodeType hierarchy looking for `_Provider` subtrees, so models defined at any level are naturally available to everything below.

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
"namespace:_Provider nodeType:LanguageModel|ModelProvider scope:descendants"
"namespace:{currentPath}/_Provider nodeType:LanguageModel|ModelProvider scope:descendants"
"namespace:{nodeTypePath}/_Provider nodeType:LanguageModel|ModelProvider scope:descendants"
```

> 🚨 **CRITICAL — identical type filters across queries.** The synced collection's all-Initial gating breaks when a multi-query mixes **different** `nodeType` filters. Keep the filter identical; vary only `namespace` and `scope`. See [SyncedMeshNodeQueries.md](SyncedMeshNodeQueries.md) for the full gating contract.

---

## Credential resolution

`ChatClientCredentialResolver` is a top-level singleton on the mesh hub. Each `Resolve` reads live from the same `workspace.GetQuery` cache the picker uses (no materialised dictionary of node content), follows `ModelDefinition.ProviderRef` to the parent `ModelProvider`, and returns the credential.

Resolution proceeds in this order:

| Priority | Source | When it applies |
|---|---|---|
| 1 | **Explicit `ProviderRef`** → `ModelProvider` content at that path | Normal path for static catalog entries and user-supplied providers |
| 2 | **Conventional fallback** at `_Provider/{Provider}` | Legacy catalog entries without a stamped `ProviderRef` |
| 3 | **`ModelDefinition.ApiKeySecretRef` / `Endpoint`** | Pre-`_Provider` layouts that put the key directly on the `LanguageModel` node |
| 4 | **`CredentialResolution.Missing`** | Factory falls back to its own `IOptions<XxxConfiguration>` binding |

`ChatClientCredentialResolver.WatchPartition(userPath)` widens the live read to include a specific user or org partition's `_Provider` subtree. The fixture and chat pipeline call this once per active partition (idempotent; records the path only — no node content is cached).

---

## Hierarchical discovery

`ModelDiscoveryService` exposes three layers, all anchored on the top-level mesh hub — never on a per-thread or `_Exec` hub that could block.

```csharp
// (a) one node's _Provider subtree
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
| `_Provider/Anthropic.ApiKey` (system) | `Permission.Api` on root namespace |
| `{user}/_Provider/Anthropic.ApiKey` (user) | `Permission.Api` on user partition — owner only |
| `_Provider/Anthropic/claude-opus-4-7.ApiKeySecretRef` (LanguageModel) | `null` — LanguageModel nodes never carry the key (`WithPublicRead`) |

`CreateNodePermissionAttribute.GetPermissionForNodeType` maps `"ModelProvider"` → `Permission.Api`, the same gate as `"ApiToken"`. Cross-partition reads are blocked by the standard RLS path. An owner can view their own key through authenticated MCP or the Settings UI — the same access model as API tokens.

---

## Settings UI

`ModelsSettingsTab` is the Models tab in the user-settings page and in any node's settings page.

- The provider dropdown is populated from the live `LanguageModelCatalogOptions.Sources` list.
- On save, `ModelProviderService` writes a `ModelProvider` node plus one `LanguageModel` per default model ID under `{ownerPath}/_Provider/{provider}`.

`ModelProviderService.GetProvidersForOwner(path)` powers the listed view — backed by a per-owner `Replay(1).RefCount` stream with a one-hour TTL. Writes invalidate the cache entry immediately.

---

## See also

- [SyncedMeshNodeQueries.md](SyncedMeshNodeQueries.md) — the synced collection's all-Initial gating contract
- [ExtensibleDefaults.md](ExtensibleDefaults.md) — system defaults + mesh extensions pattern
- [CqrsAndContentAccess.md](CqrsAndContentAccess.md) — when to use synced queries vs `GetMeshNodeStream` vs `QueryAsync`
- [AccessControl.md](AccessControl.md) — `Permission.Api` and per-partition RLS
