---
Name: A restricted parent now refuses a type the form never offered
Category: Fix
Description: A NodeType's CreatableTypes list is enforced when the node is written, not only when the Create form is drawn — so a type the parent withholds can no longer be created around the form.
Icon: Add
Order: -20260913
---

# A restricted parent now refuses a type the form never offered

When a NodeType declares `CreatableTypes`, the Create form has always offered only the types on that
list. The **write** did not check it. Anything that created a node without going through the form —
an agent, a script, the MCP `create` tool, or a page that sent the form's type value directly —
could put any type under that parent, and nothing said no.

Now the write checks it too. Creating a type the parent's NodeType does not list is refused, and the
message names the type, the parent type that set the restriction, and what is allowed instead.

## What can now fail that used to succeed

A create is refused where it previously succeeded **only** when all of these are true at once:

- the node is being created under a parent whose NodeType declares an explicit `CreatableTypes`
  list (a parent that declares nothing restricts nothing — unchanged);
- the type being created is not on that list, and not one of the global types (unless the parent
  also set `IncludeGlobalTypes: false`);
- and the creator is a **person** — an agent, a script or a signed-in user.

Installs, git sync, plugin installs, migrations and every other write the platform makes on its own
behalf are not affected: they were never what this setting is about, and a curation list that
refused an install would look like data loss. Bookkeeping nodes filed beside a node (`_Access`,
`_GitSync`, `_Policy` …) are not affected either.

If a create is refused that you expect to work, the fix is on the parent's NodeType: add the type to
its `CreatableTypes`. The refusal names which type that is.
