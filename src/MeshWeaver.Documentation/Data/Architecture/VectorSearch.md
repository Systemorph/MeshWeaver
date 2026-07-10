---
Name: Vector Search
Category: Architecture
Description: How free-floating text tokens in a mesh query are routed through HNSW cosine similarity on stored embeddings (cloud Cohere or a local on-host model), while structured field-filters stay on the regular SQL path.
Icon: Search
---

# Vector Search

When you type `laptop nodeType:Story namespace:ACME` into a mesh query, something interesting happens: the bare word `laptop` drives a semantic cosine-similarity search through Postgres pgvector's HNSW index, while `nodeType:Story` and `namespace:ACME` remain as precise SQL filters. The result is a ranked list of Story nodes in ACME's subtree — nearest semantically to "laptop" — served in sub-linear time.

This page explains how the routing decision is made, what the write/read loop looks like, the schema it depends on, and where the edges are.
<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <polygon points="0 0, 8 3.5, 0 7" fill="currentColor" fill-opacity=".55"/>
    </marker>
  </defs>
  <rect x="10" y="120" width="140" height="48" rx="10" fill="#5c6bc0"/>
  <text x="80" y="140" text-anchor="middle" fill="#fff" font-weight="bold">Query Input</text>
  <text x="80" y="158" text-anchor="middle" fill="#fff" font-size="11">e.g. laptop nodeType:Story</text>
  <line x1="150" y1="144" x2="195" y2="144" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <rect x="197" y="108" width="130" height="72" rx="10" fill="#37474f" stroke="currentColor" stroke-opacity=".35" stroke-width="1"/>
  <text x="262" y="130" text-anchor="middle" fill="#fff" font-weight="bold">Token Parser</text>
  <text x="262" y="150" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">bare text → TextSearch</text>
  <text x="262" y="166" text-anchor="middle" fill="currentColor" fill-opacity=".7" font-size="11">field:value → Filter</text>
  <line x1="327" y1="120" x2="380" y2="68" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <line x1="327" y1="168" x2="380" y2="220" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <rect x="382" y="30" width="160" height="76" rx="10" fill="#1e88e5"/>
  <text x="462" y="55" text-anchor="middle" fill="#fff" font-weight="bold">Vector Path</text>
  <text x="462" y="73" text-anchor="middle" fill="#fff" font-size="11">TextSearch present +</text>
  <text x="462" y="89" text-anchor="middle" fill="#fff" font-size="11">IEmbeddingProvider registered</text>
  <rect x="382" y="188" width="160" height="60" rx="10" fill="#43a047"/>
  <text x="462" y="213" text-anchor="middle" fill="#fff" font-weight="bold">SQL Path</text>
  <text x="462" y="231" text-anchor="middle" fill="#fff" font-size="11">Structured filters only</text>
  <text x="374" y="158" text-anchor="middle" fill="currentColor" fill-opacity=".6" font-size="11" font-style="italic">TextSearch?</text>
  <line x1="542" y1="68" x2="590" y2="68" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <line x1="542" y1="218" x2="590" y2="218" stroke="currentColor" stroke-opacity=".55" stroke-width="2" marker-end="url(#arr)"/>
  <rect x="592" y="30" width="155" height="76" rx="10" fill="#26a69a"/>
  <text x="669" y="55" text-anchor="middle" fill="#fff" font-weight="bold">HNSW cosine</text>
  <text x="669" y="73" text-anchor="middle" fill="#fff" font-size="11">pgvector index +</text>
  <text x="669" y="89" text-anchor="middle" fill="#fff" font-size="11">access-control WHERE</text>
  <rect x="592" y="188" width="155" height="60" rx="10" fill="#26a69a"/>
  <text x="669" y="213" text-anchor="middle" fill="#fff" font-weight="bold">ILIKE / B-tree</text>
  <text x="669" y="231" text-anchor="middle" fill="#fff" font-size="11">regular SQL + filters</text>
  <line x1="669" y1="106" x2="669" y2="182" stroke="currentColor" stroke-opacity=".3" stroke-width="1.5" stroke-dasharray="5,4"/>
  <text x="683" y="148" fill="currentColor" fill-opacity=".5" font-size="11">merge</text>
  <text x="669" y="248" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11">same ranked result set</text>
  <text x="669" y="262" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11">to caller</text>
