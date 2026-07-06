---
Name: Combining Layout Areas in Markdown
Category: Documentation
Description: How a markdown document embeds live, data-bound layout areas inline with prose — the @@ live embed, the --render executable cell, and the compose-with-text pattern behind every course page and configurable home.
Icon: /static/NodeTypeIcons/code.svg
---

A markdown document on the mesh is not a flat wall of text. It is a **host for live UI**: anywhere in the prose you can drop a layout area — a table, a chart, a chat composer, another node's whole view — and it renders inline, data-bound and updating, right where you wrote it. This page is about that seam: how you *combine* layout areas into a document.

If you want the deeper story of what a layout area is and how to author one in C#, read [Layout Areas](../LayoutAreas) first. Here we assume you have areas to embed and want to weave them into a page.

<svg viewBox="0 0 760 250" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <rect x="20" y="20" width="340" height="210" rx="10" fill="#1e2a3a" stroke="currentColor" stroke-opacity=".25" stroke-width="1.5"/>
  <text x="190" y="44" text-anchor="middle" fill="currentColor" fill-opacity=".5" font-size="11" font-weight="600" letter-spacing="1">MARKDOWN DOCUMENT</text>
  <rect x="40" y="56" width="300" height="20" rx="4" fill="currentColor" fill-opacity=".12"/>
  <rect x="40" y="80" width="230" height="20" rx="4" fill="currentColor" fill-opacity=".12"/>
  <rect x="40" y="108" width="300" height="44" rx="8" fill="#26a69a"/>
  <text x="190" y="126" text-anchor="middle" fill="#fff" font-weight="600" font-size="12">@@("area/Chart")</text>
  <text x="190" y="143" text-anchor="middle" fill="#fff" font-size="10" fill-opacity=".85">live embed — a data-bound area</text>
  <rect x="40" y="160" width="300" height="20" rx="4" fill="currentColor" fill-opacity=".12"/>
  <rect x="40" y="188" width="300" height="30" rx="8" fill="#5c6bc0"/>
  <text x="190" y="207" text-anchor="middle" fill="#fff" font-weight="600" font-size="12">```csharp --render Demo``` — executable cell</text>
  <rect x="420" y="55" width="320" height="140" rx="10" fill="#1a2530" stroke="#26a69a" stroke-opacity=".6" stroke-width="1.5"/>
  <text x="580" y="80" text-anchor="middle" fill="currentColor" fill-opacity=".55" font-size="11" font-weight="600" letter-spacing="1">RENDERED PAGE</text>
  <text x="580" y="104" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="12">prose flows top to bottom,</text>
  <text x="580" y="123" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="12">each embed becomes a live area</text>
  <text x="580" y="142" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="12">in place — updating, interactive,</text>
  <text x="580" y="161" text-anchor="middle" fill="currentColor" fill-opacity=".75" font-size="12">not a screenshot.</text>
  <line x1="360" y1="130" x2="418" y2="120" stroke="currentColor" stroke-opacity=".45" stroke-width="1.5"/>
</svg>
*A document is a container. Prose and live areas interleave; the renderer resolves each embed against a node path and streams the real control inline.*

# Three ways to combine an area

There are exactly three, and they answer three different questions.

| You want to… | Use | The area comes from |
|---|---|---|
| Embed an area that **already exists** on some node | `@@("path/area/Name")` | another node (or this one) |
| **Author a fresh area inline**, code visible, runnable | ` ```csharp --render Name --show-code ``` ` | the code you write in the fence |
| Drop a **live demo** whose code isn't the point | ` ```csharp --render Name ``` ` | the code you write in the fence |

The first reuses UI a node already defines. The second and third define new UI on the spot. All three render as real, data-bound controls — never static HTML.

# 1 — The `@@` live embed

`@@("…")` embeds a layout area **inline** where it appears (a single `@` makes a hyperlink instead). The string resolves against **this node's path** at render time, so the same document works wherever it lives:

```markdown
@@("area/Search")              this node's own Search area
@@("Chat/area/Overview")       a child node's area, relative to this node
@@("/Doc/GUI/MeshSearch")      any node, absolute path (default area)
@@("/Doc/GUI/DataGrid/area/Overview")   any node, a named area
```

The grammar is `@@("‹node-path›/area/‹AreaName›")`. Omit the `/area/Name` suffix (`@@("/Doc/GUI/MeshSearch")`) and the target's **default area** renders — the one it registered with `WithDefaultArea`. A relative path (`Chat/area/Overview`) resolves against the current node; an absolute path starts with `/`.

Because the reference resolves against the page's own path, an embed like `@@("area/Search")` renders *this* node's catalog — put the same line on a Space, a folder, or a home page and each shows its own. That path-relative resolution is what makes configurable home pages and node overviews reusable; see [Configurable Pages](../ConfigurablePages) for the full set of home-page areas (`area/Pinned`, `area/Threads`, `area/Catalog`, `area/Composer`).

> **Local-only.** `@@` and `@/` are mesh authoring syntax — they work in markdown body text, never inside a raw HTML `href=""`. The renderer resolves `@@` into a live area component; an `href` passes through verbatim and would break.

# 2 — The `--render` executable cell

When the area you want doesn't exist yet, **write it in the document**. An executable fenced block runs in the kernel and streams its last expression into a result area rendered right below the code:

```csharp --render StackDemo --show-code
using MeshWeaver.Layout;

Controls.Stack
    .WithView(Controls.Markdown("**A layout area, authored inline.**"))
    .WithView(Controls.Html("It renders as a real control — not a screenshot."))
```

