---
Name: A struggling server no longer drags its neighbours down with it
Category: Fix
Description: When one server ran short of memory it was taken out of service a full minute before it was restarted, and its traffic moved to the servers closest to running short themselves. One slow server could therefore make the whole portal slow. The two health signals now ask different questions, so a struggling server is repaired instead of being emptied onto its neighbours.
Icon: TopSpeed
Order: -20260905
---

# A struggling server no longer drags its neighbours down with it

The portal runs on several servers at once. Two automatic health signals watch each of them, and
they exist to trigger two different remedies:

- *Can this server take a request?* — if not, send its visitors to the others.
- *Is this server still making progress?* — if not, restart it.

Those are opposite responses, and which one is right depends entirely on the others. Restarting one
struggling server takes load off the system. Emptying it adds load — to whichever server is next in
line.

Both signals were reading the same measurement, so they could not answer differently. When the
platform learned to notice a server labouring under memory pressure, both signals noticed it, and
the *empty it* response fired first — a full minute before the restart that would actually have
fixed it. For that minute the struggling server's visitors were sent to its neighbours, which on a
portal that has been running for a day are all under similar pressure, all at once. One slow server
became several.

Nobody saw this as an error message. It looked like the portal getting gradually slower, and then
recovering.

The two signals are now separate measurements. A server short of memory keeps serving while it is
restarted, and its neighbours are left alone. Nothing else changed: the same memory pressure is
detected, at the same threshold, and it still ends in a restart — just without emptying a server
onto the ones least able to take it.
