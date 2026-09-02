---
Name: A background compile watcher stops when its host stops
Category: Fix
Description: The watcher that tracks a node type's source files no longer keeps working against a host that has already begun shutting down, which used to cost every teardown two seconds and could take the whole process down with it.
Icon: Bug
Order: -20260902
---

# A background compile watcher stops when its host stops

Every node type has a small background watcher that keeps an eye on its source files, so the
platform knows when the type needs recompiling. Part of that work is reading the other files a
source pulls in with an `@@` include — a request to wherever those files live.

When a node type's host shut down, that watcher kept going. It was only stopped in the very last
step of the shutdown, and until then it still reacted to changes and still sent requests — from a
host that was on its way out, to hosts that were on their way out too. The shutdown then waited its
full two-second budget for answers that could never come, gave up, and logged a warning about an
include that "could not be established". In one test run that happened twenty-two times in a row,
and once the same late work ran on a thread with nothing to catch it and ended the process.

**The watcher now stops the moment its host begins shutting down** — whether the host itself is
being stopped or one of its parents is — and hands back any request it had in flight instead of
leaving the shutdown to wait for it. Shutdowns that used to pause for two seconds finish at once,
the warning is gone, and nothing runs after the host has started to leave.

One smaller thing came with it: anything else that wants to react to a host *beginning* to shut down
now has a signal to subscribe to, rather than a flag to keep checking.
