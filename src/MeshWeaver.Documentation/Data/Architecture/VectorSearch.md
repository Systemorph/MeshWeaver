---
Name: Vector Search
Category: Architecture
Description: How free-floating text tokens in a mesh query are routed through HNSW cosine similarity on stored embeddings (cloud Cohere or a local on-host model), while structured field-filters stay on the regular SQL path.
Icon: Search
---

# Vector Search

When you type `laptop nodeType:Story namespace:ACME` into a mesh query, something interesting happens: the bare word `laptop` FILTERS the Story nodes in ACME's subtree to those that contain it, and drives a semantic cosine-similarity ranking through Postgres pgvector — which may also admit a node that does not contain the word but is a *close* semantic neighbour of it — while `nodeType:Story` and `namespace:ACME` remain as precise SQL filters.

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

When the parsed query has a non-empty `TextSearch` **and** an `IEmbeddingProvider` is registered, the query is ranked by cosine similarity on **both** routes a query can take:

- **Pinned** (`namespace:X …`, or a path whose first segment names a partition): `PostgreSqlMeshQuery.QueryRowsAsync` intercepts and routes through `PostgreSqlStorageAdapter.VectorSearchAsync`. Structured filters present in the same query are preserved on the WHERE clause of the vector query — see `PostgreSqlSqlGenerator.GenerateVectorSearchQuery`.
- **Unpinned** (the omnibox, the MCP `search` tool and the agent `Search` tool with no `namespace:`): `PostgreSqlPartitionedMeshQuery` embeds the term once and hands the vector to `PostgreSqlCrossSchemaQueryProvider.QueryAcrossSchemasSemanticAsync`, whose UNION gives every partition arm the same hybrid contract — eligible when the row contains *every* typed word *or* is a close semantic neighbour (see "Free text filters" below), ranked by lexical tier then cosine distance, each arm capped at the top-K and merged by the outer ORDER BY (`GenerateCrossSchemaSelectQuery` with a `queryVector`).

🚨 **Until MeshWeaver.Plugins#1493 (2026-09-08) the unpinned route was lexical only.** `GenerateTextSearchClause` demanded every whitespace-separated term as an ILIKE substring of name/path/description/node_type, AND-ed, so a natural-language query returned nothing mesh-wide while the same words pinned to one partition found their documents — measured on both production portals on 2026-09-07 (`asynchronous calls observable subscribe never await task namespace:Doc scope:descendants` → 3 hits; the same words unpinned → 0; the hit count shrank with every added word). The 80k stored embeddings were consulted exactly when a caller already knew where to look. A host with no embedding provider, and the Snowflake fan-out, keep the lexical contract — the table below describes a Postgres host with a provider.

### 🚨 Free text filters; semantics may only order and augment

**A typed word restricts the answer to the nodes that contain it** — every whitespace-separated
word a case-insensitive substring of the node's path (which carries its id), name, description,
node type or one of its content **string values** (never a content property *name*: `status` is a
key of every row). Semantic ranking orders that set, and may add a node that shares no word only
when it is a **close** neighbour: cosine distance at most
`PostgreSqlSqlGenerator.SemanticNeighbourMaxDistance` (0.5, i.e. similarity ≥ 0.5; the SQLite
provider applies the same bar as `SqliteVectorMeshQuery.MinNeighbourSimilarity`). An authored
`sort:` then orders the filtered set (the merge applies `ParsedQuery.EffectiveOrderBy`). The
in-memory `QueryEvaluator` has always FILTERED this way (every word a substring, AND-ed), and the
SQL routes now filter too. One difference in reach remains: `QueryEvaluator.ExtractSearchableText`
walks the node to depth 3, counting the node itself, so it sees the content's TOP-LEVEL string
values (`{"contact":"Jane Roe"}` matches `roe`) but not a nested one
(`{"contact":{"name":"Jane Roe"}}` does not), while the Postgres routes read every string value at
any depth. A word that matches only a deeply nested value is found on Postgres and not in memory.

