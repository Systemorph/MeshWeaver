---
Name: Partitioned Persistence
Category: Documentation
Description: How persistence is partitioned by the first path segment to isolate domains across PostgreSQL schemas, Cosmos containers, and file-system directories
Icon: /static/DocContent/Architecture/icon.svg
---

Partitioned persistence routes every storage operation by the first segment of a node's path, giving each top-level domain strict isolation in its own PostgreSQL schema, Cosmos DB container, or file-system partition — while keeping the `IMeshStorage` and `IMeshQuery` interfaces completely unchanged for callers.

<svg viewBox="0 0 760 370" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity="0.6"/>
    </marker>
  </defs>
  <rect x="270" y="10" width="220" height="44" rx="10" fill="#1e88e5"/>
  <text x="380" y="28" text-anchor="middle" fill="#fff" font-weight="bold">PersistenceService</text>
  <text x="380" y="46" text-anchor="middle" fill="#ffffffbb" font-size="11">(scoped, caller-facing)</text>
  <line x1="380" y1="54" x2="380" y2="86" stroke="currentColor" stroke-opacity="0.5" marker-end="url(#arr)"/>
  <rect x="220" y="88" width="320" height="50" rx="10" fill="#5c6bc0"/>
  <text x="380" y="108" text-anchor="middle" fill="#fff" font-weight="bold">RoutingPersistenceServiceCore</text>
  <text x="380" y="126" text-anchor="middle" fill="#ffffffbb" font-size="11">Extract first path segment → choose partition</text>
  <text x="155" y="122" text-anchor="middle" fill="currentColor" fill-opacity="0.55" font-size="11">node path:</text>
  <rect x="58" y="108" width="180" height="30" rx="6" fill="none" stroke="currentColor" stroke-opacity="0.35"/>
  <text x="148" y="128" text-anchor="middle" fill="currentColor" fill-opacity="0.8" font-size="12" font-style="italic">"ACME/Projects/Alpha"</text>
  <text x="148" y="155" text-anchor="middle" fill="#f57c00" font-size="11">↓  first segment = "ACME"</text>
  <line x1="300" y1="138" x2="175" y2="192" stroke="currentColor" stroke-opacity="0.4" marker-end="url(#arr)"/>
  <line x1="380" y1="138" x2="380" y2="192" stroke="currentColor" stroke-opacity="0.4" marker-end="url(#arr)"/>
  <line x1="460" y1="138" x2="585" y2="192" stroke="currentColor" stroke-opacity="0.4" marker-end="url(#arr)"/>
  <rect x="58" y="194" width="234" height="48" rx="10" fill="#43a047"/>
  <text x="175" y="214" text-anchor="middle" fill="#fff" font-weight="bold">ACME partition</text>
  <text x="175" y="232" text-anchor="middle" fill="#ffffffcc" font-size="11">IStorageService + IMeshQueryProvider</text>
  <rect x="263" y="194" width="234" height="48" rx="10" fill="#43a047"/>
  <text x="380" y="214" text-anchor="middle" fill="#fff" font-weight="bold">Contoso partition</text>
  <text x="380" y="232" text-anchor="middle" fill="#ffffffcc" font-size="11">IStorageService + IMeshQueryProvider</text>
  <rect x="468" y="194" width="234" height="48" rx="10" fill="#26a69a"/>
  <text x="585" y="214" text-anchor="middle" fill="#fff" font-weight="bold">… (auto-provisioned)</text>
  <text x="585" y="232" text-anchor="middle" fill="#ffffffcc" font-size="11">first save triggers CreateSchema</text>
  <line x1="175" y1="242" x2="175" y2="292" stroke="currentColor" stroke-opacity="0.4" marker-end="url(#arr)"/>
  <line x1="380" y1="242" x2="380" y2="292" stroke="currentColor" stroke-opacity="0.4" marker-end="url(#arr)"/>
  <line x1="585" y1="242" x2="585" y2="292" stroke="currentColor" stroke-opacity="0.4" marker-end="url(#arr)"/>
  <rect x="58" y="294" width="234" height="48" rx="10" fill="#e53935"/>
  <text x="175" y="314" text-anchor="middle" fill="#fff" font-weight="bold">PostgreSQL schema "acme"</text>
  <text x="175" y="332" text-anchor="middle" fill="#ffffffcc" font-size="11">mesh_nodes + satellite tables</text>
  <rect x="263" y="294" width="234" height="48" rx="10" fill="#8e24aa"/>
  <text x="380" y="314" text-anchor="middle" fill="#fff" font-weight="bold">Cosmos "contoso-nodes"</text>
  <text x="380" y="332" text-anchor="middle" fill="#ffffffcc" font-size="11">container pair per tenant</text>
  <rect x="468" y="294" width="234" height="48" rx="10" fill="#f57c00"/>
  <text x="585" y="314" text-anchor="middle" fill="#fff" font-weight="bold">File System ./data/…</text>
  <text x="585" y="332" text-anchor="middle" fill="#ffffffcc" font-size="11">per-partition in-memory cache</text>
