---
Name: A stalled save now says which silence it waited on
Category: Fix
Description: When a save gives up waiting for a node's current state, the error now says whether the node's owner answered at all — the one fact that separates an unreachable owner from a node the owner cannot see.
Icon: Clock
Order: -20260914
---

# A stalled save now says which silence it waited on

A save to a node this part of the portal does not own begins by reading the node's current state
from its owner. When that read runs out its 30-second budget, the save is abandoned and reported —
and until now the report listed four possible causes and could not tell you which one it was.

There are really only two shapes:

- **Nothing came back at all.** No snapshot of the node ever arrived.
- **Answers came back, repeatedly, and none of them carried the node.** The connection to the
  owner is working and producing — so the question is whether the owner could read that node at all.

The message now says which, in one sentence, with the number of answers it saw:

> 4 change item(s) reached the mirror inside the bound and NONE of them carried the node: frames ARE
> arriving, so the subscription is established and producing, and the owner's view of this path is
> EMPTY.

The second shape is the one that narrows the search. The first deliberately says that it has *not*
decided anything further — an owner that is unreachable and one that answered and then had nothing
to send look the same from here, and a message that picked one would send the reader the wrong way.

Nothing about the wait itself changed — the same budget, no retry, no extra work on the normal path.
Only the report is different, and only when it is the wait itself that ran out: an error that came
from somewhere else still reads exactly as it did.
