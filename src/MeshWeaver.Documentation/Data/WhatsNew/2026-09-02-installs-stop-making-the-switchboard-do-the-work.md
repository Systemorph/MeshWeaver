---
Name: Installs stop making the switchboard do the work
Category: Fix
Description: While installing a package the portal was routing two of its own messages through the component whose only job is to route everyone else's. Under a large install that is how other traffic gets held up.
Icon: ArrowRouting
Order: -20260902
---

# Installs stop making the switchboard do the work

Inside the portal there is one component whose entire job is to pass messages between all the others.
It is a switchboard: it should be forwarding, never doing. When it does its own work, everything
waiting to be forwarded waits behind it — and the busier the moment, the worse that is.

Installing a package is exactly such a moment: dozens of packages arriving at once, each one
recycling its root and then waiting for it to come back up. Two of the messages in that sequence —
the one asking a root to stand down, and the check asking whether it has come back — were being sent
**from the switchboard itself**. So during the largest bursts of traffic the portal ever handles, the
part that must stay free to forward was itself an endpoint.

Nothing failed because of it, which is why it lasted: the messages arrived, the installs completed.
What it cost was headroom, at the exact moment there was least of it.

Both now go out from the dedicated hub that already handles this kind of work — the same one the rest
of the install path has used for a while. The switchboard goes back to forwarding.

The portal has been reporting this to itself the whole time; the lines are how it was found, in a
plugin build's own log. That report stays, and stays loud, because the next time something starts
doing work in the wrong place it is the only thing that will say so.
