---
Name: A departing pod lets accepted messages land before it leaves
Category: Fix
Description: During a rolling deploy, a message the departing pod had already accepted for routing now lands — or is answered — before that pod stops, instead of being lost to a full reply timeout.
Icon: Sparkle
Order: -20260830
---

# A departing pod lets accepted messages land before it leaves

When a pod stopped — every rolling deploy does this — it could stop *over* messages it had already
accepted for delivery. The message had left the sender and was on its way; the pod then shut its
grains down, closed its transport, and disposed its service container while that delivery was still
running. The delivery could only fail at that point, and the failure notice it tried to send back
failed for the same reason, so the sender simply waited out its full reply budget and, in the worst
case, gave up on a subscription that would have come back on the surviving pod seconds later. Each
roll re-created the window.

The pod now holds its own shutdown, as the very first thing it does, until every delivery it has
accepted has either landed or been answered — while its hubs, its transport and its container are
all still alive. On a healthy pod that takes milliseconds. A delivery that genuinely will not
finish is reported by name after a bounded wait, and shutdown proceeds; it no longer hides behind a
timeout on the sender's side.
