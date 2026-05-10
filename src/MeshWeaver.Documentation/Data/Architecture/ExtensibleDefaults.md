---
Name: Extensible Defaults
Category: Documentation
Description: Pattern for NodeType entities where the framework ships system defaults and the mesh adds user-configured extensions, surfaced as one live synced collection per hub.
Icon: /static/DocContent/Architecture/AccessControl/icon.svg
---

# Extensible Defaults

A pattern for NodeType entities where **the framework ships built-in
defaults** *and* **the mesh allows user-defined extensions** at any
namespace. Every per-node hub sees one live synced collection that
unions both layers — built-ins are visible immediately on first
subscribe, user extensions stream in as they are created.

Applies whenever a feature has

- a small set of canonical entities the platform must ship (so the
  feature works out-of-the-box on a blank mesh), and
- an open extension point so customers/tenants can add their own
  instances at any node in the hierarchy.

Current callers in the codebase:

| Entity | NodeType | Root namespace | Static provider | Picker projection |
|--------|----------|----------------|-----------------|-------------------|
| Agent | `Agent` | `Agent` | [`BuiltInAgentProvider`](xref:MeshWeaver.AI.BuiltInAgentProvider) | [`AgentPickerProjection.BuildAgentQueries`](xref:MeshWeaver.AI.AgentPickerProjection.BuildAgentQueries*) |
| Language Model | `LanguageModel` | `Model` | `BuiltInModelProvider` | `AgentPickerProjection.BuildModelQueries` |
| Role | `Role` | `Role` | [`RoleNodeType.BuiltInRolesProvider`](xref:MeshWeaver.Graph.Configuration.RoleNodeType) | *(to follow Agent/Model)* |

# The three layers

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

`scope:selfAndAncestors` on (2) and (3) means a hub at
`acme/team/proj` sees extensions defined at `acme/team/proj`,
`acme/team`, `acme`, and the root — closest-wins behaviour is the
caller's job (the same convention used by AccessAssignment).

The union is computed by [`MeshQueryEngine`](xref:MeshWeaver.Hosting.Persistence.Query.MeshQueryEngine)
inside a single `IMeshQueryCore.ObserveQuery` call — see
[Synced Query Data Source](../DataMesh/SyncedQueryDataSource) for the
delta protocol. Static-provider nodes participate via
[`StaticNodeQueryProvider`](xref:MeshWeaver.Hosting.Persistence.Query.StaticNodeQueryProvider),
so a query against `namespace:Agent` returns built-in Agents without
touching persistence.

# Why this shape

**Instant Initial.** Static nodes are in-memory; the union's first
emission carries every built-in synchronously on first subscribe. No
permission check, no first-render path waits multiple seconds for a
Postgres round-trip. The synced query is a Replay(1).RefCount stream,
so subsequent consumers in the same workspace get the cached snapshot
immediately.

**Zero-config defaults.** A fresh mesh works without any
AccessAssignment, Agent, or Model rows in Postgres — the static repo
covers the baseline. The framework never blocks on "did the DB warm
up yet".

**Mesh-level customisation.** Users create a `Role` / `Agent` /
`LanguageModel` MeshNode anywhere in their hierarchy. The synced
query picks it up on the next `IDataChangeNotifier` tick and emits an
`Added` delta; every consuming hub re-projects.

**Read-only built-ins.** The static provider ships a
`PartitionAccessPolicy` named `_Policy` at the root namespace with
`Create/Update/Delete/Comment/Thread = false`. That makes
`namespace:Agent` (or `:Role`, `:Model`) unmodifiable — extensions
must live in user namespaces.

**Parallel to Agent/Model.** New entities replicate the same wiring
verbatim. No bespoke service, no per-feature cache layer, no special
deadlock-handling.

# Anatomy of an Extensible Default

Three pieces of code per entity.

## 1. Static provider — the built-ins

`IStaticNodeProvider` is a singleton that returns the MeshNodes the
framework wants visible on every mesh. The provider's `GetStaticNodes`
runs synchronously at routing time — keep it cheap.

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

## 2. NodeType — the extension surface

