---
Name: Pages no longer stall on "Building layout…"
Category: Fix
Description: A page could open, connect, and then sit on its loading frame forever because one update overtook another in transit and the real content was discarded as stale.
Icon: Sparkle
Order: -20260810
---

# Pages no longer stall on "Building layout…"

Occasionally a page would connect normally and then never finish: the loading
frame stayed up, nothing failed, and nothing appeared in the logs. Reloading
usually fixed it, which made it look like a hiccup rather than a bug.

It was not a hiccup. Updates for one page travel from the node that owns it to
your browser as a numbered sequence, and the receiving side deliberately ignores
an update older than the one it already applied — that is what stops a delayed
message from undoing newer state. The distributed router, however, was sending
those updates on independent threads, so two of them could swap places on the
way. When the swap happened to hop over the update carrying the page's actual
content, that content was ignored as "old" and never sent again: the page kept
the loading frame it started with, forever.

Updates for the same destination are now sent strictly in the order they were
produced, so the page always receives its content.
