---
Name: Extensible Defaults
Category: Documentation
Description: Pattern for NodeType entities where the framework ships system defaults and the mesh adds user-configured extensions, surfaced as one live synced collection per hub.
Icon: /static/DocContent/Architecture/AccessControl/icon.svg
---

# Extensible Defaults

Some features need to work on a blank mesh — no database rows, no user configuration — but they also need to grow: customers and tenants must be able to add their own instances anywhere in the node hierarchy. The **Extensible Defaults** pattern satisfies both requirements without compromise.

> **Core idea:** the framework ships *built-in* entities via a read-only static provider; the mesh allows *user-defined* extensions at any namespace. Every per-node hub sees one live synced collection that unions both layers. Built-ins are visible the instant a consumer subscribes; user extensions stream in as they are created.

## When to use this pattern

Apply Extensible Defaults whenever a feature has:

- A small set of canonical entities the platform must ship so the feature works out-of-the-box on a blank mesh, **and**
- An open extension point so customers or tenants can add their own instances at any node in the hierarchy.

### Current callers in the codebase

| Entity | NodeType | Root namespace | Static provider | Picker projection |
|--------|----------|----------------|-----------------|-------------------|
| Agent | `Agent` | `Agent` | [`BuiltInAgentProvider`](xref:MeshWeaver.AI.BuiltInAgentProvider) | [`AgentPickerProjection.BuildAgentQueries`](xref:MeshWeaver.AI.AgentPickerProjection.BuildAgentQueries*) |
| Language Model | `LanguageModel` | `Model` | `BuiltInModelProvider` | `AgentPickerProjection.BuildModelQueries` |
| Role | `Role` | `Role` | [`RoleNodeType.BuiltInRolesProvider`](xref:MeshWeaver.Graph.Configuration.RoleNodeType) | *(to follow Agent/Model)* |

---

## The three layers

