---
Name: Query Provider Parity
Category: Architecture
Description: The query language has two implementations in two repositories, and for two releases they disagreed about which selectors exist — silently, in the direction that reads as a clean mesh. What the divergence cost, the fallback that closes most of it, the shared corpus that now pins both providers, and the selectors on which they still differ.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h7"/><path d="M13 6h7"/><circle cx="8.5" cy="6" r="2.5"/><path d="M4 18h7"/><path d="M13 18h7"/><circle cx="15.5" cy="18" r="2.5"/><path d="M12 9v6"/></svg>
---

# Query Provider Parity

**The query language has ONE grammar and TWO executors, and they live in different
repositories.** [`QueryParser`](/Doc/DataMesh/QuerySyntax) turns a query string into an AST in
`MeshWeaver.Mesh.Contract`; what happens to a selector after that is decided twice:

| | executor | where | runs for |
|---|---|---|---|
| **A** | `QueryEvaluator` | core, `src/MeshWeaver.Hosting/Persistence/Query/` | in-memory, FileSystem and static-node hosts; **and, on every backend, live-query relevance and merged-result ordering** |
| **B** | `PostgreSqlSqlGenerator.MapSelector` | MeshWeaver.Plugins, `src/MeshWeaver.Hosting.PostgreSql/` | SQL against the `content` JSONB on every portal |

Nothing compared them. They drifted.

## What the drift was, and why nobody saw it

`MapSelector` has resolved a selector the same way from the start:

```
PropertyMap hit          → n.name, n.node_type, n.display_order, …
starts with "content."   → n.content->'X'->>'Y'
anything else            → n.content->>'{selector}'     ← the fallback
```

`QueryEvaluator` had the first two and **not the third**. `GetDirectPropertyValue` reflected
against the object it was handed — a `MeshNode` — and returned `property?.GetValue(obj)`, so an
unknown selector was `null`. `CompareEqual(null, "Error")` is `false`, so the predicate matched
**nothing**.

That is not "half the queries broke". It is worse: **the same query returned different result sets
on a portal and on a dev Monolith, and the in-memory answer was always the reassuring one.** A
filter that should have found something found nothing, which is indistinguishable from there being
nothing to find.

### The instance that exposed it

AGENTS.md prescribed one instrument for the check that gates a production deploy — *is any NodeType
in `Error`?* — and argued for it by construction: the sweep reads the same field the readiness gate
reads, so the two agree by construction rather than by coincidence.

```
search 'nodeType:NodeType compilationStatus:Error'
```

`compilationStatus` is not a `MeshNode` property. It lives on `NodeTypeDefinition`, inside
`Content`. Measured on a live Postgres mesh, 2026-09-07:

| query | Postgres | QueryEvaluator |
|---|---|---|
| `nodeType:NodeType compilationStatus:Error` | **5** (LinkedIn/Skill, Store/Catalog, Store/Maintenance, Store/Order, Store/Plugin) | **0**, always |
| `nodeType:NodeType compilationStatus:Ok` | **195**, none of the five among them | **0**, always |
| `nodeType:NodeType content.compilationStatus:Error` | the same 5 | the same 5 |

So the bare selector **discriminated on the production path** and was **blind everywhere else** —
and a rehearsal of the sweep on a local or CI mesh was green by construction, proving nothing about
the sweep it was rehearsing. That is the shape AGENTS.md forbids in a CI gate, in AGENTS.md's own
prescribed instrument: *it never ran* and *it passed* paint the same colour.

🚨 **The negated form was worse than blind.** `CompareEqual(null, "Ok")` is `false`, so
`NotEqual` inverted it to `true`: in memory, `-compilationStatus:Ok` matched **every node**, and
`-status:Decommissioned` matched **every deployment**. A filter written to exclude returned the
things it was written to exclude.

## The fix: the fallback, and why the order is that way round

`QueryEvaluator.GetPropertyValue` now resolves the **first hop** as *the object's own property,
else the same name read out of its `Content`* — the rule `MapSelector` always had, so
`compilationStatus` is reachable because the RULE reaches it, not because anybody named it. There
is no special case for `compilationStatus` anywhere.

Three decisions, each of which could have gone the other way:

**1. The node's own field wins; content is only the fallback.** This is `MapSelector`'s order —
`PropertyMap` is consulted before the `n.content->>` default — and the only order that changes
nothing that already works. Content-first would silently re-point every live query whose selector
names both, and `name`, `description`, `category`, `icon`, `order`, `state` and `version` are
common content field names, so `name:Foo` would start filtering on the content's name for a large
part of the mesh. This way round, **the only selectors whose resolution moves are the ones that
resolved to `null` before** — i.e. the ones that matched nothing.

