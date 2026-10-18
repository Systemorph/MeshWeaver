---
Name: The Supplied Navigation Rail
Category: Architecture
Description: A module that owns a family of pages supplies its own left-hand index through INodeNavigationProvider. What core promises about that index — above all that the supplier's ORDER is the rendered order — and the four defects that shaped the rail.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="18" rx="1"/><line x1="5" y1="7" x2="8" y2="7"/><line x1="5" y1="11" x2="8" y2="11"/><line x1="5" y1="15" x2="8" y2="15"/><line x1="14" y1="8" x2="21" y2="8"/><line x1="14" y1="16" x2="21" y2="16"/></svg>
---

# The Supplied Navigation Rail

Core's default left-hand index is **the tree of the page's Space** — its first path segment
(`Infrastructure` for every page under it, `{viewer}` inside a home). Every page of the Space shows
the same index: the Space's children in their declared order, an entry with children as a
collapsible group, the groups on the reader's path open, and the page being read marked as current
(`DefaultNodeNavigation`).

It used to be **the current node's own children**, which was right on the root of a document tree
and wrong one level down: a sub-page has no children, so the index vanished the moment the reader
clicked into it, and nothing told them where they were (reported 2026-09-14 on
`Infrastructure/Inference`). A first fix rooted the index one level below the Space, which left the
Space's own pages — `Infrastructure/Options` beside `Infrastructure/Inference` — outside every index
again (reported 2026-09-15: "Infra has one; show it, with where we are"). **The root is the Space**:
a Space is the document and its overview is the title page, so every page under it shows the one
index of the Space, with the page being read marked and only the groups on the reader's path open.

**The index is an explorer, loaded lazily** (policy [`explorer-navigation`](../PolicyNotProse)):

- **Only the reader's path is loaded.** One direct-children read per level
  (`DefaultNodeNavigation.PathLevels`): the Space's children, each ancestor's children, and the
  page's own. The cost grows with the page's depth, never with the size of the Space. A folder off
  the path is listed and not opened; the reader drills in by opening it.
- **A level shows 10 entries at first** (`LevelCap`), and **reads only those plus one**. Every
  read of a level is a query bounded to `Cap + 1` rows and ordered **by the store** in the key the
  level renders by (`DefaultNodeNavigation.LevelQueries`), so a large folder never loads whole. A
  level that holds more ends with two entries: **Load more** (`common.loadMore`) reloads the page
  with `?more={level}:{n}`, raising that one level by 10, up to 100; **Show all** (`common.showAll`)
  opens the level's direct children in the mesh search
  (`/search?ns={level}&hq=namespace:{level} is:main sort:…`).
- **Each loaded level can be sorted** — a dropdown on its group heading (the top level's sits beside
  the search box): **Order, then recent** (the default: declared `Order` first, unordered last, then
  the most recently modified), **Recently modified**, **Recently viewed** (the viewer's own access
  log, then the newest of the rest) and **Name A–Z**. The choice travels in the URL as
  `?sort={level}:{order|modified|viewed|name}`, so a sorted index is a link; re-sorting a level resets
  its Load more. Every leg excludes internal `_` entries in the query (`-id:_*`), so they cannot
  use up the window. A two-key order is read as bounded legs whose union holds the first `Cap + 1`
  rows of the combined order: the ordered rows (`sort:order-asc`), the newest
  (`sort:lastModified-desc`) and a **tie leg** — the rows sharing the Order value the first leg was
  cut at, newest first (`order:{n}`), because that leg is clipped by Order alone. For Recently
  viewed the `source:accessed` leg (sorted on the access time it projects into `lastModified`)
  ranks, and the newest leg lists what the viewer never opened. Name A–Z clips and renders by
  `lower(name)`, unnamed entries last. This relies on nulls sorting last in both directions on
  every backend (QuerySyntax, `sort:`). The sort values are open string constants (`IndexSort`);
  an unknown one in a URL is logged and the level keeps the default order. The dropdown is offered only where a level is loaded — a
  folder off the reader's path is a link. The control is `NavGroupSkin.SortOptions` /
  `NavMenuSkin.SortOptions` (`NavSortOption`: label, href, current); the Blazor views that render
  it ship in MeshWeaver.Plugins.
- **A search box sits above the tree** (`NavMenuSkin.SearchNamespace`), scoped to the page's Space
  (its first path segment), also when a module supplied the index. Submitting it opens the mesh
  search (`/search?ns={space}&q=…`). The Blazor nav menu that renders the box ships in
  MeshWeaver.Plugins.
- **Markdown and typed pages have it.** The typed default Overview is wrapped by
  `MarkdownOverviewLayoutArea.WithIndexRail`, so a page's children are navigated in the rail and
  are not listed at the bottom of the page.
