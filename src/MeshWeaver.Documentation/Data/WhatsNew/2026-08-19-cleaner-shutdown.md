---
Name: Portals shut down without crashing on the way out
Category: Fix
Description: A restarting portal now stops its background work before releasing it, instead of occasionally crashing at the very end of shutdown.
Icon: Sparkle
Order: -20260819
---

# Portals shut down without crashing on the way out

When a portal restarted, it could release the memory holding your workspace's
compiled views while background work was still using it. Everything had already
finished successfully by then, so the only sign was a crash right at the end of
shutdown — untidy, and it made restarts harder to tell apart from real faults.

Shutdown now stops that background work and waits for it to finish before
letting go of anything, so a restart ends cleanly.
