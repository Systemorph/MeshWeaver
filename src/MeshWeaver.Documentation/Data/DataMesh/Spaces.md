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

## Quick reference

| You want to… | Do this |
|---|---|
| Share a body of work with a team | Create a **Space**, invite the team |
| Keep a private draft | Use your personal partition (`{your-id}/…`) |
| Add a page/doc/thread to a team's work | Create it **inside** the team's Space |
| Separate two efforts with different collaborators | Use **two** Spaces |
| Organize within one team's work | Folders/pages **inside** one Space |
