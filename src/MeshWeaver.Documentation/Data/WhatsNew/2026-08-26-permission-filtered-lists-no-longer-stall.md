---
Name: Permission-filtered lists no longer stall
Category: Fix
Description: A list narrowed to what you are allowed to see could open and never load — no error, no spinner resolution, just permanently empty.
Icon: ShieldTask
Order: -20260826
---

# Permission-filtered lists no longer stall

Most live lists in the portal are filtered to what the person looking at them is allowed to read.
That filter runs over every update the list receives, keeping the entries you have access to and
dropping the rest.

Under a specific timing, the filter's work could be queued behind the very code that was waiting for
it. The list then never received that update at all: nothing was raised and nothing timed out, so the
view simply stayed as it was — empty, for a list that had not loaded yet — for as long as it stayed
open, while the same content opened another way appeared immediately. An empty result stalled the
same way a populated one did, so "you have access to nothing here" and "this never answered" looked
identical on screen.

The filter now always runs on the spot rather than being queued, so a permission-filtered list
always receives its updates — including the ones that legitimately come back empty.
