---
Name: A page whose node is restarting now waits for it
Category: Fix
Description: A read that lands while its node's hub is being recycled used to give up after three attempts spent in the same millisecond, then sit there until it timed out. It now waits for the restart to finish and shows the content the moment the node is back.
Icon: ArrowSync
Order: -20260902
---

# A page whose node is restarting now waits for it

Nodes restart. It is a normal, deliberate thing: when a type publishes a new build, every instance
that was bound to the old one is recycled so the next look at it is against the real build. The
restart takes a few tens of milliseconds and nobody is supposed to notice.

If your read landed inside that window, you noticed. The page — or the import, or the test — sat
there and did nothing for its full minute, then failed with "the operation has timed out". Not an
error you could act on. Just a wait, ending in the least informative sentence a system can produce.

The read was not waiting. It had **stopped trying**, ten seconds into the first second.

## Three chances that all fell inside one millisecond

A reader whose node is mid-restart is told exactly that: *"the address may reactivate — ask again."*
So it asks again, and it is allowed three attempts before it concludes the node is stuck in a
restart loop rather than merely restarting.

Three attempts is plenty — if they are spread across the restart. They were not. The reader waited
for the old instance to finish shutting down, and then re-asked; but "finished shutting down" is
reached *before* the address stops pointing at the old instance. So all three attempts went to the
same instance that had just refused them, in the same millisecond, and the fourth was never made.
Fifty-nine seconds later the read gave up.

The transcript said so plainly, once anyone looked: three refusals, one instance, one millisecond,
then nothing at all.

## What changed

The reader now counts the two things separately, because they are different questions:

- **"Is this node restarting over and over?"** — that is about *how many restarts* refuse us, and
  three is still the limit. A node that cannot settle is a real problem and the reader still stops.
- **"Is this one restart still going?"** — that is about *time*, and being refused twice by the same
  instance is not evidence of a loop. It is evidence the restart has not finished. The reader now
  rests briefly and asks again, for as long as one restart can plausibly take.

So a read that arrives mid-restart waits it out and shows the content the moment the node is back —
which is what everyone assumed it was doing all along.

And when it genuinely does give up, **it now says why**, naming the node and which of the two limits
it hit. "This node has refused sixteen attempts without ever coming back" sends you to the node.
"The operation has timed out" sent you nowhere.
