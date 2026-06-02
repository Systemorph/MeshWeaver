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
```

### Wildcard Patterns

Use `*` anywhere in a value to do substring, prefix, or suffix matching:

```
name:*claims*     # Contains 'claims'
name:Acme*        # Starts with 'Acme'
name:*Corp        # Ends with 'Corp'
```

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

### `scope`

Controls the traversal direction relative to `namespace` or `path`:

| Value | Description |
|---|---|
| `descendants` | All descendants recursively (excludes self) |
| `ancestors` | Parent hierarchy upward (excludes self) |
| `hierarchy` | Ancestors + self + descendants |
| `subtree` | Self + all descendants |
| `ancestorsandself` | Self + all ancestors |

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

### `sort`

Specifies sort order. Default is ascending; append `-desc` for descending:

```
sort:name               # Name ascending
sort:name-desc          # Name descending
sort:lastModified-desc  # Most recently modified first
```

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

Switches the data source that backs the query. Currently, one special source is available:

```
source:activity    # Results ordered by the current user's most recent access time
```

When `source:activity` is set:
- Results are ordered by most recent access, newest first.
- Items the user has never accessed appear after activity-tracked items.
- All other filters (`nodeType:`, `namespace:`, text search, etc.) still apply.

```
source:activity nodeType:Thread namespace:ACME scope:descendants   # Recently visited threads in ACME
source:activity nodeType:Document limit:10                         # Last 10 accessed documents
```

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

Projects results to include only the specified properties. Returns lightweight dictionaries instead of full nodes — ideal for autocomplete, dropdowns, and large-result queries where only a few fields are needed.

```
select:name                      # Single property
select:name,nodeType,icon        # Multiple properties (comma-separated)
```

```
namespace:Systemorph select:name,nodeType
nodeType:Story select:path,name sort:name limit:10
```

> **Always `select:` only the fields the consumer reads.** Existence checks and "is-this-up-to-date?" polls need only `(path, version)`, never the full node. Loading full `Content` for a subtree is the antipattern this qualifier eliminates.

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

## The `SelectAsync` API

The `SelectAsync` API retrieves a single property from a node at a known path without deserializing the full content blob. Use it when you only need one field and overhead matters.

```csharp
// Get the display name of a node
var name = await meshQuery.SelectAsync<string>("Systemorph/Marketing", "Name");

// Get the node type
var nodeType = await meshQuery.SelectAsync<string>("ACME/Project", "NodeType");

// Get the icon
var icon = await meshQuery.SelectAsync<string>("ACME", "Icon");
```

Returns `default` if the node is not found or the property is null. Any property on `MeshNode` is valid: `Name`, `NodeType`, `Path`, `Icon`, `Description`, and so on.

---

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
5. **`SelectAsync` for single properties** — when you know the path and need just one field, `SelectAsync` avoids full-node deserialization.
6. **Vector search** — free-floating text tokens (`laptop nodeType:Story`) trigger HNSW cosine-index search on Postgres backends when an `IEmbeddingProvider` is registered; structured-only queries stay on the regular SQL path.
