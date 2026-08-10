---
Name: Health alerts stop crying wolf about ordinary traffic
Category: Fix
Description: An internal health check flagged normal message delivery as a fault, burying the genuine warnings in noise.
Icon: Sparkle
Order: -20260810
---

# Health alerts stop crying wolf about ordinary traffic

The portal watches itself for one particular kind of trouble: the component whose only job is to
pass messages between parts of the system being handed actual work instead, which can slow
everything down at once. When it spotted that, it raised an alert.

The check was reading the wrong end of each message. It looked at whichever component was handling
a message at that moment, rather than where the message was actually addressed — and since almost
every message passes through the message-passing component on its way somewhere else, ordinary
delivery looked exactly like the fault. A single trip through the portal could raise several
alerts, none of them real, and the alerts named the wrong component too.

The check now looks at where a message is going and where it came from, so it stays quiet while
messages are simply being carried, and still speaks up — naming the right component — when
something genuinely puts work on the carrier.
