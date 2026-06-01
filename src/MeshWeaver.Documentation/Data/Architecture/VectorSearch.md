---
Name: Vector Search
Category: Architecture
Description: How free-floating text tokens in a mesh query are routed through HNSW cosine similarity on stored Cohere embeddings, while structured field-filters stay on the regular SQL path.
Icon: Search
---

# Vector Search

When you type `laptop nodeType:Story namespace:ACME` into a mesh query, something interesting happens: the bare word `laptop` drives a semantic cosine-similarity search through Postgres pgvector's HNSW index, while `nodeType:Story` and `namespace:ACME` remain as precise SQL filters. The result is a ranked list of Story nodes in ACME's subtree — nearest semantically to "laptop" — served in sub-linear time.

This page explains how the routing decision is made, what the write/read loop looks like, the schema it depends on, and where the edges are.

---

## Routing: structured vs. vector

The query parser splits tokens into two buckets:

| Token shape | Bucket | Example |
|---|---|---|
| bare text | `TextSearch` | `laptop` |
| `field:value` | `Filter` | `nodeType:Story` |

When the parsed query has a non-empty `TextSearch` **and** an `IEmbeddingProvider` is registered, `PostgreSqlMeshQuery.QueryAsync` intercepts and routes through `PostgreSqlStorageAdapter.VectorSearchAsync`. Structured filters present in the same query are preserved on the WHERE clause of the vector query — see `PostgreSqlSqlGenerator.GenerateVectorSearchQuery`.

**Routing examples:**

| Query | TextSearch | Vector path? |
|---|---|---|
| `laptop` | `"laptop"` | yes — full semantic search |
| `laptop nodeType:Story` | `"laptop"` | yes — ranks Story rows by similarity to "laptop" |
| `nodeType:Story namespace:ACME` | `null` | no — pure structured filter, regular SQL |
| `name:*laptop*` | `null` | no — explicit LIKE filter, regular SQL |
| `path:ACME scope:descendants` | `null` | no — pure scope query |

A caller can force the regular path by expressing the term as a field filter (`name:laptop` instead of bare `laptop`).

---

## Call sites — no API change required

The same call sites every consumer already uses continue to work without modification:

```csharp
// Search bar in the portal
meshService.ObserveQuery<MeshNode>(new MeshQueryRequest { Query = "laptop", Limit = 20 });

// MCP `Search` tool (Blazor.AI/McpMeshPlugin.cs)
ops.Search("laptop", basePath: "@graph");

// Agent `Search` tool (AI/MeshPlugin.cs)
ops.Search("laptop");
```

The vector path activates transparently when both conditions hold: a bare-text token is present in the query, and an embedding provider is registered in the DI container.

---

## Explicit invocation

There are two ways to reach vector semantics:

**(a) Implicit** — pass a query with bare-text content to any existing call site (shown above).

**(b) Explicit** — resolve `IVectorSearchProvider` from DI when you want vector semantics regardless of how the query string parses:

```csharp
var vec = sp.GetService<IVectorSearchProvider>();
if (vec is not null)
{
    await foreach (var node in vec.SearchAsync(
        queryText, options, namespacePath: "@graph", topK: 20, ct: ct))
        ...
}
```

> `IVectorSearchProvider` is registered as a singleton shared with `PostgreSqlMeshQuery` — the same instance appears under both interfaces. `GetService` returns `null` when no PG-backed mesh is registered, so the null check is required.

---

## The closed write/read loop

Every node write has generated an embedding vector since the PG adapter shipped:

```csharp
// PostgreSqlStorageAdapter.WriteAsyncCore (paraphrased)
var embeddingText = $"{node.Name} {node.NodeType}";
var vec = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText);
// embeddingVector goes into the INSERT as $13
```

Before vector search was wired, that column was write-only — the embedding HTTP call was paid per write, but the stored vectors were never read. Now the same model that generated vectors at write time generates the query embedding (the provider is injected from the same DI registration), and the closed loop yields meaningful cosine similarity.

---

## Schema requirements

