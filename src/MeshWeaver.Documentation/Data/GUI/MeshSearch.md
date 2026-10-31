---
Name: Mesh Search & Catalogs
Category: Documentation
Description: One URL-driven Search area per node — group by namespace, type, or category, drill down lazily, and embed catalogs anywhere with @@.
Icon: /static/DocContent/GUI/icon.svg
---

Every node exposes a **`Search`** layout area — a configurable catalog of its content. Instead of a
zoo of bespoke "children" / "instances" / "by-type" views, there is **one** area whose shape is
controlled entirely by the URL. Navigate to `‹node›/Search`, or embed it anywhere with `@@`, and
tune it with query parameters.

> The old dedicated `Children` area is now a thin alias of `Search?groupBy=namespace` — both share
> one implementation, so they can never drift. Prefer `Search` in new content.

---

## The Search area

| Reference | What it shows |
|---|---|
| `‹node›/Search` | This node's content catalog (its direct children), as a lazy **namespace tree** |
| `@@/‹node›/area/Search` | The same catalog, embedded inline in any Markdown body |
| `@@("area/Search")` | The catalog of the **current** node (relative self-reference) |

For a `NodeType` node the area instead lists the **instances** of that type; for any other node it
lists the node's own children.

---

## `?groupBy=` — how results are organised

The single most important knob. Append it to the URL (or, for an embedded area, to a standalone
page link):

| `?groupBy=` | Render mode | Result |
|---|---|---|
| `graph` *(default)* · `nav` · `navigator` | Graph navigator | Re-rooting navigation along the graph's edges: ancestors **above** (clickable breadcrumb rail) + the next populated level **below** (nearest real nodes, skipping empty namespace hops) as a card grid. Clicking re-roots and recomputes |
| `namespace` · `ns` · `tree` | Namespace tree | Sub-namespaces become nested, collapsible folders with counts; levels load lazily on expand |
| `type` · `nodeType` | Grouped | One section per **NodeType** (Markdown, Code, Thread, …) |
| `category` · `cat` | Grouped | One section per node **Category** (falls back to NodeType when a node has none) |
| `flat` · `none` · `grid` | Flat | A single grid of thumbnail cards, no grouping |
| `hierarchy` · `hierarchical` | Hierarchical | Parent → child indentation, each root subtree kept in one cell |

The **Search** area defaults to the graph navigator. Unknown or missing values fall back to the graph navigator (for a content catalog)
or a hierarchical list (for a NodeType's instances).

### Graph navigator

The default Search experience navigates the mesh *along its edges*. For the current node it shows:

- **Above** — the real ancestors as a clickable breadcrumb rail (`scope:ancestors`). Empty namespace
  segments are never nodes, so they don't appear.
- **Below** — the current node's subtree (one `scope:descendants` query), split into two groups: the
  real **nodes** at this level as a card grid (a node that *also* groups content gets a "drill in"
  affordance), and the pure **sub-namespaces** below it — path segments that group content but have no
  node of their own — as drill-down links. Empty intermediate namespace segments are skipped, so a
  node at `a/b/node` (where `a`, `a/b` are not nodes) surfaces directly.

Clicking a node card (or its drill affordance) or an ancestor **re-roots** the navigator there
(`/{path}/Search`) and recomputes above + below — "navigate → visualize → navigate"; a secondary
"open ↗" opens the node's own page. A pure sub-namespace has **no node** to open, so clicking it
redirects to the **search control scoped to that namespace** (`/search?ns={namespace}`) instead of
re-rooting into a non-existent node page. Both levels are live `hub.GetQuery(...)` collections (see
[Synced Mesh Node Queries](/Doc/Architecture/SyncedMeshNodeQueries)).

```
/ACME/Search                     → graph navigator rooted at ACME
/ACME/Search?groupBy=tree        → namespace tree of ACME's content
/ACME/Search?groupBy=type        → ACME's content grouped by NodeType
/ACME/Search?groupBy=flat        → a flat card grid
```

---

## `?subtree=` — depth

By default the catalog shows only the node's **direct children**. Set `?subtree=true` (also `1`,
`yes`, `on`) to include the **entire descendant subtree** — useful with `groupBy=type` to answer
"every Story anywhere under this space", or with `groupBy=namespace` to pre-expand the whole tree.

```
/ACME/Search?groupBy=type&subtree=true     → every node under ACME, grouped by type
```

---

## Display options

Every knob of the catalog is URL-driven — there is nothing hard-coded you can't override.
Booleans accept `true`/`1`/`yes`/`on` (anything else, or absence, is the stated default); ints
must be positive.