**2. The fallback keys on the property being ABSENT, never on its value being null.** SQL answers
the `n.description` column for `description:` and never falls through, so a node with no
description must keep answering "empty" rather than reaching into its content. This is why
`GetDirectPropertyValue` was split into a presence-reporting `TryGetDirectPropertyValue`: `null`
the value and `null` because there is no such member are different answers, and only the second
opens the fallback.

**3. First hop only.** SQL's default is a single root-level `n.content->>'X'`, so a miss deeper in
a `content.a.b` walk stays a miss rather than re-entering some nested object's own `Content`.

### What this changes on a running portal

The evaluator is **not** dev-only. `PostgreSqlPartitionedMeshQuery` holds one for `IsRelevant` —
the live-query change-feed filter — and `MeshQuery.ClipMergedInitial` builds one to order merged
results on **every** backend. So before the fallback, a portal could run a Postgres query whose
predicate discriminated while the in-memory relevance filter for the *same* query did not, and a
`sort:` on a content field (`sort:CreatedAt-desc`, the notification bell) was a silent no-op in the
merge step. Both converge now.

### The fallback made the sort comparator's partiality reachable

`sort:` resolves through the same `GetPropertyValue`, so the fallback widened it from *node fields
and explicitly dotted `content.X`* to *any selector*. JSON has no schema: the same key arrives as a
string on one node and a number on another, and `Comparer<object>.Default` answers that pair with

```
InvalidOperationException: Failed to compare two elements in the array
 ---> ArgumentException: Object must be of type String
```

— which would turn a widened selector into a **failed query** rather than a differently-ordered
one, on the merge path (`MeshQuery.ClipMergedInitial`) that every backend runs. The same latent
hole already swallowed `int` against `long`.

`OrderResults` now orders through a **total** comparer. Uniform keys keep their existing order
exactly (same type + `IComparable` is dispatched first and untouched); only pairs the default
comparer *refused* are newly decided — mixed numerics numerically, anything else by its invariant
string form. Postgres has no equivalent hazard, because `n.content->>'X'` is always `TEXT`.

## The corpus: one rule, both providers pinned to it

Prose parity is the state that produced this. The rule is now **data**, in a project both
repositories reference:

`test/MeshWeaver.Fixture/SelectorResolutionCorpus.cs`

Each row is a selector plus the side each provider reads it from and the exact column expression
SQL must emit. Core's `SweepSelectorReachesTheCompileStatusTest` asserts `QueryEvaluator` resolves
every row on its recorded side; the Plugins-side twin asserts `MapSelector` emits every row's
`SqlExpression`. Neither suite can pass while its provider disagrees with the other's recorded
behaviour, and a row whose two sides differ is named out loud rather than merely absent.

This is the shape `QueryRouteClassifier` already uses for the router's routing and refusal rules.

🚨 **Every row is MEASURED behaviour, not intent.** Changing one is a claim that a provider
changed; adding a divergence is a claim that one of them is wrong. Neither is a way to make a test
pass.

### The control that makes the corpus worth having

The test carries a row where a *healthy* type answers `Ok` rather than `Error`, and a control on
the negation arm. Without them, a fallback that had stopped resolving anything — or one that
answered `Error` for everything — would satisfy the headline assertion and score identically.

🚨 **And the assertions stringify through interpolation, never `?.ToString()`.**
`GetPropertyValue(...)?.ToString().Should().Be("Error")` short-circuits the *whole chain* when the
value is null: `.Should()` is never reached and the test passes having asserted nothing. Two
assertions shipped that way and were caught only by running the suite against the un-fixed
evaluator — with the fallback removed they stayed **green**. Removing the fallback now fails 7 of
36; before the assertions were repaired it failed 5.

## What is still divergent

The fallback closes the case where a selector is unknown to **both** providers. It does not close
the opposite one: a selector that is a `MeshNode` property but is **absent from `MapSelector`'s
`PropertyMap`**. There the evaluator reads the property and SQL reads the content field of the same
name — which is empty for essentially every node.

