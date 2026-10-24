---
Name: Catalog App
Category: Documentation
Description: A menu of catalog categories, each a column of MeshSearch catalogs over hidden queries, with ONE query bar shared by all of them — declared, pluggable, framework controls only.
Icon: /static/DocContent/GUI/icon.svg
---

A **catalog app** is the page shape for "several lists a reader perceives as different places":
the clients, the partners, the deals, what is waiting on us. It is nothing but a menu of
**categories**, each delegating to [`Controls.Catalog`](/Doc/GUI/MeshSearch) (MeshSearch) with its
hidden queries, and **one query bar** shared by all of them.

```csharp
public static IObservable<UiControl?> Portfolio(LayoutAreaHost host, RenderingContext _) =>
    Observable.Return<UiControl?>(new CatalogApp("Portfolio")
        .WithCategory("reply", "Reply needed",
            CatalogSearch.Computed(replyNeededFilter, itemArea: "Card", whenEmpty: host.Localize(NothingWaitingKey)))
        .WithCategory("clients", "Clients",
            CatalogSearch.Of("nodeType:Crm/Counterparty scope:subtree content.type:Crm/CounterpartyType/Client partitions:all",
                itemArea: "Card"))
        .WithCategory("deals", "Deals",
            CatalogSearch.Of(openDeals, "Open", "Strip"),
            CatalogSearch.Of(wonDeals, "Won", "Strip"))
        .Render(host));
```

## The contract