- `--render <AreaName>` executes the block and streams its **last expression** into the named area below.
- `--show-code` shows the source above the result, turning the block into a full **notebook cell** with a Run toolbar. Omit it for a clean live demo (see §3).
- The fence language routes the submission: ` ```python --render X ``` ` goes to the connected Python worker instead of the in-process Roslyn kernel.

The kernel pre-imports the common namespaces (`MeshWeaver.Layout`, `MeshWeaver.Layout.Composition`, `System.Linq`, and more), so `Controls`, `LayoutAreaHost`, and `RenderingContext` resolve without a `using`. This is the shape every worked example, chart, and pivot in these docs uses — and every one is executed by CI (`DocExecutableBlocksTest`), so a block that stops compiling fails the build.

## Building a data-bound area inline

The real power is that an inline cell can return a **view function** `(host, ctx) => …` — the same signature an authored area uses — so it participates in data binding and live updates, not just a one-shot render:

```csharp --render GridDemo --show-code
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;

record Resort(string Name, int Visitors, double RevenueKChf);

var rows = new[]
{
    new Resort("Zermatt", 428, 20580),
    new Resort("Verbier", 286, 12300),
    new Resort("St. Moritz", 256, 14940),
}
.OrderByDescending(r => r.RevenueKChf)
.ToArray();

Controls.Stack
    .WithView(Controls.Markdown("### Season summary"))
    .WithView(Controls.DataGrid(rows))
```

That is a genuine layout area — a `DataGrid` bound to a record collection — living inside a paragraph of prose. Sorting, formatting, and theming come for free from the control; there is no hand-built HTML table. (For the full column API — titles, formats, alignment — see [DataGrid](../DataGrid).)

# 3 — The inline live demo

Drop `--show-code` and the block still runs, but only the **result** shows — a live area with no source. Use it when the point is the rendered UI, not the code behind it:

```csharp --render TitleDemo
using MeshWeaver.Layout;

Controls.Stack
    .WithView(Controls.Html("<strong>A live area, no code shown</strong>"))
    .WithView(Controls.Markdown("Interactive and data-bound, just without the editor above it."))
```

# The compose-with-prose pattern

Put the pieces together and a document reads like a narrated dashboard: explain a thing, show it live, explain the next. This is the exact shape behind the Agentic Engineering course pages — a heading, a sentence of framing, a live area, repeat:

```markdown
## Ask for a table

A season of ski data sits behind this page. Ask for the summary:

```csharp --render Summary --show-code
… the area that builds the table …
```

Now compare it as a chart:

@@("Chart/area/Overview")
```

Two embeds, two mechanisms — a fresh `--render` cell for the code you want the reader to see, and an `@@` embed to pull in a chart another node already defines — interleaved with the prose that ties them together. The reader scrolls one document; every area inside it is live.

A few rules keep such a page healthy:

- **Reach for a control, never a string of HTML.** Tables, lists, charts, and pivots are `Controls.DataGrid` / `Controls.LayoutGrid` / the chart and pivot builders — compose them with `Controls.Stack`, `Controls.Title`, `Controls.Markdown`. Hand-built `<table>` markup forfeits sorting, theming, and binding.
- **Keep each executable cell self-contained.** A `--render` block compiles on its own; declare the records and data it needs inside it.
- **`@@` embeds the *default* area unless you name one.** `@@("/Doc/GUI/DataGrid")` renders that page's default view; add `/area/Overview` to pick a specific one.

# Course affordances: "Go to Exercise" and a collapsible side-nav

Any markdown page — a course page especially — gets two ready-made embeds that turn a wall of prose into a place a learner can *work*. Both resolve against the page's own path, so you enable each with a single line and nothing else.

## "Go to Exercise" — copy-to-home button

Drop this on an exercise page and the reader gets a button that gives them their **own writable copy** to work in:

```markdown
@@("area/GoToMyCopy")
```

On the first press it copies the page's parent *module* subtree into the reader's home space and opens the copy; on the second press it just goes to the copy — idempotent, never a duplicate. A signed-out visitor sees a gentle "sign in to take this" instead, and the author of the template (anyone with edit rights) is taken straight to the template rather than a copy. It reuses the same copy-to-home machinery as the read-only-node "copy to my home" flow, so the learner always lands on something they can edit.

> Why a button and not a plain link? An embedded *auto-redirect* area only navigates when it is the top-level route — inline in a page it appears to "do nothing". A button's click drives navigation from anywhere, which is exactly what an inline embed needs.

## A collapsible course side-nav

To wrap a page in a reader shell — a collapsible left rail listing the containing space's pages, the page content on the right — link a learner to the page's **`/Learn`** area, or embed just the rail inline:

```markdown
@@("area/CourseNav")
```

`CourseNav` lists the current page's sibling pages (the containing space's direct children, ordered by their `Order` then name) with the current page highlighted, so a learner always sees where they are. `/Learn` puts that same nav in a collapsible splitter pane beside the page's Overview. Both work for any markdown space — there is no course-specific node type to declare; the nav is sourced from the space's own children. "Go to Exercise" lands the learner directly in the `/Learn` shell, so the two compose: press the button, get your copy, and read it with the side-nav already beside you.

# See Also

- [Layout Areas](../LayoutAreas) — what an area is and how to author one in C#
- [Slides & Decks](../SlidesAndDecks) — Slide/Deck node types you can embed inline or sequence as a deck
- [Configurable Pages](../ConfigurablePages) — the reusable `@@("area/…")` home-page areas
- [Adding Controls to a UI](../ContainerControl) — `Controls.Stack`, `WithView`, and the container types
- [Data Binding](../DataBinding) — how a view stays live as its data changes
- [DataGrid](../DataGrid) — the table control's column API
- [Authoring Documentation](/Doc/Architecture/AuthoringDocumentation) — the executable-fence and link rules this page follows
