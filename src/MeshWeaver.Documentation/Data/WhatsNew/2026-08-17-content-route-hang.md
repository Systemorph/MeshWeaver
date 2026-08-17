---
Name: Images and downloads no longer hang
Category: Fix
Description: Content files served from /api/content could hang instead of loading on portals running more than one replica.
Icon: Sparkle
Order: -20260817
---

# Images and downloads no longer hang

Pictures, videos and file downloads stored on a node are served through the content route. On a
portal running more than one replica, roughly half of those requests never answered — the page
showed a broken image or an empty player, and a download simply stalled. Reloading sometimes
"fixed" it, which made the problem look random rather than reproducible.

The request was being issued from the mesh's internal routing hub, so when the file's owning node
happened to live on a different replica the answer had nowhere to come back to and the request sat
until it timed out. It is now issued from a normal application hub, which every other part of the
portal already used, so the answer always finds its way home.
