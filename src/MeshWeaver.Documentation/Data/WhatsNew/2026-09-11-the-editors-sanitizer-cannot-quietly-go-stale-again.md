---
Name: The code editor's sanitizer cannot quietly go stale again
Category: Fix
Description: The editor package's pinned version is now held in place with the checks a change to it requires, so the HTML sanitizer inside the editor cannot fall behind again without anyone noticing.
Icon: Shield
Order: -20260911
---

# The code editor's sanitizer cannot quietly go stale again

The code and markdown editor sanitizes the HTML it renders in hovers and suggestions. The editor
package the platform depends on carries its own copy of that sanitizer inside its assets rather
than declaring it as a dependency — so the copy the portal was serving had fallen three years
behind its upstream, and neither the automated dependency updates nor a normal build could see it.
The portal now builds and serves its own current editor instead, and the outdated files are no
longer published at all.

What was still missing was anything that would notice the problem coming back. The version of that
package is chosen in one place, nothing in that part of the platform is built against it, and the
part of the platform that does use it takes whatever version it is given — so changing one line
could have quietly changed what the editor is built from. That version is now held in place
together with the checks a change to it requires, and the editor itself has a documented,
repeatable end-to-end verification: highlighting, error squiggles, language services and sanitizing
are all exercised against the running portal rather than inferred from the files it ships.
