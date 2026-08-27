---
Name: Data views survive a failing data provider
Category: Fix
Description: A data source whose provider fails now reports the failure instead of taking the whole process down.
Icon: Sparkle
Order: -20260827
---

# Data views survive a failing data provider

A view backed by computed ("virtual") data could take the whole application down when the
computation behind it failed — for example when a lookup it needed timed out. The failure was
re-thrown on a background timer, where nothing could catch it, so instead of one view showing an
error the process ended abruptly and whatever it was doing produced no result at all.

That failure is now reported: the affected collection stops updating and says so in the log, and
everything else keeps running. A related cause is fixed too — the short-lived hub used to work out
a node type's data model no longer stalls for ten seconds when the type's own code looks up the
node it is running on.