</svg>
*Query routing: bare-text tokens activate HNSW vector search; structured field filters stay on the regular SQL path — both apply access-control and return the same ranked result interface.*

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
meshService.Query<MeshNode>(new MeshQueryRequest { Query = "laptop", Limit = 20 });

// MCP `Search` tool (Mcp/McpMeshPlugin.cs)
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
vec?.Search(queryText, options, namespacePath: "@graph", topK: 20)
    .Subscribe(nodes => ..., ex => logger.LogWarning(ex, "vector search failed"));
```

> `Search` is reactive — one snapshot emission of the top-K nodes. The embedding round-trip and the HNSW SQL pump run inside the provider's `IIoPool`; cancellation is subscription disposal. There is no `IAsyncEnumerable` surface.

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

## Provider backends

`IEmbeddingProvider` has three implementations; the active one is chosen by the `Embedding:Provider` config key and wired by `PostgreSqlExtensions.AddEmbeddings(EmbeddingOptions)`:

| `Embedding:Provider` | Implementation | Backend | Needs |
|---|---|---|---|
| `AzureFoundry` *(default)* | `AzureFoundryEmbeddingProvider` | Cohere `embed-v4` via Azure AI Foundry (cloud) | `Endpoint` **and** `ApiKey` |
| `Ollama` / `OpenAICompatible` | `OllamaEmbeddingProvider` | any OpenAI-compatible `/v1/embeddings` — e.g. a local **Ollama** | `Endpoint` (+ `Model`); no key |
| *(none — no `Endpoint`)* | `NullEmbeddingProvider` | — | falls through to the ILIKE path |

`AddEmbeddings` registers nothing when `Endpoint` is empty (so search stays on ILIKE), and the default cloud path additionally needs an `ApiKey`. The same `EmbeddingOptions` is bound by **both** the portal (`Memex.Portal.Distributed/Program.cs`) and the migration (`Memex.Database.Migration/Program.cs`) — they must agree, because the migration sizes the pgvector column from `Embedding:Model` and the portal generates the query vectors.

### Config keys

| Key | Meaning |
|---|---|
| `Embedding:Provider` | backend selector (table above) |
| `Embedding:Endpoint` | provider URL — for Ollama the OpenAI-compatible base, e.g. `http://ollama:11434/v1` |
| `Embedding:Model` | model name; drives the column dimension |
| `Embedding:ApiKey` | required for `AzureFoundry`; ignored by Ollama (a dummy bearer is sent) |
| `Embedding:Dimensions` | override; otherwise auto-derived from `Model` |
| `Embedding:TimeoutSeconds` | OpenAI-compatible request timeout (default 30) — a finite bound so a hung leaf never pins an `IIoPool` slot |

Model → dimension defaults: `embed-v-4-0`=1536, `text-embedding-3-large`=3072, **`bge-m3`=1024**, `nomic-embed-text`=768, `mxbai-embed-large`=1024.

---

## Running embeddings locally (Ollama)

The local/self-host stack already runs **Ollama on the host** for the chat model (the in-cluster `ollama` Service → host gateway). The *same* server hosts embedding models, so vector search runs fully on-host with no cloud round-trip — reuse the server, not the chat model (a generation model makes poor retrieval vectors and has a huge hidden dimension; pull a dedicated embedding model instead).

1. **Pull a dedicated embedding model** into the same Ollama: `ollama pull bge-m3` (1024-dim, multilingual). It coexists with the chat model — one server, two models.
2. **Point both the portal and the migration** at it:
   ```
   Embedding__Provider = Ollama
   Embedding__Endpoint = http://ollama:11434/v1
   Embedding__Model    = bge-m3
   ```
   In the helm chart these flow through `config.memex_portal.Embedding__*` and `config.memex_migration.Embedding__*`.