Vector search depends on three things being in place:

- `mesh_nodes.embedding vector({dim})` — the vector column, populated by writes
- `idx_mn_embedding ... USING hnsw (embedding vector_cosine_ops)` — the HNSW search index
- pgvector extension installed (`pgvector/pgvector:pg17` is the test container image)

The dimension `{dim}` is configured via `PostgreSqlStorageOptions.EmbeddingDimensions`.

> **The provider's `Dimensions` must match the column type, or you will get an Npgsql cast error on the Vector parameter.** The schema initializer migrates the column automatically when dimensions change — see `PostgreSqlSchemaInitializer.cs:408-419`.

---

## Fallback when no embedding provider is registered

`NullEmbeddingProvider.GenerateEmbeddingAsync` returns `null`. The intercept detects this and falls through to the existing `GenerateTextSearchClause` ILIKE path, so callers still get results instead of an empty page. Tests that do not wire an embedding provider get the regular ILIKE behaviour automatically.

---

## Caveats

**First-write race.** A node written and queried in the same millisecond may not appear in results — HNSW indexes are eventually consistent (documented by pgvector). Reads-after-writes via `workspace.GetMeshNodeStream(path)` are unaffected because they hit the row directly.

**Embedding text is Name + NodeType only.** Content body is not embedded today. Two nodes with the same Name and different Content rank identically. Extending `WriteAsyncCore`'s `embeddingText` to include body content is the right fix — but be aware that re-embedding full content on every write is expensive.

**No hybrid path yet.** The routing decision is binary: `TextSearch` present → vector; otherwise → regular SQL. A hybrid path (issue both, merge by score) is a meaningful product improvement and is deferred.

**Per-user access control is honoured.** `VectorSearchAsync` applies the same access-control WHERE clause via the `userId` parameter that regular `QueryAsync` uses — the HNSW index ranks the access-filtered subset, not the full table.

---

## Tests

`test/MeshWeaver.Hosting.PostgreSql.Test/VectorSearchTests.cs` pins three behaviours:

1. `IVectorSearchProvider.SearchAsync` returns the bucket-matching node for a deterministic stub embedding.
2. `QueryAsync` with `TextSearch` and a namespace filter routes through the vector path AND preserves the structured filter.
3. Structured-only queries do **not** invoke the embedding provider — the intercept is gated on `TextSearch` being non-empty.

`StubEmbeddingProvider` maps text to sparse 1536-dim float vectors via `text.GetHashCode() % 1536`. Same input always produces the same vector, which is sufficient for wiring tests without requiring realistic semantics.

---

## Why ILIKE was not enough

The previous text-search path used `LOWER(name||path||description||node_type) ILIKE '%term%'` per term. Two problems made it unsuitable at scale:

1. **No semantic match.** A search for "phone" would not surface a node named "iPhone 15" or "smartphone review".
2. **ILIKE cannot use a B-tree index.** Every search performed a sequential scan of the entire `mesh_nodes` table — acceptable in tests, unacceptable in production with millions of rows.

HNSW gives sub-linear search time and semantic ranking. The vector column was already being written; it just was not being read.

---

## Live query-routing demo

The cell below illustrates how the parser classifies tokens — the same logic `PostgreSqlMeshQuery` uses to decide whether to route through the vector path:

```csharp --render VectorRoutingDemo --show-code
var examples = new[]
{
    ("laptop",                        "\"laptop\"",     true),
    ("laptop nodeType:Story",         "\"laptop\"",     true),
    ("nodeType:Story namespace:ACME", "null",           false),
    ("name:*laptop*",                 "null",           false),
    ("path:ACME scope:descendants",   "null",           false),
};

var rows = examples.Select(e =>
    $"| `{e.Item1}` | `{e.Item2}` | {(e.Item3 ? "**vector**" : "SQL")} |");

var table = string.Join("\n",
    new[]
    {
        "| Query | TextSearch | Path |",
        "|---|---|---|",
    }.Concat(rows));

MeshWeaver.Layout.Controls.Markdown(table)
```
