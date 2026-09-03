---
Name: One fewer mesh-wide question on every page
Category: Fix
Description: Every permission check used to ask the database, across every space at once, for a platform-wide access policy that cannot exist there — hundreds of times a minute on a busy portal. It now asks the one place such a policy could live, so that question stopped competing with your pages for the database.
Icon: ShieldCheckmark
Order: -20260902
---

# One fewer mesh-wide question on every page

Everything you see is filtered by what you may see, and part of that filter is a platform-wide
access policy that would apply to every space. Reading it was spelled in a way that gave the
database no idea where to look, so it looked everywhere: one combined scan across every space on the
portal, repeated whenever anything anywhere changed. On a portal with a couple of hundred spaces and
eight servers that was a few hundred scans every five minutes, each taking seconds and each holding
locks that every other page was waiting behind — for an answer that was always empty, because the
storage layer cannot even hold such a policy today.

The read now names the one place the policy could live. The database answers it from there in
microseconds, and the pages that used to queue behind those scans get the database back. Nothing
about who may see what has changed: the same policy, if it existed, would be read from the same place.

Two things this did NOT change, so the numbers are honest: the notification bell and mail listings
still ask across every space (that is a separate change in the portal shell), and the group
membership read genuinely has to look everywhere, because a group can live in a different space
than the content it opens.
