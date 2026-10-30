---
Name: Query Syntax Reference
Category: Documentation
Description: GitHub-style query syntax for filtering, searching, and navigating MeshWeaver nodes
Icon: /static/DocContent/DataMesh/QuerySyntax/icon.svg
---

MeshWeaver uses a GitHub-style query syntax — space-separated terms that combine field filters, text search, and structural qualifiers — to find, filter, and navigate nodes across the mesh.

<svg viewBox="0 0 760 220" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="7" refY="3.5" orient="auto">
      <path d="M0,0 L8,3.5 L0,7 Z" fill="currentColor" fill-opacity="0.55"/>
    </marker>
  </defs>
  <rect x="0" y="0" width="760" height="220" rx="12" fill="none"/>
  <text x="380" y="22" text-anchor="middle" font-size="14" font-weight="bold" fill="currentColor" fill-opacity="0.8">Query Term Building Blocks</text>
  <rect x="20" y="40" width="140" height="54" rx="10" fill="#1e88e5"/>
  <text x="90" y="63" text-anchor="middle" fill="#fff" font-weight="bold">Field Filter</text>
  <text x="90" y="81" text-anchor="middle" fill="#fff" font-size="11">nodeType:Organization</text>
  <rect x="20" y="112" width="140" height="54" rx="10" fill="#e53935"/>
  <text x="90" y="135" text-anchor="middle" fill="#fff" font-weight="bold">Negation</text>
  <text x="90" y="153" text-anchor="middle" fill="#fff" font-size="11">-status:Archived</text>
  <rect x="200" y="40" width="140" height="54" rx="10" fill="#f57c00"/>
  <text x="270" y="63" text-anchor="middle" fill="#fff" font-weight="bold">Wildcard / OR</text>
  <text x="270" y="81" text-anchor="middle" fill="#fff" font-size="11">name:Acme*  |  a|b|c</text>
  <rect x="200" y="112" width="140" height="54" rx="10" fill="#8e24aa"/>
  <text x="270" y="135" text-anchor="middle" fill="#fff" font-weight="bold">Scope</text>
  <text x="270" y="153" text-anchor="middle" fill="#fff" font-size="11">namespace:X scope:desc</text>
  <rect x="380" y="40" width="140" height="54" rx="10" fill="#26a69a"/>
  <text x="450" y="63" text-anchor="middle" fill="#fff" font-weight="bold">Sort / Limit</text>
  <text x="450" y="81" text-anchor="middle" fill="#fff" font-size="11">sort:name-desc limit:10</text>
  <rect x="380" y="112" width="140" height="54" rx="10" fill="#5c6bc0"/>
  <text x="450" y="135" text-anchor="middle" fill="#fff" font-weight="bold">Text Search</text>
  <text x="450" y="153" text-anchor="middle" fill="#fff" font-size="11">quarterly report</text>
  <rect x="560" y="40" width="140" height="54" rx="10" fill="#43a047"/>
  <text x="630" y="63" text-anchor="middle" fill="#fff" font-weight="bold">Projection</text>
  <text x="630" y="81" text-anchor="middle" fill="#fff" font-size="11">select:name,nodeType</text>
  <rect x="560" y="112" width="140" height="54" rx="10" fill="#0097a7"/>
  <text x="630" y="135" text-anchor="middle" fill="#fff" font-weight="bold">Source</text>
  <text x="630" y="153" text-anchor="middle" fill="#fff" font-size="11">source:activity</text>
  <line x1="90" y1="94" x2="90" y2="112" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="270" y1="94" x2="270" y2="112" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="450" y1="94" x2="450" y2="112" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="630" y1="94" x2="630" y2="112" stroke="currentColor" stroke-opacity="0.4" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="160" y1="139" x2="304" y2="175" stroke="currentColor" stroke-opacity="0.3" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="340" y1="139" x2="313" y2="175" stroke="currentColor" stroke-opacity="0.3" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="520" y1="139" x2="430" y2="175" stroke="currentColor" stroke-opacity="0.3" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="700" y1="139" x2="456" y2="175" stroke="currentColor" stroke-opacity="0.3" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="270" y="172" width="220" height="38" rx="10" fill="#37474f" stroke="currentColor" stroke-opacity="0.3" stroke-width="1"/>
  <text x="380" y="195" text-anchor="middle" fill="#fff" font-size="13" font-weight="bold">Filtered Node Results</text>
</svg>

*All term types are space-separated and composable — each additional term narrows the result set.*

---

## Basic Syntax

Every query is a sequence of space-separated terms. Three building blocks cover almost every use case:

| Form | Example | Meaning |
|---|---|---|
| Field filter | `nodeType:Organization` | Nodes where field equals value |
| Negation | `-status:Archived` | Exclude nodes where field equals value |
| Text search | `quarterly report` | Full-text match in name and description |

Terms are **case-insensitive** and composable — additional terms narrow the result set.

---

## Field Filters

### Equality

