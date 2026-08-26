---
Name: A freshly created node's own server no longer stays silent about it
Category: Fix
Description: A newly created item could intermittently stay unreachable — "page not found" — even though it was saved correctly, until the server restarted. A second internal announcement that was skipping the same step other saves already fixed is now fixed too.
Icon: Sparkle
Order: -20260826
---

# A freshly created node's own server no longer stays silent about it

A recently fixed issue let some newly created items stay invisible to the running server until it
restarted, even though the content was safely saved. That fix covered two of the places a save could
skip telling the rest of the server "this now exists" — but a third one, closer to the item's own
home, could still skip it under exactly the same kind of timing.

When a brand-new item's own server activates for the first time, it can end up writing the item to
storage a second time as part of settling in. If that second write lands before the first
announcement would have, and it also stayed silent, the item kept the earlier "not found" answer
memorized — most visibly right after installing a course or other bundle of content, where one item
would refuse to open while everything around it worked fine.

This last silent spot now announces too, so a freshly created item is reachable the moment it is
written, without needing a restart to notice it. Items reactivated later, already known to the
server, are unaffected — nothing changes for those.
