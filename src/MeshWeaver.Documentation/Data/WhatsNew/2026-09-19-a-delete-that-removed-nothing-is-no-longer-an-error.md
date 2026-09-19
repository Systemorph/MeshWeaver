---
Name: A delete that removed nothing is no longer reported as an error
Category: Fix
Description: A recursive delete whose target had already been removed was logged at the same severity as one that deleted half a subtree and then failed. Every other branch of that classification already told those apart by whether anything had been removed; the not-found branch never did. It now does, and "unexpected" keeps its error whatever was removed.
Icon: Bug
Order: -20260919
---

# A delete that removed nothing is no longer reported as an error

When a delete fails, the thing worth shouting about is a **torn subtree** — nodes were removed, the
rest never will be, and the tree is now half gone. The classification in `DeleteNode` already said
so, branch by branch:

| failure | nodes already removed | reported at |
|---|---|---|
| permission denied | yes | Error — *"the subtree is left partially deleted"* |
| permission denied | no | Warning |
| cancelled | yes | Error — *"already deleted before the cancellation"* |
| cancelled | no | Debug — *"nothing is inconsistent"* |
| **not found** | **either** | **Error** |

The last row was the odd one out. A delete whose target was *already gone* mutated nothing, and the
commonest way to reach it is benign: a cascade racing a concurrent delete of the same satellites.
Reported as an error it became a ticket — and through a shared fingerprint it kept **reopening** a
different, already-fixed issue, which was closed on its own subject twice and reopened twice by
these lines.

A not-found that removed nothing is now a Warning: worth seeing, because a caller may be naming a
path that never existed, but not a fault. A not-found that *had* removed nodes keeps its Error,
because that is a torn subtree like any other. And `unexpected` keeps its Error whatever the
count — an unclassified failure is exactly when loud is correct.

The decision is a small function of those two facts rather than a line buried at the bottom of a
`catch`, so it can be tested on its own, and it is — in both directions, because demoting too much
is the worse mistake.
