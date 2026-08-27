---
Name: A deploy no longer cancels what you were doing
Category: Fix
Description: For a few seconds during every update, the platform briefly cannot tell which server holds which piece of your work. It used to report that momentary confusion as a permanent failure — dropping the request and tearing down the live page behind it — instead of simply asking again a moment later. It now waits for the move to finish.
Icon: Sparkle
Order: -20260827
---

# A deploy no longer cancels what you were doing

Updates are rolled out one server at a time, so for a few seconds there are two: the one being
retired and the one taking over. While that handover happens, the platform's map of which server
holds which piece of your work is briefly out of date — it is being rewritten. That window is normal,
it is short, and it ends by itself.

The problem was what happened to a message that arrived inside it. The underlying platform said, in
so many words, *"ask again in a moment"* — and we were not reading that. The message was treated as
having failed permanently: it was not sent again, and the thing waiting for it was told the failure
was final.

## Why that was worse than losing one message

Losing one message costs you a click. But a live page — an open document, a chat that is streaming, a
list that updates as data changes — keeps a standing connection, and that connection is built to
survive exactly this kind of hiccup: told *"the server is moving"*, it waits and reconnects; told
*"this failed"*, it gives up and closes. Because the handover was reported as a permanent failure,
pages that would have carried straight on through the update instead went quiet and stayed quiet
until reloaded.

There was also a rarer version with no visible error at all. When the reply that says *"this did not
work"* was itself sent during the same handover, it could go nowhere — leaving the request to sit
until it timed out roughly a minute later, with nothing on screen to explain the wait.

## What changed

The handover is now recognised for what it is. A message that arrives mid-move is simply sent again
once the move completes — using machinery that was already there and was only ever missing the cue to
run. If it still cannot be delivered afterwards, the answer now says *"the server is moving"* rather
than *"this failed"*, so a live page waits it out instead of tearing itself down. And a failure
notice that cannot take the direct route now takes the second one rather than vanishing, so nothing
is left waiting on an answer that was never coming.

Genuine failures are still reported as final and still arrive immediately — a real error is not made
slower or quieter by any of this.

## What you will notice

Nothing at all, most of the time, which is the point: pages that used to go blank or stop updating
during an update should now carry on through it, and the occasional minute-long pause with no
explanation should not happen.
