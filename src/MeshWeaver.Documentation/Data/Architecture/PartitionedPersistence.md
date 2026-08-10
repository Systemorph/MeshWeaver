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
IStorageAdapter  =  PersistenceService (singleton)
  └─> bundles every registered IPartitionStorageProvider
        ├─> "ACME"    → that provider's IStorageAdapter
        ├─> "Contoso" → that provider's IStorageAdapter
        └─> ...        (pure delegation by path — no cache, no init, no factory wrapper)

IMeshQueryCore  =  MeshQuery (singleton)
  └─> fans out across every registered IMeshQueryProvider
        ├─> StorageAdapterMeshQueryProvider   (pedestrian exact-path probe — always present)
        ├─> the backend's native provider     (PostgreSqlMeshQuery / CosmosMeshQuery / …)
        └─> StaticNodeQueryProvider           (built-in catalogs)
```

There is no `RoutingPersistenceServiceCore` and no `RoutingMeshQueryProvider`. `PersistenceService` delegates by path across the registered providers; query routing is the `MeshQuery` fan-in described in [Query Result Scoring](/Doc/Architecture/QueryResultScoring).

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

## 🚨 Provisioning is EXPLICIT — "no partition, no write"

**A write to an unprovisioned partition is refused; it does not conjure a schema.** Lazy
`CREATE SCHEMA`-on-first-write was deleted from both PostgreSQL write paths
(`PostgreSqlPathRoutingAdapter.RouteWrite` / `CreateAdapterForTable`) because *any* unrecognised
first path segment — NodeType names, reserved words, request URLs — spawned a ghost schema in
production. A write to a partition that does not exist now faults with `42P01`.

The one sanctioned entry point is on `IPartitionStorageProvider`:

```csharp
// Reactive, idempotent (promise-cached), and POOLED on the pg:{adapter} IIoPool.
provider.EnsurePartitionProvisioned(@namespace)      // IObservable<Unit>
        .SelectMany(_ => /* now write */)
        .Subscribe(_ => { }, ex => logger.LogWarning(ex, "provision+write failed"));

provider.PartitionExists(@namespace)                 // IObservable<bool?>  (null = indeterminate)
```

🚨 **Do NOT declare a `PartitionDefinition` node to force a schema into existence.** The router
lowercases a path's first segment (`seg.ToLowerInvariant()`), but a `PartitionDefinition` whose
`Schema` is left null provisions the name **verbatim** — so `"Agent"` creates schema `Agent` while
every write targets `agent`, and you get `42P01` anyway.

The schema DDL runs inside the `IIoPool`, never `Observable.FromAsync` — see
[Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

Creating a **partition-owning node** (a `NodeType` with `OwnsPartition` — `User`, `Space`) is the
one place provisioning happens implicitly, and it is a deliberate, single trigger:
`OwnsPartitionProvisioningValidator` requires the node to be top-level and provisions the schema
*before* the root write. See [Partition Storage Routing](/Doc/Architecture/PartitionStorageRouting).

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

Nodes whose `Content` is a `PartitionDefinition` declare a partition explicitly — a provider surfaces one through its `PartitionDefinition` property so consumers (Global Settings, the Schema view) can list it. 🚨 **A `PartitionDefinition` node is a *description*, not a provisioning trigger** — see the warning under "Provisioning is EXPLICIT" above: leaving `Schema` null provisions the namespace verbatim while writes go to the lowercased name, which is a `42P01` waiting to happen. To make a not-yet-provisioned partition writable, subscribe `EnsurePartitionProvisioned(namespace)`.

```csharp
new MeshNode("ACME", "")
{
    NodeType = "Partition",
    Content = new PartitionDefinition
    {
        Namespace = "ACME",
        Schema = "acme",
        TableMappings = PartitionDefinition.DefaultSegmentTableMappings()
    }
}
```

Use this when a domain has its own backing store (PostgreSQL schema, Cosmos container, dedicated FS subtree). Triggered at startup.

## 3. Backend discovery (runtime, automatic)

A wildcard provider answers for partitions that already exist in the backing store, discovered by scanning it:

- **File system** — top-level directories
- **PostgreSQL** — schemas containing a `mesh_nodes` table
- **Cosmos** — containers ending in `-nodes`

Discovered partitions are served without an explicit `PartitionDefinition`. Use this when the storage layout already encodes partition boundaries (deployed environments, restored backups). **Discovery finds partitions that exist; it does not create them.**

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

Each partition is defined by a `PartitionDefinition` that specifies its namespace, data source, schema, and table mappings. Per-tenant partitions (Space, User) are provisioned by **`OwnsPartitionProvisioningValidator`** when the partition-owning node is created — **not** lazily on first write (that path was deleted; see "Provisioning is EXPLICIT" above) — and no explicit `Partition` MeshNode is emitted. `DefaultPartitionProvider` seeds only `Admin` and `Auth`; the `Portal` / `Kernel` session partitions were removed (compilation and script execution are Activities inside the owning partition). Tenant partitions get their satellite routing from `PartitionDefinition.DefaultSegmentTableMappings()`.

## Satellite Sub-Namespaces

Satellite entities are stored in dedicated sub-namespaces within the node hierarchy. Each satellite type has a reserved prefix:

| Sub-namespace | PostgreSQL table | Node types | Description |
|---|---|---|---|
| `_Activity` | `activities` | Activity | Node lifecycle events |
| `_UserActivity` | `user_activities` | UserActivity | Per-user access tracking |
| `_Thread` | `threads` | Thread, ThreadMessage | Chat / discussion threads |
| `_Tracking` | `annotations` | TrackedChange | Legacy track changes — no longer written (computed from version history) |
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

## Default table mappings

The static `PartitionDefinition.StandardTableMappings` / `NodeTypeToSuffix` dictionaries are **gone**. The defaults now come from `SatelliteTableMapping.Defaults` (`src/MeshWeaver.Mesh.Contract/SatelliteTableMapping.cs`), surfaced as:

```csharp
PartitionDefinition.DefaultSegmentTableMappings()    // segment  → table, for TableMappings
PartitionDefinition.DefaultNodeTypeTableMappings()   // nodeType → table, for NodeTypeTableMappings
```

A partition with no `TableMappings` stores every node in `mesh_nodes`.

# Backend Implementations

## IPartitionStorageProvider

Each backend implements this interface (`src/MeshWeaver.Mesh.Contract/Services/IPartitionStorageProvider.cs`). It is **reactive**, not `Task`-based — there is no `IPartitionedStoreFactory` / `PartitionedStore` pair:

```csharp
public interface IPartitionStorageProvider
{
    string Name { get; }                       // diagnostics / partition listings
    bool   IsReadOnly { get; }                 // read-only seed ⇒ excluded from the write chain
    IStorageAdapter Adapter { get; }           // Write returns null = "not my path, try the next"
    PartitionDefinition? PartitionDefinition => null;
    int    Priority => 0;                      // claim precedence within a specificity band

