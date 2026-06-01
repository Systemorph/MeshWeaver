---
Name: Query Result Scoring
Description: "How MeshQuery merges and ranks multi-provider results using per-provider scores, sort dimensions, and PostgreSQL scoring components."
---

# Query Result Scoring

Every `path:` / `namespace:` / `nodeType:` / `source:` query in the mesh flows through `MeshQuery`. It fans out to every registered `IMeshQueryProvider`, collects their results, and emits a single sorted `QueryResultChange<T>` to the caller. This page explains how that merge orders results — the contract each provider must follow, and the sorting rules the aggregator applies.

## Result Shape

`QueryResultChange<T>` carries the following fields:

| Field | Purpose |
|---|---|
| `Items` | The result items (typically `MeshNode`). |
| `Scores` | **Optional** parallel array — one `double` per item. Higher = stronger match. |
| `Query` | The parsed query, giving the aggregator access to `OrderBy`. |
| `Version`, `Timestamp` | Bookkeeping for change feeds. |

When `Scores` is `null`, the aggregator treats the batch as **unscored** and falls back to insertion order. When non-null, its length **must equal** `Items.Count`. Each provider independently decides whether to score its results.

## Sort Dimensions

`MeshQuery.ClipMergedInitial` is the authoritative sort pass. It runs after every provider's `Initial` emission has arrived and before `Skip` / `Limit` trim the window.

Dimensions are applied in this order:

1. **`ParsedQuery.OrderBy` (when present).** User intent always wins. A query like `... sort:LastModified-desc` sorts by `MeshNode.LastModified` descending via `QueryEvaluator.OrderResults`. Score acts as a tiebreaker within equivalence classes.
2. **Score descending.** When `OrderBy` is absent, score is the sole sort key. Highest score lands at index 0. LINQ's `OrderByDescending` is stable, so equal scores preserve insertion order.
3. **Insertion order** as the final tiebreaker.

After sorting, `Skip` and `Limit` clip the window. The `select:` projection runs last — projected dicts and anonymous types are emitted only at this boundary.

## Per-Provider Scoring Conventions

> **Cross-provider comparability is the key invariant.** Score scales must be comparable across providers for the same query. A `PostgreSqlMeshQuery` name-prefix hit (score 100) should beat a `StaticNodeQueryProvider` plain-listing hit (score 0) when the same query reaches both providers.

### `StaticNodeQueryProvider`

Source: `src/MeshWeaver.Hosting/Persistence/Query/StaticNodeQueryProvider.cs`

| Query shape | Score |
|---|---|
| Text search (`textSearch:foo` or free-text tokens in the query) | `FuzzyScorer.Score(name, query)` — fzf-style with boundary bonuses, consecutive-character bonus, case bonus, and camelCase boost. Range ~0..1000. |
| Filter / namespace / nodeType only | `0` — the provider's role is to surface seed nodes; ranking is the engine's job when multiple providers contribute. |

### `StorageAdapterMeshQueryProvider`

Source: `src/MeshWeaver.Hosting/Persistence/Query/StorageAdapterMeshQueryProvider.cs`

When the wrapped adapter has a native query layer (Postgres, Cosmos), the adapter computes scores and the provider passes them through unchanged. For plain in-memory or file-system adapters, scores default to `0` — the static catalog handles ranking for those cases.

### `PostgreSqlMeshQuery` and `PostgreSqlPartitionedMeshQuery`

Sources: `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlMeshQuery.cs` and `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSqlGenerator.cs`

The PostgreSQL layer composes a score entirely in SQL so the database ranks rows on the indexed side. The individual components:

| Component | Score | Source |
|---|---|---|
| Name prefix match | `100 - (name.Length - prefixLength)` — shorter prefix-matched names rank higher | `PostgreSqlMeshQuery.cs` |
| Name substring match | `50` | `PostgreSqlMeshQuery.cs` |
| Path substring match | `30` | `PostgreSqlMeshQuery.cs` |
| Path proximity boost | `PathProximity.Score(contextPath, resultPath)` — max 40, decays with namespace segment distance | `src/MeshWeaver.Mesh.Contract/Query/PathProximity.cs` |
| Vector embedding similarity | `1 - (embedding <=> queryEmbedding)` cosine distance, scaled to the same range as the prefix/substring buckets | `PostgreSqlSqlGenerator.GenerateVectorSearchQuery` |

The composite score is the `SUM` of all applicable components for each row. The provider emits `Scores[i]` matching that sum.

## Adding a New Scored Provider

To hook into the aggregator's ranking:

1. Compute one numeric score per result item inside your provider.
2. When building the `Initial` `QueryResultChange<T>`, set `Scores = items.Select(ComputeScore).ToList()`.
3. Choose a scale that won't be drowned out by the PostgreSQL bonuses (100 / 50 / 30) when the same query reaches both. If you can't reasonably rank, set `Scores = null`.

## Why the Aggregator Owns the Sort

A single provider can rank within its own result set, but cross-provider tie-breaking requires a single decision point. A PostgreSQL hit with name-prefix score 100 must beat a static-catalog hit with score 0, even though both `Initial` emissions arrive independently. Placing the sort in `ClipMergedInitial` guarantees that every downstream consumer of `ObserveQuery<T>` / `QueryAsync` sees the same deterministic ranking regardless of which providers contributed.

## Legacy: The "Writable First, Static Last" Ordering

Before the current scoring contract, `MeshQuery.MergeProviderObservables` ordered provider buckets as *writable-persistence first, static catalog last* to prevent static entries from crowding out user content under a `limit:` clause. That heuristic was a stand-in for proper scoring. With per-provider `Scores` it is gone: PostgreSQL sets a high score for relevant rows, the static catalog sets `0` for filter-only matches, and `Limit` clips exactly the right tail.

## See Also

- [AggregatingProviders.md](Doc/Architecture/AggregatingProviders.md) — the broader pattern for multi-provider aggregation in MeshWeaver (autocomplete, menus, search).
- [QuerySyntax.md](Doc/DataMesh/QuerySyntax.md) — the query language `OrderBy` / `Skip` / `Limit` semantics.
- `src/MeshWeaver.Data/Completion/FuzzyScorer.cs` — the fzf-style scorer used by the static provider for text-search queries.
- `src/MeshWeaver.Mesh.Contract/Query/PathProximity.cs` — the namespace-distance boost used by the PostgreSQL providers.