| Part | What it is |
|---|---|
| `CatalogApp(title)` | A `PageTitle` (when a title is given), the shared bar, and the menu. `Render(host)` renders it into the host's area. |
| **Category** — `WithCategory(id, label, params CatalogSearch[])` | One menu entry. `id` is stable and URL-safe: it is the menu AREA id and what a deep link names; `label` is the visible text (localise it before passing it). A category shows its searches one under the other. |
| `CatalogCategory { When = IObservable<bool> }` | A conditional category: in the menu only while `When` emits true (the AI catalogs' "This space" tab). Until it first answers it counts as false, so it never holds back the other categories. |
| `CatalogCategory { Header = control }` | A control above the category's searches — e.g. the legend of what the hits' badges mean. |
| **Search** — `CatalogSearch.Of(filter, title?, itemArea?, sort?)` | One hidden query: a filter WITHOUT `sort:` plus its default `CatalogSort` (Last activity unless stated; `CatalogSort.Name` is cheaper over `partitions:all`, because last activity costs a read per hit). Rendered as `Controls.Catalog` with the search box hidden. |
| `CatalogSearch.Computed(IObservable<string?> filter, …, whenEmpty?)` | A COMPUTED hidden query. Each distinct emission re-aims the section; a `null` emission is the empty state and renders `whenEmpty` (localised by the caller, shown as a plain label — never parsed as markup) instead of running a query. Until the filter FIRST answers, the section shows the platform's indeterminate progress bar — never nothing — carrying `whileLoading` when the six-parameter overload passes one (localised by the caller). |
| `.WithCreate(nodeType, namespace, when?)` | The section's "+" (`CreateNodeType` / `CreateNamespace`). Always state the namespace for a subtree query. `when` (an `IObservable<bool>`) hides it while false — e.g. "only once the set has a member"; until it answers, the results render without the "+". |
| `.WithMaxColumns(n)` · `.WithPageSize(n)` | A card grid instead of one hit per row; hits per page before "Load more". |
| `.WithConfigure(c => …)` | The last-mile hook over the composed `MeshSearchControl` — grouping (`WithGroupBy(…).WithSectionCounts(true)`), render mode, anything. Applied last. |
| `WithoutQueryBar()` | No shared bar — an embedded start tab carries none. |
| `WithPlaceholder(text)` | The bar's placeholder (localise before passing); otherwise a language-neutral `🔍 sort:name, sort:lastModified-desc`. |

### The shared query bar

- It is a framework `TextFieldControl` bound to the area's data section (`QueryDataId`), sending as
  the reader types. Each search's section is its OWN area, re-emitted with the typed text as the
  catalog's `VisibleQuery` — so the existing MeshSearch view re-queries; **no view change is
  involved**, and a section whose inputs did not change is not re-rendered.
- The bar is seeded once per area host (`LayoutAreaHost.TryMarkEditStateInitialized`), so an area
  that re-renders the app keeps what was typed.
- Computed filters and conditions are kept for the life of the area host (`CatalogApp.Retained`, latest
  value replayed, connections owned by the host): a menu rebuilt because one category's `When`
  changed does not blank another category's computed section.
- 🚨 **A computed section is LOADING, never absent, until its filter first answers** (2026-10-21). It
  used to emit nothing until then, so its area did not exist — and a one-shot read of the page (the MCP
  `get …/area/…` route returns the first frame in which the requested area ITSELF has landed, not its
  later sub-areas) reported the CRM board's attention tabs as "never rendering" while a live
  subscription had them within a quarter of a second. A filter that never answers now reads as an
  endless progress bar: still a defect to chase, but a visible one.
- A changed typed text is a new result set: the section's search re-queries and its paging restarts.
- The typed text follows each hidden query, and the parser keeps the last `sort:`, so a typed order
  (`sort:name`) wins. Switching category keeps the text.
- 🚨 **A union is ordered by its FIRST line, so every line carries the order.** A hidden query may
  be a union — one sub-query per line. The MeshSearch view sends the lines as one
  `MeshQueryRequest.FromQueries`, each followed by the typed text, and the engine takes sort and
  limit from the FIRST. `CatalogSort.Sorted` therefore appends the default order to EVERY line (a
  line that already names its own `sort:` keeps it), and a typed `sort:` still wins because it
  follows each line. Before this, the order rode only on the LAST line and a union catalog came back
  unsorted (MeshWeaver.Plugins#4055).
- 🚨 **Typing can only NARROW — as the production parser reads it.** `CatalogQuery.Narrowing`
  removes every reserved single-slot qualifier but `sort:` (`namespace:`, `path:`, `scope:`,
  `partitions:`, `basePath:`, `source:`, `context:`, `select:`, `is:`, `limit:` — derived from
  `QueryParser.ReservedQualifierNames`) whatever its sign or grouping, then PARSES
  `hidden + " " + typed` and keeps the typed text only if the result is the hidden query and
  something more: the same anchor, scope, limit, source, projection and context, and every conjunct
  of the hidden filter still a conjunct. An `OR` that would reach past the hidden filter
  (`content.status:Open OR content.status:Won`, a leading `OR nodeType:Other`) fails that and keeps
  only its free text and `sort:`; a grouped `(a OR b)` passes. This is why the type lives in
  `MeshWeaver.Mesh.Contract`: it must use the real parser.
- 🚨 **Every hidden query is anchored.** A filter that names no positive `namespace:`, `path:`,
  `basePath:` or `partitions:all` is not run; the section shows THAT it was refused (the anchors it
  lacks — never the hidden query, which goes to the log as a warning). The rule is the production
  one, `ParsedQuery.IsSufficientlySpecified` (MeshWeaver#4274): `path:*` is not an anchor. `partitions:all` queries are accepted unchanged.

### Deep links

The menu's area ids are the category ids. `Render(host, selected)` opens on `selected`, else on the
area reference's id without its URL query (`…/area/{App}/{categoryId}?x=1` → `categoryId`), else on the first category; an unknown id falls
back to the first. The selection is carried as the tabs' `ActiveTabId`.

`CatalogApp.CategoryIdOf` (the reference id without its URL query) is applied only inside
`Render(host)`: a caller that composes the app itself with `Compose(typed, selected)` must call
`CategoryIdOf` on its own reference id before passing `selected`. In an AppTabs page a pack tab id
and a category id share the ONE reference id, so the host decides which wins — the Plugins host
gives the pack tab precedence, compared case-insensitively (MeshWeaver.Plugins#4056).

## What is NOT in yet

- **Counts and icons in the menu.** The tab skin carries a label only. A count must stay LAZY (never
  run every category's query on first paint) and an icon needs the tab skin to carry one — both a
  view-pack change, to be added as optional members without breaking this API.
- **A non-tab menu.** The menu is built in one place (`CatalogApp.Menu`); a nav-menu variant can be
  added beside it.
- **Writing the selected category back to the URL** when the reader switches tabs.

## Adopters

| Where | Shape |
|---|---|
| CRM portfolio board (MeshWeaver.Crm) | Reply needed / Waiting on them (computed) · one category per counterparty kind · Deals (legend + pipeline). |
| AI catalogs — `src/MeshWeaver.AI/AiCatalogLayoutAreas.cs` (Plugins) | Agents / Skills / Providers / Models / Tiers × This space · User · Global (or one category per source); a "+" per tab into that tab's namespace; "This space" conditional. |
| AI Settings — `MyAi/Panel/Source/AiSettingsAreas.cs` (Plugins) | Per kind (Skill / Agent / Model) one category per ACTIVE source row, name order, four columns; one bar per kind (`QueryDataId` per kind, because the apps share the page's area). |
| Submissions book — `ReinsuranceDemo/Book` (MeshWeaver.Reinsurance) | One category per tab of `BookQueries.Tabs` under the node's bound header; embedded with `Render(host)` inside a template. The last caller of `WithMeshSearch`. |
| Examples start tab — `Developer/DeveloperApp` | Categories per extension slot, `sort:name`, 3 columns, **no bar**. |
| AI onboarding wizard | Chapters as categories, one search each, a non-default per-viewer `itemArea`. |

### Surveyed and deliberately NOT on `CatalogApp`

An inventory of every `Controls.MeshSearch` / `Controls.Catalog` / `WithMeshSearch` site on the five
repos' main (2026-10-24) found most of them are not a catalog PAGE at all — a catalog app is a menu of
several lists a reader perceives as different places, with one shared bar. These stay as they are:

| Where | Why it stays |
|---|---|
| Threads app — `AiThreadsApplication.cs`, `ThreadGroups.cs` (Plugins) | Not on `WithMeshSearch` either: its groups partition ONE projected query IN MEMORY by content facts (declared group, legacy agent/path placement, "mine"), delegated work is folded into its root, the group list is whatever the readable threads declare, and the menu carries lazy Count/Active badges. It adopts when groups are query-expressible (or a rows variant of `CatalogSearch` exists) and the menu carries counts. |
| Home catalog — `UserActivityLayoutAreas` (core) | Scope tabs INSIDE one `MeshSearchControl` (`MeshSearchScopeTab`): sort dropdown, view options, create href, per-viewer privacy and drag-and-drop apps band (`Architecture/AppsHome`). None of those has a `CatalogApp` equivalent. |
| Inbox / Mailbox (`Essentials/Mailbox`, core Inbox) | A reading-pane layout (list beside a message); the filter bar is its own design (`Architecture/CommunicationHub`, "why it is not `MeshSearchControl`"). Mailbox's three pages are routes, not tabs. |
| Social feed — `SocialMedia/PostsHub` | One search whose scope tabs are the LIVE set of authors; `CatalogApp`'s menu is fixed per render, and a changing label resets the active tab. |
| Underwriting Workbench, Claims Register (`WithReach` partition tabs) | A list inside a dashboard of KPIs and a board, scoped by partition. |
| Playroom, Chess history, Store sources / extension shelf / exercise grids, Edu module, Expenses, Approvals, DataModelling, Fleet console, settings tabs of the connector apps | A single list embedded in a detail page, a settings tab or a dashboard section (often with a summary above it). There is no menu of categories to make. |

`CatalogExtensions.WithMeshSearch` is superseded by this and will be marked `[Obsolete]` once its
callers have moved (consumers build with TreatWarningsAsErrors, so the attribute would break them on
the day they took the platform).

## See also

- [Mesh Search & Catalogs](/Doc/GUI/MeshSearch) — `Controls.Catalog`, the Search area and its parameters
- [Data Binding](/Doc/GUI/DataBinding) — how the bar's text reaches the sections
