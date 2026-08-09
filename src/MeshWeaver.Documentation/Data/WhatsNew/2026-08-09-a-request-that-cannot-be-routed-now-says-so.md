---
Name: A request that cannot be routed now says so
Category: Fix
Description: Requests that failed while being routed used to spin forever instead of reporting an error.
Icon: Sparkle
Order: -20260809
---

# A request that cannot be routed now says so

When a request could not be routed onward — because the hub it had to travel through was in the
middle of shutting down, or because it was caught bouncing between hubs with nowhere to land — the
failure was thrown away instead of being sent back. Nothing was broken and nothing was busy; the
caller simply waited for an answer that no longer existed. In the browser that looked like a page or
a panel that spins indefinitely.

Those failures are now reported. The caller gets a real error naming what went wrong, and where the
cause is a hub restarting, it is reported as temporary so anything that can recover on its own —
a live view, an open document — reconnects instead of giving up.
