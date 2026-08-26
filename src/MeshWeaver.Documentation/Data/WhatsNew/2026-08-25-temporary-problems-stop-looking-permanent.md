---
Name: Temporary problems stop looking permanent
Category: Fix
Description: Fixes three ways a passing problem was reported as a lasting one — a view that failed instead of reconnecting, a plugin dropped for good after one slow moment, and a partly-imported space that reported itself up to date.
Icon: ArrowSync
Order: -20260825
---

# Temporary problems stop looking permanent

Three separate faults shared one shape: something that was only going to last a few seconds got
written down as final, and nothing ever went back to check.

**A view that failed instead of reconnecting.** Parts of the platform restart quietly in the
background — that is normal housekeeping. If a page happened to be drawing itself at that exact
moment, it stopped with an error panel and the internal fault text, and it stayed that way until you
reloaded. It now says it is reconnecting, and the content reappears on its own once the restart
finishes. Nothing had gone wrong, so nothing needs fixing by hand any more.

**A plugin dropped for good after one slow moment.** When a deployment starts up, it installs the
packages it is configured to ship with. If one of them could not be reached in time it was skipped —
correctly, so the others still install — but it was then recorded as though it *had* been installed,
so no later start-up ever tried again. A single slow moment quietly removed a package from an
installation for good. Failed packages are now recorded as failed, retried on the next start-up, and
listed so a missing package is visible without reading server logs.

**A partly-imported space that reported itself up to date.** When a space syncs from a repository and
most files import but one does not, the sync used to move its bookmark forward anyway. Every later
sync then answered "already up to date" and the missing file was never retried — while the space's
own screen said everything was fine. The bookmark now stays put until every file has landed, so the
next sync picks up what was missed.

A related cause behind the first two has also been fixed: one internal readiness check was asking
about content that legitimately did not exist yet, and that question could briefly block the very
write it was waiting for.
