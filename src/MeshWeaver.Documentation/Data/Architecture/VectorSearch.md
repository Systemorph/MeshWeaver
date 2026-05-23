---
Name: Vector Search
Category: Architecture
Description: How free-floating words in a mesh query are routed through HNSW cosine similarity on the stored Cohere embedding, vs. structured field-filter queries that stay on the regular SQL path.
Icon: Search
---

# Vector Search

Free-floating words in a `MeshQueryRequest` route through Postgres pgvector's HNSW cosine-similarity index against the `mesh_nodes.embedding` column. Structured field filters (`nodeType:Story`, `namespace:ACME`, `scope:descendants`) stay on the regular SQL path. A single query like `laptop nodeType:Story namespace:ACME scope:descendants` ranks Story rows in ACME's subtree by cosine similarity to "laptop".

## What's wired

The same call sites every consumer was using continue to work — no API change:

```csharp
// Search bar in the portal
meshService.ObserveQuery<MeshNode>(new MeshQueryRequest { Query = "laptop", Limit = 20 });

// MCP `Search` tool (Blazor.AI/McpMeshPlugin.cs)
ops.Search("laptop", basePath: "@graph");

// Agent `Search` tool (AI/MeshPlugin.cs)
ops.Search("laptop");
```

When the parsed query has a non-empty `TextSearch` (bare-text tokens) and an `IEmbeddingProvider` is registered, `PostgreSqlMeshQuery.QueryAsync` intercepts and routes through `PostgreSqlStorageAdapter.VectorSearchAsync`. Structured filters in the same query (`namespace:`, `nodeType:`, `scope:`) are preserved on the WHERE clause of the vector query — see `PostgreSqlSqlGenerator.GenerateVectorSearchQuery`.

## When the vector path fires

| Query | TextSearch | Vector path? |
|---|---|---|
| `laptop` | `"laptop"` | ✅ yes |
| `laptop nodeType:Story` | `"laptop"` | ✅ yes — ranks Story rows by similarity to "laptop" |
| `nodeType:Story namespace:ACME` | `null` | ❌ no — pure structured filter, regular SQL |
| `name:*laptop*` | `null` | ❌ no — explicit LIKE filter, regular SQL |
| `path:ACME scope:descendants` | `null` | ❌ no — pure scope query |

The parser puts bare tokens into `ParsedQuery.TextSearch`; field-shaped tokens (`field:value`) go into `ParsedQuery.Filter`. So a caller can force the regular path by quoting their search as a field filter (`name:laptop`).

## When NO embedding provider is registered

`NullEmbeddingProvider.GenerateEmbeddingAsync` returns `null`. The intercept detects that and falls through to the existing `GenerateTextSearchClause` ILIKE path so callers still get *some* result instead of an empty page. Same behaviour for tests that don't wire an embedding provider — the regular ILIKE path serves them.

## The closed write/read loop

The embedding column has been populated on every `WriteAsync` since the PG adapter shipped:

```csharp
// PostgreSqlStorageAdapter.WriteAsyncCore (paraphrased)
var embeddingText = $"{node.Name} {node.NodeType}";
var vec = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText);
// embeddingVector goes into the INSERT as $13
```

Before vector search was wired, that column was write-only — we paid the embedding HTTP call per write and stored vectors nothing read. Now the same model that generated the column at write time generates the query embedding (the provider is injected from the same DI registration), and the closed loop yields meaningful cosine similarity.

## Schema requirements

* `mesh_nodes.embedding vector({dim})` column — populated by writes
* `idx_mn_embedding ... USING hnsw (embedding vector_cosine_ops)` — the search index
* pgvector extension installed (`pgvector/pgvector:pg17` is the test container)

The dimension `{dim}` is configured via `PostgreSqlStorageOptions.EmbeddingDimensions`. **The provider's `Dimensions` must match the column type, or you'll get an Npgsql cast error on Vector parameter.** The schema initializer migrates the column if the dimensions changed (`PostgreSqlSchemaInitializer.cs:408-419`).

## Capability surface

Two ways to invoke vector search:

**(a) Implicit** — pass a query with bare-text content to any existing call site:

```csharp
meshService.ObserveQuery<MeshNode>(new MeshQueryRequest { Query = "laptop", Limit = 20 });
```

**(b) Explicit** — resolve `IVectorSearchProvider` from DI when you want vector semantics regardless of how the query parses:

```csharp
var vec = sp.GetService<IVectorSearchProvider>();
if (vec is not null)
{
    await foreach (var node in vec.SearchAsync(
        queryText, options, namespacePath: "@graph", topK: 20, ct: ct))
        ...
}
```

Path (b) is registered as a singleton shared with `PostgreSqlMeshQuery` — same instance under both `IMeshQueryProvider` and `IVectorSearchProvider`. Returns `null` from `GetService` when no PG-backed mesh is registered.

## Caveats

- **First-write race:** a node written *and* queried in the same millisecond may not show up — HNSW indexes are eventually consistent (the pgvector docs call this out). Reads-after-writes via `workspace.GetMeshNodeStream(path)` are unaffected because they hit the row directly.
- **Embedding text is only Name + NodeType.** Content body is NOT embedded today. Two nodes with the same Name and different Content rank identically. If you need content-aware search, the right fix is to extend `WriteAsyncCore`'s `embeddingText` — but be aware that re-embedding the full content on every write is expensive.
- **No hybrid yet.** The choice is binary today: TextSearch present → vector; otherwise → regular SQL. A hybrid path (issue both, merge by score) is a real product win and is deferred.
- **Per-user RLS:** `VectorSearchAsync` honours the access-control WHERE clause via the same `userId` parameter the regular `QueryAsync` does — the HNSW index ranks the access-filtered subset, not the full table.

## Tests

`test/MeshWeaver.Hosting.PostgreSql.Test/VectorSearchTests.cs` pins:

1. `IVectorSearchProvider.SearchAsync` returns the bucket-matching node for a deterministic stub embedding.
2. `QueryAsync` with `TextSearch` + namespace filter routes through vector AND preserves the structured filter.
3. Structured-only queries do NOT invoke the embedding provider — the intercept is gated on `TextSearch` non-empty.

`StubEmbeddingProvider` maps text → sparse 1536-dim float vectors via `text.GetHashCode() % 1536`. Same input → same vector. Sufficient for wiring tests; not realistic semantics.

## Why ILIKE wasn't enough

The previous text-search path used `LOWER(name||path||description||node_type) ILIKE '%term%'` per term. Two problems:

1. **No semantic match.** A search for "phone" wouldn't surface a node named "iPhone 15" or "smartphone review".
2. **ILIKE doesn't use the regular B-tree index.** Every search did a sequential scan of the entire `mesh_nodes` table — fine for tests, slow for prod with millions of rows.

HNSW gives sub-linear search time AND semantic ranking. The infrastructure was already there; it just wasn't called.
