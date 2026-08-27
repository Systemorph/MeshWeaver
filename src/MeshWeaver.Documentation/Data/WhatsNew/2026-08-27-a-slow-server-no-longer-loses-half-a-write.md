---
Name: A slow server no longer loses half of a write
Category: Fix
Description: The fix that stopped finished answers sticking on "Generating response…" only worked while the server answered quickly — on a busy one it did nothing, which is exactly when it was needed. Writes to the same item now wait for the previous one's outcome before starting, so the last thing written is what you see however loaded the server is.
Icon: Sparkle
Order: -20260827
---

# A slow server no longer loses half of a write

Yesterday's fix for messages that finish still reading *"Generating response…"* was real, and it was
incomplete in a way that made it stop working under precisely the conditions that caused the problem:
a busy server. The symptom came back the next day.

It was never only about chat. Anything written in quick succession to the same item could lose part
of what it wrote — an edited comment that saves and then shows the old text, a tool result that
records everything except which sub-agent produced it. The write reports success either way, so there
is nothing to notice until you read the item back and find one field on the old value.

## Why it came back

Writes to one item go through a single queue so each builds on what the one before it produced. The
queue released the next write as soon as the *sender* was done waiting — and when the server is busy,
the sender stops waiting after two seconds and carries on optimistically, without ever hearing what
happened to the write. So on a busy server the next write started with no idea what the previous one
had done, went back to the item's last known state — one write out of date — and described its change
against that.

The owner of the item is right to reject a change described against a state it has already moved
past: that is what protects two people editing at once. Here there was only ever one writer, in
order, and the state it had "moved past" was that same writer's own previous write a moment earlier.
So the conflict was manufactured, the colliding field was dropped, the rest of the same write was
kept, and the whole thing was reported as a success.

## What changed

The queue now releases the next write when the previous one's outcome is actually known, not when the
sender gives up waiting for it. A confirmation that arrives late still counts — it is the same
confirmation, and on a loaded server it is the normal case. Nothing about how quickly your own action
completes has changed: the sender still returns after two seconds, so nothing on screen waits longer
than before.

When the server never answers at all, the next write falls back to the item's last known state, as it
always did — but that now says so in the log instead of passing silently.

## What you will notice

Less on a fast machine and more on a slow one, which is the point: an edit that saves shows the value
you typed, a finished answer shows its answer, and a busy server no longer quietly keeps the older of
two values you wrote.