```
nodeType:Organization
name:Acme
status:Active
```

### Negation

```
-status:Archived
-content.apiKey:enc*   # every node whose apiKey does NOT start with 'enc'
```

A negation is the **complement within the same scope**: `-field:value` returns exactly the nodes
`field:value` does not, wildcards included. A node that does not have the field at all is part of
the complement — it does not match `field:value`, so `-field:value` keeps it.

### Wildcard Patterns

Use `*` anywhere in a value to do substring, prefix, or suffix matching:

```
name:*claims*     # Contains 'claims'
name:Acme*        # Starts with 'Acme'
name:*Corp        # Ends with 'Corp'
```

**`*` is the only wildcard.** Every other character in a value is literal, on every backend —
including SQL's `%`, `_` and `\`. `code:50%*` matches `50%off` and not `50xoff`; `code:a_*`
matches `a_b` and not `axb`. The SQL backends escape those characters before they translate `*`
into their own wildcard ([Query Provider Parity](/Doc/Architecture/QueryProviderParity)).

### Quoted values are literal

A value in quotes (`"…"` or `'…'`) is taken **exactly as written**: a `|` inside the quotes is not
alternation and a `*` inside the quotes is not a wildcard. Quotes are also how a value keeps its
spaces.

```
namespace:"acme/Docs/a|b"     # exactly the namespace acme/Docs/a|b — not a OR b
namespace:"acme/report*"      # exactly the namespace acme/report* — not a prefix pattern
-name:"draft*"                # every node whose name is not literally draft*
path:"a/b c"|"a"              # the bar BETWEEN quoted values is still alternation
namespace:("acme/report*")    # a one-value list keeps the quoting: the same literal anchor
-namespace:("acme/report*")   # ... and its negation stays an exclusion of exactly that namespace
```

