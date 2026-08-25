---
Name: Live lists stop missing a just-created node
Category: Fix
Description: A view watching a folder could permanently miss a node created a fraction of a second after it opened — the token counter reading zero, a notification never appearing — while opening the same thing fresh showed it immediately. The change notification was being dropped in a handoff; now it is never dropped.
Icon: List
Order: -20260826
---

# Live lists stop missing a just-created node

A view that watches a set of nodes — the chat token counter over a thread's usage, a notification
bell, a folder listing — could **miss a node created moments after the view opened, and never
recover**. Not show it late: never show it, for as long as that view stayed open, while opening the
same list again showed the node immediately.

The give-away was how strange it looked. The node was written, correct, and readable: ask for it by
path and it came back at once. Only the view that had been watching all along failed to see it.

## What was happening

Every live list works the same way: it takes a snapshot, then follows the change notifications that
follow. Until now it did that with **two** subscriptions to the change feed — one that buffered
notifications while the snapshot was still being taken, and a second that took over once it was
done, at which point the first was closed.

The change feed decides who a notification goes to *before* it delivers it, which it must, because
subscribers come and go while a notification is on its way. So a write published in the instant
before the second subscription existed was addressed only to the first — and by the time it arrived,
the handoff had happened and the first subscription had been told to stop buffering. It discarded
the notification. The second never received it, because it had not been on the list when the
notification set out.

Nothing asks again after that. The list re-reads only when a notification arrives, so a lost
notification means a lost row, permanently.

## What changed

A live list now keeps **one** subscription for its whole life. It buffers, then switches to
following — and the decision about which of the two a notification belongs to is made in the same
indivisible step that performs the switch. There is no longer an in-between for a notification to
fall into.

The same handoff existed in all four storage backends and was corrected in all of them.

## What you will notice

Nothing, most of the time — the window was small. What it removes is a rare, confusing class of
"the page is stale and reloading fixes it": a token counter stuck at zero after a reply that plainly
used tokens, a folder that does not show a file that is certainly there, a list that quietly stopped
keeping up. Token usage was always recorded correctly; only the reading of it could go missing.
