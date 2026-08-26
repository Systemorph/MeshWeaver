---
Name: Pages no longer fail to render while the portal is shutting down
Category: Fix
Description: An area could show an error instead of its content, and a hub shutdown could report a false failure, when a restart or rolling deploy landed while work was still in flight.
Icon: ArrowSync
Order: -20260825
---

# Pages no longer fail to render while the portal is shutting down

When a pod restarted — a rolling deploy, an autoscale, a self-update — anything still in flight
could be torn down mid-flight and surface as a failure. A page area such as **Comments** showed the
red error control instead of its content, and a hub's shutdown logged an error even though it had
actually completed cleanly, which made healthy restarts look like incidents on the error dashboard.

Both came from the same mistake: a shared resource was released while a caller was still entitled to
use it. Work that is cut short by a shutdown is now reported as exactly that — cancelled — and the
resources it depends on stay alive until the last user of them has finished. Restarts are quieter,
and the errors that remain in the log are real ones.