    IObservable<Unit>  EnsurePartitionProvisioned(string @namespace);
    IObservable<bool?> PartitionExists(string @namespace);   // null = indeterminate
    IObservable<Unit>  DeletePartition(string @namespace);
}
```

Two details that are load-bearing:

- **`Adapter.Write` returning `null` means "not my path"** — that is how `PersistenceService` walks the chain, not an exception.
- **`Priority` exists because registration order is not enough.** Durable backends (Postgres, FileSystem, Cosmos, AzureBlob) return `100` so they always beat the in-memory wildcard catch-all (default `0`) that `AddOrleansMeshServices` registers as a baseline. Without it, a host that wired its durable backend *after* the Orleans defaults silently persisted every node into RAM — acked, searchable nowhere, gone on restart (2026-06-11 prod create-loss).
- **`PartitionExists` returns `bool?`** — `null` means "this provider cannot tell". Callers OR-fold across providers rather than reading an indeterminate answer as "no" (see `PartitionWriteGuardValidator`).

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
│   ├── approvals                    ← _Approval satellite nodes
│   ├── access                       ← _Access satellite nodes (permissions)
│   ├── comments                     ← _Comment satellite nodes
│   └── node_type_permissions        ← 🪦 legacy, empty, read by nothing (#953)
├── schema "acme_versions"           ← History tracking
│   ├── mesh_nodes
│   ├── activities
│   └── ...
├── schema "admin"                   ← Platform partition (version tracking, invites, admin grants)
│   ├── mesh_nodes
│   ├── access
│   └── user_effective_permissions   ← per-schema, denormalized permission fold
├── schema "auth"                    ← access-object lookup MIRROR (trigger-written only)
│   └── mesh_nodes
└── schema "rbuergi"                 ← a user partition (same shape as a Space partition)
    ├── mesh_nodes
    ├── activities
    ├── user_activities
    ├── threads
    ├── approvals
    ├── access
    └── comments
```

