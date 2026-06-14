# Spaces

A **Space** is a container for work that belongs together — a company, a team, a
project, or an initiative. It is the unit of **storage**, **access**, and
**collaboration** in MeshWeaver: everything inside a Space shares one home, one
set of access rights, and one group of collaborators.

> **In one sentence:** a Space groups related content, with common access rights
> and collaborators, behind a single boundary.

## What a Space actually is

- **A partition root.** Each Space owns its own partition (its own storage schema).
  Content created inside a Space lives in that partition and is governed by the
  Space's access rules — it is isolated from every other Space.
- **Always top-level.** A Space's path is just its name (`Acme`, `RiskTeam`,
  `Pension2026`). Spaces never nest inside other Spaces; content nests inside a
  Space, not the other way around.
- **Yours to run.** Whoever creates a Space becomes its **Admin** and can invite
  collaborators and grant them roles. Any signed-in user may create a Space.

Your own personal partition (`{your-id}/…`) is effectively your private space —
use it for personal drafts. Create a **Space** when work needs to be *shared*.

## When to create a Space

Create a Space when you have a **coherent body of work and a set of people who
should share access to it**. The two questions that decide it:

1. **Does this stuff belong together?** Same topic, same goal, same lifecycle.
2. **Do these people share roughly the same access?** The collaborators and their
   rights are *common-ish* across everything in it.

If both are yes, it's one Space.

### Right-sizing: as small as possible, as large as necessary

A Space should be **as small as possible, as large as necessary**.

- **Too small** — a Space per document or per meeting. You drown in boundaries and
  re-grant the same people access over and over.
- **Too large** — one giant Space for the whole company. Everyone can see
  everything; access becomes meaningless.
- **Just right** — one coherent unit (a team, a product, a client engagement) whose
  members share a purpose and a permission set.

**Rule of thumb:** if two bodies of work have *different collaborators or
different access needs*, they belong in **different** Spaces. If they share
collaborators, access, and purpose, keep them in **one** Space and organize with
folders/pages inside it.

## What lives inside a Space

Once a Space exists, everything created under it — pages and documents, threads
and chats, agents, demos, uploaded files — lives in the Space's partition and is
governed by the Space's access. To add content you don't create another top-level
node; you create it *inside* the Space.

## Access and collaborators

