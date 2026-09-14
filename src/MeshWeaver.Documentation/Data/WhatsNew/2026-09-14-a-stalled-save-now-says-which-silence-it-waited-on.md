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

There are really only two, and they are opposites:

- **The owner never said anything.** It was not reachable, or it never got a turn. The node's
  content and who may read it are then irrelevant — a node that does not exist, and one the reader
  is not allowed to see, both still produce an answer that carries nothing.
- **The owner answered, repeatedly, and its view of that node was empty.** Now it *is* about the
  node — whether the owner could read it at all.

The message now says which, in one sentence, with the number of answers it saw:

> The mirror produced 4 change item(s) inside the bound and NONE of them carried the node: the owner
> IS answering and its view of this path is EMPTY.

Nothing about the wait itself changed — the same budget, no retry, no extra work on the normal path.
Only the report is different, and only when it is the wait itself that ran out: an error that came
from somewhere else still reads exactly as it did.
