---
Name: A node you only opened is no longer rewritten
Category: Fix
Description: Opening a node on a second replica re-saved the row it had just read, which could bump the node to a revision that was never stored and file a write-conflict record against an edit nobody made.
Icon: ShieldCheckmark
Order: -20260826
---

# A node you only opened is no longer rewritten

Every time a node became active on a replica, that replica read the stored row —
and then saved it straight back. Reading is not editing, so the write was never
supposed to happen; on a quiet node it was invisible, because re-saving an
unchanged row leaves it unchanged.

It stopped being invisible the moment somebody else was editing the same node at
the same time. The replica's echo of the row it had read is, by then, an *older*
revision than the one your colleague just saved. The store's write guard
correctly refused it — and refusing it made the refusing replica reconcile
itself one revision *above* the stored row, so a replica that had only ever
displayed the node ended up holding a version number the store never had, and
filed a "write conflict" record against an edit nobody made.

Now the row a node reads on activation is recorded as already-stored, so it is
never queued for saving. A real edit still saves exactly as before — it carries a
higher revision and is unaffected. What disappears is the redundant write on
every activation, the phantom revision it could produce, and the conflict record
that came with it.
