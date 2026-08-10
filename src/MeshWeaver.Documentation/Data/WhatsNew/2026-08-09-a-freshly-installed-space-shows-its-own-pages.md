---
Name: A freshly installed space no longer opens with only the generic pages
Category: Fix
Description: A space whose main page was first visited while it was still being set up could stay stuck on the standard pages forever; it now picks up its own pages as soon as they exist.
Icon: Sparkle
Order: -20260809
---

# A freshly installed space no longer opens with only the generic pages

Opening a space's main page for the first time — while the space was still being created, or in the
first moments of installing a package into it — could leave that page permanently showing only the
standard set of pages every node gets (Overview, Settings, Versions and so on). None of the pages
the space itself defines appeared, and following a link to one answered "Area not found". Nothing
was logged, nothing recovered, and only restarting the portal brought the missing pages back.

The portal was remembering a stand-in for the space that it invents while the real one is still
being written, and it kept serving that stand-in even after the real space had landed. Two related
mix-ups did it: the invented stand-in was remembered as though it were real, and an answer worked
out just before the space was saved could still be remembered just after — at which point nothing
was left to correct it.

Invented stand-ins are no longer remembered at all, and an answer is only remembered if nothing
changed underneath it while it was being worked out. A page that opens early now simply picks up
the real space on the next visit instead of being stuck with the placeholder.