> The pre-V27 shared `"user"` schema is **gone** (V27/V31 renamed and unified it into `auth`; every user now has their own partition named after their userId), and the `portal` / `kernel` session schemas were removed with the legacy partitions. `public.mesh_nodes` is empty by design — `public` holds the shared tables (`partition_access`, the top-level index matview, the `ensure_partition_schema` proc). **Nothing writes to `auth` from application code** — `PartitionWriteGuardValidator` blocks it; a trigger populates it.

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
// MeshBuilder extension, not IServiceCollection:
builder.AddPartitionedFileSystemPersistence(baseDirectory);
```

## PostgreSQL

```csharp
services.AddPartitionedPostgreSqlPersistence(connectionString);
```

## Cosmos DB

```csharp
services.AddPartitionedCosmosPersistence(cosmosClient, databaseName);
```

Each registration method calls `AddPartitionedCoreAndWrapperServices()` (idempotent — guarded by a `CoreAndWrapperServicesMarker`), which registers:

- `PersistenceService` as the singleton `IStorageAdapter`, bundling every `IPartitionStorageProvider`
- `StorageAdapterMeshQueryProvider` as an `IMeshQueryProvider` — deliberately `AddSingleton`, **not** `TryAddSingleton`: `TryAdd` is first-wins by service type, so a backend that registered its own `IMeshQueryProvider` first would silently drop the pedestrian exact-path probe (symptom: `PathResolver` returns null for partition-scoped path queries)
- `StaticNodeQueryProvider` for the built-in catalogs
- `MeshQuery` as `IMeshQueryCore`, and a no-op `IVersionQuery` that PostgreSQL / FileSystem override

# Key Design Decisions

**Full paths preserved everywhere.** No path stripping occurs. `Cornerstone/Article` is stored with that full path inside the Cornerstone partition. This simplifies the implementation and eliminates path-translation bugs.

**Provisioned exactly once, without a lock.** `EnsurePartitionProvisioned` is a **promise-cache**: the `pool.Run(...)` observable is stashed in an *instance* `ConcurrentDictionary<schema, IObservable<Unit>>` (`PostgreSqlPartitionStorageProvider._provisioned`), which is `ReplaySubject`-backed — the first caller kicks the DDL off on the `pg:{adapter}` pool (capped at 1, so the gate *is* the single Npgsql connection) and every later subscriber replays the completed result. 🚨 **There is no `SemaphoreSlim`, and adding one would be a bug**: a hand-rolled async gate parks the hub's single-threaded action block and deadlocks. See [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling).

**Scoped fan-out for search.** When searching across all partitions (`parentPath == null`), each partition's search is scoped to its own first segment to avoid duplicate results from shared storage adapters.

**Cross-partition moves.** Moving a node between partitions (e.g., `ACME/Doc` → `Contoso/Doc`) performs a read-write-delete sequence: read from source, write to target, delete from source.

# Source Files

| File | Purpose |
|---|---|
| `src/MeshWeaver.Hosting/Persistence/PathPartition.cs` | `GetFirstSegment` utility |
| `src/MeshWeaver.Mesh.Contract/Services/IPartitionStorageProvider.cs` | Provider contract (`EnsurePartitionProvisioned`, `PartitionExists`, `DeletePartition`) + `PartitionContexts` |
| `src/MeshWeaver.Mesh.Contract/Services/IStorageAdapter.cs` | The adapter surface every provider hands out |
| `src/MeshWeaver.Hosting/Persistence/PersistenceService.cs` | The singleton `IStorageAdapter` that bundles the providers |
| `src/MeshWeaver.Hosting/Persistence/EmbeddedResourceStorageAdapter.cs` | Embedded-resource adapter |
| `src/MeshWeaver.Hosting/Persistence/EmbeddedResourcePartitionStorageProvider.cs` | Embedded-resource provider |
| `src/MeshWeaver.Hosting/Persistence/FileSystemPartitionStorageProvider.cs` | File-system provider |
| `src/MeshWeaver.Hosting/Persistence/InMemoryPartitionStorageProvider.cs` | In-memory provider |
| `src/MeshWeaver.Hosting/Persistence/PartitionConfigurationExtensions.cs` | Fluent `MeshBuilder.Add*Partition` extensions |
| `src/MeshWeaver.Hosting/Persistence/PersistenceExtensions.cs` | DI helpers (`AddPartitionedCoreAndWrapperServices`) |
| `src/MeshWeaver.Hosting/Persistence/Query/StorageAdapterMeshQueryProvider.cs` | Pedestrian exact-path query provider |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionStorageProvider.cs` | PostgreSQL provider (schema provisioning promise-cache) |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPathRoutingAdapter.cs` | PostgreSQL path → schema/table routing |
| `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlExtensions.cs` | PostgreSQL DI helpers |
| `src/MeshWeaver.Hosting.Cosmos/CosmosPartitionStorageProvider.cs` | Cosmos DB provider |
| `src/MeshWeaver.Hosting.Cosmos/PersistenceExtensions.cs` | Cosmos DB DI helpers |
