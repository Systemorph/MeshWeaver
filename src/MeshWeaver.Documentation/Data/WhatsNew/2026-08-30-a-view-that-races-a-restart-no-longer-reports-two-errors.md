---
Name: A view that races a restart no longer fails twice
Category: Fix
Description: When part of the server was restarting while a page was still rendering, the page could log two errors and show neither its content nor a message. The restart is now recognised for what it is, and the page reconnects on its own.
Icon: ArrowSync
Order: -20260830
---

# A view that races a restart no longer fails twice

When a page was rendering at the very moment the part of the server serving it was being restarted
— a rolling deploy, or a node's worker being recycled — the permission check behind the page kept
asking the departing worker for its services, hit the closed door, and reported that as a rendering
error. It then tried to show you an error message through the same closed door and failed a second
time. You got neither the page nor the message, and two errors were filed for what was a routine
restart.

The permission check now takes everything it needs from the worker once, when it starts, so a
restart in the middle of a page can no longer trip it. And if a render does run into a worker that
has already gone, that is treated as the restart it is: nothing is reported as an error, no
message is attempted through the departed worker, and your page reconnects to the fresh worker by
itself — the same way it already does when a worker announces its own restart.
