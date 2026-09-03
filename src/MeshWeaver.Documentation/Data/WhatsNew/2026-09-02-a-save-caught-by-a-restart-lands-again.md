---
Name: A save caught by the owner restarting lands again instead of failing
Category: Fix
Description: A write in flight while the hub owning the content shut down was answered too early, so its automatic retry hit the dying hub and the save failed — or, during a full shutdown, the answer never reached the waiting save at all. The retry now waits for the hub to actually let go of the address.
Icon: Warning
Order: -20260902
---

# A save caught by the owner restarting lands again instead of failing

When you save content that lives on another hub and that hub happens to be shutting down at the same
moment — a recycle, a restart, a whole-portal stop — the owner tells your side "I did not apply this;
retry against my successor", and your side retries automatically. That has worked for a while.

Yesterday's fix for saves that hung when the owner *stopped watching* introduced a second place that
could give that same "retry" answer — and it gave it one step too early. The owner's stream ends
partway through its shutdown, while the old hub still holds the address. Answering at that instant
sent the automatic retry straight into the hub that was still going down, which refused it, and the
save failed with an unexplained error instead of landing. During a full shutdown the early answer was
worse: it was sent over a route that no longer delivers, and because a save is answered only once, the
proper answer — the one that does reach the waiting save — was never given. The save then waited out
its whole confirmation window in silence.

The early answer now stands aside whenever the owner is shutting down. The verdict comes, as before,
from the owner's final shutdown step — the point at which it has released the address — so the
retry lands on the fresh hub and the save completes, and it is handed to the waiting save directly,
so a portal-wide shutdown no longer leaves a save hanging. The "owner stopped watching" case keeps
its prompt answer; the two are told apart by whether the owner is going down.
