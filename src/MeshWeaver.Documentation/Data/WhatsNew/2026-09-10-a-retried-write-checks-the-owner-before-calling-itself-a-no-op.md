---
Name: A retried write checks the owner before calling itself a no-op
Category: Fix
Description: When a node's owner reports that an update never applied, the retry now asks the owner what it holds in every case where it would otherwise have posted nothing — closing the last route by which a write could be reported saved while no store held it.
Icon: CheckCircle
Order: -20260910
---

# A retried write checks the owner before calling itself a no-op

An update to a node held by another part of the mesh can be answered with "that never applied"
while the shutting-down owner has already told every reader about it. The retry then saw its own
value already present, decided there was nothing to write, and reported success — for a change that
reached no store.

That was closed for one form of "nothing to write". A second form remained: when the value was
rebuilt rather than reused, it compared as different and only turned out to be identical once
written out. Both forms now ask the owner what it actually holds. If the owner has the value the
success is real; if it does not, the retry writes it.

The [phantom-base investigation](/Doc/Architecture/PhantomBaseAfterOwnerDisposal) records the
mechanism, why a mirror can never answer the question on its own, and what a test must observe for a
lost write to be reported as one.