<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 Z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="10" y="10" width="220" height="80" rx="10" fill="#1565c0"/>
  <text x="120" y="36" text-anchor="middle" fill="#fff" font-weight="bold">Static Provider</text>
  <text x="120" y="54" text-anchor="middle" fill="#bbdefb" font-size="11">IStaticNodeProvider</text>
  <text x="120" y="72" text-anchor="middle" fill="#bbdefb" font-size="11">Built-in Agents · Models · Roles</text>
  <rect x="270" y="10" width="220" height="80" rx="10" fill="#2e7d32"/>
  <text x="380" y="36" text-anchor="middle" fill="#fff" font-weight="bold">User Extensions</text>
  <text x="380" y="54" text-anchor="middle" fill="#c8e6c9" font-size="11">MeshNode created anywhere</text>
  <text x="380" y="72" text-anchor="middle" fill="#c8e6c9" font-size="11">in the namespace hierarchy</text>
  <line x1="120" y1="90" x2="120" y2="130" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="380" y1="90" x2="380" y2="130" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="122" y1="130" x2="368" y2="130" stroke="currentColor" stroke-opacity=".35" stroke-width="1"/>
  <rect x="60" y="130" width="380" height="90" rx="10" fill="#6a1b9a"/>
  <text x="250" y="155" text-anchor="middle" fill="#fff" font-weight="bold">Synced Query Union</text>
  <text x="250" y="174" text-anchor="middle" fill="#e1bee7" font-size="11">① namespace:{root}  nodeType:{T}</text>
  <text x="250" y="191" text-anchor="middle" fill="#e1bee7" font-size="11">② namespace:{currentPath}  scope:selfAndAncestors</text>
  <text x="250" y="208" text-anchor="middle" fill="#e1bee7" font-size="11">③ namespace:{nodeTypePath}  scope:selfAndAncestors</text>
  <line x1="250" y1="220" x2="250" y2="258" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="60" y="258" width="380" height="72" rx="10" fill="#00695c"/>
  <text x="250" y="282" text-anchor="middle" fill="#fff" font-weight="bold">Per-hub Replicated Collection</text>
  <text x="250" y="300" text-anchor="middle" fill="#b2dfdb" font-size="11">Replay(1).RefCount — first emit = built-ins (instant)</text>
  <text x="250" y="317" text-anchor="middle" fill="#b2dfdb" font-size="11">User extensions stream in via IDataChangeNotifier</text>
  <rect x="530" y="100" width="190" height="170" rx="10" fill="none" stroke="currentColor" stroke-opacity=".35" stroke-dasharray="5,4"/>
  <text x="625" y="122" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11" font-weight="bold">Ancestor traversal</text>
  <rect x="552" y="132" width="140" height="26" rx="6" fill="#37474f"/>
  <text x="622" y="150" text-anchor="middle" fill="#cfd8dc" font-size="11">acme  (root)</text>
  <rect x="564" y="168" width="116" height="26" rx="6" fill="#455a64"/>
  <text x="622" y="186" text-anchor="middle" fill="#cfd8dc" font-size="11">acme/team</text>
  <rect x="576" y="204" width="92" height="26" rx="6" fill="#546e7a"/>
  <text x="622" y="222" text-anchor="middle" fill="#cfd8dc" font-size="11">acme/team/proj</text>
  <line x1="622" y1="158" x2="622" y2="168" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="622" y1="194" x2="622" y2="204" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5" marker-end="url(#arr)"/>
  <text x="622" y="255" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="10">hub sees extensions</text>
  <text x="622" y="268" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="10">at all ancestor levels</text>
</svg>

*Built-in entities (static provider) and user-defined extensions (any namespace) merge into one per-hub synced collection; ancestor traversal surfaces extensions defined at any level of the hierarchy.*

```
┌─────────────────────────────────────────────────────────────┐
│  Static Repo            (code-shipped — IStaticNodeProvider)│
│   - Read-only _Policy at root namespace                     │
│   - Built-in instances (Admin, Editor, … / GPT-4, Claude …) │
└──────────────────────┬──────────────────────────────────────┘
                       │
                       ▼ folded into IMeshQueryCore by the
                         routing layer (StaticNodeQueryProvider)
┌─────────────────────────────────────────────────────────────┐
│  Synced query union     (three queries, one subscription)   │
│   1. namespace:{root}             nodeType:{T}              │
│   2. namespace:{currentPath}      nodeType:{T} scope:sAA    │
│   3. namespace:{nodeTypePath}     nodeType:{T} scope:sAA    │
└──────────────────────┬──────────────────────────────────────┘
                       │  workspace.GetQuery(id, queries)
                       ▼
┌─────────────────────────────────────────────────────────────┐
│  Per-hub replicated collection  (SyncedQueryMeshNodes)      │
│   Local read-only view in every consuming hub's workspace.  │
│   First emission = built-ins (instant from static provider) │
│   + any matching user-created nodes already in the index.   │
└─────────────────────────────────────────────────────────────┘
```

`scope:selfAndAncestors` on queries (2) and (3) means a hub at `acme/team/proj` sees extensions defined at `acme/team/proj`, `acme/team`, `acme`, and the root — closest-wins behaviour is the caller's responsibility (the same convention used by AccessAssignment).

The union is computed by [`MeshQueryEngine`](xref:MeshWeaver.Hosting.Persistence.Query.MeshQueryEngine) inside a single `IMeshQueryCore.ObserveQuery` call — see [Synced Query Data Source](../DataMesh/SyncedQueryDataSource) for the delta protocol. Static-provider nodes participate via [`StaticNodeQueryProvider`](xref:MeshWeaver.Hosting.Persistence.Query.StaticNodeQueryProvider), so a query against `namespace:Agent` returns built-in Agents without touching persistence.

---

## Why this shape

**Instant first emission.** Static nodes are in-memory; the union's first emission carries every built-in synchronously on first subscribe. No permission check, no first-render path waits on a Postgres round-trip. The synced query is a `Replay(1).RefCount` stream, so subsequent consumers in the same workspace get the cached snapshot immediately.

**Zero-config defaults.** A fresh mesh works without any AccessAssignment, Agent, or Model rows in Postgres — the static repo covers the baseline. The framework never blocks on "did the database warm up yet?"

**Mesh-level customisation.** Users create a `Role`, `Agent`, or `LanguageModel` MeshNode anywhere in their hierarchy. The synced query picks it up on the next `IDataChangeNotifier` tick and emits an `Added` delta; every consuming hub re-projects automatically.

**Read-only built-ins.** The static provider ships a `PartitionAccessPolicy` named `_Policy` at the root namespace with `Create/Update/Delete/Comment/Thread = false`. That makes `namespace:Agent` (or `:Role`, `:Model`) unmodifiable — extensions must live in user namespaces.

**Replicate, don't reinvent.** New entities replicate the same wiring verbatim. No bespoke service, no per-feature cache layer, no special deadlock-handling.

---

## Anatomy of an Extensible Default

Three pieces of code per entity.

### 1. Static provider — the built-ins

`IStaticNodeProvider` is a singleton that returns the MeshNodes the framework wants visible on every mesh. `GetStaticNodes` runs synchronously at routing time — keep it cheap.

```csharp
private class BuiltInRolesProvider : IStaticNodeProvider
{
    private static readonly MeshNode[] Nodes =
    [
        new("_Policy", "Role")
        {
            NodeType = "PartitionAccessPolicy",
            Content = new PartitionAccessPolicy
            {
                Create = false, Update = false, Delete = false,
                Comment = false, Thread = false,
            },
        },
        new("Admin",     "Role") { NodeType = "Role", Content = Role.Admin },
        new("Editor",    "Role") { NodeType = "Role", Content = Role.Editor },
        new("Viewer",    "Role") { NodeType = "Role", Content = Role.Viewer },
        new("Commenter", "Role") { NodeType = "Role", Content = Role.Commenter },
    ];

    public IEnumerable<MeshNode> GetStaticNodes() => Nodes;
}
```

Register in the NodeType's `AddXxxType<TBuilder>` builder extension:

```csharp
builder.ConfigureServices(services =>
    services.AddSingleton<IStaticNodeProvider, BuiltInRolesProvider>());
```

### 2. NodeType — the extension surface

Register the NodeType MeshNode itself so the routing layer knows the content type and how to host the per-instance hub. This is the same shape every NodeType uses — see [`RoleNodeType.AddRoleType`](xref:MeshWeaver.Graph.Configuration.RoleNodeType.AddRoleType*).

### 3. Picker / projection — the consumer entry point

A small static helper that builds the three query strings and projects the resulting MeshNode snapshot into the typed view the feature actually needs. Modelled on [`AgentPickerProjection`](xref:MeshWeaver.AI.AgentPickerProjection):

```csharp
public static class RolePickerProjection
{
    public const string RolesQueryId = "Roles";
    public const string RootNamespace = "Role";

    public static string[] BuildRoleQueries(string? currentPath = null,
        string? nodeTypePath = null)
    {
        var queries = new List<string>
        {
            $"namespace:{RootNamespace} nodeType:{RoleNodeType.NodeType}",
        };
        if (!string.IsNullOrEmpty(currentPath))
            queries.Add($"namespace:{currentPath} nodeType:{RoleNodeType.NodeType} scope:selfAndAncestors");
        if (!string.IsNullOrEmpty(nodeTypePath))
            queries.Add($"namespace:{nodeTypePath} nodeType:{RoleNodeType.NodeType} scope:selfAndAncestors");
        return queries.ToArray();
    }

    public static IObservable<IReadOnlyList<Role>> ObserveRoles(
        IWorkspace workspace, IMessageHub hub,
        string? currentPath = null, string? nodeTypePath = null) =>
            workspace.GetQuery(RolesQueryId,
                    BuildRoleQueries(currentPath, nodeTypePath))
                .Select(snapshot => ProjectRoles(snapshot, hub.JsonSerializerOptions));

    public static IReadOnlyList<Role> ProjectRoles(
        IEnumerable<MeshNode> snapshot, JsonSerializerOptions options) =>
            snapshot.Where(n => n.NodeType == RoleNodeType.NodeType)
                    .Select(n => ToRole(n, options))
                    .Where(r => r is not null).Select(r => r!)
                    .ToList();
}
```

Using the same query id everywhere means a single shared upstream subscription via the workspace's per-id cache. Every consumer in the same hub — chat picker UI, permission evaluator, RLS validator — gets the cached `Replay(1)` snapshot at no extra cost.

---

## Hot mistakes — and why this pattern fixes them

| Mistake | Symptom | What this pattern enforces |
|---------|---------|----------------------------|
| Per-user `MemoryCache` with a `Timeout()` fallback. | First permission check after process start waits the full timeout (e.g. 2 s) while the upstream synced query warms; the fallback emits empty roles and the UI looks "logged out". | The `Replay(1)` is fed by the static provider's nodes *synchronously* on first subscribe — there is no warm-up window to time out against. |
| Reading the entity via `IMeshQueryCore.QueryAsync` (CQRS read side). | Index-lag staleness after writes; missed Initial emissions. | Reads come from the local workspace's synced collection, which folds `Added`/`Updated`/`Removed` deltas verbatim. See [CQRS and Content Access](CqrsAndContentAccess). |
| Calling `ConfigResolver.ResolveConfigurationAsync` on every per-node activation. | Every grain activation does a Postgres round-trip plus an async resolution before the hub can answer any messages. | The static repo carries enough state for activation; user extensions arrive lazily via the same synced collection. |
| Application-level caching (e.g. `PermissionEvaluator._userScopeRolesCache`). | Cache invalidation is its own deadlock surface; runtime updates need a separate invalidation hook. | No application cache. The synced collection *is* the cache, kept consistent by `IDataChangeNotifier`. |

---

## Applying this to Roles & AccessAssignments

Today `PermissionEvaluator` hand-rolls a per-user `MemoryCache` over a synced AccessAssignment query and falls through a 2 s `Timeout()` on first use. That fallback fires hundreds of times during a single thread render and is the dominant cost of opening a chat. Migrating to this pattern means:

1. **Keep `BuiltInRolesProvider`** — it already ships the four canonical roles plus the read-only `_Policy`.
2. **Add `BuiltInAccessAssignmentProvider`** for baseline assignments (e.g. `Public → Viewer` on shipped namespaces), so the synced collection has a non-empty Initial on a blank mesh.
3. **Replace `PermissionEvaluator.GetUserScopeRolesStream`** with a workspace-local consumer of the same `workspace.GetQuery(id, BuildRoleAssignmentQueries(...))` projection that Agent and Model use. No `Timeout`, no `Catch`-to-empty fallback — the `Replay(1)` snapshot is already populated when the first permission check arrives.

See [Access Control](AccessControl) for the role / assignment data model and the per-hub `PermissionEvaluator` that consumes the projection.

---

## References

- [`AgentPickerProjection`](xref:MeshWeaver.AI.AgentPickerProjection) — canonical caller (Agent & Model).
- [`BuiltInAgentProvider`](xref:MeshWeaver.AI.BuiltInAgentProvider), [`RoleNodeType.BuiltInRolesProvider`](xref:MeshWeaver.Graph.Configuration.RoleNodeType) — static repo examples.
- [`StaticNodeQueryProvider`](xref:MeshWeaver.Hosting.Persistence.Query.StaticNodeQueryProvider) — how static nodes fold into `IMeshQueryCore`.
- [Synced Query Data Source](../DataMesh/SyncedQueryDataSource) — delta protocol, gating, Replay semantics.
- [Access Control](AccessControl) — the role/permission evaluator that will consume this pattern.
- [CQRS and Content Access](CqrsAndContentAccess) — when to use synced collections vs `GetRemoteStream` vs `QueryAsync`.
