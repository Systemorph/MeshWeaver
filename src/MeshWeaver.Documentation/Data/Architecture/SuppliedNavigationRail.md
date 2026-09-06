---
Name: The Supplied Navigation Rail
Category: Architecture
Description: A module that owns a family of pages supplies its own left-hand index through INodeNavigationProvider. What core promises about that index — above all that the supplier's ORDER is the rendered order — and the four defects that shaped the rail.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="3" width="7" height="18" rx="1"/><line x1="5" y1="7" x2="8" y2="7"/><line x1="5" y1="11" x2="8" y2="11"/><line x1="5" y1="15" x2="8" y2="15"/><line x1="14" y1="8" x2="21" y2="8"/><line x1="14" y1="16" x2="21" y2="16"/></svg>
---

# The Supplied Navigation Rail

Core's default left-hand menu lists **the current node's own children**. That is right for a document
and for a space, and wrong for a course: a learner standing in lesson 2 sees lesson 2's sub-pages and
nothing else, so they cannot tell how long the course is, what is coming, or that they are nearly
done.

`INodeNavigationProvider` is the seam that fixes it without core having to learn what a lesson is. A
module that OWNS a family of pages hands core a whole index; core renders it. **The division of
labour is the contract: the module decides WHAT is in the index and in WHAT ORDER; core decides how
it looks.**

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
`Plan`, then `Render`. Four properties are deliberate, and each of them is a defect that was
reported on a live course.

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
| A markdown page's Overview | automatic — `MarkdownOverviewLayoutArea` asks every registered provider, and falls back to the default child list when none claims the page |
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