3. **Restart the portal.** Schema init (`PostgreSqlSchemaInitializer`, run by the portal on connect — *not only* by the migration job) sees the new dimension and re-migrates: `DROP INDEX idx_mn_embedding; ALTER TABLE mesh_nodes ALTER COLUMN embedding TYPE vector(1024) USING NULL;` then rebuilds the HNSW index. This runs for the base schema **and** every already-provisioned partition.
4. **Re-embed existing content** — see the warning below. This step is mandatory, not optional.

> **Why not just point the cloud provider at Azure from local?** `AzureFoundryEmbeddingProvider` builds its `EmbeddingsClient` with **no timeout**. If the configured endpoint is unreachable from the cluster, every bare-text query blocks on the embedding round-trip for the HttpClient default (~100 s) — search appears **frozen**. `OllamaEmbeddingProvider` sets a finite timeout for exactly this reason. Never wire embeddings at an endpoint the cluster can't reach.

### 🚨 The re-embed requirement (don't skip)

Registering *any* provider flips a switch: bare-text queries stop using ILIKE and route to the vector path, whose SQL hard-filters `WHERE embedding IS NOT NULL` (`PostgreSqlSqlGenerator.GenerateVectorSearchQuery`). The per-row ILIKE fallback does **not** exist — ILIKE only returns if the *provider itself* fails for the query embedding.

So a row is searchable only once it has an embedding, and embeddings are written **only at node-write time** (`PostgreSqlStorageAdapter.WriteAsyncCore`). On a stack that previously had no provider, every existing row's `embedding` is NULL. The column re-migration in step 3 also nulls anything that was there. Consequences:

- **New / edited nodes** embed automatically and become vector-searchable.
- **Pre-existing, untouched nodes** stay NULL → they **vanish from search** until re-written.
- There is **no general mesh-node backfill** today. `DocumentationBackfill` (in the migration) re-embeds only the `doc` schema; ordinary mesh nodes have no equivalent.

Therefore: enabling a provider **without** first re-embedding existing content makes search *worse* (empty results instead of ILIKE substring hits). A one-time re-embed — iterate every partition's `mesh_nodes`/satellite rows with a NULL embedding, compute the vector via the provider on the `IIoPool`, and `UPDATE` the column — is the missing piece that makes "turn on local vectors" actually deliver. Treat the provider config and the backfill as a single change set.

### Apple Intelligence / on-device — not a fit here

There is no Apple service you can call from a containerized .NET portal to get embeddings or a vector index. The Natural Language framework's `NLEmbedding` is in-process macOS/iOS only; the Foundation Models framework (on-device Apple Intelligence) is Swift-only, exposes tool calling but **no embeddings API**, and its vector space wouldn't match the server's index anyway. The local answer is pgvector (already installed via the `pgvector/pgvector:pg17` image) plus a local Ollama embedding model, as above.

---

## Caveats

**First-write race.** A node written and queried in the same millisecond may not appear in results — HNSW indexes are eventually consistent (documented by pgvector). Reads-after-writes via `workspace.GetMeshNodeStream(path)` are unaffected because they hit the row directly.

**Embedding text is Name + NodeType only.** Content body is not embedded today. Two nodes with the same Name and different Content rank identically. Extending `WriteAsyncCore`'s `embeddingText` to include body content is the right fix — but be aware that re-embedding full content on every write is expensive.

**No hybrid path yet.** The routing decision is binary: `TextSearch` present → vector; otherwise → regular SQL. A hybrid path (issue both, merge by score) is a meaningful product improvement and is deferred.

**Per-user access control is honoured.** `VectorSearchAsync` applies the same access-control WHERE clause via the `userId` parameter that regular `QueryAsync` uses — the HNSW index ranks the access-filtered subset, not the full table.

---

## Tests

`test/MeshWeaver.Hosting.PostgreSql.Test/VectorSearchTests.cs` pins three behaviours:

1. `IVectorSearchProvider.Search` returns the bucket-matching node for a deterministic stub embedding.
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
