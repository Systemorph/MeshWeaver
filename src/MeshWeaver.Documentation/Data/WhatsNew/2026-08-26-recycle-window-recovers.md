---
Name: A page caught mid-recycle recovers instead of erroring
Category: Fix
Description: A request that arrived while its node was restarting was reported as a permanent failure; it is now recognised as the temporary state it is, and the view reloads by itself.
Icon: ArrowSync
Order: -20260826
---

# A page caught mid-recycle recovers by itself

Nodes restart routinely — you save an edit that recompiles a view, an administrator recycles a
space, a background release rolls a node forward. Restarting takes a moment, and any request that
arrives inside that moment cannot be served yet.

The platform has always known the difference between *"this address is coming back in a second"* and
*"this address is gone"*, and everything that reads live data is built to sit out the first and give
up only on the second. What went wrong is that the verdict was being lost in transit: the node
correctly said "I am restarting", and the layer that carried the answer back to the page rewrote it
as a permanent failure. The page then stopped waiting and showed an error for something that had
already fixed itself.

Now the original verdict travels intact. In practice: a view you open (or that is already open)
while its node is restarting waits the extra moment and renders, instead of showing an error you had
to reload past. Genuinely missing content is unaffected — that still reports as missing straight
away, with no retrying.
