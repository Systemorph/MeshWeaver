# Query Result Scoring

`MeshQuery` is the boss for every `path:` / `namespace:` / `nodeType:` /
`source:` query in the mesh. It fans out to every registered
`IMeshQueryProvider`, merges their results, and emits a single
`QueryResultChange<T>` to the caller. This page documents how the merge
**orders** that result — the contract every provider conforms to, and the
sort rule the aggregator applies.

## The shape

`QueryResultChange<T>` carries:

| Field | Purpose |
|---|---|
| `Items` | The result items (typically `MeshNode`). |
| `Scores` | **Optional** parallel array — one `double` per item. Higher = stronger match. |
| `Query` | The parsed query, so the aggregator can see `OrderBy`. |
| `Version`, `Timestamp` | Bookkeeping for change feeds. |

When `Scores` is `null` the aggregator treats the batch as **unscored**
(falls back to insertion order). When non-null its length **must equal**
`Items.Count`. Each provider decides whether to score.

## Sort dimensions, in order

`MeshQuery.ClipMergedInitial` is the authoritative sort pass. It runs
*after* every provider's `Initial` has arrived and *before* `Skip` / `Limit`
clip the window. Dimensions:

1. **`ParsedQuery.OrderBy` (when present).** User intent always wins. If
   the query is `... sort:LastModified-desc`, the aggregator sorts by
   `MeshNode.LastModified` descending using `QueryEvaluator.OrderResults`.
   Score is the tiebreaker within OrderBy equivalence classes.
2. **Score descending.** When OrderBy is absent, score is the sole sort
   key. Highest score lands at index 0. LINQ's `OrderByDescending` is
   stable, so equal scores preserve insertion order.
3. **Insertion order** as the final tiebreaker.

Then `Skip` and `Limit` clip the window. `select:` projection runs last —
projected dicts/anon types are emitted only at this boundary.

## Per-provider scoring conventions

Score scales must be **comparable across providers for the same query** —
not absolute. A `PostgreSqlMeshQuery` name-prefix hit (score 100) should
beat a `StaticNodeQueryProvider` plain-listing hit (score 0) when the same
query reaches both.

### `StaticNodeQueryProvider`

Source of truth: `src/MeshWeaver.Hosting/Persistence/Query/StaticNodeQueryProvider.cs`.

| Query shape | Score |
|---|---|
| Text search (`textSearch:foo` or free-text inside the query) | `FuzzyScorer.Score(name, query)` — fzf-style: boundary bonuses, consecutive-char bonus, case bonus, camelCase boost. Range ~0..1000. |
| Filter / namespace / nodeType only | `0` (the provider's role is to surface seed nodes; ranking is the engine's job when both contribute). |

### `StorageAdapterMeshQueryProvider`

Source of truth:
`src/MeshWeaver.Hosting/Persistence/Query/StorageAdapterMeshQueryProvider.cs`.

When the wrapped adapter has a native query layer (Postgres, Cosmos) the
adapter computes scores and the provider passes them through. For plain
in-memory / file-system adapters, scores default to `0` — the static
catalog handles the ranking for those cases.

### `PostgreSqlMeshQuery` and `PostgreSqlPartitionedMeshQuery`

Source of truth: `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlMeshQuery.cs`
+ `src/MeshWeaver.Hosting.PostgreSql/PostgreSqlSqlGenerator.cs`.

The PG layer composes a score in SQL so the database does the ranking on
the indexed side. The pieces:

| Component | Score | Source |
|---|---|---|
| Name prefix match | `100 - (name.Length - prefixLength)` — shorter prefix-matched names rank higher. | `PostgreSqlMeshQuery.cs` |
| Name substring match | `50` | `PostgreSqlMeshQuery.cs` |
| Path substring match | `30` | `PostgreSqlMeshQuery.cs` |
| Path proximity boost | `PathProximity.Score(contextPath, resultPath)` — max 40, decays with namespace segment distance | `src/MeshWeaver.Mesh.Contract/Query/PathProximity.cs` |
| Vector embedding similarity | `1 - (embedding <=> queryEmbedding)` cosine, scaled into the same range as the prefix/substring buckets | `PostgreSqlSqlGenerator.GenerateVectorSearchQuery` |

The composite is `SUM` of the components for each row; the provider emits
`Scores[i]` matching that sum.

## How to add a new scored provider

1. Compute one numeric score per result item in your provider.
2. When you build the `Initial` `QueryResultChange<T>`, set
   `Scores = items.Select(ComputeScore).ToList()`.
3. Pick a scale that won't be drowned out by the PG bonuses (100/50/30) when
   the same query reaches both. If you can't reasonably rank, set `Scores = null`.

## Why the aggregator owns the sort, not each provider

A single provider can rank within itself, but cross-provider tie-breaking
needs a single decision point. A PG hit with name-prefix score 100 must
beat a static-catalog hit with score 0 even though both Initial emissions
arrive independently. Putting the sort in `ClipMergedInitial` means every
downstream consumer of `ObserveQuery<T>` / `QueryAsync` sees the same
deterministic ranking regardless of which providers contributed.

## Legacy: the "writable first, static last" hack

Before this contract, `MeshQuery.MergeProviderObservables` ordered
provider buckets as *writable-persistence first, static catalog last* to
keep static entries from crowding out user content under a `limit:` clause.
That heuristic was a stand-in for proper scoring. With per-provider
`Scores` it's gone: PG sets a high score for relevant rows, the static
catalog sets 0 for filter-only matches, and `Limit` clips exactly the
right tail.

## See also

- `Doc/Architecture/AggregatingProviders.md` — the broader pattern for
  multi-provider aggregation in MeshWeaver (autocomplete, menus, search).
- `Doc/DataMesh/QuerySyntax.md` — the query language `OrderBy` /
  `Skip` / `Limit` semantics.
- `src/MeshWeaver.Data/Completion/FuzzyScorer.cs` — the fzf-style scorer
  used by the static provider for text-search queries.
- `src/MeshWeaver.Mesh.Contract/Query/PathProximity.cs` — the namespace-
  distance boost used by the PG providers.
