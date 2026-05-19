---
nodeType: Markdown
name: Model Providers and BYO Credentials
category: Architecture
description: How AI provider credentials live in the mesh — _Provider satellite layout, decentralised registration, hierarchical discovery.
icon: /static/NodeTypeIcons/sparkle.svg
---

# Model Providers and BYO Credentials

Every AI provider credential — system default *and* user-supplied — is a
MeshNode under the `_Provider` satellite. The chat-client factory looks
up the credential via the live synced query the picker already uses. No
central registry, no IConfiguration sidecar, no per-user MemoryCache to
invalidate.

## Path layout

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

`_Provider` follows the same satellite-path convention as `_Access`,
`_Thread`, `_Comment`. Anywhere a MeshNode can live, that node can have
a `_Provider` satellite next to it; the chat picker walks both the
namespace hierarchy AND the NodeType hierarchy looking for `_Provider`
subtrees.

## Provider self-registration

Each AI provider package ships a single builder extension that wires
**everything** for that provider: the catalog source, the IOptions
binding, and the IChatClientFactory.

```csharp
builder
    .AddAI()
    .AddAnthropic()       // direct api.anthropic.com (Anthropic Messages API)
    .AddAzureFoundry()    // Azure-hosted multi-model gateway
    .AddAzureOpenAI();    // Azure OpenAI
```

No central registry — `LanguageModelCatalogOptions.Sources` is populated
by each `AddXxx` call. Settings UI + `ModelProviderService` read from
this live list. Adding a new provider is one builder extension in a new
package; no edits to a shared file.

## Picker query shape (canonical)

`AgentPickerProjection.BuildModelQueries` follows the documented
single-`nodeType`-filter pattern. Every query varies only on namespace
+ scope; the type filter is the same union throughout.

```csharp
"namespace:_Provider nodeType:LanguageModel|ModelProvider scope:descendants"
"namespace:{currentPath}/_Provider nodeType:LanguageModel|ModelProvider scope:descendants"
"namespace:{nodeTypePath}/_Provider nodeType:LanguageModel|ModelProvider scope:descendants"
```

> 🚨 The synced collection's all-Initial gating breaks when multi-query
> mixes **different** nodeType filters across queries. Keep the type
> filter identical; vary only namespace + scope. See
> [SyncedMeshNodeQueries.md](SyncedMeshNodeQueries.md) for the gating
> contract.

## Credential resolution

`ChatClientCredentialResolver` is a top-level singleton on the mesh
hub. It consumes the same workspace.GetQuery cache the picker uses,
follows `ModelDefinition.ProviderRef` to the parent `ModelProvider`,
and returns the credential.

Precedence:

1. **Explicit `ProviderRef`** → `ModelProvider` content at that path.
   The normal path for both static catalog entries and user-supplied
   providers.
2. **Conventional fallback** at `_Provider/{Provider}` — covers legacy
   catalog entries without a stamped `ProviderRef`.
3. **`ModelDefinition.ApiKeySecretRef` / `Endpoint`** — pre-`_Provider`
   layouts that put the key directly on the LanguageModel node.
4. **`CredentialResolution.Missing`** — factory falls back to its own
   `IOptions<XxxConfiguration>` binding.

`ChatClientCredentialResolver.WatchPartition(userPath)` extends the
subscription to a specific user/org partition. The fixture / chat
pipeline calls this once per active partition.

## Hierarchical discovery

`ModelDiscoveryService` exposes three layers, all anchored on the
top-level mesh hub (never on a per-thread or `_Exec` hub that can
block):

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

Each layer is a `Replay(1).RefCount` stream cached per key. Writes
invalidate via `service.Invalidate(path)`. RLS is per-subscription —
denied callers see empty without poisoning other callers' caches.

## Key protection

| Where the key lives | Read access |
|---|---|
| `_Provider/Anthropic.ApiKey` (system) | Permission.Api on root namespace |
| `{user}/_Provider/Anthropic.ApiKey` (user) | Permission.Api on user partition (owner-only) |
| `_Provider/Anthropic/claude-opus-4-7.ApiKeySecretRef` (LanguageModel) | `null` — never carries the key (LanguageModel is `WithPublicRead`) |

`CreateNodePermissionAttribute.GetPermissionForNodeType` maps
`"ModelProvider"` → `Permission.Api`, the same gate as `"ApiToken"`.
Cross-partition reads are blocked by the standard RLS path. The owner
can see their own key via authenticated MCP / settings UI — same model
as API tokens.

## Settings UI

`ModelsSettingsTab` is the Models tab in the user-settings page (and
any node's settings page). Provider dropdown is populated from the live
`LanguageModelCatalogOptions.Sources`; on save, `ModelProviderService`
writes a `ModelProvider` node + one `LanguageModel` per default model id
under `{ownerPath}/_Provider/{provider}`.

`ModelProviderService.GetProvidersForOwner(path)` is the listed view —
backed by a per-owner `Replay(1).RefCount` stream with a 1-hour TTL.
Writes invalidate the cache entry.

## See also

- [SyncedMeshNodeQueries.md](SyncedMeshNodeQueries.md) — the synced
  collection's all-Initial gating contract.
- [ExtensibleDefaults.md](ExtensibleDefaults.md) — system defaults +
  mesh extensions pattern.
- [CqrsAndContentAccess.md](CqrsAndContentAccess.md) — when to use
  synced queries vs `GetMeshNodeStream` vs `QueryAsync`.
- [AccessControl.md](AccessControl.md) — Permission.Api + per-partition
  RLS.
