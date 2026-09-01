---
Name: An oversized message no longer takes a whole connection down with it
Category: Fix
Description: A single message too large for the cluster transport used to destroy the connection carrying it — dropping unrelated traffic and retrying forever, silently. It is now refused at the sender, with a message that names what was too big.
Icon: Sparkle
Order: -20260901
---

# An oversized message no longer takes a whole connection down with it

Very large operations — importing a big course, syncing a repository full of rich pages —
could produce a single internal message bigger than the cluster is able to carry. Until now
that message was sent anyway. It was refused at the very last moment, deep inside the
connection that was writing it, and the whole connection was torn down as a result.

That is far worse than losing one message. **Every other message queued on that connection at
that moment was lost too** — updates and page loads that had nothing to do with the large
operation. The system then reconnected and sent the same oversized message again, and again,
because nothing about retrying makes a message smaller. From the outside it looked like a
space that had simply stopped responding, with no error pointing at the cause.

Now the message is measured before it is sent, against the limit the transport actually
enforces on that deployment. If it cannot be carried, it is refused immediately and the
sender is told at once, so an operation fails fast and visibly instead of hanging. The refusal
names the destination, the size, the limit, who sent it and what kind of message it was —
enough to find and fix the thing that produced it.

Nothing that works today is affected: the limit being checked is the transport's own, so only
messages that were already being thrown away are refused — the difference is that they are now
refused loudly, and without collateral damage.
