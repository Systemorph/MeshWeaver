---
nodeType: Skill
name: /markdown
description: Author a Markdown node (a page) the right way — always an icon, never a title (node.Name is already the H1), live @@ region embeds, and interactive (executable) code cells.
icon: 📝
category: Skills
order: 6
autoMount: true
---
# 🚨 WRITE INTO THE USER'S OWN HOME — never into the space you are reading

The chat you are running in is almost always opened **from a page**, so your context is that page's
node — a course lesson, a doc, someone else's space. On any of those the signed-in reader typically
holds **Read only**: a course is gated (entitlement = the `Viewer` role), a plugin/doc space is
GitSynced and writable by `system-security` alone.

Create your output there and the write fails with

> ⚠️ Access denied: Create permission required for node '{Space}/…'

which is what the person sees **instead of the thing they asked for**. It is not a permissions bug to
report, not something for them to "get access" to, and not a reason to stop: it is the wrong target.

**Everything you CREATE belongs under the user's own home — `{userId}/…`** (e.g.
`felice.buergi/DiceGame`, `felice.buergi/Skill/…`). That covers the program, the deck, the document,
the poster, the data node, the scratch node you needed on the way — and the **thread / activity**
that produces them.

- **Resolve the user id from the signed-in identity**, never from the page you happen to be on.
  Being *on* `AgenticPrimerDe/02-CodeWunsch` says what the request is ABOUT, not where it goes.
- **Read from anywhere; write only there.** Reading the lesson to understand the ask is right;
  writing next to it is not.
- If the user explicitly names a target they own, use it. Otherwise **default to their home** — do
  not ask them where to put it; they mostly do not know the mesh has places.
- If a write is denied anyway, **do not retry against the same space and do not escalate to the
  user**: re-target their home, finish the job, and tell them one line about where it landed.
- Say where it went when you are done ("I put it in your space at `…`"), with a link — a thing
  created somewhere the user cannot find is a thing you did not create.

You are authoring a **Markdown node** — a page. A Markdown node is the most common content node: `nodeType: "Markdown"`, its content is markdown, and it renders as a page with a header (icon + name) and your body below. The rules here also apply to the body of any node that carries markdown (a Space, a Doc, …).

# 1. Create it — shape

```json
{
  "nodeType": "Markdown",
  "name": "Getting Started",
  "icon": "📘",
  "content": { "$type": "MarkdownContent", "content": "…the page markdown…" }
}
```

- **`name`** is the page title (see §3 — do NOT restate it in the body).
- **`icon`** is on the NODE (§2).
- **`content.content`** is the markdown body (§3–§5).
- Create under a parent by setting `namespace` to the parent path (e.g. `namespace: "ACME"`), or top-level with an empty namespace.

# 2. ALWAYS set an icon — and make it RENDERABLE

Every page should carry an **`icon`** — it shows on the page header, in the parent's contents catalog, in menus and lists. A page without one falls back to bland initials.

> 🚨 The icon must be a **RENDERABLE** value:
> - an **emoji** — `📝`, `📊`, `🚀` (simplest, always renders), OR
> - an **inline `<svg>…</svg>`** (embedded on the page and in the catalog), OR
> - an **image URL** — e.g. `/static/NodeTypeIcons/document.svg`.
>
> **NEVER a Fluent icon *name*** (a bare word like `"Building"` or `"Document"`). A name is not an image — it renders as literal text or a broken image (it can't be put into an `<img src>`). Emoji and inline SVG are the safe, portable choices.

# 3. NEVER put a title in the body — `node.Name` is already the H1

The node's **`name`** is rendered as the page **`<h1>`** header (with the icon beside it) — automatically, from `node.Name`. So:

> 🚨 **Do NOT start the body with `# {the page name}`** (or any restatement of the title). It duplicates the header. **Begin the body with the first real content** — an intro sentence or the first section heading (`##`). Use `##`/`###` for sections *within* the page, never a top-level `#` that repeats the title.

# 4. Embed live areas inline with `@@`

Markdown can embed **regions** — live layout areas rendered in place. Put the reference at the **start of a line**. `@` makes a *link*; `@@` *embeds* the area.

| Want | Write |
|---|---|
| This node's contents catalog (children/index) | `@@("area/Search")` |
| This node's threads / files / comments / versions | `@@("area/Threads")`, `@@("area/Files")`, `@@("area/Comments")`, `@@("area/Versions")` |
| Another node's default (Overview) view | `@@Some/Node` |
| A specific area of another node | `@@Some/Node/Threads` |
| An absolute region from anywhere | `@@/{Node}/area/Search` |

Tune a search embed with query params: `@@("area/Search?groupBy=type")` (`category` / `flat`), `@@("area/Search?subtree=true")`.

> 🚨 The contents catalog is the **Search** area, not "Catalog" — embed it with **`@@("area/Search")`** (`@@Catalog` does not render the index). If the page has children, end the body with a `## Contents` section + `@@("area/Search")`.

# 5. Interactive markdown — executable code cells

A fenced code block becomes a **runnable notebook cell** — the reader sees the code, a **Run** button, and the result **directly below** — when you add flags to the fence. A plain fence is the explicit marker for non-runnable code (pseudo-code, wire shapes, bash).

    ```csharp --render MyDemo --show-code
    Controls.Markdown("Hello from the kernel")
    ```

- **`--render <AreaName>`** — executes the block in the kernel and streams its last expression into a result area **below the code** (the reader sees the REAL rendered control, not a screenshot). Blocks auto-execute on load; Run re-executes.
- **`--show-code`** — shows the source above the result (turns it into a full notebook cell with the Run toolbar). Omit for a live demo whose code isn't the point.
- **`--execute <id>`** — runs **silently** (shared setup for later blocks). All blocks on one page share **one kernel REPL session**, in document order — so an earlier `--execute` block can define variables a later `--render` block uses.
- The fence **language routes** the submission: `` ```csharp `` → the in-process Roslyn kernel; `` ```python --render X `` → the connected Python worker.

**Author every UI claim as a live cell** — if the page says a control renders, make it *render it*. (Embedded doc pages enforce this: `DocExecutableBlocksTest` runs every `--render`/`--execute` block through a real kernel in CI.)

# 6. Verify

Open the page: the **icon + name** show as the header (and the body does NOT repeat the title), every `@@` embed renders its live area, and each executable cell shows its Run toolbar + result below. If children exist, the `@@("area/Search")` catalog lists them.