| Param | Default | Effect |
|---|---|---|
| `?searchBar=` | `true` | Show/hide the search box — `?searchBar=false` for a compact embedded catalog |
| `?emptyMessage=` | `false` | Show the "No items found." message when there are no results |
| `?loading=` | `false` | Show skeleton loading cards while results stream in |
| `?counts=` | `true` | Show the per-section item counts |
| `?collapsible=` | `true` | Allow sections to collapse (`false` keeps everything expanded) |
| `?reactive=` | `true` | Live-update on data changes (moves, renames, new children) without a reload |
| `?limit=N` | `50` | Items shown per section |
| `?maxRows=N` | `3` | Collapsed rows per section before "show more" |
| `?maxColumns=N` | `3` | Grid columns |
| `?title=…` | `Catalog` | Section title shown next to the search bar |
| `?placeholder=…` | `Search…` | Search box placeholder text |

Combine freely with `?groupBy` / `?subtree`:

```
/ACME/Search?groupBy=type&searchBar=false&maxColumns=4&counts=false&title=Contents
```

---

## Drilldown

In **namespace-tree** mode the catalog is lazy: only the direct children of the root are queried up
front, and each sub-namespace folder is queried **on expand** — so a deep space opens instantly and
you pay only for the branches you actually open. Typing in the search box switches to a subtree
search whose hits are regrouped by relative namespace. In the **flat / grouped / hierarchical**
modes, clicking a card navigates to that node (its own `Search` area, one level down).

---

## Embedding a catalog in a page

A page's children are already listed in the **index on the left** (the side rail), so never end a
body with a "children section" that restates it — the Space welcome page ships no catalog. Embed one
only where the **listing is the content**: a log or register, a store or catalog page, a filtered or
grouped view the rail cannot express, or a typed node rendered without the rail. Put it under a
heading that names what it is, inside any Markdown body (a Space body, a Markdown node, an agent
definition):

```markdown
## Reports by type

@@("area/Search?groupBy=type")
```

The `@@` double-at prefix renders the area **inline**; a single `@` would render a link instead.
Absolute (`@@/ACME/area/Search`) and relative (`@@("area/Search")`) forms both work — see the
embed syntax in [Layout Areas](../LayoutAreas).

---

## `Controls.Catalog` — the standard list of nodes

When a page shows ONE set of nodes (the clients, the funds, the partners, an inbox) declare it with
`Controls.Catalog` and nothing else. It is only the search control with the set as its hidden query,
plus the settings every such list should share:

```csharp
Controls.Catalog("Partners",
    "nodeType:Crm/Counterparty scope:subtree content.type:Crm/CounterpartyType/Partner partitions:all",
    itemArea: "Card")
```

- **A search bar and "Load more" — nothing else.** No sort dropdown, no view-options bar. The
  hits come `pageSize` (20) at a time from the STORE, and "Load more" fetches the next page.
- **The query language is typed into the same bar**, and the placeholder teaches it by example
  ("Search partners — e.g. a name, sort:name, sort:lastModified-desc"). The typed text follows the
  hidden query, so a typed `sort:` replaces the default order.
- **The filter carries no `sort:`.** The catalog appends the default order (`CatalogSort`, **Last
  activity** — `sort:subtreeLastModified-desc` — unless you pass another) to EVERY line of a union
  filter (the union is ordered by its first line), and the store orders; there
  is no client-side `WithSortBy` to re-sort the hits behind the reader's back.
- **One hit per row, through the hit's own area** (`itemArea`).
- **Places a reader sees as different are different catalogs.** Clients, partners and suppliers are
  three catalogs over three filters, not one list with a type column.

### The row card plus an action from the hit's own hub

An `itemArea` hands the WHOLE card to the hit's own hub, so a row paints nothing until that hub has
activated and rendered: one hub per hit, before the reader sees a single name. When the card itself is
already in the row (name, description, icon) and only ONE part needs the hit's hub, such as an install
or purchase button decided per viewer, declare that part as an **item action area** instead:

```csharp
Controls.MeshSearch
    .WithHiddenQuery("nodeType:Store/Plugin content.hostedIn:\"AI/AiThreads\" is:main partitions:all sort:name")
    .WithItemActionArea("CoverCta")
```

Each hit renders the default card painted from its row at once, with the named area beneath it,
loaded from the hit's own hub (`MeshNodeCardControl.ActionArea`). The area sits outside the card's
navigation link, because it holds buttons. An `itemArea`, when one is set too, wins: it already owns
the whole card.

Refine with any `With…` of the control (`WithCreateNodeType`/`WithCreateNamespace` for the "+",
`WithMaxColumns` for a grid). A view that has to COMPUTE something across the set (a total, a KPI)
computes it beside the catalog, never instead of it.

## A catalog app — a menu of catalogs with one query bar

