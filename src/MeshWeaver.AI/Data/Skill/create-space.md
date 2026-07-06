---
nodeType: Skill
name: /create-space
description: Create a Space (the generic top-level container) end-to-end — proper create, a warm body, a logo, the live @@ regions that make a page useful, and the contents catalog at the bottom.
icon: Sparkle
category: Skills
order: 5
autoMount: true
---

You are creating a new **Space** and wiring its home page so it works end-to-end — not an empty shell. A Space is a tenant container: its own Postgres partition, a home page, an Admin grant for you, and content underneath.

# 0. Top level can be ANY type — Space is the generic one

A **top-level** node has an empty `namespace`, so its path is just its id. A top-level node can be **any** node type that owns a partition — but **`Space` is the most generic**, so reach for it unless a more specific top-level type clearly fits (a company, team, topic, product, or project workspace all map naturally to a Space). Everything below (regions, catalog, body) applies to a Space; most of it applies to any node with a markdown body.

# 1. Create it the right way — `create`, never `update`

A Space MUST be created with a real **`create`** (CreateNodeRequest / MCP `create`). Creating triggers the server-side post-creation handler that **provisions the partition's Postgres schema, primes routing, and grants you Admin** at `{space}/_Access`. Converting a pre-existing bare node with `update` SKIPS that handler and leaves the Space half-provisioned (missing routing/grants → embedded areas don't load).

- **Top-level only:** empty `namespace`, path = its id. Use a short PascalCase id.
- Shape: `nodeType: "Space"`, content `{ "$type": "Space", "name": "...", "description": "...", "body": "...", "icon": "..." }`.

# 2. Write the body — the page (the most important step)

Two distinct fields:

- **`description`** — a one-line tagline shown under the title in the header. Short.
- **`body`** — the page itself: markdown authored in the owner's voice (purpose, what lives here, 2–4 bullets on how to use it, then the **contents catalog** — see §4). Do NOT leave `body` empty: an empty Space falls back to the default welcome page (which itself ends in `@@("area/Search")`), but that's a placeholder — write a real one.

> 🚨 **Do NOT repeat the title in the body.** The space's **`name`** is already rendered as the page **`<h1>`** header — with its icon — from `node.Name`. Starting the body with `# {space name}` (or any restatement of the title) duplicates it. **Begin the body with the intro paragraph**, not a title heading.

> 🚨 **`icon` must be a RENDERABLE value** — an image URL (e.g. `/static/NodeTypeIcons/space.svg`), an inline `<svg>…</svg>`, or an emoji. **NEVER a Fluent icon *name*** (a bare word like `"Building"` is not an image — it renders as text or a broken image). Inline SVG and emoji render on the page and in the catalog; a Fluent name renders nowhere.

The body is plain markdown — headings, links, tables, and **`@@` region embeds** all work.

# 3. Regions: embed live areas inline with `@@`

A page's markdown can embed **regions** — live layout areas rendered inline. Put the reference at the **start of a line**. `@` makes a *link*; `@@` *embeds* the area in place.

**Common node regions** (available on every node; embed by name):

| Region | Embed (relative) | Shows |
|---|---|---|
| **Search** (the contents catalog) | `@@("area/Search")` | The node's children / namespace index — the "what's inside" catalog |
| Overview | `@@("area/Overview")` | The node's default content view |
| Threads | `@@("area/Threads")` | Discussion threads on the node |
| Files | `@@("area/Files")` | The node's content-collection file browser |
| Comments | `@@("area/Comments")` | Comments on the node |
| Versions | `@@("area/Versions")` | Version history |

**Reference forms:**

- `@@("area/Search")` — **relative** to the current page (resolves to THIS node's Search area). Use this inside a Space body.
- `@@/{Space}/area/Search` — **absolute** (any node, from anywhere).
- `@@Some/Node` — embeds another node's **default** (Overview) area.
- `@@Some/Node/Threads` — embeds a **specific** area of another node.

> 🚨 The contents catalog region is named **Search**, not "Catalog". `@@Catalog` does **not** render the children index — embed the catalog with **`@@("area/Search")`**.

# 4. Put the contents catalog at the END of the body

The standard idiom (what the default Space template ships) is to end the `body` with a Contents section:

```markdown
## Contents

@@("area/Search")
```

Tune it via query params: `@@("area/Search?groupBy=type")` (or `category` / `flat`), `@@("area/Search?subtree=true")` to include the whole subtree. See the *Mesh Search & Catalogs* doc.

# 5. How to list a node's layout areas (regions)

To see the areas registered on a node, use the **plural `layoutAreas`** reference:

```
Get('@{path}/layoutAreas/')
```

It returns each area's name + description. Two caveats the test below pins:

- It is **`layoutAreas`** (plural — LISTS the areas). The singular **`area/{Name}`** fetches ONE area's rendered payload — that's not how you list.
- The listing returns only **catalog-visible** areas (custom / author-defined ones). The **standard node regions in the table above are `[Browsable(false)]`** — fully embeddable via `@@("area/Search")` etc., but they do **not** show up in the `layoutAreas` listing. So don't conclude a region is missing just because it isn't listed — the standard ones you embed by name.

Retrieval mechanism + the visible-vs-embeddable rule are pinned by `LayoutAreaRetrievalTest` (test/MeshWeaver.Graph.Test).

# 6. Give it a logo and an icon

- **`icon`** — a RENDERABLE value shown in lists, menus, and the page header: an inline `<svg>`, an emoji, or an image URL (e.g. `/static/NodeTypeIcons/space.svg`). **NEVER a Fluent icon name** like `Building` (a name is not an image — it won't render). See §2.
- **`logo`** — an image URL or data URI for the large header image (e.g. a served `/static/...svg`). Without a logo the header falls back to the node icon or the name's initials.

# 7. (Optional) Link a GitHub repository

To work on code from inside the Space, create a `{space}/_GitSync` node (`nodeType: GitHubSyncConfig`) with `repositoryUrl` + `branch` (default `main`). The **Code workspace** settings tab can then check out the repo, edit files in the browser, and commit + push as the user.

# 8. Verify

Open the Space's home page: the logo, name, and your `body` should render (not the welcome placeholder), the `@@("area/Search")` catalog should list the children, and `{space}/_Access` should hold your Admin grant. Then add the first pages or start a thread.
