---
nodeType: WhatsNew
Name: A ticket that moved repositories is no longer lost
Category: Fix
Description: "An issue transferred to another repository read as deleted: the platform followed the transfer, read the ticket, and then threw it away because its comments could not be read. Now the ticket survives, and it says so when its comments are missing."
Icon: ArrowSwap
Order: -20260918
---

An issue that has been **transferred** to another repository read as though it had been deleted.

GitHub follows a transfer for the issue itself but not for the things attached to it. Asking for the
issue answers normally — with its new number, in its new repository — while asking for its comments
answers "not found". The platform asked for both and treated the second answer as the verdict on the
first, so a ticket it had just read successfully was discarded. From the outside there was no way to
tell a moved ticket from a deleted one, and anything holding a reference to it could only conclude
that the ticket was gone.

That was not hypothetical: an incident record pointed at a ticket that had moved, and sat in a failed
state for over two weeks because every attempt to look the ticket up came back empty.

**Two things changed.**

A ticket that was read is now kept, even when its comments cannot be. The comment list on a ticket
carries an explicit "this is the whole list" marker, so an empty list no longer means two different
things — anything that actually needs the comments can tell the difference, and anything that only
needs the ticket's state gets the answer it already had.

And there is now a way to ask for a ticket's state directly, without asking for its comments at all.
It is one request instead of two, it follows a transfer, and it answers with the ticket's current
home — so a stored reference to a moved ticket can be pointed at where the ticket now lives, instead
of being abandoned and re-created.
