---
Name: A module version that does not load here no longer takes your working copy away
Category: Feature
Description: When a newer version of an installed module cannot load on the platform your installation runs, the installation keeps running the version it has instead of losing the module. The status row names both versions; nothing asks you to restart, and the newer version takes over by itself once a build that loads here ships.
Icon: ArrowSync
Order: -20260908
---

# A module version that does not load here no longer takes your working copy away

Every module you install is kept as a *generation* on your installation's volume, and the newest
one is the one that loads at boot. Until now, when that newest generation could not load on the
platform your installation runs — because it was built against a newer platform than yours, which
the platform measures before loading anything — the module was simply **absent**: the previous
generation, the one that had been working, was no longer referenced by anything, and the next
housekeeping pass removed it. Unless the platform image happened to ship a copy of the module
itself, a feature you had installed disappeared because a newer version of it existed.

The rule is now the one an installation would expect: **it runs the newest version of every
installed module that loads, and keeps the one it has until a newer one does.**

- The generation a new landing replaces is kept as the module's *fallback*, and housekeeping never
  removes a fallback while it is the one that loads.
- At boot, when the newest generation is refused or fails to load, the fallback is loaded in its
  place and the module is **present** — its features work, the readiness probe stays healthy, and
  the module counts as running for everything that checks whether it is.
- The module's status row says exactly what happened: *runs v1.2.3 (gen A); v1.3.0 (gen B) landed
  but does not load here: …*, naming the type the newer version needs and your platform lacks.
  The package card carries the same note. Nothing reports "restart required" — a restart would
  measure the same bytes and fall back again — and nothing reports "not running here", because it
  is running.
- Only when *no* generation of a module loads is it absent, exactly as before.
- Uninstalling a module clears both generations.

The newer version starts running by itself once a build of it that loads on your platform ships,
or once your platform updates. What the installation runs is also recorded with the mesh's module
set, so every replica and every status page agree on which generation is actually live.
