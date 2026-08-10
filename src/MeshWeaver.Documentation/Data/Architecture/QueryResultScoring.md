---
Name: Query Result Scoring
Description: "How MeshQuery merges and ranks multi-provider results using per-provider scores, sort dimensions, and PostgreSQL scoring components."
---

# Query Result Scoring

Every `path:` / `namespace:` / `nodeType:` / `source:` query in the mesh flows through `MeshQuery`. It fans out to every registered `IMeshQueryProvider`, collects their results, and emits a single sorted `QueryResultChange<T>` to the caller. This page explains how that merge orders results — the contract each provider must follow, and the sorting rules the aggregator applies.
<svg viewBox="0 0 760 310" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
<defs>
<marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3" orient="auto">
<path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
</marker>
</defs>
<rect x="1" y="1" width="758" height="308" rx="10" fill="none" stroke="currentColor" stroke-opacity=".15" stroke-width="1"/>
<rect x="30" y="120" width="130" height="48" rx="8" fill="#5c6bc0"/>
<text x="95" y="140" text-anchor="middle" fill="#fff" font-weight="bold">MeshQuery</text>
<text x="95" y="158" text-anchor="middle" fill="#fff" font-size="11">fan-out</text>
<line x1="160" y1="144" x2="210" y2="84" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="160" y1="144" x2="210" y2="144" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="160" y1="144" x2="210" y2="204" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<rect x="210" y="58" width="160" height="52" rx="8" fill="#1e88e5"/>
<text x="290" y="79" text-anchor="middle" fill="#fff" font-weight="bold">PostgreSQL</text>
<text x="290" y="97" text-anchor="middle" fill="#fff" font-size="11">prefix 100 · sub 50 · prox 40</text>
<rect x="210" y="118" width="160" height="52" rx="8" fill="#26a69a"/>
<text x="290" y="139" text-anchor="middle" fill="#fff" font-weight="bold">StaticNodeQuery</text>
<text x="290" y="157" text-anchor="middle" fill="#fff" font-size="11">FuzzyScorer / unscored</text>
<rect x="210" y="178" width="160" height="52" rx="8" fill="#8e24aa"/>
<text x="290" y="199" text-anchor="middle" fill="#fff" font-weight="bold">Custom Provider</text>
<text x="290" y="217" text-anchor="middle" fill="#fff" font-size="11">Scores[ ] or null</text>
<line x1="370" y1="84" x2="420" y2="144" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="370" y1="144" x2="420" y2="144" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<line x1="370" y1="204" x2="420" y2="164" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<rect x="420" y="100" width="150" height="88" rx="8" fill="#f57c00"/>
<text x="495" y="126" text-anchor="middle" fill="#fff" font-weight="bold">ClipMergedInitial</text>
<text x="495" y="148" text-anchor="middle" fill="#fff" font-size="11">1. OrderBy (user intent)</text>
<text x="495" y="164" text-anchor="middle" fill="#fff" font-size="11">2. Score desc</text>
<text x="495" y="180" text-anchor="middle" fill="#fff" font-size="11">3. Insertion order</text>
<line x1="570" y1="144" x2="620" y2="144" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
<rect x="620" y="100" width="118" height="88" rx="8" fill="#43a047"/>
<text x="679" y="130" text-anchor="middle" fill="#fff" font-weight="bold">Sorted Results</text>
<text x="679" y="150" text-anchor="middle" fill="#fff" font-size="11">Skip / Limit</text>
<text x="679" y="168" text-anchor="middle" fill="#fff" font-size="11">select: projection</text>
<text x="679" y="186" text-anchor="middle" fill="#fff" font-size="11">QueryResultChange</text>
<text x="380" y="270" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="12">Each provider scores independently; ClipMergedInitial owns the cross-provider sort.</text>
</svg>

*Query fan-out, per-provider scoring, and aggregated sort pipeline.*

## Result Shape

`QueryResultChange<T>` carries the following fields:

| Field | Purpose |
|---|---|
| `Items` | The result items (typically `MeshNode`). |
| `Scores` | **Optional** parallel array — one `double` per item. Higher = stronger match. |
| `Query` | The parsed query, giving the aggregator access to `OrderBy`. |
| `Version`, `Timestamp` | Bookkeeping for change feeds. |

When `Scores` is `null`, the aggregator pairs **every** item in that batch with `0.0`. Because `OrderByDescending` is stable, an all-unscored result keeps its insertion order — but against a provider that *did* score, an unscored batch competes as score 0 and lands below any positive hit. When non-null, its length **must equal** `Items.Count`. Each provider independently decides whether to score its results.

## Sort Dimensions

`MeshQuery.ClipMergedInitial` is the authoritative sort pass. It runs after every provider's `Initial` emission has arrived and before `Skip` / `Limit` trim the window.

Dimensions are applied in this order:

1. **`ParsedQuery.OrderBy` (when present).** User intent always wins. A query like `... sort:LastModified-desc` sorts by `MeshNode.LastModified` descending via `QueryEvaluator.OrderResults`. Score acts as a tiebreaker within equivalence classes.
2. **Score descending.** When `OrderBy` is absent, score is the sole sort key. Highest score lands at index 0. LINQ's `OrderByDescending` is stable, so equal scores preserve insertion order.
3. **Insertion order** as the final tiebreaker.

After sorting, `Skip` and `Limit` clip the window. The `select:` projection runs last — projected dicts and anonymous types are emitted only at this boundary.

## Per-Provider Scoring Conventions

