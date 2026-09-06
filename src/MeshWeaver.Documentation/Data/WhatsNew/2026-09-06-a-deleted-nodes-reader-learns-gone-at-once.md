---
Name: A deleted node no longer leaves its readers waiting
Category: Fix
Description: A view or tool reading a node that has just been deleted learns it is gone at once instead of waiting out a timeout.
Icon: Sparkle
Order: -20260906
---

# A deleted node no longer leaves its readers waiting

When a node was deleted while something was still reading it, the reader could sit in silence until its own timeout expired, although the platform had already answered that the node was gone. A page could show "Not found" only after twenty seconds, and an assistant tool call could time out instead of reporting the delete. The answer is now delivered the moment it exists, so readers of a deleted node see it disappear immediately.
