---
Name: A write under a package root being recycled is answered
Category: Fix
Description: Installing a plugin package recycles the package's root, and an edit to a node under that root that was in flight at that moment could be left with no answer at all — the change had been applied, but the writer waited half a minute and then reported the write as failed. The write is now confirmed as soon as it has been applied, whatever the recycle is doing.
Icon: Checkmark
Order: -20260908
---

# A write under a package root being recycled is answered

Installing a plugin package ends by recycling the package's root, so that the root re-activates
against the package's own configuration. That recycle is by design and an install is supposed to
survive it. What it could not survive was an edit to a node **under** that root that happened to be
in flight at that moment.

Such an edit had already been applied: the node that owns it merged the change and raised its
version, and every view of the node was already showing the new state. What was still running was
the last step, writing the new state down to storage — and that step goes through the partition the
recycle is taking away. With nowhere to send it, the step simply stopped, part-way, on the thread
that had just applied the change. Nothing failed and nothing was logged, so nothing ever told the
writer what had happened: it waited out its confirmation window and reported the write as failed,
for a change that had in fact landed.

The confirmation deadline now exists from the moment the change is applied, rather than being set up
after the storage step has been started. The storage step still runs, and still keeps running when
it is merely slow — but it can no longer be the only thing that could answer. A writer is therefore
told that its change applied as soon as it has, whether storage is behind, or the partition it was
being written to is mid-recycle.

The effect is visible in the continuous-delivery bake gate, where about thirty writes per run met a
root recycle in exactly this way. Occasionally one of them was the write a package re-install was
waiting on, and the whole seal failed — reported as a timeout on a node whose content was already
correct.
