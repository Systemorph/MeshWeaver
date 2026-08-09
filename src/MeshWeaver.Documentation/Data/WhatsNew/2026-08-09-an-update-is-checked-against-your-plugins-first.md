---
Name: An update is now checked against your plugins before it is offered
Category: Fix
Description: A new platform build is only offered to portals once every plugin repository has been rebuilt against that exact build, so an update can no longer arrive and leave pages hanging.
Icon: ShieldTask
Order: -20260809
---

Until now, a new platform build was offered to portals as soon as the platform's own tests passed.
The code that lives inside your mesh — the node types, the layout areas — is built by the portal
when it runs, not by that test suite, so a change to the platform could remove something your node
types were still using and nothing would notice until the update had already been taken.

That is not a theoretical worry. It happened: three node types in a plugin were still calling a
helper the platform had dropped. Every check was green, the update shipped, and the portals that
took it could not finish starting up — pages hung, and each request waited out the full start-up
budget before giving up.

Each plugin repository does check itself against the platform, but deliberately against a fixed
platform build, so that a plugin's own pull requests never turn red because the platform moved
underneath them. Nothing was re-running those checks when the platform *did* move.

Now it does. A freshly built platform is treated as a candidate rather than a release: every plugin
repository rebuilds against that exact candidate first, and only if all of them come back clean is
the build offered to portals. If any of them break, the build is kept as a preview that no portal
will pick up, and the report names every affected node type across every repository at once —
rather than stopping at the first one and hiding the rest.
