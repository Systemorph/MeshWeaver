---
Name: The docs no longer name tools that were deleted
Category: Fix
Description: A round of corrections to the GUI, Data Mesh and AI documentation — pages that described services, menus and APIs that no longer exist, or that had never existed.
Icon: Sparkle
Order: -20260810
---

# The docs no longer name tools that were deleted

Documentation drifts quietly. Code gets renamed, an interface is deleted, a menu is
rebuilt — and the page describing it keeps reading perfectly well while being wrong.
This is a pass over the GUI, Data Mesh and AI sections looking for exactly that:
statements that are false, checked one at a time against the code they describe.

Some of what turned up:

- **Node Operations** documented two services for exporting and importing subtrees
  that were deleted more than two months ago, complete with code samples that would
  have thrown on their first line. It also described the export archive in the wrong
  shape entirely. Both now describe the surface that actually runs.
- **The node menu** page attributed the built-in entries to a provider that does not
  exist, grouped under an "Actions" sub-menu that does not exist either. Every row of
  its reference table had the wrong order, the wrong permission, or the wrong menu.
- **The agent tool reference** listed three database tables that are not in any
  schema, so an agent filtering on those names would have found nothing. It also
  described the "recently changed" feed as if it were the "recently opened" feed —
  two different things that answer very different questions.
- **The MeshPlugin reference** presented a tool only available over MCP as part of
  the in-portal agent's tool set, omitted the two tools that exist precisely so an
  agent does not have to rewrite a whole document to change one field, and did not
  mention that deleting a node deletes everything beneath it.
- Two pages taught a way of loading initial data that the framework does not accept,
  one of them contradicting its own guidance forty lines further down the same page.
- Several code comments advertised an asynchronous helper that is forbidden in this
  codebase, in methods that had already been rewritten not to use it — including two
  that recommended it to the next person to touch them.

Nothing here changes behaviour. It changes what the platform tells you its behaviour
is, which is the part you act on.
