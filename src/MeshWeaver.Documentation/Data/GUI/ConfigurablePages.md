---
Name: Configurable Home & Space Pages
Category: Documentation
Description: Your home page and every Space Overview are a single editable markdown page (the node's Body) that embeds live regions with @@ — keep the default, or make it your own.
Icon: /static/DocContent/GUI/icon.svg
---

Your **home page** and every **Space Overview** are the same thing: a single, editable **markdown
page** that lives on the node's **`Body`**. There is no fixed dashboard layout to fight with — the
page *is* the layout, and it can embed any live region with the `@@` operator.

> **One page, one source of truth.** Edits go to the node's `Body` field. Nothing is replicated into
> a separate store, so what you see is exactly what is saved.

---

## Default vs. your own

By default `Body` is empty, so the page shows a **welcome template** — a short greeting plus a few
embedded regions. The moment you write anything into `Body`, your markdown replaces the template
**verbatim**. Two ways to author it:

- **Ask the assistant.** Use the chat on your home page: *"put my open threads at the top and drop
  the catalog."* It writes to the same `Body`.
- **Edit it yourself.** Write plain markdown — headings, links, tables, images, and `@@`-embeds all
  work.

Clear `Body` again and the live default comes back.

---

## Embedding regions with `@@`

A region is just a layout area embedded inline. Put the embed on its own line:

```markdown
## My work

@@("area:Pinned")

@@("area:Search")
```

`@@("area:Name")` resolves against **this** node, so `@@("area:Search")` renders this node's own
catalog. You can also embed a **specific node's** area with a path:

```markdown
@@("Chat/area/Overview")        // a child node's area (relative to this node)
@@("/Doc/GUI/MeshSearch")       // any node, absolute path
```

The full reference for the `@` / `@@` syntax (links vs. inline render, query parameters, content
references) is in [Interactive Markdown](/Doc/DataMesh/InteractiveMarkdown).

---

## Regions you can embed

Some areas exist on **every** node; others are specific to the home page.

| Embed | Where | What it shows |
|---|---|---|
| `@@("area:Search")` | every node | The node's [search catalog](/Doc/GUI/MeshSearch) — group, filter, and drill down via query params |
| `@@("area:Pinned")` | home page | The items you've pinned |
| `@@("area:Threads")` | home page | Your open (not-done) threads, newest first |
| `@@("area:Catalog")` | home page | A tabbed catalog — Spaces, your items, recently read, recently edited |
| `@@("area:Composer")` | home page | The chat composer (start a new thread right from your home) |

> Generic areas like `Search` are not special to the home — they come from the standard node layout,
> so the same `@@("area:Search")` works on a Space, a folder, or any other node.

---

## Tips

- **Order is yours.** The embeds render top-to-bottom in the order you write them.
- **Tune a region.** Areas read query params off the embed, e.g.
  `@@("area:Search?groupBy=type")` — see [Mesh Search & Catalogs](/Doc/GUI/MeshSearch).
- **Mix prose and regions.** Add a heading or a paragraph above any embed to frame it.
- **Start from the default.** Ask the assistant to "show me the default page as markdown" and edit
  from there.

---

## Where to go next

| Topic | Description |
|---|---|
| [Interactive Markdown](/Doc/DataMesh/InteractiveMarkdown) | The full `@` / `@@` embed syntax |
| [Mesh Search & Catalogs](/Doc/GUI/MeshSearch) | The `Search` area and its query parameters |
| [Layout Areas](/Doc/GUI/LayoutAreas) | What an "area" is and how embeds resolve |
