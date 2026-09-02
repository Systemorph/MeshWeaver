---
Name: A refused large message now tells you why instead of vanishing
Category: Fix
Description: Fixed error reports about very large messages being lost in transit, which left the sender waiting with no message and no explanation.
Icon: Sparkle
Order: -20260902
---

# A refused large message now tells you why instead of vanishing

When something goes wrong with a message — you lack permission for it, or nothing is
listening at the address you sent it to — the mesh sends you back an error report that
quotes the message it is about, so you can see which one failed.

For a very large message that quote was the problem. The report carried a full copy of
the message inside it, which made the report at least as large as the message — so a
report explaining that a 37 MB import could not be delivered was itself a 37 MB message,
and it failed to be delivered for exactly the same reason. Nothing was logged on your
side and nothing came back: the request simply sat there until it timed out, with no
message and no explanation of what had happened to it.

Reports about messages that are too big to carry now quote the message's size and its
type instead of its contents, so the report gets through. You see which message failed
and how far over the limit it was, which is what you need in order to act — usually by
importing or writing in smaller batches.

Nothing changes for ordinary errors. A report about a normal-sized message still quotes
it in full, exactly as before; the summary only replaces content that would have cost you
the report itself.
