---
Name: The phone home fills in and doc links navigate
Category: Fix
Description: Home sections show your content instead of "No results", empty regions stop spinning, doc links open their pages, and a signed-in portal lands on your own home.
Icon: Sparkle
Order: -20260821
---

# The phone home fills in and doc links navigate

Four fixes from phone testing. The home catalog ran its combined query as one string the server
could not parse, so every section said "No results" — the sub-queries now run individually and
merge, and your content appears. A region the server deliberately renders empty (like Pinned with
no pins) showed an endless spinner — it now shows nothing, as on the web. Documentation links
rendered as dead text — they now navigate to their pages. And connecting to a portal you are signed
in to lands on your own home, like the web portal, instead of the documentation.
