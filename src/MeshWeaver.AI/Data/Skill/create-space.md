---
nodeType: Skill
name: /create-space
description: Create a new Space so everything works — proper create, a nice summary, a logo, and an optional repo link
icon: Sparkle
category: Skills
order: 5
autoMount: true
---

You are creating a new **Space** — a top-level tenant container with its own partition, home page, and content. Follow these steps so the Space works end-to-end, not just renders an empty shell.

# 1. Create it the right way — `create`, never `update`

A Space MUST be created with a real **`create`** (CreateNodeRequest / MCP `create`). Creating triggers the server-side post-creation handler that **provisions the partition's Postgres schema, primes routing, and grants you Admin** at `{space}/_Access`. Converting a pre-existing bare node with `update` SKIPS that handler and leaves the Space half-provisioned (missing routing/grants → embedded areas don't load).

- **Top-level only:** a Space's path is just its id (empty namespace). Use a short PascalCase id.
- Shape: `nodeType: "Space"`, content `{ "$type": "Space", "name": "..." }`.

# 2. Write a nice summary (the most important step)

Always author the Space's **`body`** (markdown) — a short, warm summary of **what the Space is about and what it's for**, plus how to get started. Do NOT leave it empty: an empty Space falls back to a generic welcome placeholder whose catalog embed shows nothing, which looks broken. Also set a one-line **`description`** (shown under the title).

Write the summary in the owner's voice: its purpose, what kind of content lives here, and 2–4 bullet points on how to use it. A focused summary beats a wall of text.

# 3. Give it a logo and an icon

- **`icon`** — an inline SVG (or named icon) for the node, shown in lists and menus.
- **`logo`** — an image URL or data URI for the large header image (e.g. a served `/static/...svg`). Without a logo the header falls back to the node icon or the name's initials.

# 4. (Optional) Link a GitHub repository

To work on code from inside the Space, create a `{space}/_GitSync` node (`nodeType: GitHubSyncConfig`) with `repositoryUrl` + `branch` (default `main`). The **Code workspace** settings tab can then check the repo out, edit files in the browser, and commit + push as the user.

# 5. Verify

Open the Space's home page: the logo, name, and your summary should render (not the welcome placeholder), and `{space}/_Access` should hold your Admin grant. Then add the first pages or start a thread.