</svg>

*Routing layer: every call carries a node path; the first segment determines the partition; each partition owns its isolated backend store.*

# Why Partitioning Exists

In a multi-tenant or multi-domain mesh, data from different organizations must not bleed into one another. When `Cornerstone/Policy` and `Contoso/HR/Employee` coexist in the same mesh, their data needs to live in separate storage partitions:

| Backend | Isolation mechanism |
|---|---|
| PostgreSQL | Separate schemas (`acme`, `contoso`) |
| Cosmos DB | Separate container pairs (`acme-nodes`, `acme-partitions`) |
| File System | Logical routing with isolated per-partition caches |

The routing layer sits between the existing scoped wrappers (`PersistenceService`, `MeshQuery`) and the backend stores. From a caller's perspective, nothing changes.

# Architecture

## Routing Layer

```
PersistenceService (scoped, unchanged)
  └─> RoutingPersistenceServiceCore (singleton)
        ├─> "ACME"    → per-partition IStorageService
        ├─> "Contoso" → per-partition IStorageService
        └─> ...        (auto-provisioned on first access)

MeshQuery (scoped, unchanged)
  └─> RoutingMeshQueryProvider (singleton)
        ├─> "ACME"    → per-partition IMeshQueryProvider
        └─> "Contoso" → per-partition IMeshQueryProvider
```

## Path Segment Extraction

The `PathPartition.GetFirstSegment` utility extracts the routing key from any node path:

| Input | Routing key |
|---|---|
| `"Cornerstone/Article"` | `"Cornerstone"` |
| `"ACME"` | `"ACME"` |
| `""` or `null` | `null` (root level) |

## Operation Routing

| Operation | Routing strategy |
|---|---|
| `SaveNodeAsync(node)` | Extract first segment, auto-provision if new, route to partition |
| `GetNodeAsync(path)` | Route to partition by first segment |
| `DeleteNodeAsync(path)` | Route to partition |
| `GetChildrenAsync(null)` | Fan out — each partition returns its root node |
| `GetChildrenAsync("ACME")` | Route to ACME partition |
| `GetDescendantsAsync(null)` | Fan out to all partitions |
| `SearchAsync(null, query)` | Fan out — each partition searches within its own scope |
| `SearchAsync("ACME", query)` | Route to ACME partition |
| `MoveNodeAsync(src, tgt)` | Same partition: delegate. Cross-partition: copy + delete |
| `ExistsAsync(path)` | Route to partition |
| Query with namespace | Parse namespace, route to partition |
| Query without namespace | Fan out to all partitions, deduplicate |

## Auto-Provisioning

When a node is saved whose first segment matches no registered partition, the factory provisions the backend automatically:

```csharp
// First save to "NewOrg/..." triggers provisioning
await persistence.SaveNodeAsync(
    MeshNode.FromPath("NewOrg/Department/Team"),
    options);
// The factory creates:
// - PostgreSQL: CREATE SCHEMA IF NOT EXISTS "neworg"
// - Cosmos: CreateContainerIfNotExistsAsync("neworg-nodes", ...)
// - FileSystem: Directory.CreateDirectory("baseDir/NewOrg")
```

On startup, `InitializeAsync` discovers existing partitions and restores the routing table.

# Where Partitions Come From

A partition must be **registered with the routing layer** before any path under it can be read. Four complementary sources contribute partitions; within each source, rules are evaluated in registration order (first match wins).

## 1. IPartitionStorageProvider rules (config-time, explicit)

Each partition declares its own backend at registration time via fluent `MeshBuilder` extensions. There is no `DataSource` string discrimination inside the routing core — adding a new backend means registering a new provider rule, not editing the routing core.

```csharp
mesh
    // Pin "Doc" to a read-only embedded-resource partition
    .AddEmbeddedResourcePartition(
        "Doc",
        typeof(DocumentationExtensions).Assembly,
        "MeshWeaver.Documentation.Data",
        "Built-in MeshWeaver platform documentation")

    // Future shape: pin specific namespaces to specific backends
    // .AddFileSystemPartition("Northwind", "./data/northwind")
    // .AddPostgresPartition("ACME", connStr, schema: "acme")

    // Catch-all: anything not matched by an earlier rule
    // .AddPostgresPartitionPattern("*");
```

