---
Name: A hub shutting down no longer waits on another hub's thread
Category: Fix
Description: When many hubs were torn down at once, some could sit with their shutdown message queued and untouched — because the work that was supposed to pick it up had been parked on the thread of a completely unrelated hub. It is now handed to the thread pool proper.
Icon: ArrowSyncCheckmark
Order: -20260915
---

# A hub shutting down no longer waits on another hub's thread

A hub tears itself down by putting a shutdown message on its own queue and letting its message pump
pick it up. On 2026-09-07 a portal reported 47 hubs that had been asked to shut down and never got
that far: each one had exactly one message waiting — the shutdown message — and the pump that was
supposed to take it never ran. Nothing was lost and nothing crashed, but each of those hubs stayed
alive, holding its stream and its subscriptions, for as long as the condition lasted.

**The cause was where the pump's next run was queued.** When a hub is given a message, the work that
drains its queue is scheduled from whichever thread did the posting. On the .NET thread pool, work
scheduled that way goes into a queue *private to that thread* — fine when the thread is about to go
looking for more work, and not fine at all when it is busy running some other hub. Measured on an
18-core machine with every pool thread busy: work parked that way sat untouched for a full 20
seconds, and the pool did not even add a thread, because it cannot see work hiding on a busy
thread's private queue.

That is exactly what a mass teardown looks like. A hub disposing its children posts every child's
shutdown message from one thread, one after another, so every child's pump ends up queued behind
that one thread's own work — and each child then waits not for anything of its own, but for an
unrelated hub to run out of things to do.

**Each hub's pump is now queued to the shared pool**, where any thread can take it and where the
runtime's own back-pressure can see it and add capacity. A hub's shutdown now depends only on its
own work, never on another hub's. Under a genuinely saturated pool a teardown can still take a
moment — that is the pool doing its job — but it can no longer stall indefinitely behind an
unrelated hub.

The diagnostic that reported these stalls was already accurate, and it is what pinned the cause; it
now says the pool is out of threads rather than pointing at a queue this fix has emptied.
