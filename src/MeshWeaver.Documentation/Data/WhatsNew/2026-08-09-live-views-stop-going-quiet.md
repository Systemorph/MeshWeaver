---
Name: Live views no longer go quiet after a save
Category: Fix
Description: A list, table or search result could silently stop updating after new content appeared; it now always catches the change.
Icon: Sparkle
Order: -20260809
---

# Live views no longer go quiet after a save

Everything you see in the portal is live: create a page and the folder listing next to it grows, add
content and an open search result picks it up. Occasionally that stopped happening — the item was
saved correctly, but a view already on screen kept showing the world as it was a moment earlier, and
only a reload brought it back in line.

The cause was one notification reaching some watchers and not others. Whenever anything was saved,
every open view was told about it in turn, and a single watcher that was in the middle of closing
could cut the announcement short — so every view further down the line was never told at all, with
nothing reported anywhere.

Each view is now told independently, so no view can be silenced by another, and a watcher that does
run into trouble is recorded instead of disappearing.
