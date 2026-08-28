---
Name: A restarted browser session or disconnected worker no longer costs the server a failed delivery per change
Category: Fix
Description: Owners now drop a subscriber the cluster reports as gone instead of pushing every change at it forever, and the routing log reports a known-dead address once per minute rather than per delivery.
Icon: Sparkle
Order: -20260828
---

# A restarted browser session or disconnected worker no longer costs the server a failed delivery per change

When a portal restarted, or a script worker connected over gRPC went away, every node those sessions
had open kept sending its changes to the address that was no longer there. The delivery was refused
each time, an error was logged each time, and because the refusal could not be delivered back to the
node that sent it, nothing ever told that node to stop — one production portal spent hours refusing
tens of thousands of deliveries a second, burning CPU and log budget that real work competes for.

The refusal now carries the cluster's verdict that the address is genuinely unserved, that verdict
reaches the sending node, and the node drops the dead subscription on the spot. A session that is
still alive is unaffected: it re-subscribes on its own, exactly as it does after the node it watches
is recycled. The routing log also stops paying per delivery — a known-dead address earns one full
error line per minute carrying a count of what it absorbed, so an incident stays visible without
burying everything else.
