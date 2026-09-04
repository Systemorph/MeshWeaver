---
Name: An answer owed to you survives a shutdown
Category: Fix
Description: A save or an edit that was being answered exactly as a portal began shutting down could sit there for half a minute and then report the owner as unreachable — even though the answer had already been decided and sent. The reply was refused by the shutdown and thrown away, because the refusal was addressed to whoever sent it rather than to whoever was waiting for it. It now reaches you.
Icon: ArrowSyncCheckmark
Order: -20260904
---

# An answer owed to you survives a shutdown

Saving a page, renaming a node, editing a document — each of those asks whoever owns the thing to
apply the change and answer *yes, it landed* or *no, and here is why*. Almost always that answer
comes back in a few milliseconds and you never think about it.

If the portal happened to be shutting down at that exact moment — a deploy, a scale-down, a node
recycling — the answer could vanish. Not the change: the change had been applied and the answer
decided. The **answer** vanished. The screen then sat waiting for roughly half a minute and finally
reported that the owner could not be reached, for a save that had in fact succeeded.

## Why it happened

A part of the system that is shutting down turns away messages it can no longer carry, and it tells
whoever sent the message so — *"try again in a moment"*. That is exactly right for a **question**:
the sender is the one waiting, and asking again is something it can do.

It is meaningless for an **answer**. The sender of an answer is the side that already did the work.
It is not waiting for anything and it will never ask again — and the side that *is* waiting, yours,
was never told at all. So the reply was turned away, the note explaining it went to somebody with
no use for it, and your screen heard nothing.

## What changes

A reply that cannot be carried is now handed directly to whoever is waiting for it, inside the same
process, before it is discarded. The save completes, the error shows, the editor unblocks — in
milliseconds instead of half a minute, and with the real verdict instead of a misleading
"unreachable".

Nothing changes for a healthy portal: replies travel the way they always did. This only applies at
the moment the transport has genuinely given up.