> **Dependency rule:** Providers are constructed from the parent service collection only. They **MUST NOT** depend on `IMessageHub` or `IMeshQueryCore` — they run during persistence init, before (or during) the singleton `IMessageHub` factory. Re-entering that factory was the cyclic-DI root cause of the Documentation stack overflow that this redesign retired.

Each provider also declares which **query contexts** it participates in (`search`, `create`, `autocomplete`, `browse` — see [QuerySyntax](/Doc/DataMesh/QuerySyntax) for the `context:` qualifier vocabulary). Consumers running with `context:search` skip every partition whose context set does not include `search`. This is a partition-level participation gate that complements the per-node `ExcludeFromContext` flag.

## 2. PartitionDefinition nodes (config-time, declared)

Nodes whose `Content` is a `PartitionDefinition` declare a partition explicitly. `RoutingPersistenceServiceCore.InitializeAsync` collects every `PartitionDefinition` from `IStaticNodeProvider`s and `MeshConfiguration.Nodes`, then calls `IPartitionedStoreFactory.InitializeDefaultPartitionsAsync(...)` so each backend can pre-create its store (PostgreSQL `CREATE SCHEMA`, Cosmos container, etc.).

```csharp
new MeshNode("ACME", "")
{
    NodeType = "Partition",
    Content = new PartitionDefinition
    {
        Namespace = "ACME",
        Schema = "acme",
        TableMappings = PartitionDefinition.StandardTableMappings
    }
}
```

Use this when a domain has its own backing store (PostgreSQL schema, Cosmos container, dedicated FS subtree). Triggered at startup.

## 3. Backend discovery (runtime, automatic)

`IPartitionedStoreFactory.DiscoverPartitionsAsync` scans the backing store for partitions that already exist:

- **File system** — top-level directories
- **PostgreSQL** — schemas containing a `mesh_nodes` table
- **Cosmos** — containers ending in `-nodes`

Discovered partitions are auto-registered without an explicit `PartitionDefinition`. Use this when the storage layout already encodes partition boundaries (deployed environments, restored backups).

## 4. Static-provider seed nodes (read-only fallback)

`IStaticNodeProvider`s also publish nodes that are not `PartitionDefinition`s — NodeType definitions, seed users, doc namespaces, test fixtures. The routing layer registers a **read-only static partition store** for the first segment of each such node, so that `GetNodeAsync(path)` resolves them without a writable backend.

If the same first segment also has a writable partition (declared, discovered, or auto-provisioned), the routing layer **layers** them: writes go to the writable store; reads check the writable store first, then fall through to the static store. This keeps "an immutable seed plus runtime mutations under the same partition" working transparently.

```csharp
public sealed class MyNodeTypeProvider : IStaticNodeProvider
{
    public IEnumerable<MeshNode> GetStaticNodes() =>
    [
        new MeshNode("readable")
        {
            Name = "Readable",
            AssemblyLocation = typeof(MyNodeTypeProvider).Assembly.Location,
            HubConfiguration = c => c.AddMeshDataSource()
        }
    ];
}

// Auto-registers a "readable" read-only partition; the per-node hub for a
// MeshNode { NodeType = "readable", ... } picks up HubConfiguration and
// gets AddMeshDataSource so GetDataRequest works.
services.AddSingleton<IStaticNodeProvider, MyNodeTypeProvider>();
```

See [Test State Isolation](/Doc/Architecture/TestStateIsolation) for the test-fixture pattern.

# Satellite Tables and Sub-Namespaces

## PartitionDefinition

Each partition is defined by a `PartitionDefinition` that specifies its namespace, data source, schema, and table mappings. Per-tenant partitions (Space, User) are created lazily on first write by the routing adapter — no explicit `Partition` MeshNode is emitted. System-level partitions (Admin, Auth, Portal, Kernel, global satellites like `_Access`) are registered statically by `DefaultPartitionProvider`. Tenant partitions use `StandardTableMappings` to route satellite node types to dedicated tables.

## Satellite Sub-Namespaces

Satellite entities are stored in dedicated sub-namespaces within the node hierarchy. Each satellite type has a reserved prefix:

| Sub-namespace | PostgreSQL table | Node types | Description |
|---|---|---|---|
| `_Activity` | `activities` | Activity | Node lifecycle events |
| `_UserActivity` | `user_activities` | UserActivity | Per-user access tracking |
| `_Thread` | `threads` | Thread, ThreadMessage | Chat / discussion threads |
| `_Tracking` | `tracking` | TrackedChange | Suggested edits (track changes) |
| `_Approval` | `approvals` | Approval | Approval workflow records |
| `_Access` | `access` | AccessAssignment | Permission grants / denials |
| `_Comment` | `comments` | Comment | Document comments |
| `Source` | `code` | Code | Source code files (.cs) — **primary content, not a satellite**. Routed to `code` as a storage optimization. |
| `Test` | `code` | Code | Test code files (.cs) — **primary content, not a satellite**. Routed to `code` as a storage optimization. |

## File-System Layout

Satellite nodes live in `_SubNamespace/` directories within their parent:

```
ACME/
  index.md                          ← Main ACME node
  _Access/
    Public_Access.json              ← Access assignments
    Alice_Access.json
  Projects/
    Alpha/
      index.md                      ← Main Alpha node
      Source/
        Alpha.cs                    ← Source code
        AlphaLayoutAreas.cs         ← Layout area definitions
      _Comment/
        c1.json                     ← Comment on Alpha
        c1/
          reply1.json               ← Reply to comment c1
      _Approval/
        a1.json                     ← Approval record
      _Thread/
        abc123.json                 ← Discussion thread
      _Access/
        Bob_Access.json             ← Bob's access to Alpha
```

## PostgreSQL Table Routing

`PartitionDefinition.ResolveTable(path)` determines the target table by matching the path against `TableMappings`:

```csharp
var def = new PartitionDefinition
{
    Namespace = "ACME",
    Schema = "acme",
    TableMappings = PartitionDefinition.StandardTableMappings
};

def.ResolveTable("ACME/Projects/Alpha")               // → "mesh_nodes"
def.ResolveTable("ACME/Projects/Alpha/_Comment/c1")   // → "comments"
def.ResolveTable("ACME/Projects/Alpha/_Access/Bob")   // → "access"
def.ResolveTable("ACME/Projects/Alpha/_Thread/abc123")// → "threads"
```

Satellite tables share the same schema as `mesh_nodes` (including a `main_node` column for back-reference to the parent entity) and are indexed on `main_node` for efficient per-entity queries.

## StandardTableMappings

`PartitionDefinition.StandardTableMappings` defines the default satellite routing for content partitions:

```csharp
public static Dictionary<string, string> StandardTableMappings => new()
{
    ["_Activity"]     = "activities",
    ["_UserActivity"] = "user_activities",
    ["_Thread"]       = "threads",
    ["_Tracking"]     = "tracking",
    ["_Approval"]     = "approvals",
    ["_Access"]       = "access",
    ["_Comment"]      = "comments",
};
```

System partitions (Admin, Portal, Kernel) typically have no `TableMappings` and store all nodes in `mesh_nodes`.

# Backend Implementations

## IPartitionedStoreFactory

Each backend implements this interface:

```csharp
public interface IPartitionedStoreFactory
{
    Task<PartitionedStore> CreateStoreAsync(
        string firstSegment, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DiscoverPartitionsAsync(
        CancellationToken ct = default);
}

public record PartitionedStore(
    IStorageService PersistenceCore,
    IMeshQueryProvider? QueryProvider);
```

## File System

All partitions share the same `FileSystemStorageAdapter`. Isolation is logical: each partition gets its own `FileSystemPersistenceService` instance with a separate in-memory cache. The file layout is unchanged (`baseDir/Cornerstone/Article.json`).

Discovery scans top-level directories for `.json` files.

## PostgreSQL

Each partition gets its own PostgreSQL schema. A per-schema `NpgsqlDataSource` is created with `SearchPath` set, so all unqualified table references resolve within the partition's schema — no SQL modifications required.

```
Database
├── schema "acme"                    ← Space partition (with satellite tables)
│   ├── mesh_nodes                   ← Primary entities
│   ├── activities                   ← _Activity satellite nodes
│   ├── user_activities              ← _UserActivity satellite nodes
│   ├── threads                      ← _Thread satellite nodes
│   ├── tracking                     ← _Tracking satellite nodes (track changes)
│   ├── approvals                    ← _Approval satellite nodes
│   ├── access                       ← _Access satellite nodes (permissions)
│   ├── comments                     ← _Comment satellite nodes
│   └── node_type_permissions        ← Public-read node type flags
├── schema "acme_versions"           ← History tracking
│   ├── mesh_nodes
│   ├── activities
│   └── ...
├── schema "admin"                   ← System partition (no satellite tables)
│   ├── mesh_nodes
│   └── node_type_permissions
├── schema "user"                    ← User partition (with satellite tables)
│   ├── mesh_nodes
│   ├── activities
│   ├── user_activities
│   ├── threads
│   ├── tracking
│   ├── approvals
│   ├── access
│   ├── comments
│   └── node_type_permissions
├── schema "portal"                  ← Portal sessions (no satellite tables)
│   └── mesh_nodes
└── schema "kernel"                  ← Kernel sessions (no satellite tables)
    └── mesh_nodes
```