- **A page a node type supplies itself does not have it yet.** A type that replaces its landing
  page through `WithNodePage` (a redirect, an enriched type's own Overview) renders that page
  without this wrapper. Decorating at the node-page composition boundary, with an opt-out for
  pages that must stay bare, is owed (policy `explorer-navigation`).
- **Space pages have it.** A Space's Overview (`SpaceLayoutAreas`, MeshWeaver.Plugins) takes the
  rail and appends no bottom **Contents** catalog (MeshWeaver.Plugins#3363), and the default Space
  welcome text (`SpaceNodeType.WelcomeMarkdown`) no longer ends with `## Contents` and an
  `@@("area/Search")` embed. A body embeds a catalog only where the listing is the content.

## The seam

```csharp
public interface INodeNavigationProvider
{
    IObservable<NodeNavigation?>? GetNavigation(LayoutAreaHost host);
}
```

Returning `null` — or a stream that emits `null` or no entries — declines the page, and core's
default child list stands unchanged. That is the normal answer for nodes the module does not own,
and it must be **cheap** (a path-shape check, never a query) and must never throw. A provider that
throws is logged and skipped: a module's navigation is a nicety, the page is not.

What comes back is a heading plus a flat list of entries, each of which may have children:

```csharp
public record NodeNavigation(string Title, IReadOnlyList<NodeNavigationEntry> Entries)
{
    public string? TitlePath { get; init; }   // what the heading links to; null for a plain heading
    public string? Icon { get; init; }
}

public record NodeNavigationEntry(string Label, string Path, bool IsCurrent = false, string? Icon = null)
{
    public IReadOnlyList<NodeNavigationEntry> Children { get; init; } = [];
}
```

The seam is **reactive by construction**. A whole-course index is a query, and a query is an
`IObservable<T>`, so the provider hands back a stream rather than a materialised list — which is what
lets it walk up to the course root and read the subtree without blocking the render (nothing on a hub
may await; see [Asynchronous Calls](../AsynchronousCalls)). The stream feeds straight into the
area's `CombineLatest`, so an added, renamed or re-ordered page re-renders the index live. It must
emit **promptly**: one that stays silent holds the whole page back.

## What core promises

`SuppliedNavigationRail` turns the supplied navigation into the rail in two steps — a pure
`Plan`, then `Render`. Five properties are deliberate, and each of them is a defect that was
reported on a live page.

### 1 · The title is a link, never the collapsible root

Nesting the whole index under one `NavGroupControl` made its heading both a link and a toggle, so
clicking the course name collapsed the entire index. **Groups toggle, links navigate — never both on
one control.** The heading is a sibling of the entries, not their parent.

The one case where the heading is *not* a link: when `TitlePath` is null, because the index root does
not exist. Linking a node that is not there does not render "not found" — path resolution matches the
longest existing prefix and reads the trailing segment as an AREA, so the reader gets *"no renderer
is registered for area {Root}"*, a rendering error for what is really a missing node.

### 2 · An entry with children is a group AND its own first link

The heading expands; the link directly beneath it opens the page. The self-link is what carries the
position marker, because a group heading has no active state to carry one.

### 3 · The current entry stays in the tree, as an active link

It used to be swapped for bare body text — no icon, no indentation — so the line the reader was
standing on jumped to the far-left margin and read as belonging to nothing. It is a normal
`NavLinkControl` with `IsActive` set, which gives it the accent bar, background and weight the nav
menu already styles. None of those cues is colour-only.

Only the group the reader is inside is expanded, so a long index stays a scannable list of chevrons
and every other group visibly offers its expander.

### 4 · The supplier's order is the rail's order

**Having children decides WHAT an entry becomes, never WHERE it goes.**

The plan used to hold two buckets — `Pages` for entries with no children, `Groups` for entries with
them — and render every page before every group. The supplied order survived *inside* each bucket and
was lost *between* them, so an entry with children could never precede one without:

```text
supplied:  Read me (0) · Lesson 1 (1) · Lesson 2 (2) · Lesson 3 (3) · Exercises (55) · Video (90)
rendered:  Read me · Exercises · Video · Lesson 1 · Lesson 2 · Lesson 3
```

Measured on a live course (2026-09-06, MeshWeaver#3406): four lessons carrying orders 1–4 rendered
tenth to thirteenth, behind every leaf page, because each lesson had an exercise, a solution, a quiz
and a documents folder beneath it. **No `Order` value could fix it — the numbers were already
right.** A course author's only lever over the rail is `Order`, and it silently could not express a
reading order that mixes leaf pages with lesson folders, which is every course.

The plan therefore carries **one ordered sequence**, `Rail.Items`, whose element is a `RailLink` or a
`RailGroup`, and `Render` walks it once:

```csharp
public abstract record RailItem;                       // RailLink | RailGroup, and nothing else
public sealed record Rail(RailLink Home, IReadOnlyList<RailItem> Items);
```

Both container renderers — Blazor's `NavMenuView` and the React `NavMenu` skin — emit a container's
areas in declaration order, and `WithNavLink` and `WithNavGroup` both append to that one ordered
list. So preserving the order in the plan is the whole fix; there was never a second place that
re-sorted.

The alternative a module might reach for — **stop declaring children, so every entry is a leaf and
the order comes out right** — is not a fix. It buys the ordering by giving up the collapsible
lessons, which is the thing that makes a long index scannable at all.

### 5 · A group nests

An entry's child that has children of its own is a group *inside* the group. `RailGroup` carries
`Items` — the same ordered sequence of `RailLink | RailGroup` as the rail itself, its own link first —
and `Render` walks it recursively. The plan used to flatten a group to its links, so a page three
levels down a documentation tree had no line to stand on and the page above it showed a link where
a folder was. It surfaced the day the default index became a tree (2026-09-14): a two-level rail was
enough for a course (module → lesson pages) and not for a document tree of any depth. Only the groups
on the reader's path are open, at every level. `RailGroup.Links` remains as a filter — the group's
direct links — never the render order.

## The rail's chrome — resize and collapse

The rail is one pane of a horizontal `SplitterControl` beside the content, so the divider is a real
drag handle (180–480px). Core stamps the pane with `MarkdownOverviewLayoutArea.NavigationPaneClass`
(`nav-rail-pane`), and a portal shell that knows the class hosts the pane's **collapse toggle in its
top bar** — beside the logo, where a browser keeps its sidebar button — and keeps the collapsed state
across pages and reloads; the button shows only on pages that have a rail, and while the rail is
hidden it turns into the expand button. A shell that does not know the class leaves the splitter
bar's own collapse chevron in charge. (The Blazor portal's half lives in MeshWeaver.Plugins:
`NavRailStateService`, `NavRailPanePresentation`, and the toggle in `PortalLayoutBase`.)

**One sidebar, one button.** The index rail is not the only left-hand sidebar a page can have: the
Threads app (`/{user}/Chat`) and every full-page thread show their thread list in the same place. It
joins the same toggle and the same state (2026-09-16, the maintainer: *"when side menu (on left) is
collapsed … the expand button is still to the left consuming real estate … integrate the open button
… in the top menu bar, similar to how safari is doing it"*). The rule for any left sidebar is
therefore:

- **Collapsed takes zero width.** Nothing stays behind on the left edge — no strip, no reveal
  button, no splitter bar. The header button is the only way back, which is why it is always there
  while the page has a sidebar.
- **The button never moves.** It sits at the left end of the top bar in both states and only swaps
  its icon (`PanelLeftContract` ↔ `PanelLeftExpand`) and its localized name (`menu.hideSidebar` ↔
  `menu.showSidebar`). A sidebar carries no collapse control of its own while a shell hosts this one
  — two controls for one state is how the residual strip came about.
- **The state is the reader's, not the page's.** Hiding the thread list also hides a document's index,
  the way Safari's sidebar button hides whichever sidebar the window shows; the choice follows the
  reader across pages and reloads.
- **A sidebar reports its presence** (`NavRailStateService.Attach`/`Detach`, reconciled after every
  render) so the button shows only while there is something to toggle, and it resolves the service
  OPTIONALLY: in a host with no shell toggle it keeps its own collapse and reveal controls rather than
  becoming impossible to reopen. The thread list's fold is `ThreadNavPresentation` in
  `MeshWeaver.Blazor.Chat`.

## Why the plan is a pure record

A `ContainerControl`'s child views are `protected`. A rail built straight into controls can be
asserted on for its area count and its skin, and nothing else — and **every defect above was
invisible to exactly that kind of test.** The plan is an ordinary record, so what the rail contains —
which entry is current, which group is open, what each line links to, and in what order — is pinned
by unit tests instead of by opening a page and looking. `SuppliedNavigationRailTest` is where those
assertions live.

## Where the rail is rendered

| Surface | How |
|---|---|
| A markdown page's Overview | automatic — `MarkdownOverviewLayoutArea` asks every registered provider, and falls back to the default index (the tree under the page's index root, `DefaultNodeNavigation`) when none claims the page |
| A typed node's default Overview | automatic — `MeshNodeLayoutAreas.Overview` wraps the page with `MarkdownOverviewLayoutArea.WithIndexRail`, same providers, same default |
| A layout that composes its own page | embed the standalone area by name: `new LayoutAreaControl(address, new LayoutAreaReference(MarkdownOverviewLayoutArea.SuppliedNavArea))` |

The standalone area renders nothing when no provider claims the page, so embedding it on a
non-course page costs an empty area rather than an error box.

An `@@` embed never gets a side menu at all — its providers are not even asked, so no query is opened
for a page that could not show the result.

## Registering a provider

A provider is an ordinary DI singleton. Registering it in a node type's configuration puts it on the
per-node hubs of pages of that type, which is how a module claims its own pages without core knowing
anything about them:

```csharp
config.WithServices(services =>
    services.AddSingleton<INodeNavigationProvider, MyCourseNavigationProvider>());
```

When several providers are registered, the first one that returns entries wins.

## Related

- [User Interface](../UserInterface) — layout areas and controls generally
- [UI Extensibility](../UiExtensibility) — the full table of extension seams a module can implement
- [Asynchronous Calls](../AsynchronousCalls) — why the seam hands back a stream and never a task
