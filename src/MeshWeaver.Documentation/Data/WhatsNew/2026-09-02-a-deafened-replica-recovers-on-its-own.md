---
Name: A deafened replica now recovers on its own
Category: Fix
Description: If a piece of the portal lost its cross-replica messaging during a long cluster reshuffle, it stayed lost until a restart. It now gets it back the next time the cluster changes.
Icon: ArrowSync
Order: -20260902
---

# A deafened replica now recovers on its own

This finishes the repair described in *One bad moment no longer deafens a hub for good*.

That change taught the platform to recognise a particular way the cluster says "not now, ask again",
so the retry it already had actually ran. The retry gives up after a few seconds — which covers the
ordinary reshuffle, and those are over almost immediately.

**What it did not cover was a long one.** If the cluster took longer than that to settle, the piece
gave up — and the giving up was final. It kept working perfectly for anyone whose request happened
to land on the same replica, and silently received nothing from any other, for as long as that
replica kept running. Only a restart brought it back.

That is a bad shape for an outage: everything looks healthy, because from the affected replica's own
point of view everything *is* healthy. It is simply no longer being spoken to.

**It now re-announces itself whenever the cluster's membership changes** — which is exactly the
moment the announcement can be lost, and exactly the moment it can be re-made. Not a timer and not a
retry loop: the same event that breaks it is the one that repairs it. A replica that misses its slot
during a fifteen-minute reshuffle gets it back when the reshuffle ends, instead of staying deaf until
someone notices and restarts it.

One deliberate limit worth stating, because it is what makes this safe: a piece that is **still
connected** is never re-announced. Announcing twice would mean receiving every message twice — a
quieter and considerably worse fault than the one being fixed — so the recovery only ever runs when
there is genuinely nothing connected.
