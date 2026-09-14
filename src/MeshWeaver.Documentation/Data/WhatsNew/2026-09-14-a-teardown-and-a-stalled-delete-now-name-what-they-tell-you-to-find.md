---
Name: A teardown report and a stalled delete now name what they tell you to look for
Category: Fix
Description: Two failure reports ended by sending the reader off to find a fact the reporting code was already holding — a hub discarding accepted work would not say which teardown discarded it, and a delete that stalled halfway through a subtree printed the field meant for outstanding work as "none". Both now name it, and so does the answer sent to the caller waiting on the other side.
Icon: Search
Order: -20260914
---

# A teardown report and a stalled delete now name what they tell you to look for

Two unrelated parts of the platform had the same habit: a failure report that ends with an
instruction — *find why this happened* — while the code writing it already knows the answer.

## "Find why this hub disposed"

When a hub is torn down while it is still starting up, any request it had accepted and parked is
thrown away, and the sender is told to ask again. That is by design. It is also reported, because
work the platform accepted and did not finish is worth a look.

The report named the message, the sender, and the gate the message was waiting behind. Then it ended:
*"find why this hub disposed before its deferred work could run."*

The hub knew. Every teardown already records who asked for it and why — a rebind, an operator
recycle, an owner going down and taking its children with it — and it was already printing that on
a *different* line, at a level the automatic issue-filing never reads. So the one report that
becomes a ticket was the one report without the answer. Over six days it was filed **364 times
across thirteen servers**, and not one copy said which of those three very different situations it
was.

It now says. So does the reply sent to whoever was waiting — often a different process entirely,
which can see none of this server's log at all. And when there was no request to attribute, it says
*that*, because "nobody asked over the wire; something disposed it directly" rules out a whole class
of cause and is an answer in its own right.

One related gap closed with it: the operator **Recycle** action was the single place in the platform
that tore a hub down without stating a reason, so everything it recycled reported "reason not stated
by the caller". It now says what it is and who asked.

## "3 paths removed" — and nothing about the rest

Deleting a folder removes its contents from the bottom up. If the store stops answering partway
through, the operation gives up rather than hang, and reports what happened.

That report carries a field for *what is still outstanding* — the earlier stage of the same
operation fills it in, listing exactly which items never answered. The final stage never did. So the
field printed `-`, which is also how the report renders **"nothing is outstanding"**. A delete that
was stuck on most of a subtree was reporting that it owed nothing, beside a count of the three items
it had already managed. A count of what succeeded identifies nothing about what failed.

It now lists the paths the delete still owes — the ones an operator actually has to go and look at —
and keeps the progress count beside them.

---

Neither change quiets anything. Every report is still filed, at the same level, with everything it
said before; they simply stop sending the reader to look for something that was in the room the
whole time.
