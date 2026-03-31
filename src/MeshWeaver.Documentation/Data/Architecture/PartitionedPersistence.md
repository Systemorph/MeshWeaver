---
Name: Partitioned Persistence
Category: Documentation
Description: How persistence is partitioned by the first path segment to isolate domains across PostgreSQL schemas, Cosmos containers, and file system directories
Icon: /static/DocContent/Architecture/icon.svg
---

Partitioned persistence routes storage operations by the first segment of a node's path, giving each top-level domain strict isolation in its own PostgreSQL schema, Cosmos DB container, or file system partition.

# Motivation

In a multi-tenant or multi-domain mesh, data from different organizations or business domains must be isolated. When `Cornerstone/Policy` and `Contoso/HR/Employee` coexist in the same mesh, their data should live in separate storage partitions:

- **PostgreSQL**: separate schemas (`acme`, `contoso`)
- **Cosmos DB**: separate container pairs (`acme-nodes`, `contoso-nodes`)
- **File System**: same directory tree with logical routing and isolated caches

This isolation is transparent to callers. The `IMeshStorage` and `IMeshQuery` interfaces remain unchanged.

# Architecture

## Routing Layer

```
PersistenceService (scoped, unchanged)
  +-> RoutingPersistenceServiceCore (singleton)
        +-> "ACME" -> per-partition IStorageService
        +-> "Contoso" -> per-partition IStorageService
        +-> ... (auto-provisioned on first access)

MeshQuery (scoped, unchanged)
  +-> RoutingMeshQueryProvider (singleton)
        +-> "ACME" -> per-partition IMeshQueryProvider
        +-> "Contoso" -> per-partition IMeshQueryProvider
```

The routing layer sits between the scoped wrappers and the backend-specific stores. All existing interfaces are preserved.

## Path Segment Extraction

The `PathPartition.GetFirstSegment` utility extracts the routing key:

| Input | Result |
|---|---|
| `"Cornerstone/Article"` | `"ACME"` |
| `"ACME"` | `"ACME"` |
| `""` or `null` | `null` (root level) |

## Operation Routing

| Operation | Routing Strategy |
|---|---|
| `SaveNodeAsync(node)` | Extract first segment, auto-provision if new, route to partition |
| `GetNodeAsync(path)` | Route to partition by first segment |
| `DeleteNodeAsync(path)` | Route to partition |
| `GetChildrenAsync(null)` | Fan out: each partition returns its root node |
| `GetChildrenAsync("ACME")` | Route to ACME partition |
| `GetDescendantsAsync(null)` | Fan out to all partitions |
| `SearchAsync(null, query)` | Fan out: each partition searches within its own scope |
| `SearchAsync("ACME", query)` | Route to ACME partition |
| `MoveNodeAsync(src, tgt)` | Same partition: delegate. Cross-partition: copy + delete |
| `ExistsAsync(path)` | Route to partition |
| Query with namespace | Parse namespace, route to partition |
| Query without namespace | Fan out to all partitions, deduplicate |

## Auto-Provisioning

When a node is saved with a new first segment, the factory automatically creates the backing store:

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

On startup, `InitializeAsync` discovers existing partitions and restores routing tables.

# Satellite Tables and Sub-Namespaces

## PartitionDefinition

Each partition is defined by a `PartitionDefinition` that specifies its namespace, data source, schema, and table mappings. Organization partitions use `StandardTableMappings` to route satellite node types to dedicated tables.

## Satellite Sub-Namespaces

Satellite entities are stored in dedicated sub-namespaces within the node hierarchy. Each satellite type has a reserved prefix:

| Sub-Namespace | PostgreSQL Table | Node Types | Description |
|---------------|-----------------|------------|-------------|
| `_Activity` | `activities` | Activity | Node lifecycle events |
| `_UserActivity` | `user_activities` | UserActivity | Per-user access tracking |
| `_Thread` | `threads` | Thread, ThreadMessage | Chat/discussion threads |
| `_Tracking` | `tracking` | TrackedChange | Suggested edits (track changes) |
| `_Approval` | `approvals` | Approval | Approval workflow records |
| `_Access` | `access` | AccessAssignment | Permission grants/denials |
| `_Comment` | `comments` | Comment | Document comments |
| `_Source` | (file system only) | Code | Source code files (.cs) |
| `_Test` | (file system only) | Code | Test code files (.cs) |

## File System Layout

On disk, satellite nodes live in `_SubNamespace/` directories within their parent:

```
ACME/
  index.md                          ← Main ACME node
  _Access/
    Public_Access.json              ← Access assignments
    Alice_Access.json
  Projects/
    Alpha/
      index.md                      ← Main Alpha node
      _Source/
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

In PostgreSQL, `PartitionDefinition.ResolveTable(path)` determines the target table by matching the path against `TableMappings`:

```csharp
var def = new PartitionDefinition
{
    Namespace = "ACME",
    Schema = "acme",
    TableMappings = PartitionDefinition.StandardTableMappings
};