| selector | backing `mesh_nodes` column | closable by widening `PropertyMap`? |
|---|---|---|
| `createdBy` | `created_by` | ✅ |
| `createdDate` | `created_date` | ✅ |
| `lastModifiedBy` | `last_modified_by` | ✅ |
| `desiredId` | `desired_id` | ✅ |
| `syncBehavior` | `sync_behavior` | ✅ |
| `excludeFromContext` | `exclude_from_context` | ✅ |
| `isDefinitionOnly` | — | ❌ `MeshNode`-only |
| `isSatelliteType` | — | ❌ `MeshNode`-only |
| `preRenderedHtml` | — | ❌ `MeshNode`-only |
| `hasExplicitMainNode` | — | ❌ computed, `[JsonIgnore, NotMapped]` |

The first six are the **same defect one column over as Plugins#1310**, where the authorship columns
were left out of a `SELECT` list and every queried node came back with `CreatedBy = null` while the
row held the value. The columns exist and are projected; `PropertyMap` simply does not list them.

🚨 **`createdBy` is in live use** — the chat history selector filters threads by
`createdBy:{userId}`. It happens to work on Postgres for `Thread` nodes because thread *content*
also carries a `createdBy`; for any node type whose content does not, the same selector reads
empty. Fixing it is a MeshWeaver.Plugins change (widen `PropertyMap`), not a core one, and the
corpus is the checklist.

### The free-text divergence, and why it is not closable by widening a map

Everything above is about **selectors**. The bare-text half of a query diverged too, and that one
cannot be fixed by teaching one executor another column name — the two are answering genuinely
different questions:

| | `QueryEvaluator.GetFuzzyScore` (A) | Postgres (B) |
|---|---|---|
| free text | EVERY term must be a case-insensitive **substring** of the node's searchable text | **cosine distance** to the query's embedding, hybridised with a lexical tier |
| a semantic neighbour sharing no literal token | no match | ranked in |
| adding a word to the query | can only ever **narrow** | re-ranks |

Before MeshWeaver.Plugins#1493 both were lexical and agreed. #1493 gave the unpinned fan-out a
vector branch — and *that is what created the divergence*, because A is not dev-only: it is the
live-query relevance gate on **every** backend. SQL ranked a row in; A, asked about the same row,
said no; the change notification was dropped and the live query never re-read.

🚨 **The direction is the dangerous one.** A stops the result set from ever updating, silently, on
the exact queries the vector index was added to serve — and no test of the Initial read can see it.

**A cannot be made to agree**, because it has no embeddings in memory and no business calling an
embedding provider from a change-feed filter. So the resolution is not parity of *answers* but
parity of *scope*: for a query that takes the semantic route, A is asked only the **structured**
half (`parsed with { TextSearch = null }`) — the half both executors resolve identically — and the
free-text half is dropped rather than answered wrongly. One predicate,
`PostgreSqlPartitionedMeshQuery.TakesSemanticRoute`, is read by both the routing site and the gate,
so the two cannot drift; a host with no embedding provider keeps the lexical contract on both sides.

This is the general shape whenever B gains a capability A structurally lacks: **narrow what the
weaker executor is asked, never let it veto on a question it cannot answer.** Erring toward an
extra re-query is bounded; erring toward a veto drops rows and looks like an empty mesh.

See [Vector Search](/Doc/Architecture/VectorSearch) for the routing and the no-provider fallback.

## A third resolver exists

`MeshSearchView.razor.cs` (MeshWeaver.Plugins) carries its own private
`GetPropertyValue(MeshNode, string)` for grid grouping and sorting. It already implements
node-property-then-content — but it falls back when the property's **value** is null, not when the
property is **absent**, so it diverges from both executors on an empty node field. It should
delegate rather than reimplement.

## Reading this before you touch a selector

- Adding a selector to `PropertyMap` in Plugins **changes what an existing query means** on
  Postgres, from "the content field" to "the column". That is usually the fix, and it is never
  silent — add the corpus row in the same change set.
- Adding a public property to `MeshNode` **creates a divergence by default**: the evaluator will
  find it by reflection and SQL will not. Either add it to `PropertyMap` in the paired change or
  record it in `KnownDivergences`.
- A query in a documented procedure, a script, or an in-mesh Code node should use the **explicit
  `content.X` form**. It reaches the field on every backend and every deployed image, and it needs
  no reader to know which of these two executors is running.

## Related

- [Query Syntax Reference](/Doc/DataMesh/QuerySyntax) — the grammar, and the three-step selector
  resolution rule as an author sees it
- [Build Identity Admission](/Doc/Architecture/BuildIdentityAdmission) — the pre-deploy sweep this
  divergence sat under, and what else that sweep cannot see
- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — when a query is the wrong
  instrument regardless of which provider serves it
