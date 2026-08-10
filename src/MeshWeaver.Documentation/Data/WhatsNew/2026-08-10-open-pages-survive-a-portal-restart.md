---
Name: Open pages survive a portal restart
Category: Fix
Description: A page you had open while the portal was updating could be told its data was permanently gone, when the portal was only restarting. It is now told the truth, and reconnects.
Icon: ArrowSync
Order: -20260810
---

# Open pages survive a portal restart

The portal updates itself, and every update restarts it. That restart is meant to
be something you barely notice: your open pages lose their connection for a
moment, wait, and pick up where they left off.

What actually happened was that some of them gave up instead.

As the portal shuts down it keeps working for a few hundred milliseconds — long
enough to still be handling live traffic while the machinery underneath it has
already started to leave. Anything it tried to deliver in that window failed, which
is expected and harmless. The problem was what it said about the failure. It
reported it as permanent: *this could not be delivered*, full stop, with nothing to
suggest trying again.

A page that is watching data for you reacts to that exactly as it reads. A
temporary interruption is something to sit out; a permanent failure is something to
stop watching. So a view told the second thing tore itself down, and stayed down
after the portal came back — until you reloaded it by hand. During a rolling update
this could happen to hundreds of open views at once, none of which had anything
wrong with them.

Now the portal recognises its own shutdown and says so. The message that goes back
is the one that already exists for exactly this situation — *temporarily
unavailable* — and every view that knows how to wait, waits, and reconnects when the
new instance is serving.

You do not need to do anything. The next time your portal updates, a page you left
open should still be showing live data afterwards.
