---
Name: Creating a partition asks its type once instead of three times
Category: Feature
Description: A top-level create of a NodeType declared in mesh content resolved "does this type own its partition?" three times over — six reads for one fact. The checks of one create now share a single answer, and the one that runs after the write still re-establishes it on its own.
Icon: TopSpeed
Order: -20260916
---

# Creating a partition asks its type once instead of three times

Creating a top-level instance of a type that declares `ownsPartition` — a new CRM client, a new
instance of any type a package brings — is judged by three checks before the row is written: who may
create a partition at all, whether a non-partition node is trying to land at the root, and whether
the backing schema has to be provisioned first.

Each of them asked the type's declaration independently. For a type registered in the platform that
is free — the answer is in process. For a type declared in **mesh content** it is two reads: one to
establish that the definition exists, one to read it authoritatively. Three checks, two reads each:
**six reads for one fact that cannot meaningfully change between them**. Those three now share one
resolution, so the same create costs two.

## What this changes, and what it deliberately does not

Sharing one answer is not purely an optimisation, so it is worth being explicit: three independent
resolutions could in principle *disagree*, and a disagreement failed the create closed. Sharing
removes that — on purpose. A create should be judged against one view of its type, and the window
those three checks spanned is the instant between two validators of the same chain.

The window that is actually wide is the storage write itself, and it is untouched. The check that
grants the creator ownership of the brand-new partition runs **after** the row lands, and it still
resolves the declaration on its own: a type that reads "not owning" there — or does not answer —
still fails the create, rather than handing out ownership on the strength of a fact nobody
re-established.

An answer that cannot be established stays an answer. It is remembered as "unestablished" rather
than resolved again, so every check of that create refuses it the same way and for the same stated
reason, instead of one check refusing and another quietly succeeding.

Full detail, including why a nested instance of such a type is still not refused and what it would
cost to close that: [Partition Ownership Resolution](/Doc/Architecture/PartitionOwnershipResolution).