Register the NodeType MeshNode itself so the routing layer knows the
content type and how to host the per-instance hub. This is the same
shape every NodeType uses — see
[`RoleNodeType.AddRoleType`](xref:MeshWeaver.Graph.Configuration.RoleNodeType.AddRoleType*).

## 3. Picker / projection — the consumer entry point

A small static helper that builds the three query strings and
projects the resulting MeshNode snapshot into the typed view the
feature actually wants. Modelled on
[`AgentPickerProjection`](xref:MeshWeaver.AI.AgentPickerProjection):

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

Same query id everywhere = one shared upstream subscription via the
workspace's per-id cache. Every consumer in the same hub (chat picker
UI, permission evaluator, RLS validator) gets the cached
`Replay(1)` snapshot.

# Hot mistakes — and why this pattern fixes them

| Mistake | Symptom | What this pattern enforces |
|---------|---------|----------------------------|
| Per-user `MemoryCache` with a `Timeout()` fallback. | First permission check after process start waits the full Timeout (e.g. 2 s) while the upstream synced query warms; the fallback emits empty roles and the UI looks "logged out". | The Replay(1) is fed by the static provider's nodes *synchronously* on first subscribe. There is no "warm-up window" to time out against. |
| Reading the entity via `IMeshQueryCore.QueryAsync` (CQRS read side). | Index-lag staleness after writes; missed Initial emissions. | Reads come from the local workspace's synced collection, which folds `Added`/`Updated`/`Removed` deltas verbatim. See [CQRS and Content Access](CqrsAndContentAccess). |
| Reaching for `ConfigResolver.ResolveConfigurationAsync` on every per-node activation. | Every grain activation does a Postgres round-trip + an extra async resolution before the hub can answer messages. | The static repo carries enough state for the activation; user extensions arrive lazily via the same synced collection. |
| Caching at the application service ("`SecurityService._userScopeRolesCache`"). | Cache invalidation is its own deadlock surface; runtime updates need a separate invalidation hook. | No application cache. The synced collection *is* the cache, kept consistent by `IDataChangeNotifier`. |

# Applying this to Roles & AccessAssignments

Today `SecurityService` hand-rolls a per-user `MemoryCache` over a
synced AccessAssignment query and falls through a 2 s `Timeout()` on
first use — that fallback fires hundreds of times during a single
thread render and is the dominant cost of opening a chat. Migrating
the surface to this pattern means:

1. Keep `BuiltInRolesProvider` (already shipping the four canonical
   roles + the read-only `_Policy`).
2. Add a `BuiltInAccessAssignmentProvider` for baseline assignments
   (e.g. `Public → Viewer` on shipped namespaces) so the synced
   collection has a non-empty Initial on a blank mesh.
3. Replace `SecurityService.GetUserScopeRolesStream` with a
   workspace-local consumer of the same `workspace.GetQuery(id,
   BuildRoleAssignmentQueries(...))` projection that Agent/Model use.
   No `Timeout`, no `Catch`-to-empty fallback — the Replay(1)
   snapshot is already populated when the first permission check
   arrives.

See [Access Control](AccessControl) for the role / assignment data
model and the per-hub `ISecurityService` evaluator that consumes the
projection.

# References

- [`AgentPickerProjection`](xref:MeshWeaver.AI.AgentPickerProjection) — canonical caller (Agent & Model).
- [`BuiltInAgentProvider`](xref:MeshWeaver.AI.BuiltInAgentProvider), [`RoleNodeType.BuiltInRolesProvider`](xref:MeshWeaver.Graph.Configuration.RoleNodeType) — static repo examples.
- [`StaticNodeQueryProvider`](xref:MeshWeaver.Hosting.Persistence.Query.StaticNodeQueryProvider) — how static nodes fold into `IMeshQueryCore`.
- [Synced Query Data Source](../DataMesh/SyncedQueryDataSource) — delta protocol, gating, Replay semantics.
- [Access Control](AccessControl) — the role/permission evaluator that will consume this pattern.
- [CQRS and Content Access](CqrsAndContentAccess) — when to use synced collections vs `GetRemoteStream` vs `QueryAsync`.