def.ResolveTable("ACME/Projects/Alpha")                 // → "mesh_nodes"
def.ResolveTable("ACME/Projects/Alpha/_Comment/c1")      // → "comments"
def.ResolveTable("ACME/Projects/Alpha/_Access/Bob")       // → "access"
def.ResolveTable("ACME/Projects/Alpha/_Thread/abc123")    // → "threads"
```

Satellite tables have the same schema as `mesh_nodes` (including `main_node` for back-reference to the parent entity) and are indexed on `main_node` for efficient queries.

## StandardTableMappings

`PartitionDefinition.StandardTableMappings` defines the default satellite routing for content partitions:

```csharp
public static Dictionary<string, string> StandardTableMappings => new()
{
    ["_Activity"] = "activities",
    ["_UserActivity"] = "user_activities",
    ["_Thread"] = "threads",
    ["_Tracking"] = "tracking",
    ["_Approval"] = "approvals",
    ["_Access"] = "access",
    ["_Comment"] = "comments",
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

All partitions share the same `FileSystemStorageAdapter`. Isolation is logical: each partition gets its own `FileSystemPersistenceService` instance with a separate in-memory cache. The file layout remains unchanged (`baseDir/Cornerstone/Article.json`).

Discovery scans top-level directories for `.json` files.

## PostgreSQL

Each partition gets its own PostgreSQL schema. A per-schema `NpgsqlDataSource` is created with `SearchPath` set, so all unqualified table references resolve within the partition's schema. No SQL modifications are needed.

```
Database
+-- schema "acme"                    ← Organization partition (with satellite tables)
|   +-- mesh_nodes                   ← Primary entities
|   +-- activities                   ← _Activity satellite nodes
|   +-- user_activities              ← _UserActivity satellite nodes
|   +-- threads                      ← _Thread satellite nodes
|   +-- tracking                     ← _Tracking satellite nodes (track changes)
|   +-- approvals                    ← _Approval satellite nodes
|   +-- access                       ← _Access satellite nodes (permissions)
|   +-- comments                     ← _Comment satellite nodes
|   +-- node_type_permissions        ← Public-read node type flags
+-- schema "acme_versions"           ← History tracking
|   +-- mesh_nodes
|   +-- activities
|   +-- ...
+-- schema "admin"                   ← System partition (no satellite tables)
|   +-- mesh_nodes
|   +-- node_type_permissions
+-- schema "user"                    ← User partition (with satellite tables)
|   +-- mesh_nodes
|   +-- activities
|   +-- user_activities
|   +-- threads
|   +-- tracking
|   +-- approvals
|   +-- access
|   +-- comments
|   +-- node_type_permissions
+-- schema "portal"                  ← Portal sessions (no satellite tables)
|   +-- mesh_nodes
+-- schema "kernel"                  ← Kernel sessions (no satellite tables)
    +-- mesh_nodes
```

Schema names are sanitized: lowercased, non-alphanumeric replaced with underscore, digit-leading names prefixed with underscore.

Discovery queries `information_schema.schemata` for schemas containing a `mesh_nodes` table.

## Cosmos DB

Each partition gets a container pair: `{segment}-nodes` and `{segment}-partitions`. Containers are created with `CreateContainerIfNotExistsAsync` (idempotent).

Container names are sanitized: lowercased, non-alphanumeric replaced with hyphen, padded to minimum 3 characters, truncated to fit suffix constraints.

Discovery lists all containers and identifies partitions by the `-nodes` suffix convention.

# Registration

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

Each registration method calls `AddPartitionedCoreAndWrapperServices()` which registers:
- `RoutingPersistenceServiceCore` as `IStorageService`
- `RoutingMeshQueryProvider` as `IMeshQueryProvider`
- `StaticNodeQueryProvider` for static node providers
- Scoped `PersistenceService` and `MeshQuery` wrappers

# Key Design Decisions

**Full paths preserved everywhere.** No path stripping occurs. `Cornerstone/Article` is stored with that full path inside the ACME partition. This simplifies the implementation and avoids path translation bugs.

**Thread-safe provisioning.** `RoutingPersistenceServiceCore` uses `ConcurrentDictionary` with a `SemaphoreSlim` to ensure each partition is provisioned exactly once, even under concurrent access.

**Scoped fan-out for search.** When searching across all partitions (`parentPath == null`), each partition's search is scoped to its own segment to avoid duplicate results from shared storage adapters.

**Cross-partition moves.** Moving a node between partitions (e.g., `ACME/Doc` to `Contoso/Doc`) performs a read-write-delete sequence: read from source, write to target, delete from source.

# Source Files

- `src/MeshWeaver.Hosting/Persistence/PathPartition.cs`
- `src/MeshWeaver.Hosting/Persistence/IPartitionedStoreFactory.cs`
- `src/MeshWeaver.Hosting/Persistence/RoutingPersistenceServiceCore.cs`
- `src/MeshWeaver.Hosting/Persistence/Query/RoutingMeshQueryProvider.cs`
- `src/MeshWeaver.Hosting/Persistence/FileSystemPartitionedStoreFactory.cs`
- `src/MeshWeaver.Hosting/Persistence/PersistenceExtensions.cs`
- `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionedStoreFactory.cs`
- `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlExtensions.cs`
- `src/MeshWeaver.Hosting.Cosmos/CosmosPartitionedStoreFactory.cs`
- `src/MeshWeaver.Hosting.Cosmos/PersistenceExtensions.cs`
