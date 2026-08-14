---
Name: Clean portal shutdowns
Category: Fix
Description: The portal now finishes serving before the mesh is torn down, so a rolling update no longer produces a burst of shutdown errors.
Icon: Sparkle
Order: -20260814
---

# Clean portal shutdowns

Every time a portal was replaced during a deployment, the last moments of the old pod produced a
burst of errors: requests that were still being served found the mesh already gone. The mesh was
being shut down too early — before the web server had stopped accepting traffic — so anything still
in flight ran against a portal that was half torn down.

Shutdown now happens in the right order: the portal stops serving first, and only then does the mesh
drain. A rolling update is quieter, and the errors that used to appear at the tail of every roll are
gone.
