---
Name: A save that said it worked during a restart really did
Category: Fix
Description: When the part of the platform that owns a page was shutting down at the exact moment you saved, the save could come back successful — with your text on screen — and yet be missing after the restart. The platform now asks the owner what it actually holds instead of believing its own copy.
Icon: Save
Order: -20260907
---

# A save that said it worked during a restart really did

Nodes are owned. When you edit one, your edit is sent to whichever part of the platform owns it,
that owner applies it, tells everyone watching, and then writes it down. Those last two steps are in
that order on purpose: everyone sees your change the instant it is made, rather than waiting for the
disk.

That leaves a very short window in which your change is on every screen and in no store yet. If the
owner is torn down inside that window — a recycle, a restart, a plugin being reinstalled — it says
so: *this write never landed, do it again*. The platform then does exactly that, automatically, and
you never notice.

Except it was doing it against its own copy of the node. And its own copy already showed your
change, because the owner had announced it before it went down. So the retry compared your text with
your text, concluded there was nothing left to do, and reported the save as complete. Nothing was
sent, nothing was written, and nothing was logged — the only way to find out was to come back later
and see the old text.

The retry now asks the owner what it actually holds. If the owner has your change, the save really
is complete and nothing further happens. If it does not, the change is recomputed and sent again, and
this time it lands. And if the owner cannot answer at all, the save fails and says so, instead of
quietly claiming to have worked.

Nothing else changed: an ordinary save takes exactly the path it always took, with no extra step.