Until 2026-10-18 the hybrid eligibility read `n.embedding IS NOT NULL OR <lexical match>` — so
**every embedded row was eligible**, and each arm returned its top-K (50) by distance. Under a
structural filter smaller than K a typed word therefore narrowed nothing: measured on a production
search control over a 48-row submission catalog (`namespace:(A/Submissions OR B/Submissions OR
C/Submissions) nodeType:…` plus a company name), the answer was all 48 rows with the match merely
ranked first, and once the tab's `sort:` applied even that was lost. Only an explicit field filter
(`name:*maple*`) narrowed. The same shape answered an MCP `search` and the omnibox.

| Typed | Answer |
|---|---|
| a word some nodes contain | exactly those nodes, plus any close semantic neighbour |
| several words | the nodes that contain all of them, plus close neighbours |
| words no node contains (pinned route) | the nearest nodes within `Embedding:NearestFallbackMaxDistance` (default 0.6), ranked by distance — see below |
| words no node contains, nothing within that limit | nothing — the search control's empty state |
| nothing | the hidden query alone |

`IVectorSearchProvider.Search` with no lexical term is the one nearest-K call left: it asks for
neighbours by definition.

### 🧭 Nearest pages when the words find nothing

A question asked in other words than the page that answers it, or in another language, shares no
word with that page, so it can only arrive as a semantic neighbour. One absolute bar for every case
cannot serve both jobs: measured with `bge-m3` on a guest-handbook space (MeshWeaver.Plugins#4556),
the question `von wem ist das Bild im Gang` sat at cosine distance 0.521 from the page that answers
it — its English form at 0.501 — while an unanswerable `where is the bathroom` sat at 0.517 from an
unrelated page. Raising 0.5 for everyone would admit unrelated rows into searches whose words DO
match, which is the narrowing the bar exists to protect.

So the pinned route (`namespace:X …`, the shape a space agent and a space's search control use)
decides per search, inside one statement (`PostgreSqlSqlGenerator.GenerateVectorSearchQuery`):

1. **Some row in the scope contains every typed word** → unchanged: those rows, plus any close
   neighbour within `SemanticNeighbourMaxDistance` (0.5).
2. **No row in the scope contains every typed word** → the rows within the looser
   `NearestFallbackMaxDistance`, ranked by distance (lexical tier first, as always).
3. **Nothing within that limit either** → nothing.

"No row" is decided over every candidate the structural filter, the access predicate and the
namespace admit — a window flag (`bool_or(_lex) OVER ()`) computed before the `LIMIT`, never from
the rows that happened to make the top-K. With a content index it spans both arms: a document
counts as carrying the words when any of its chunks does, and a document the narrowing answer
carries keeps the chunk it carried before.

The limit is **declared per deployment, next to the model**, because each model puts distances on
its own scale: `Embedding:NearestFallbackMaxDistance` (env `Embedding__NearestFallbackMaxDistance`,
default 0.6, a cosine distance in `[0, 2]`; a value outside that range refuses the host at
composition, naming the key). The startup line `Semantic (vector) search ENABLED: …` prints the
value in force. A value below 0.5 leaves the 0.5 close-neighbour bar in force.

What it does NOT cover:

- **The unpinned cross-schema fan-out** keeps rule 1 alone — "no row matched" over two hundred
  partitions is a different question, and no measurement asked for it.
- **SQLite and Snowflake** keep their close-neighbour bar alone.
- **The in-memory `QueryEvaluator` has no vector route at all** — it filters lexically — so there
  is no in-memory half to keep in parity. The live gate already relaxes the free-text half on a
  semantic host (`PostgreSqlPartitionedMeshQuery.RelevanceShape`, below), so a newly written
  fallback answer re-reads instead of being vetoed by a word test.

### 🚨 The live half: a semantic query must not be gated by a lexical matcher

Ranking the *first* read semantically is only half of it. A `Query` is live — it re-reads when a
change notification looks like it could alter the result set — and that relevance decision is taken
by the **other** executor of the query language, in memory: `PostgreSqlPartitionedMeshQuery`'s
`IsRelevant` asks core's `QueryEvaluator.Matches` whether a newly written node could enter.

`QueryEvaluator.GetFuzzyScore` requires **every** term to be a case-insensitive substring of the
node's searchable text. That is precisely the predicate the vector index replaced. So for one day
after #1493 the two executors disagreed in the direction that drops rows: SQL ranked a semantic
neighbour in, the in-memory gate answered `false` for the same node, the notification was dropped,
and the live query never re-read. **A search that works on first paint and then silently stops
updating is the same "returns nothing" symptom, deferred** — and it is invisible to any test that
only measures the Initial read.

The rule is now one predicate, `TakesSemanticRoute(parsed, semanticCapable)`, read by **both** the
routing site and the relevance gate, so they cannot drift apart again:

- **Semantic query** → the gate evaluates the **structured half only** (`parsed with { TextSearch =
  null }`). `nodeType:`, `namespace:` and every `content.` selector are resolved identically by both
  executors, so the gate stays narrow enough not to re-query on every write
  (Systemorph/MeshWeaver#2194) — but the free-text half is **dropped rather than answered wrongly**.
  Dropping it can only cost an extra re-query, which the serialised re-query path already bounds;
  answering it lexically drops rows.
- **No embedding provider, or a satellite table** (`nodeType:Thread` → `threads`, which carries no
  embedding column) → nothing changes: the SQL is lexical there too, so `Matches` is the right
  question and is asked unchanged. The capability is read as *is an `IEmbeddingProvider`
  registered* — the same `GetService` signal described under the fallback below, which is why
  `NullEmbeddingProvider` must never be registered as a default.
- **A registered provider that returns `null` for one query** (a transient failure) is the one
  asymmetric case: the SQL falls back to lexical for that query while the gate, which decides on
  the host's capability rather than on the outcome of a single embed call, stays widened. That is
  deliberately the safe direction — an extra re-query, never a dropped row.

This is not a new idiom. `SqliteVectorMeshQuery` — the SQLite-local vector provider — has always
filtered its cosine-ranked candidates with `parsed with { TextSearch = null }`, the identical
expression, for the identical reason: once a vector ranks the rows, the evaluator's job is the
structured half and nothing else. The Postgres fan-out's live gate was simply the one place the
pattern had not reached.

Pinned by `SemanticRelevanceGateParityTests` in MeshWeaver.Plugins, which carries the no-provider
control and a `nodeType:` control — without the second, a gate that had been widened to "always
relevant" would score identically on the first.


**Routing examples:**

| Query | TextSearch | Vector path? |
|---|---|---|
| `laptop` | `"laptop"` | yes — the nodes containing "laptop", plus close neighbours, ranked |
| `laptop nodeType:Story` | `"laptop"` | yes — the Story rows containing "laptop" (plus close neighbours), ranked by similarity |
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

The vector path activates transparently when both conditions hold: a bare-text token is present in the query, and an embedding provider is registered in the DI container — whether or not the query names a partition (see the two routes above).

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
var embeddingText = MeshNodeEmbeddingText.Compose(node.Name, node.NodeType, node.Description, contentJson);
var embeddingVector = await _embeddingProvider.GenerateEmbeddingAsync(embeddingText);
// bound to the `embedding` column of the INSERT … ON CONFLICT upsert
```

The text is the node's own words: name and type, description, and the string values of its
content (a Markdown body included), cleaned and capped at 1800 characters. Every writer (Postgres,
Snowflake, SQLite) and the migration's backfill share it. Until MeshWeaver.Plugins#4554 it was
`"{Name} {NodeType}"` only, so a page's vector never carried its body: a paraphrase, a synonym or
a question in another language was never a close semantic neighbour of the page that answered it,
and the free-text gate (every word, or a close neighbour) admitted nothing. The Plugins page
`Hosting/NodeEmbeddingText` carries the rule, the re-embed of stored vectors (migration V61 plus
the always-run backfill) and the tests.

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

### 🚨 The fallback is announced, not silent

Degrading to ILIKE is a supported configuration, not a fault — but a capability that degrades in
silence is indistinguishable from one that is broken. `TryAddEmbeddingProvider` therefore records
the decision as a singleton **`EmbeddingCapability`** and registers `EmbeddingCapabilityReporter`,
which writes exactly **one** `Information` line at host start, in *both* directions:

```
Semantic (vector) search ENABLED: provider=Ollama, endpoint=…, model=bge-m3, dimensions=1024.
Semantic (vector) search DISABLED — no Embedding:Endpoint is configured. Free-text queries fall
back to an ILIKE substring scan and content indexing stays inert; this is a supported
configuration, not a fault.
```

A line emitted only when the capability is ON would be no better than the silence, so the disabled
branch logs too — and names the configuration key that would turn it on (`Embedding:Endpoint`, or
`Embedding:ApiKey` for the keyed cloud backend). Before this, an operator seeing plainly-lexical
results could not tell "never configured" from "configured and failing", which is how issue #1642
came to be triaged from a stack trace.

`EmbeddingCapability.IsEnabled` is taken from whether a provider was *actually* created, never
re-derived from the options — a second copy of `CreateEmbeddingProvider`'s branch logic could drift
and then advertise a capability the host does not have.

🚨 **Do not "fix" the null by registering `NullEmbeddingProvider` as a default.** The PRESENCE of an
`IEmbeddingProvider` registration is the capability signal every consumer reads — both storage
backends resolve it with `GetService`, and the content-indexing module's resolve-time `enabledWhen`
gate asks the same question. A Null default answers *yes* on a host that has no embeddings, which
lights the indexing pipeline up against an embedder that can never embed.

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
| `Embedding:NearestFallbackMaxDistance` | cosine distance (default 0.6) within which the nearest pages answer a pinned free-text search whose words match no page — calibrate per model (see "Nearest pages when the words find nothing") |

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

**1. Hybrid recall — un-embedded rows do not disappear.** When the query carries a bare-text term, `GenerateVectorSearchQuery` makes a row eligible if it lexically contains every typed word **OR** it is a close semantic neighbour — an un-embedded row is found by its words exactly as before:

```sql
WHERE ( -- every word, bound one by one as @lexTerm0..n, compared literally (strpos, never LIKE)
        ( strpos(LOWER(path || name || description || node_type), LOWER(@lexTerm0)) > 0
          OR strpos(LOWER(<content string values>), LOWER(@lexTerm0)) > 0 )
        AND …
      OR (n.embedding IS NOT NULL AND (n.embedding <=> @queryVector) <= 0.5) )
```

On the pinned route that test is written as a wrap over the candidates, so the fallback above can
switch on when no candidate carries the words:

```sql
SELECT n.* FROM (
  SELECT r.*, bool_or(r._lex) OVER () AS _any_lex
  FROM (<candidates: structure + access + namespace, projecting _distance and _lex>) r
  WHERE r._lex OR r._distance <= @nearestFallbackMaxDistance) n
WHERE n._lex OR n._distance <= 0.5 OR NOT n._any_lex
ORDER BY <lexical tier>, n._distance ASC NULLS LAST LIMIT @topK
```

Only a *pure-semantic* call (no lexical term — i.e. `IVectorSearchProvider.Search` invoked with no text to blend) keeps the embedding-only filter. The `ORDER BY` then puts an exact name match first, then name-prefix, then id-prefix, then name-substring, with cosine distance breaking ties inside each tier — so typing an exact node name cannot be buried past the `LIMIT` by a semantically closer neighbour.

**2. `MeshNodeEmbeddingBackfill` — the general backfill exists.** The migration (`Memex.Database.Migration/Program.cs`) runs `DocumentationBackfill` (the `doc` schema) **and** `MeshNodeEmbeddingBackfill`, which walks *every* schema holding a `mesh_nodes` table, reconciles the `embedding` column to the provider's dimension (resizing + rebuilding the HNSW index), and embeds every row with a NULL embedding from `name + node_type` — the same text the write path uses. It runs whenever a provider is configured, is idempotent (only NULL-embedding rows are touched), and logs-and-skips individual embedding failures rather than aborting the migration.

Consequences:

- **New / edited nodes** embed automatically.
- **Pre-existing, untouched nodes** are still findable lexically immediately, and become semantically rankable once the backfill migration has run.
- Enabling a provider is therefore an **enhancement, not a cliff** — but until the backfill runs, older rows only match on name/id/description substrings.

### 🚨 A model change never breaks a query — stale columns degrade to lexical, by name

pgvector refuses to compare vectors of different dimension: `a <=> b` raises
`22000: different vector dimensions N and M`, and the error fails the **whole statement**. A
free-text search is one statement over many partition schemas, and over their `content_chunks`
too, so a single column still holding the previous model's vectors used to fail every free-text
search on the instance (MeshWeaver#7302: measured on memex when its model moved from a
1536-dimension model to `bge-m3`, 1024 — the portal embedded queries with the new model while
`mesh_nodes` columns were re-sized schema by schema by the backfill, and `content_chunks` not at
all).

The rule since then (`EmbeddingColumnDimensions`, MeshWeaver.Plugins): before a semantic search
compares anything, it reads the **declared** dimension of every `embedding` column the statement
would touch (the `vector(N)` typmod; pgvector enforces it on every stored row, so it is exact for
the whole column — one catalog query per table). An arm whose column matches the query vector's
length ranks semantically; every other arm stays in the result and answers **lexically** (every
typed word required, NULL distance, so it sorts after the semantic hits of its tier). Both routes
apply it: the cross-partition fan-out (`GenerateCrossSchemaSelectQuery`, per schema) and the pinned
search (`GenerateVectorSearchQuery`, per arm). The degradation is logged as a `Warning`, never
silently:

```
[VectorSearch] DEGRADED to lexical on the cross-partition search for 3 content_chunks column(s): the
query model emits 1024 dimensions and these columns hold another model's vectors [acme=vector(1536), …].
```

It ends per column when that column is re-embedded with the current model:
`MeshNodeEmbeddingBackfill` (migration) for `mesh_nodes`, a Content Indexing **Rebuild** for
`content_chunks`. Until then search answers, partially, and the log says where. What the gate
does **not** see: two models of the SAME dimension — the column records its dimension, not the
model that produced its vectors, so such a switch still mixes vector spaces until the re-embed.

Pinned by `EmbeddingModelSwitchTests` (MeshWeaver.Plugins): partitions on 1536 and 1024 queried
after the switch on both routes, plus a negative control in which the same arm, on the query
model, finds its purely semantic neighbour.

### Apple Intelligence / on-device — not a fit here

There is no Apple service you can call from a containerized .NET portal to get embeddings or a vector index. The Natural Language framework's `NLEmbedding` is in-process macOS/iOS only; the Foundation Models framework (on-device Apple Intelligence) is Swift-only, exposes tool calling but **no embeddings API**, and its vector space wouldn't match the server's index anyway. The local answer is pgvector (already installed via the `pgvector/pgvector:pg17` image) plus a local Ollama embedding model, as above.

---

## Caveats

**First-write race.** A node written and queried in the same millisecond may not appear in results — HNSW indexes are eventually consistent (documented by pgvector). Reads-after-writes via `workspace.GetMeshNodeStream(path)` are unaffected because they hit the row directly.

**Embedding text is Name + NodeType only.** Content body is not embedded today. Two nodes with the same Name and different Content rank identically. Extending `WriteAsyncCore`'s `embeddingText` to include body content is the right fix — but be aware that re-embedding full content on every write is expensive.

**Routing is binary; recall inside the vector path is hybrid.** The *route* decision is still all-or-nothing — `TextSearch` present (+ a provider) → vector SQL; otherwise → regular SQL. But the vector SQL itself is hybrid: a row is in when it contains every typed word or is a close neighbour, and exact/prefix name matches rank ahead of pure-semantic neighbours (see "Free text filters" and "Re-embedding existing content"). What is still missing is issuing both *queries* and merging by score across providers.

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
