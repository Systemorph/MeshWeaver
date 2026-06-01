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

`ChatClientCredentialResolver` is a top-level singleton on the mesh hub. It consumes the same `workspace.GetQuery` cache the picker uses, follows `ModelDefinition.ProviderRef` to the parent `ModelProvider`, and returns the credential.

Resolution proceeds in this order:

| Priority | Source | When it applies |
|---|---|---|
| 1 | **Explicit `ProviderRef`** → `ModelProvider` content at that path | Normal path for static catalog entries and user-supplied providers |
| 2 | **Conventional fallback** at `_Provider/{Provider}` | Legacy catalog entries without a stamped `ProviderRef` |
| 3 | **`ModelDefinition.ApiKeySecretRef` / `Endpoint`** | Pre-`_Provider` layouts that put the key directly on the `LanguageModel` node |
| 4 | **`CredentialResolution.Missing`** | Factory falls back to its own `IOptions<XxxConfiguration>` binding |

`ChatClientCredentialResolver.WatchPartition(userPath)` extends the subscription to a specific user or org partition. The fixture and chat pipeline call this once per active partition.

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

Each layer is a `Replay(1).RefCount` stream cached per key. Writes invalidate via `service.Invalidate(path)`. RLS is per-subscription — denied callers see an empty result without poisoning other callers' caches.

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
