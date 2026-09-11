---
Name: A repository whose content CI cannot be found now says so
Category: Fix
Description: Requiring the content CI to publish could have frozen a synced Space silently; a Space that stops receiving content because its repository's build workflow was not recognised is now reported, and the deployment repository is recognised again.
Icon: Checkmark
Order: -20260911
---

Accepting a build only from a repository's content-CI workflow has one way to go wrong: the platform
looks for that workflow where the repository does not keep it. Every delivery is then refused, GitHub
is answered normally, and every Space syncing that repository quietly stops receiving content — the
failure that once left a Space 38 hours behind with nothing reporting a problem.

Two changes close that. `Systemorph/Memex`, whose deployment records two portals sync, has no
`ci.yml` and is now declared by the platform, so it keeps publishing. And when a repository that a
Space actually syncs has a green run refused while no run of its expected content CI has ever been
accepted, the portal logs a warning naming the workflow it found, the one it expected, the Spaces
that are frozen, and how to declare the right one — instead of failing silently.