When a page holds SEVERAL places a reader perceives as different (clients, partners, the deals,
"reply needed"), declare a **catalog app**: a menu of categories, each delegating to the catalog
above with its hidden queries, and ONE query bar shared by all of them. What the reader types is
appended after every hidden query of the active category and survives switching categories; it can
only narrow (anchoring terms are dropped from it). Full contract, adopters and requirements:
[Catalog App](/Doc/GUI/CatalogApp).

```csharp
new CatalogApp("Portfolio")
    .WithCategory("clients", "Clients", CatalogSearch.Of(clients, itemArea: "Card"))
    .WithCategory("deals", "Deals", CatalogSearch.Of(openDeals, "Open"), CatalogSearch.Of(wonDeals, "Won"))
    .Render(host);
```

---


## Listing containers by last activity

A roster of containers — clients, projects, cases — usually wants the one worked on most recently
first, and a container's own `lastModified` does not say that: the work lands on its descendants,
while the container itself is re-stamped by the platform. Put
[`sort:subtreeLastModified-desc`](/Doc/DataMesh/QuerySyntax) in the **hidden query** — the order
is the newest change anywhere below each hit, read under the viewer's own access, so a viewer's
order never reflects rows they cannot read:

```csharp
new MeshSearchControl()
    .WithHiddenQuery("nodeType:Crm/Counterparty scope:subtree partitions:all sort:subtreeLastModified-desc")
    .WithRenderMode(MeshSearchRenderMode.Flat);
```

The same string works as a `MeshSearchSortOption` query, so "Last activity" can sit in the view's
*Sort by* dropdown beside "Alphabetical":

```csharp
.WithSortOptions(
    new MeshSearchSortOption("Last activity", "nodeType:Crm/Counterparty scope:subtree partitions:all sort:subtreeLastModified-desc"),
    new MeshSearchSortOption("Alphabetical", "nodeType:Crm/Counterparty scope:subtree partitions:all sort:name"))
```

🚨 **Do not use `WithSortBy("subtreeLastModified")`.** `WithSortBy` (the control's `Sorting`) is a
CLIENT-side re-sort by a property of each rendered node, and the subtree key is not a property of
the node — every row would key equal. With no `Sorting` set, the view keeps the order the query
returned whenever the query carries a `sort:`, which is what this needs. The key is computed by
the query engine and costs one read per hit, so it is for container lists: a query matching more
than `MeshOperationOptions.SubtreeSortMaxCandidates` hits (500) fails with an error rather than
paying one read per row of the mesh. Full contract: [Query Syntax](/Doc/DataMesh/QuerySyntax) →
*Sorting containers by last activity*.

---

## How typed words are ranked

When the search box holds words and the query carries no `sort:`, hits are ordered by relevance,
and two kinds of hit always come first (MeshWeaver#7381):

1. **An exact name** — a node whose name (or id) is exactly what was typed, ignoring case.
2. **A Space** — so looking for a Space by its name finds the Space, not the slides and PDFs that
   mention it.

An exactly named Space leads an exactly named page, which leads a Space that only contains the words,
which leads everything else; inside each group the usual relevance order holds. A typed or declared
`sort:` (a catalog's default order included) replaces relevance entirely. The rule is the same on
every backend — see [Query Provider Parity](/Doc/Architecture/QueryProviderParity).

---

## Under the hood

The area is built on the `MeshSearchControl` — a hidden query (always applied) plus a visible query
(the search box) over the mesh, rendered through one of the modes above. `groupBy=namespace` uses a
per-level builder with child-count probes; `groupBy=type`/`category` group the result set by a node
property. The hidden query is just the [Query Syntax](/Doc/DataMesh/QuerySyntax) — e.g.
`namespace:ACME is:main -nodeType:NodeType`, extended with `scope:descendants` when `subtree` is on —
so anything you can express as a query, you can scope a catalog to.

You can declare the control directly in your own layout areas. This one searches the GUI
documentation you are reading — type into the box and the grid filters live:

```csharp --render MeshSearchLive --show-code
new MeshSearchControl()
    .WithTitle("GUI documentation")
    .WithHiddenQuery("namespace:Doc/GUI scope:descendants nodeType:Markdown")
    .WithNamespace("Doc/GUI")
    .WithRenderMode(MeshSearchRenderMode.Flat)
    .WithMaxColumns(3)
    .WithItemLimit(6)
    .WithPlaceholder("Search GUI docs…")
```

For just the autocomplete input — without the result grid — use the lighter `SearchBoxControl`:

```csharp --render SearchBoxLive --show-code
Controls.SearchBox
    .WithPlaceholder("Search the documentation…")
    .WithNamespace("Doc")
    .WithNavigateOnSelect(false)
    .WithMaxSuggestions(8)
```

The search box completes the query language as you type — qualifiers, values, content properties from
the NodeType's schema, node names and semantic matches: see [Query Autocomplete](/Doc/GUI/QueryAutocomplete).

Back to the [GUI overview](/Doc/GUI).