Schema names are sanitized: lowercased, non-alphanumeric characters replaced with underscore, digit-leading names prefixed with underscore.

Discovery queries `information_schema.schemata` for schemas containing a `mesh_nodes` table.

## Cosmos DB

Each partition gets a container pair: `{segment}-nodes` and `{segment}-partitions`. Containers are created with `CreateContainerIfNotExistsAsync` (idempotent).

Container names are sanitized: lowercased, non-alphanumeric characters replaced with hyphen, padded to a minimum of 3 characters, and truncated to satisfy suffix constraints.

Discovery lists all containers and identifies partitions by the `-nodes` suffix convention.

# Registration

Register the backend once in `ConfigureServices`; the routing wrappers are wired automatically:

## File System

```csharp
services.AddPartitionedFileSystemPersistence(baseDirectory);
```

## PostgreSQL

```csharp
services.AddPartitionedPostgreSqlPersistence(connectionString);
```

## Cosmos DB

```csharp
services.AddPartitionedCosmosPersistence(cosmosClient, databaseName);
```

Each registration method calls `AddPartitionedCoreAndWrapperServices()`, which registers:

- `RoutingPersistenceServiceCore` as `IStorageService`
- `RoutingMeshQueryProvider` as `IMeshQueryProvider`
- `StaticNodeQueryProvider` for static node providers
- Scoped `PersistenceService` and `MeshQuery` wrappers

# Key Design Decisions

**Full paths preserved everywhere.** No path stripping occurs. `Cornerstone/Article` is stored with that full path inside the Cornerstone partition. This simplifies the implementation and eliminates path-translation bugs.

**Thread-safe provisioning.** `RoutingPersistenceServiceCore` uses `ConcurrentDictionary` with a `SemaphoreSlim` to ensure each partition is provisioned exactly once, even under concurrent access.

**Scoped fan-out for search.** When searching across all partitions (`parentPath == null`), each partition's search is scoped to its own first segment to avoid duplicate results from shared storage adapters.

**Cross-partition moves.** Moving a node between partitions (e.g., `ACME/Doc` → `Contoso/Doc`) performs a read-write-delete sequence: read from source, write to target, delete from source.

# Source Files

| File | Purpose |
|---|---|
| `src/MeshWeaver.Hosting/Persistence/PathPartition.cs` | `GetFirstSegment` utility |
| `src/MeshWeaver.Hosting/Persistence/IPartitionStorageProvider.cs` | Sequential rule contract + `PartitionContexts` |
| `src/MeshWeaver.Hosting/Persistence/EmbeddedResourceStorageAdapter.cs` | Embedded-resource adapter |
| `src/MeshWeaver.Hosting/Persistence/EmbeddedResourcePartitionStorageProvider.cs` | Embedded-resource provider |
| `src/MeshWeaver.Hosting/Persistence/PartitionConfigurationExtensions.cs` | Fluent `MeshBuilder.Add*Partition` extensions |
| `src/MeshWeaver.Hosting/Persistence/IPartitionedStoreFactory.cs` | Backend factory contract |
| `src/MeshWeaver.Hosting/Persistence/RoutingPersistenceServiceCore.cs` | Routing singleton |
| `src/MeshWeaver.Hosting/Persistence/Query/RoutingMeshQueryProvider.cs` | Query routing |
| `src/MeshWeaver.Hosting/Persistence/FileSystemPartitionedStoreFactory.cs` | File-system factory |
| `src/MeshWeaver.Hosting/Persistence/PersistenceExtensions.cs` | DI helpers |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionedStoreFactory.cs` | PostgreSQL factory |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlExtensions.cs` | PostgreSQL DI helpers |
| `src/MeshWeaver.Hosting.Cosmos/CosmosPartitionedStoreFactory.cs` | Cosmos DB factory |
| `src/MeshWeaver.Hosting.Cosmos/PersistenceExtensions.cs` | Cosmos DB DI helpers |
