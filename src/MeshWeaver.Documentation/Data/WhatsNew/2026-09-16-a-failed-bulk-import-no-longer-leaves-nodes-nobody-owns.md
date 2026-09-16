---
Name: A failed bulk create no longer leaves nodes nobody owns
Category: Fix
Description: When a batch of nodes was created at once and a required follow-up step failed part way through — the step that makes a new Space owned by its creator, for instance — every node of that batch stayed behind. The ones past the failure had never had their follow-up run at all, yet looked exactly like ones that worked. A batch that cannot finish now takes back what it could not finish, and keeps what it did.
Icon: ArrowClockwise
Order: -20260916
---

# A failed bulk create no longer leaves nodes nobody owns

Some things the platform creates need a second step before they are really there. A new Space needs
its creator to be granted ownership of it; a new partition needs to be announced to routing. If that
step does not happen, what you are left with is not a half-made node — it is a node **nobody can
write to, delete, or create again**, because the grant that would have let you is the very thing that
failed.

Creating one node at a time has handled this since last year: if the required step fails, the node is
taken back out and you are told why, so you can simply try again.

**Creating many nodes at once did not.** Imports, installs and package copies send nodes to the mesh
in batches. All of them are written first, and only then does each one's follow-up step run, in
order. So when one failed, the batch left two different kinds of debris behind:

- the node whose step failed — the case the single-node path already handled; and
- **every node after it in the batch, whose step never ran at all.**

The second kind is the one that mattered. Those nodes are indistinguishable from ones that worked.
Nothing about them says the grant was never made or the partition was never announced — they simply
sit there looking finished, until somebody tries to use one.

## What changes

A batch that cannot finish now **takes back exactly what it could not finish**, and keeps the rest:

- nodes created **before** the failure, whose follow-up completed, are kept;
- the node that failed, and every node after it, are removed — youngest first, so a child never
  outlives its parent;
- the answer names the node that failed, quotes the original error, says what was rolled back, and
  lists what was kept.

So a failed import or install is something you can simply run again, instead of something that
leaves a trail of nodes that look fine and are not.

**It will not remove anything that is not its own.** Before removing a node the platform re-reads it
and checks it is still the one this request created. If somebody has re-created that path in the
meantime, it is left alone and said so, in words, rather than deleted.

And a node whose path already existed before the batch — the ones a batch reports as "already
there" and never overwrites — is never touched by any of this.

Background, with the measurements: [A Bulk Create Compensates Per
Node](/Doc/Architecture/BulkCreateCompensation).
