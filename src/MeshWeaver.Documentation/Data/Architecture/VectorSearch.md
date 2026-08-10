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
// PostgreSqlStorageAdapter.BuildUpsertAsync (paraphrased) — shared by
// WriteAsyncCore (single command) and WriteMany (batched), so the two can't drift.
var embeddingText = string.Join(" ",
    new[] { node.Name, node.NodeType }.Where(s => !string.IsNullOrEmpty(s)));
var embeddingVector = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText);
// bound to the `embedding` column of the INSERT … ON CONFLICT upsert
```

Before vector search was wired, that column was write-only — the embedding HTTP call was paid per write, but the stored vectors were never read. Now the same model that generated vectors at write time generates the query embedding (the provider is injected from the same DI registration), and the closed loop yields meaningful cosine similarity.

---

## Schema requirements

Vector search depends on three things being in place:

- `mesh_nodes.embedding vector({dim})` — the vector column, populated by writes
- `idx_mn_embedding ... USING hnsw (embedding vector_cosine_ops)` — the HNSW search index
- pgvector extension installed (`pgvector/pgvector:pg17` is the test container image)

The dimension `{dim}` is configured via `PostgreSqlStorageOptions.EmbeddingDimensions`.

> **The provider's `Dimensions` must match the column type, or you will get an Npgsql cast error on the Vector parameter.** The schema initializer migrates the column automatically when dimensions change — it reads the current `atttypmod` off `mesh_nodes.embedding` and, on a mismatch, runs `DROP INDEX idx_mn_embedding; ALTER TABLE mesh_nodes ALTER COLUMN embedding TYPE vector({dim}) USING NULL;` then rebuilds the HNSW index (`PostgreSqlSchemaInitializer`, in both the base-schema and per-partition DDL blocks).

---

## Fallback when no embedding provider is registered

When `AddEmbeddings` registers nothing, `PostgreSqlMeshQuery` holds a **null** `IEmbeddingProvider`, so the vector intercept is skipped outright and the query takes the `GenerateTextSearchClause` ILIKE path. The intercept is also skipped when a provider *is* registered but returns `null` for this query (a transient failure) — same fallback, so callers get results instead of an empty page. `NullEmbeddingProvider` is the **write**-side stand-in: `PostgreSqlStorageAdapter` substitutes `NullEmbeddingProvider.Instance` when no provider is injected, and its `GenerateEmbeddingAsync` returns `null`, leaving the `embedding` column NULL. Tests that do not wire an embedding provider get the regular ILIKE behaviour automatically.

---

## Provider backends

`IEmbeddingProvider` has three implementations; the active one is chosen by the `Embedding:Provider` config key and wired by `PostgreSqlExtensions.AddEmbeddings(EmbeddingOptions)`:

| `Embedding:Provider` | Implementation | Backend | Needs |
|---|---|---|---|
| `AzureFoundry` *(default)* | `AzureFoundryEmbeddingProvider` | Cohere `embed-v4` via Azure AI Foundry (cloud) | `Endpoint` **and** `ApiKey` |
| `Ollama` / `OpenAICompatible` | `OllamaEmbeddingProvider` | any OpenAI-compatible `/v1/embeddings` — e.g. a local **Ollama** | `Endpoint` (+ `Model`); no key |
| *(none — no `Endpoint`, or `AzureFoundry` without `ApiKey`)* | *(nothing registered)* | — | falls through to the ILIKE path; the adapter writes NULL embeddings via `NullEmbeddingProvider.Instance` |

`AddEmbeddings` registers nothing when `Endpoint` is empty (so search stays on ILIKE), and the default cloud path additionally needs an `ApiKey`. The same `EmbeddingOptions` is bound by **both** the portal (`Memex.Portal.Distributed/Program.cs`) and the migration (`Memex.Database.Migration/Program.cs`) — they must agree, because the migration sizes the pgvector column from `Embedding:Model` and the portal generates the query vectors.

### Config keys

| Key | Meaning |
|---|---|
| `Embedding:Provider` | backend selector (table above) |
| `Embedding:Endpoint` | provider URL — for Ollama the OpenAI-compatible base, e.g. `http://ollama:11434/v1` |
| `Embedding:Model` | model name; drives the column dimension |
| `Embedding:ApiKey` | required for `AzureFoundry` (without it nothing is registered); optional for the OpenAI-compatible provider — it is sent as the bearer when set, and a dummy `ollama` bearer is used when unset |
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
4. **Run the migration** so existing rows get embedded — see "Re-embedding existing content" below. Search keeps working without it (hybrid recall), but pre-existing rows are lexical-only until it runs.