> **Cross-provider comparability is the key invariant.** Score scales must be comparable across providers for the same query. A `PostgreSqlMeshQuery` name-prefix hit (score 100) should beat a `StaticNodeQueryProvider` plain-listing hit (which emits no score, so it competes as 0) when the same query reaches both providers.

### `StaticNodeQueryProvider`

Source: `src/MeshWeaver.Hosting/Persistence/Query/StaticNodeQueryProvider.cs`

| Query shape | Score |
|---|---|
| Text search (`textSearch:foo` or free-text tokens in the query) | `FuzzyScorer.Score(...)` against the node's `Name` (falling back to `Path`) — fzf-style: ~16 points per matched character plus boundary / consecutive / camelCase / after-separator bonuses. **Not normalised** — the magnitude scales with query length, so a typical 5–10 character term lands in the low hundreds. Non-`MeshNode` items score `0`. |
| Filter / namespace / nodeType only | `Scores = null` — no relevance signal to surface ("give me all Threads in this namespace" is unordered with respect to score). The aggregator then treats every item as 0. |

### `StorageAdapterMeshQueryProvider`

Source: `src/MeshWeaver.Hosting/Persistence/Query/StorageAdapterMeshQueryProvider.cs`

**This provider does not score.** Every `Initial` it emits leaves `Scores` unset (`null`), whatever adapter is wrapped — it uses a fuzzy score internally for its *own* autocomplete/suggestion ordering, but never publishes one on the `QueryResultChange`. Its items therefore enter the merge at 0 and keep their insertion order among themselves.

### `PostgreSqlMeshQuery` and `PostgreSqlPartitionedMeshQuery`

Sources: `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlMeshQuery.cs`, `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlPartitionedMeshQuery.cs`, `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSqlGenerator.cs`

There are **two distinct rankings** in the PostgreSQL layer; do not conflate them.

**(1) The published `Scores[]` are computed in C#**, by `PostgreSqlMeshQuery.ComputeRowScores`, over the rows the initial emission carries:

| Component | Score |
|---|---|
| Name prefix match | `100 - (name.Length - termLength)` — shorter prefix-matched names rank higher |
| Name substring match | `50` |
| Path substring match | `30` |
| Path proximity boost | `PathProximity.ComputeBoost(contextPath, resultPath)` — `40 / (1 + segmentDistance)`, so max 40, decaying with namespace segment distance (`src/MeshWeaver.Mesh.Contract/Query/PathProximity.cs`) |

The three text buckets are **mutually exclusive** (first match wins — prefix, else substring, else path); the proximity boost is then *added*. `ComputeRowScores` returns **`null`** — i.e. no scoring at all — when the item is not a `MeshNode`, or when the query has neither a text term nor a context path, so a purely structured query is deliberately left unranked rather than amplifying a constant 0.

**(2) A separate SQL-side relevance ladder decides which rows survive `LIMIT`**, on its own scale (exact name 1000, name-prefix 600, id-prefix 500, name-substring 300, id-substring 200, description-substring 100) in `PostgreSqlSqlGenerator`. It exists so the database keeps the most relevant rows when it clips, *before* the C# merge ever sees them. It is **not** what lands in `Scores[]`.

Vector search is a third, separate ordering: `GenerateVectorSearchQuery` orders by cosine distance (`n.embedding <=> @queryVector`, with a lexical tier in front when a term is present) and projects `_distance`. Cosine similarity is **not** folded into `Scores[]` — rows returned by the vector path are re-scored by `ComputeRowScores` like any other. See [Vector Search](/Doc/Architecture/VectorSearch).

## Adding a New Scored Provider

To hook into the aggregator's ranking:

1. Compute one numeric score per result item inside your provider.
2. When building the `Initial` `QueryResultChange<T>`, set `Scores = items.Select(ComputeScore).ToList()`.
3. Choose a scale that won't be drowned out by the PostgreSQL bonuses (100 / 50 / 30) when the same query reaches both. If you can't reasonably rank, set `Scores = null`.

## Why the Aggregator Owns the Sort

A single provider can rank within its own result set, but cross-provider tie-breaking requires a single decision point. A PostgreSQL hit with name-prefix score 100 must beat a static-catalog hit with score 0, even though both `Initial` emissions arrive independently. Placing the sort in `ClipMergedInitial` guarantees that every downstream consumer of `Query<T>` / `QueryAsync` sees the same deterministic ranking regardless of which providers contributed.

## Legacy: The "Writable First, Static Last" Ordering

Before the current scoring contract, `MeshQuery.MergeProviderObservables` ordered provider buckets as *writable-persistence first, static catalog last* to prevent static entries from crowding out user content under a `limit:` clause. That heuristic was a stand-in for proper scoring. With per-provider `Scores` it is gone — the merge is now a flat concat with no priority shuffle: PostgreSQL sets a high score for relevant rows, the static catalog leaves `Scores` null for filter-only matches (so its items enter at 0), and `Limit` clips exactly the right tail.

## See Also

- [AggregatingProviders.md](/Doc/Architecture/AggregatingProviders) — the broader pattern for multi-provider aggregation in MeshWeaver (autocomplete, menus, search).
- [QuerySyntax.md](/Doc/DataMesh/QuerySyntax) — the query language `OrderBy` / `Skip` / `Limit` semantics.
- `src/MeshWeaver.Data/Completion/FuzzyScorer.cs` — the fzf-style scorer used by the static provider for text-search queries.
- `src/MeshWeaver.Mesh.Contract/Query/PathProximity.cs` — the namespace-distance boost used by the PostgreSQL providers.