A one-value list is read as its single value: `-namespace:(acme/Docs)` excludes like
`-namespace:acme/Docs`, never anchors the query at `acme/Docs` (#6752).

So a caller that builds a `namespace:` or `path:` token from a string it does not control (a
document path, a user-typed name) quotes it and gets exactly that one value — provided the string
contains no quote character of the same kind, which still ends the value. Both executors, the
in-memory evaluator and the SQL backends, compare a quoted value literally; the shared
`NegationCorpus` pins it for both (#6634).

### Comparison Operators

```
price:>100        # Greater than 100
price:<50         # Less than 50
price:>=100       # Greater than or equal
price:<=50        # Less than or equal
```

### List Values (OR)

Two equivalent forms let you match any value from a set. Both parse to the same `IN (...)` query AST and produce a single indexed database lookup — not N separate queries.

| Form | Example |
|---|---|
| Verbose (explicit) | `status:(Active OR Pending OR Draft)` |
| Concise (grep-style) | `status:Active\|Pending\|Draft` |

```
# These two are identical:
nodeType:(Organization OR Project)
nodeType:Organization|Project

# Negation works with both forms:
-status:(Deleted OR Archived)
-nodeType:Spam|Trash
```

The `|` form mirrors `grep -E` alternation. Choose whichever reads more naturally in context.

Inside the parentheses `OR` (any case) is the separator only as a **whole word**: a value that merely starts with those letters is a value, so `-namespace:(acme/a OR orders)` excludes both `acme/a` and `orders`, and a negated list excludes every value it names, quoted or not (#6844).

#### Multi-value `path:` — routing-layer batch lookups

When the parser sees `path:a|b|c`, it builds a multi-value path filter that pushes down as a single `WHERE path IN (...)` round-trip. The canonical use is the routing layer's "longest matching prefix" lookup:

```
path:foo/bar/baz|foo/bar|foo sort:length(path)-desc limit:1
```

One Postgres query, one indexed `IN (...)` scan, server-side sort by path length, single row returned.

### Empty Values

```
description:       # Matches nodes with no description
```

### Where a selector resolves

A selector — the token before the `:` — is resolved in **three steps, in this order**:

1. **A field of the node itself.** `name`, `nodeType`, `description`, `category`, `icon`, `order`,
   `lastModified`, `version`, `state`, `id`, `mainNode`.
2. **`content.X`, or `content.X.Y…`** — an explicit walk into the node's content.
3. **Anything else → the content field of the same name.** `compilationStatus` is
   `content.compilationStatus`; `status` is `content.status`.

Step 3 is why `nodeType:User email:alice@acme.com` and
`nodeType:User content.email:alice@acme.com` are the same query. **Prefer the explicit dotted
form** in code and in documented procedures: it says where the field lives, and it reaches it on
every backend and every deployed image — step 3 arrived in the in-memory evaluator late
([#3511](https://github.com/Systemorph/MeshWeaver/issues/3511)), so a portal running an older
platform build answers the bare form only on Postgres.

🚨 **Step 1 wins over step 3, and that matters.** A node whose content carries its own `name`,
`state` or `version` field is still filtered on the NODE's, never the content's — reach the content
one with `content.name`. The order is not an implementation detail: reversing it would silently
re-point every live query whose selector names both.

🚨 **A field that exists but is empty does not fall through.** `description:` matches nodes whose
node-level description is unset; it does not then go looking for a `description` inside the
content.

> **Two implementations, one rule.** The rule above is executed twice — by `QueryEvaluator` for
> in-memory, FileSystem and static-node hosts (and for live-query relevance and result merging on
> every backend), and by `PostgreSqlSqlGenerator` as SQL against the `content` JSONB. They are in
> different repositories, and they drifted: see
> [Query Provider Parity](/Doc/Architecture/QueryProviderParity) for what the drift cost, the shared
> corpus that now pins both, and the selectors on which they still disagree.

---

## Reserved Qualifiers

### `namespace`

Sets the search location (like a folder). Without an explicit `scope`, only immediate children are returned.

```
namespace:Systemorph              # Immediate children of Systemorph
namespace:Systemorph/Marketing    # Immediate children of Marketing
```

Add `scope:descendants` to search recursively:

```
namespace:Systemorph scope:descendants   # All items under Systemorph (recursive)
```

> **`namespace:X` never returns the node at path `X` itself.** A node's namespace is its
> *parent* path — the node at `X` has namespace `X`-minus-last-segment (a top-level node such
> as a user root has namespace `""`), so it can never satisfy `namespace:X`, with **any**
> scope. `scope:subtree` on a namespace therefore degrades to `descendants` at parse time
> (self + descendants of a *namespace* is just its descendants). This is guaranteed by the
> parser and pinned by tests (`Parse_NamespaceWithSubtreeScope_DegradesToDescendants`,
> `NamespaceQuery_NeverReturnsTheNodeAtTheNamespacePath`) — a user's home or a space's item
> list can never show the partition root itself. To include the base node, query
> `path:X scope:subtree` instead.

#### Multi-value `namespace:` — membership across namespaces

`namespace:A|B|C` matches nodes living in **any** of the listed namespaces — a single `n.namespace IN (...)` lookup, not N queries. It is **exact membership**: just the listed namespaces, with no ancestor/descendant graph walk.

```
namespace:rbuergi/Agent|AgenticPension/Agent|Agent nodeType:Agent
# Agent nodes whose namespace ∈ { rbuergi/Agent, AgenticPension/Agent, Agent }
```

This is the canonical **agent-registry** query (`AgentPickerProjection.BuildAgentQuery`): agents live in a dedicated `/Agent` sub-namespace **per partition** — the platform defaults in the bare `Agent` namespace, a space's own under `{space}/Agent`, a user's own under `{user}/Agent` — and the registry simply **lists** the relevant ones directly (no graph search). Models mirror this with `/Model`. A **single** `namespace:X` (no `|`) keeps its usual single-namespace semantics; the membership form is triggered only by the `|` alternation.

### `scope`

Controls the traversal direction relative to `namespace` or `path`:

| Value | Description |
|---|---|
| `descendants` | All descendants recursively (excludes self) |
| `ancestors` | Parent hierarchy upward (excludes self) |
| `hierarchy` | Ancestors + self + descendants |
| `subtree` | Self + all descendants (with `path:`; on `namespace:` it degrades to `descendants` — see the note above) |
| `ancestorsandself` | Self + all ancestors |
| `nextLevel` | The **next populated level** below — the nearest real nodes, skipping empty intermediate namespace segments (alias: `populated`) |

#### `scope:nextLevel` — the populated frontier

`children` returns nodes whose namespace is *exactly* the base path. But a node can sit several
segments deep with no real node in between — e.g. `a/b/node` where `a` and `a/b` are pure namespace
groupings, not nodes. `children` of the root misses it entirely.

`scope:nextLevel` returns the **frontier**: every node strictly below the base for which no *other*
node sits between it and the base. Empty segments are skipped, so:

```
namespace:Org scope:nextLevel    # nearest real nodes below Org
```

- If only `Org/a/b/node` exists, `nextLevel` of `Org` returns `Org/a/b/node` (skips the empty `a`, `a/b`).
- If `Org/a` is also a real node, `nextLevel` of `Org` returns `Org/a`, and `nextLevel` of `Org/a`
  returns `Org/a/b/node`.

This is the drill primitive for graph navigation (above = `scope:ancestors`, below =
`scope:nextLevel`). On Postgres it is a **single** indexed anti-join — no per-child count probes.
It is a *within-partition* scope: pin it to a namespace; an unscoped/cross-partition `nextLevel`
degrades to `descendants`.

### `path`

Sets the base path for search. Without a `scope` modifier, the default is an exact match:

```
path:Systemorph           # The exact Systemorph node
namespace:Systemorph      # Immediate children of Systemorph
```

#### Path Resolution — Longest-Prefix Match

The routing layer uses the query engine to answer "which MeshNode owns the longest matching prefix of this URL path?" The conceptual equivalent in C# is:

```csharp
nodes.Where(node => requestedPath.StartsWith(node.Path))
     .OrderByDescending(node => node.Path.Length)
     .First();
```

The canonical query idiom — backend-agnostic, one round-trip on indexed backends:

```
path:foo/bar/baz scope:ancestorsandself sort:length(path)-desc limit:1
```

- **Postgres** pushes the ancestor set down as a single indexed `IN (...)` lookup with `ORDER BY length(path) DESC LIMIT 1` — one row back.
- **InMemory / FileSystem** walks candidate keys (no path index), bounded at one entry per path segment and cached at the resolver level.
- **Static providers** (built-in roles, agents, partition roots, `AddMeshNodes` seed) filter their in-memory set with the same `StartsWith` predicate.

`scope:ancestorsandself` expands the candidate set to self and ancestors; `sort:length(path)-desc limit:1` collapses it to the deepest match. Both clauses are required. Callers that want the full ancestor chain (breadcrumbs, parent navigation) simply omit `limit:1`.

### `basePath`, and the `@` reference prefix

`basePath:X` is the query-text spelling of the search tools' `basePath` argument: everything
**under** `X` — `namespace:X scope:descendants`, unless the query states its own `scope:`. That
holds for every positive single-location form: a one-value list `basePath:(@acme)` is the same as
`basePath:@acme`, and a wildcard `basePath:acme/*/Contacts` is `namespace:acme/*/Contacts
scope:descendants` (the pattern and everything below it). A multi-value list `basePath:A|B` stays
exact membership, as `namespace:A|B` does.

A location value may carry the `@` reference prefix that tool arguments, markdown links and
autocomplete use; it is resolved before the query runs, so these three are the same query:

```
name:"Jane Doe" basePath:@acme
namespace:@acme scope:descendants name:"Jane Doe"
namespace:acme scope:descendants name:"Jane Doe"
```

This applies to `path:`, `namespace:` and `basePath:` values (each alternative of `path:@a|@b`
too). Only a LEADING `@` is a prefix — a partition named after an address
(`namespace:jane@example.com`) keeps its `@`. Before #6828 the prefix was kept, so `namespace:@acme`
anchored at a partition literally called `@acme`, and `basePath:` was not a qualifier but a filter
on a property no node has: both answered `count: 0` for nodes that exist.

`name:` itself is a case-insensitive **exact** match on the node's `Name`, quoted multi-word
values included (`name:"Jane Doe"` matches `jane doe`, not `Jane Q. Doe`); use `name:*Doe*` for a
contains-match.

### `partitions` — say where to look

Every partition is its own store. A query that names no partition (`path:`/`namespace:` with a
concrete first segment, or the `namespace:A|B` membership and `namespace:*/X` wildcard forms) would
have to UNION every partition schema — the lock-bomb shape behind the 2026-08-31 outage — so a
partitioned backend **refuses** it unless the query declares that spanning partitions is what it
means:

```
nodeType:NodeType content.compilationStatus:Error partitions:all   # every partition the caller can read
```

`partitions:all` is a FLAG. `path:*` is not a synonym: partition resolution reads a path's first
segment as a partition NAME, so `path:*` pins the query to a partition literally called `*` and
answers empty. A declared fan-out still reaches only the *searchable* partitions — the system
schemas (`Admin`, `Auth`, `Kernel`, `Portal`, …) are never enumerated by a fan-out; anchor to reach
them — and only those the caller holds a read grant on.

**The `search` tool refuses the unanchored form outright** (MCP `search`, the agents' `Search`) —
`Error: Query is not sufficiently specified …` naming both remedies — and every envelope it returns
carries `coverage: { scope, partitions }`, the partitions the answer was read from. A `count: 0` is
read against that list, never on its own. Full account:
[Search Coverage and Refusal](/Doc/Architecture/SearchCoverageAndRefusal).

### `sort`

Specifies sort order. Default is ascending; append `-desc` for descending:

```
sort:name               # Name ascending
sort:name-desc          # Name descending
sort:lastModified-desc  # Most recently modified first
sort:content.inception  # A content field, earliest first
sort:content.terms.renewal-desc   # A nested content field, latest first
sort:subtreeLastModified-desc     # Containers by the newest change BELOW them ("last activity")
```

#### Sorting on a content field

`sort:content.<field>` (and `content.a.b` for a nested one) orders by the field's **typed** value,
on every backend, and before `limit:` / paging is applied — the backend's own `ORDER BY` decides
which rows survive the cap, and the merge across partitions (`namespace:(A/X OR B/X)`) orders the
union by the same rule:

| Value | Compares as |
|---|---|
| Number | Numerically — `9 < 10 < 100`, never as text |
| Date, date-time | Its ISO-8601 text (`2027-07-01`, `2027-07-01T09:30:00.0000000Z`) — chronological, and the same key whether the content is a compiled type or as-written JSON |
| Text, boolean, anything else | Text, case-insensitively |
| Null or absent | **Last, in both directions** |

Numbers rank before text when a field holds both. Nulls go last for `sort:x` *and* `sort:x-desc`
so that a row without the field is never the first answer either way (Postgres's own default would
put them first in a descending sort; the generator says `NULLS LAST` explicitly). The same rules
apply to every `sort:` selector, node fields included; an enum node field (`state`, `syncBehavior`)
orders by its number, as its column does. An enum inside content keys on its wire value, as JSON
stores it: a declared member's `[EnumMember]` spelling, even one that starts with a digit (`1st`)
or is empty. Only a value with no member name (undefined, or an unnamable flags combination) keys
as its number. Date-times are reduced to one fixed-width form before
they compare — the serializer trims fractional seconds, so `…08:30:00Z` and `…08:30:00.1Z` sit in
one field, and raw text would put the later one first. Postgres does the same rewrite with string
functions only, for `Z`/`+00:00` and zoneless values, so a value that merely looks like a date
cannot fail the query. The one residual: a non-UTC offset (`+02:00`) is compared as the instant in
memory but as text on Postgres, so in a field that mixes offsets the pre-`limit:` window can
differ. A sort function (`length`, `lower`, `upper`) runs on the value as written. The in-memory
contract is `QuerySortKey`; the Postgres one is `PostgreSqlSqlGenerator.OrderByTerms`
(MeshWeaver.Plugins).

#### Sorting containers by last activity — `sort:subtreeLastModified`

`sort:lastModified` keys a node on its **own** stamp. For a container that is the wrong question:
a client partition root, a project folder or a case is stamped by the platform (a retype, a
provisioning pass, a sync) far more often than by the work done under it, and the work itself
lands on its descendants. Measured on a live portal: a client root read `lastModified`
2026-09-21 while thousands of versions had been written below it since. `source:activity` does
not answer it either — it returns only nodes that carry their own `_activity` satellites (3 of 10
clients there).

`sort:subtreeLastModified` orders by the newest change **anywhere below** the node:

```
nodeType:Crm/Counterparty scope:subtree partitions:all sort:subtreeLastModified-desc   # most recently worked-on client first
namespace:ACME/Projects sort:subtreeLastModified-desc limit:10                 # the ten liveliest projects
```

| | |
|---|---|
| **Key** | The `LastModified` of the newest row the SAME caller gets from `namespace:{node} scope:descendants sort:lastModified-desc` — the node's **strict** descendants, at any depth, exactly as that caller's own descendants query returns them. |
| **Empty container** | No descendant the caller can read → the node's **own** `LastModified`. An empty container ranks by when it was itself touched, never as never-touched. |
| **No key** | No readable descendant and an unset own stamp → **last in both directions**, like every other selector. |
| **Why strict** | The node's own stamp is excluded while anything below it is readable, because a platform re-stamp of a root is not activity: with it included, a root re-stamped yesterday would outrank a client worked on this morning. |
| **Access** | The descendants read runs under the **caller's identity**, through the normal query path, so every backend applies its own row-level security to it. A key can only come from a row the caller could have found with that query themselves — the order a viewer sees says nothing about rows they cannot read. Nothing is read as System on the caller's behalf. |
| **Ties** | Path ascending — a total order, so `skip`/`limit` page rather than sample. |

**How it runs — and what it costs.** No backend can order by this key: it is not a column of the
row, so a provider's `ORDER BY … LIMIT` (and the in-memory provider's pre-cap sort, and the merge's
re-sort) would key every row null and clip in path order — the wrong rows, before any later stage
could see the right ones. So the **query engine** computes it (`MeshQuery`, the
`SubtreeSort` contract in `MeshWeaver.Mesh.Contract`): it runs the query **without** any `sort:`
(cut by the parser's own tokenizer, so a quoted `sort:"…"` goes too) and the window, reads one key per candidate through the same surface and identity (eight at a
time), orders by the `QuerySortKey` contract above, and only then applies `skip` / `limit`. It
therefore works identically on every backend — in-memory, Postgres, any other — with no backend
change, and on Postgres each key read is one partition-pinned `limit:1` read.

The price is **one read per candidate**, and the candidates are the query's whole match set. That
is cheap for what the selector is for — a roster, a project list, tens of rows — and ruinous for
what it is not (`nodeType:Document partitions:all sort:subtreeLastModified-desc limit:20` would
read once per document in the mesh to keep twenty). So it refuses: past
`MeshOperationOptions.SubtreeSortMaxCandidates` candidates (500 by default) the query **fails** with
an error naming the bound and the remedy — narrow the query to the containers you mean to rank —
rather than paying that silently or ordering a subset and presenting it as the whole.

**Live results stay inside the window.** The candidate read is unlimited, so its live deltas
name rows on and off the page alike. The engine keeps every candidate and its key, folds each
delta in (one key read per changed candidate), re-takes the window and emits it as a **`Reset`**
whenever its membership, order or one of its rows changed — so a `limit:2` list never grows to
three, an added container is ranked into the window, and a deleted one is backfilled. A key read
that did not complete (a silent provider, a `SnapshotIncomplete` frame) marks the ranked frame
`SnapshotIncomplete` too: a ranking resting on a partial read is a floor and says so. The
candidate path is written into each key read as a quoted literal, so a space, `|` or `*` in a
path is data, never syntax.

**Two limits worth knowing.** A change below a container does not re-emit the container itself,
so a live list re-ranks on changes to the candidates and when it is re-queried, not on every
descendant write. And the hub-side synced read (`hub.GetQuery`, `workspace.GetQuery`)
hands back a path-keyed SET, as it does for every `sort:` — the order reaches readers of the
query stream itself, which is what `MeshSearchControl` renders. To list containers by last
activity in a page, see [Mesh Search](/Doc/GUI/MeshSearch) → *Listing containers by last
activity*.

#### The default when you write no `sort:`

**A query with no `sort:` and no free-text term is ordered newest first** — as if it carried
`sort:lastModified-desc`. A free-text query is ranked by relevance instead, and a change feed
(`source:activity`, `source:accessed`) keeps its own recency ranking. An authored `sort:` always wins.

The reason is the cap. `limit:` (and the tool surfaces' own caps) keep the FIRST N rows of whatever
order the result is in, and a filtered query — `nodeType:X`, `path:Y scope:descendants`,
`partitions:all nodeType:Hosting/InstanceAction` — has no relevance signal to rank on. Before the
default, that order was whatever the backend enumerated: path-alphabetical on the in-memory walk,
heap order on Postgres. A reader diagnosing an outage then read the newest row of a truncated page as
the newest row there is, and it was six days stale (MeshWeaver #4950). Ordered newest first, a
truncated page is still partial, but it is the part the question was about; unordered, it is a
misleading answer that looks complete. Read `truncated: true` against this the way a `count: 0` is
read against `coverage.partitions` — see
[Search Coverage and Refusal](/Doc/Architecture/SearchCoverageAndRefusal).

The rule is resolved once, on the contract (`ParsedQuery.EffectiveOrderBy`), and applied at every
place that takes a window — the per-provider load cap and the merge — because each clips
independently. The contract and the per-backend halves are in
[Query Result Scoring](/Doc/Architecture/QueryResultScoring).

#### SQL-Function Selectors

Sort selectors accept a small allow-listed set of SQL-style functions — useful when ordering by a derived value rather than a raw column:

```
sort:length(path)-desc   # Longest path first
sort:lower(name)         # Case-insensitive name ascending
sort:upper(nodeType)     # Case-insensitive nodeType ascending
```

> **Allowed functions:** `length`, `lower`, `upper`. Arbitrary SQL is not accepted.

The function-call form composes naturally with the rest of the query, as in the routing-layer example above.

### `limit`

Caps the number of results returned:

```
limit:10    # At most 10 results
limit:50    # At most 50 results
```

### `source`

Switches the data source that backs the query:

```
source:activity    # Only main nodes that HAVE Activity satellites (a change/activity feed),
                   # ordered by most recent activity first
```

When `source:activity` is set:
- Results are the **main content nodes** joined with their `_activity` satellites — a node with no
  recorded activity does not appear.
- Results are ordered by most recent activity, newest first.
- All other filters (`nodeType:`, `namespace:`, text search, etc.) still apply — including the
  `namespace:` guarantee above: `source:activity namespace:{user}` never returns the user node
  itself, even though that node carries its own activity satellites.

```
source:activity nodeType:Thread namespace:ACME scope:descendants   # Recently changed threads in ACME
source:activity nodeType:Document limit:10                         # Last 10 changed documents
```

There is a second source for the caller's own **access recency**:

```
source:accessed    # Only nodes the CALLER has opened, ordered by their last access time
```

`source:accessed` INNER-JOINs the caller's `UserActivity` satellites — the per-user access log.
That log lives in the **caller's own partition** (`{user}/_UserActivity` routes by its first
segment), so on the partitioned backends every branch of the cross-partition fan-out joins that
ONE `user_activities` table; this is what makes "last accessed" work **across partitions**
(opening `acme/doc` writes a row in *your* schema that matches `acme`'s `mesh_nodes` by
path-encoded id). Guarantees, pinned by tests:

- Every scope clause still applies — `namespace:` (empty) keeps its root-children push-down
  (`namespace = ''`), so a first-level feed never degrades into the whole access history
  (`RecentlyAccessed_CrossPartition_JoinsCallersAccessLog`,
  `GenerateCrossSchemaSelectQuery_Accessed_JoinsCallersUserActivitiesInEveryBranch`).
- The `namespace:X`-never-matches-node-X rule above holds here too — the user node never
  appears in its own accessed feed.
- A caller with no access log (no partition schema yet) gets an empty result, not an error.

The home catalog's **Last accessed** sort is the canonical consumer
(`namespace: is:main context:search -nodeType:User source:accessed sort:LastModified-desc` +
the `namespace:{user}` home-children leg).

### `is`

Filters by node classification:

```
is:main    # Only main nodes (excludes satellite content such as comments and threads)
```

Satellite nodes exist in support of a main node — for example, comments on a document or threads started from a page. A main node has `MainNode == Path` (or null); a satellite node has `MainNode` pointing to its primary node's path.

```
namespace:ACME is:main                     # Main nodes directly under ACME
namespace:ACME scope:descendants is:main   # All main nodes under ACME (recursive)
is:main context:search                     # Main nodes visible in search
```

### `content:stored` — content exactly as stored

A FLAG, not a filter: each result's content comes back as the stored JSON (a `JsonElement`), never
deserialised into its registered type and never re-typed by the node-stream cache.

```
path:acme select:path,nodeType,content content:stored   # the root's content, byte-for-byte as stored
```

Use it only when the content is MATCHED against something evaluated over the stored JSON elsewhere.
The one reader today is the grant-rule check (`SecurityQueries.PartitionRoot`), which must see the
same JSON the Postgres projection reads with `content->>'field'`. A typed round trip is lossy: a
field absent from the JSON reads as the member's initializer, and an explicit CLR default is
omitted on the way back ([Access Control → Cost, and the evaluators](/Doc/Architecture/AccessControl)).
A provider that keeps node objects in memory returns the content it holds, unchanged. Only the exact
term `content:stored` is the flag; `-content:stored` and any other `content:` value stay ordinary
filters.

### `context`

Filters by visibility context. Nodes (or their NodeType definitions) can declare contexts from which they should be excluded via the `ExcludeFromContext` property, enabling different views of the same data:

```
context:search    # Exclude nodes hidden from search
context:create    # Exclude nodes hidden from create menus
```

> **Nodes are inclusive by default.** A node without `ExcludeFromContext` is visible in all contexts. A node with `ExcludeFromContext: ["search"]` is excluded only from `context:search` queries.

```
nodeType:NodeType context:create                      # NodeTypes visible in create menus
namespace:ACME scope:descendants context:search       # Searchable nodes under ACME
```

### `select`

Projects results to include only the specified properties — ideal for autocomplete, dropdowns, and large-result queries where only a few fields are needed.

```
select:name                      # Single property
select:name,nodeType,icon        # Multiple properties (comma-separated)
```

```
namespace:Systemorph select:name,nodeType
nodeType:Story select:path,name sort:name limit:10
```

> **Always `select:` only the fields the consumer reads.** Existence checks and "is-this-up-to-date?" polls need only `(path, version)`, never the full node. Loading full `Content` for a subtree is the antipattern this qualifier eliminates.

#### 🚨 `select:` decides whether `Content` is loaded at all

The projection has **two different shapes** depending on what the caller is typed on, and the
difference is invisible at the call site:

| Caller | What a `select:` does |
|---|---|
| **untyped** (`Query<object>`, the MCP `search` tool) | each row becomes a `Dictionary<string, object>` of exactly the named fields |
| **typed on `MeshNode`** (`Query<MeshNode>`, and therefore **every `workspace.GetQuery` / `hub.GetQuery`**) | the row stays a **`MeshNode`** — no dictionary — but the SQL projection is still narrowed |

**The untyped projection happens AFTER the merged sort** (#6830). An untyped read runs through
every provider and the cross-provider merge as nodes, and only the final, sorted and clipped
window is turned into dictionaries — so `select:path,name sort:<anything> limit:N` returns the
mesh's top N in the requested order, and the sort key need not be among the selected fields.
Before #6830 each provider projected its own rows first; the merge could not read a sort key off a
dictionary and clipped in provider-arrival order, so a query served by two providers (a store plus
the seeded static nodes, or several partitions) returned one provider's top N.

A **free-text** read with no `sort:` is ordered by relevance, and the relevance travels with the
rows: the storage-adapter provider ranks its matches by fuzzy score before its load cap AND
reports that score on its Initial frame (`QueryResultChange.Scores`), so the merge re-sorts by the
same key (score, then path) instead of scoring every row 0 and answering path order. That held for
typed and untyped reads alike, and `Skip` pages the relevance order.

On the typed path only ONE column is actually conditional: **`content`**. Every other field is
projected regardless, so naming them is documentation rather than optimisation. But when `content`
is *not* named, the storage adapter emits `NULL::jsonb AS content` (Postgres) or omits the field
(Cosmos), and you get a fully-formed `MeshNode` whose **`Content` is silently null**.

That failure is indistinguishable from "this node has no content": no error, no warning, no empty
result set — every `node.Content is T` / `ContentAs<T>()` downstream just returns null and the
surface renders empty. It is the same silent-null class as a missing `TypeRegistry` entry.

```
# ✅ metadata-only consumer — content deliberately not loaded
namespace:Course/Intro scope:children nodeType:Module select:path,id,name,order

# ✅ content-bearing consumer — `content` named DELIBERATELY
path:{user}/_Memex/AiSettings nodeType:AiSettings select:path,id,name,nodeType,content

# ❌ reads ContentAs<ModuleConfiguration>() but never asked for content — always null
namespace:Course/Intro scope:children nodeType:Module select:path,name
```

**Rule.** Every `GetQuery` should carry a `select:`. If any consumer of that stream reads
`Content`, `content` must be in the list. When you cannot prove the whole consumer chain is
content-free, leave the query unprojected — the full node is the conservative default.

> **🚨 The projection is part of the cache key — so keep it stable per id.** The synced-query cache
> is keyed by **(id, query set)**: `GetQuery(id, queries)` answers the set you asked for, and two
> call sites that share an id but differ in their `select:` each get their own collection. It was
> keyed by id alone until issue #1311, and the discarded queries made a metadata-only reader starve
> a content reader of its content, intermittently, depending on render order — the same discard
> that froze a NodeType's source set on whichever declaration materialised first, so a newly added
> `shared=@Other/Type/Source` was ignored until the portal restarted.
>
> A drift is therefore no longer a wrong answer, but it is still waste: every distinct query set
> opens its own subscription and stays resident for the life of the process. Keep the query strings
> byte-identical wherever an id is shared, and scope ids per module so unrelated readers never
> collide.

---

## Combining Filters

Terms compose freely — every additional term narrows the result:

```
namespace:Systemorph nodeType:Project

nodeType:Story name:*claims* sort:lastModified-desc limit:20

namespace:ACME/ProductLaunch nodeType:Todo scope:descendants
```

---

## Default Queries by Context

The query engine powers several built-in views. Understanding the default query each view issues makes it easy to extend or override them.

### NodeType Catalog

When browsing a NodeType (e.g., Organization), the default query finds all instances:

```
nodeType:Organization
```

### Instance Catalog

When browsing an instance (e.g., Systemorph), the default query shows its immediate children:

```
namespace:Systemorph
```

Add `scope:descendants` to go recursive.

---

## Reading a single node's fields

There is no `SelectAsync` API. To read fields off a node at a **known path**, take the node
from its stream — that is the authoritative, live read; a query would go through the lagged
read-side index and return stale content right after a write:

```csharp
workspace.GetMeshNodeStream("Systemorph/Marketing")
    .Where(node => node is not null)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(10))
    .Subscribe(node =>
    {
        var name = node.Name;
        var nodeType = node.NodeType;
        var icon = node.Icon;
    });
```

If you want a narrower payload out of a **set** query, use `select:` (below) — but note it
drops `content`. See [CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess).

---

## Autocomplete

A search bar completes this language as you type — qualifiers, their values, `content.<property>:`
from the NodeType's schema (enum members, booleans, `[MeshNode]` reference members), node names and
semantic matches. The vocabulary it offers is exactly the one this page describes; see
[Query Autocomplete](/Doc/GUI/QueryAutocomplete).

## Live Query Syntax Explorer

The cell below renders a quick-reference card for the most common query patterns — a handy cheat sheet while you build queries:

```csharp --render QuerySyntaxExplorer --show-code
var patterns = new[]
{
    ("Field filter",        "nodeType:Organization"),
    ("Negation",            "-status:Archived"),
    ("Wildcard",            "name:Acme*"),
    ("Comparison",          "price:>100"),
    ("OR list",             "status:Active|Pending|Draft"),
    ("Namespace (folder)",  "namespace:ACME scope:descendants"),
    ("Longest-prefix",      "path:a/b/c scope:ancestorsandself sort:length(path)-desc limit:1"),
    ("Sort descending",     "sort:lastModified-desc"),
    ("Activity feed",       "source:activity nodeType:Thread limit:10"),
    ("Main nodes only",     "is:main context:search"),
    ("Select projection",   "nodeType:Story select:path,name limit:10"),
};

var rows = string.Join("\n", patterns.Select(p =>
    $"| `{p.Item1}` | `{p.Item2}` |"));

MeshWeaver.Layout.Controls.Markdown($"""
## Quick-Reference Card

| Pattern | Example |
|---|---|
{rows}
""")
```

---

## Tips

1. **Case-insensitive** — all comparisons ignore case.
2. **`namespace:X` means "folder X"** — returns immediate children by default; add `scope:descendants` to go deep.
3. **Wildcards** — `*` matches anything; prefix, suffix, and contains patterns all work.
4. **Select only what you need** — use `select:` to keep payloads small in large result sets.
5. **Known path ⇒ node stream, not a query** — `workspace.GetMeshNodeStream(path)` is authoritative and live; `QueryAsync(path:X)` is a stale-read bug.
6. **Vector search** — free-floating text tokens (`laptop nodeType:Story`) trigger HNSW cosine-index search on Postgres backends when an `IEmbeddingProvider` is registered; structured-only queries stay on the regular SQL path.