> **Why not just point the cloud provider at Azure from local?** `AzureFoundryEmbeddingProvider` constructs its `EmbeddingsClient` with **no explicit timeout or retry configuration**, so it inherits the Azure SDK defaults (a per-attempt network timeout plus automatic retries) rather than a short bound. If the configured endpoint is unreachable from the cluster, every bare-text query blocks on the embedding round-trip long enough that search appears **frozen**. `OllamaEmbeddingProvider` sets a finite `HttpClient.Timeout` (`Embedding:TimeoutSeconds`, default 30 s) for exactly this reason. Never wire embeddings at an endpoint the cluster can't reach.

### Re-embedding existing content

Rows are embedded **only at node-write time** (`PostgreSqlStorageAdapter.BuildUpsertAsync`), so on a stack that previously had no provider every existing row's `embedding` is NULL — and the column re-migration in step 3 nulls anything that was there. Two mechanisms keep that from breaking search:

**1. Hybrid recall — un-embedded rows do not disappear.** When the query carries a bare-text term, `GenerateVectorSearchQuery` makes a row eligible if it has an embedding **OR** it lexically matches the term:

```sql
WHERE (n.embedding IS NOT NULL
       OR LOWER(COALESCE(n.name,''))        LIKE '%' || LOWER(@lexTerm) || '%'
       OR LOWER(COALESCE(n.id,''))          LIKE '%' || LOWER(@lexTerm) || '%'
       OR LOWER(COALESCE(n.description,'')) LIKE '%' || LOWER(@lexTerm) || '%')
```

Only a *pure-semantic* call (no lexical term — i.e. `IVectorSearchProvider.Search` invoked with no text to blend) keeps the embedding-only filter. The `ORDER BY` then puts an exact name match first, then name-prefix, then id-prefix, then name-substring, with cosine distance breaking ties inside each tier — so typing an exact node name cannot be buried past the `LIMIT` by a semantically closer neighbour.

**2. `MeshNodeEmbeddingBackfill` — the general backfill exists.** The migration (`Memex.Database.Migration/Program.cs`) runs `DocumentationBackfill` (the `doc` schema) **and** `MeshNodeEmbeddingBackfill`, which walks *every* schema holding a `mesh_nodes` table, reconciles the `embedding` column to the provider's dimension (resizing + rebuilding the HNSW index), and embeds every row with a NULL embedding from `name + node_type` — the same text the write path uses. It runs whenever a provider is configured, is idempotent (only NULL-embedding rows are touched), and logs-and-skips individual embedding failures rather than aborting the migration.

Consequences:

- **New / edited nodes** embed automatically.
- **Pre-existing, untouched nodes** are still findable lexically immediately, and become semantically rankable once the backfill migration has run.
- Enabling a provider is therefore an **enhancement, not a cliff** — but until the backfill runs, older rows only match on name/id/description substrings.

### Apple Intelligence / on-device — not a fit here

There is no Apple service you can call from a containerized .NET portal to get embeddings or a vector index. The Natural Language framework's `NLEmbedding` is in-process macOS/iOS only; the Foundation Models framework (on-device Apple Intelligence) is Swift-only, exposes tool calling but **no embeddings API**, and its vector space wouldn't match the server's index anyway. The local answer is pgvector (already installed via the `pgvector/pgvector:pg17` image) plus a local Ollama embedding model, as above.

---

## Caveats

**First-write race.** A node written and queried in the same millisecond may not appear in results — HNSW indexes are eventually consistent (documented by pgvector). Reads-after-writes via `workspace.GetMeshNodeStream(path)` are unaffected because they hit the row directly.

**Embedding text is Name + NodeType only.** Content body is not embedded today. Two nodes with the same Name and different Content rank identically. Extending `WriteAsyncCore`'s `embeddingText` to include body content is the right fix — but be aware that re-embedding full content on every write is expensive.

**Routing is binary; recall inside the vector path is hybrid.** The *route* decision is still all-or-nothing — `TextSearch` present (+ a provider) → vector SQL; otherwise → regular SQL. But the vector SQL itself is hybrid: it ORs in a lexical `LIKE` on name/id/description and ranks exact/prefix name matches ahead of pure-semantic neighbours (see "Re-embedding existing content"). What is still missing is issuing both *queries* and merging by score across providers.

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
