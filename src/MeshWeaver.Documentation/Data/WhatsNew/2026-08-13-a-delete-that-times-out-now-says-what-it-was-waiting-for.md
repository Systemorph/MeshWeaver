---
Name: A delete that times out now says what it was waiting for
Category: Fix
Description: When deleting a node runs out of time, the error now names the step it was stuck on, the child that never answered, and everything it had already removed.
Icon: Sparkle
Order: -20260813
---

# A delete that times out now says what it was waiting for

Deleting a node with a large subtree is a multi-step operation: the node is read, your permission
is checked, its validators run, the subtree is listed, every child is asked whether it may be
deleted, and only then is anything removed. Each of those steps has the same time budget — so when
a delete ran out of time, all six looked identical from the outside. The message said only that the
delete had timed out, which left no way to tell a slow database from a single child that had stopped
responding.

It now names the step. If the delete was waiting on the children, it also names the ones that never
answered — that step asks every child at once and waits for all of them, so one unresponsive child
stops the whole delete, and knowing which one is the entire diagnosis.

The count of what had already been deleted was worse than unhelpful: it always read zero after a
timeout, whatever had actually been removed, because the parts already deleted were discarded along
with the timed-out step. A delete that gets partway through now reports exactly which nodes it
removed, so "nothing happened, safe to retry" and "the subtree is half gone" are finally
distinguishable.