- The creator is the Space **Admin**.
- The Admin invites collaborators and assigns roles (e.g. Admin, Editor, Viewer)
  on the Space; those rights apply to everything inside it ("who can read the
  Space can read its content").
- A Space must always keep at least one Admin — you can't remove the last one.

Because access is set **once at the Space level** and inherited by its contents,
grouping by *common-ish access rights* is what keeps administration simple. That
is the main reason to draw the boundary where you draw it.

## Creating a Space

Any signed-in user can create a top-level Space and immediately becomes its Admin:

- **In the portal:** create a new node of type **Space** at the top level, give it
  a name, and start adding content inside it.
- **Programmatically / MCP:** `create` a node with `nodeType: Space` and an empty
  namespace (top level). The partition schema is provisioned automatically and the
  creator is granted Admin before the create returns.

You **cannot** create ordinary content (a Markdown page, a Code node, a Group…)
directly at the top level — the root is reserved for partitions. Put it in your
own space (`{your-id}/…`) or **create a Space first and add it there**. This is by
design: it prevents stray top-level content and keeps every node inside a clear
access boundary.

### What gets created (and what does *not*)

Creating a Space `Acme` (empty namespace, id `Acme`) automatically produces:

- the **Space node** at path `Acme` — *this node is the home page* (see below);
- the **partition schema** `acme` with all its tables (provisioned before the node
  is written — no `42P01` races);
- a **partition definition** `Admin/Partition/Acme` so every silo agrees the
  partition exists and is routable;
- a root **Admin grant** `Acme/_Access` for whoever created it.

> **There is no separate "Overview" or "Home" page.** A common mistake is to create
> a Markdown child like `Acme/Overview` and treat it as the landing page. Don't —
> the Space node *is* the landing page, and a stray Overview node just duplicates
> it. The text below is everything you need to make `Acme` itself look good.

## Authoring your Space's home page

When someone opens `Acme`, the Space's **Overview** renders, in order: a header
(logo + name + description + links), then your **body markdown**, then the
**namespace catalog** of everything inside the Space. All of it is driven by fields
on the **Space node's content** — you never create a second node for it.

### The fields you set

| Field (`content.…`) | Shows up as |
|---|---|
| `name` | The large title in the header. |
| `description` | Sub-title under the name (one line, markdown allowed). |
| `logo` | The 100×100 header image. An **`https://…` URL** *or* a file in the Space's `content` collection. Falls back to the node icon, then to initials. |
| `body` | The main page content — **markdown**. This is your "overview text". |
| `website`, `email`, `location` | Small linked stats in the header row. |
| `icon` | Fluent icon name used where no logo is set (default `Building`). |

The body is resolved as **`node.PreRenderedHtml` → `content.body` → default welcome
text**. So to replace the generic starter text, just set `content.body`. Leaving it
empty falls back to the welcome placeholder — which is the "generic template text"
you see on a fresh Space.

### Writing a good overview body

Treat `content.body` as the front door. A strong one usually has:

1. **A short summary** of what the Space is for.
2. **Curated links** to the important material inside it. Link with the unified
   path syntax so links survive moves and renames:
   - `[Balance sheet model](@/Acme/Reports/BalanceSheet)` — link to a node by path.
     (`@/…` is markdown-link-only; never put it in raw `<a href>`.)
   - Relative links also work from the Space body: a link written as
     `[Reports](Reports)` resolves against the Space path (`Acme/Reports`).
3. **The live namespace catalog**, embedded inline (next section).

### Embedding the namespace catalog (the search that expands by namespace)

The catalog is the `Children` layout area: a mesh search in **namespace-tree** mode,
scoped to the Space's own partition, that lets you drill into sub-namespaces with
lazy-loaded counts and a search box. It is **not** hard-wired into the page — it lives
in the body as a **deletable `@@`-embed** (the default welcome page ships one), so the
space owner controls whether and where it appears. Delete the embed line to drop the
catalog; move it to reposition it. Embed it with:

```markdown
@@/Acme/area/Search
```

In an **authored body**, use the **absolute** form (leading `/` + full Space path): it
resolves regardless of render context because it carries its own address. The default
welcome page (shared by every Space) uses the relative `@@("area:Search")`, which
resolves against the Space's own path at render time. `@@` (double-at) renders the area
inline; a single `@` would render a hyperlink instead. The `Search` area defaults to the
namespace tree; tune it with `?groupBy=type|category|namespace|flat` and `?subtree=true`
— see [Mesh Search & Catalogs](/Doc/GUI/MeshSearch).

> The Space's body itself is read from `MeshNode.Content` (the `Space` record) — there
> is no separate `Space` data stream. The Overview reads the node's content directly,
> which is why setting `content.body` / `content.logo` is all you need.

### Setting it all via MCP

The home page is plain node content, so one `patch` against the Space node does it:

```jsonc
// patch @Acme  (content is the Space record)
{
  "content": {
    "logo": "https://example.com/acme-logo.png",
    "description": "Acme's shared workspace for pension analytics.",
    "body": "# Acme\n\nWelcome to Acme's workspace…\n\n## Explore\n\n- [Reports](@/Acme/Reports)\n- [Data model](@/Acme/Datenmodell)\n\n## Everything in this space\n\n@@/Acme/area/Search\n"
  }
}
```

`patch` merges (RFC 7396), so you only send the fields you want to change. If the
logo doesn't appear, check two things: the field is actually `content.logo` (not the
node `icon`), and the image host allows hot-linking — some CDNs (e.g. LinkedIn media
URLs) return 403 to off-site `<img>` requests, in which case upload the image into
the Space's `content` collection and reference it instead.

## Quick reference

| You want to… | Do this |
|---|---|
| Share a body of work with a team | Create a **Space**, invite the team |
| Keep a private draft | Use your personal partition (`{your-id}/…`) |
| Add a page/doc/thread to a team's work | Create it **inside** the team's Space |
| Separate two efforts with different collaborators | Use **two** Spaces |
| Organize within one team's work | Folders/pages **inside** one Space |
| Set the Space's landing page | Edit `content.body` on the **Space node** — no separate Overview page |
| Add a logo | Set `content.logo` (an `https://…` URL or a `content`-collection file) |
| Show the namespace catalog in the body | Embed `@@/{Space}/area/Search` (`?groupBy=type` to group by type) |
