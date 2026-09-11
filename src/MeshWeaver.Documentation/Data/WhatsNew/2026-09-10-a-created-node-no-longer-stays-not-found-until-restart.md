---
Name: A created node no longer stays “not found” until restart
Category: Fix
Description: A delayed response from an older read can no longer hide a node that was created while that read was in flight.
Icon: Sparkle
Order: -20260910
---

When a node is created while an earlier lookup is still finishing, the older “not found” response
can no longer overwrite the newer state. Reads and updates now recover as soon as the creation is
published instead of continuing to report the node as missing until the cache expires or the portal
is restarted.
