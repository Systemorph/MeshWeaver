---
Name: Hubs no longer get stuck in a failed state when startup races shutdown
Category: Fix
Description: A hub initializing at the exact moment its process or parent was shutting down could be marked permanently failed, answering every request with an error until restart. That race is now recognized as a normal shutdown.
Icon: Sparkle
Order: -20260828
---

# Hubs no longer get stuck in a failed state when startup races shutdown

When a part of the mesh was starting up at the exact moment its host was being
torn down — for example during a pod restart — it could observe the teardown
mid-initialization and record itself as permanently failed instead of simply
shutting down. Everything served at that address then answered with an error
until it was recreated.

That window is now recognized for what it is: a shutdown, not a failure. The
affected hub winds down cleanly, error logs no longer report routine teardown
as an initialization failure, and a genuine initialization fault still surfaces
loudly exactly as before.
