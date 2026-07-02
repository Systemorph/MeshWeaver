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

The **Search** area defaults to the graph navigator. (The Space **Children** catalog stays on the
namespace tree.) Unknown or missing values fall back to the graph navigator (for a content catalog)
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

The Space welcome page ships a catalog embed; you can move or delete it, or add your own. Inside any
Markdown body (a Space body, a Markdown node, an agent definition):

```markdown
## Everything in this space

@@("area/Search")
```

The `@@` double-at prefix renders the area **inline**; a single `@` would render a link instead.
Absolute (`@@/ACME/area/Search`) and relative (`@@("area/Search")`) forms both work — see the
embed syntax in [Layout Areas](../LayoutAreas).

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

Back to the [GUI overview](/Doc/GUI).
