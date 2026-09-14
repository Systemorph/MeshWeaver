---
Name: A view whose data feed fails is given a new one, not the broken one back
Category: Fix
Description: When a live data feed gave up, the page that was told about it asked for a replacement and was handed the very feed that had just failed — so the failure became permanent instead of momentary. It now gets a fresh feed, and anything that declines to serve the broken one can still say what went wrong.
Icon: Checkmark
Order: -20260914
---

# A view whose data feed fails is given a new one, not the broken one back

Every live view in the portal reads its data through a feed that keeps it in step with whoever owns
that data. A feed can give up — the owner moved, a snapshot never arrived, a node was removed while
someone was looking at it. Giving up is deliberate and it is the useful outcome: the view is *told*,
and being told is what lets it ask for a fresh feed and carry on. Silence is the failure mode that
costs an afternoon; an error is the one you can recover from.

The recovery had a gap. A view that reacted to the failure the instant it was told — which is the
only moment it can react — asked for a replacement feed and was handed **the one that had just
failed**. The replacement was dead on arrival, so the view stayed empty, and asking again changed
nothing: a momentary failure had become a permanent one for that page, for as long as the portal was
running.

## What changes

**A feed that fails is out of service from the instant anyone can see it has failed.** Asking for it
again now produces a new feed that reconnects from scratch — which is what "the view is told so it
can recover" was always supposed to mean.

**And whatever declines to serve the broken feed can still say what went wrong.** The two used to be
separate facts published a moment apart, and whichever order they were published in, something read
them while they disagreed: either a view was handed a feed that was already dead, or it was told the
data had simply ended when in fact it had failed — and a view that thinks the data ended has no error
to show, so it renders nothing at all. They are now one fact, so neither reading is possible.

## What you will notice

Mostly nothing, which is the point — this is the behaviour that was intended. Where it shows:

- **A page whose data feed dropped recovers by itself** rather than staying blank until you reload.
- **A view whose underlying item is gone shows its "this is gone" card** instead of an empty space.

## For the record

This also removes an intermittent test failure that had been reading as flakiness. It was not
flakiness: the same defect, reached by a consumer that had to win a race of a few microseconds to
land in the window, which it did about once in fifty runs.
