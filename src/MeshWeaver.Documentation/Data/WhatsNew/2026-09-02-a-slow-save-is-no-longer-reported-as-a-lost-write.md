---
Name: A slow save is no longer reported as a lost write
Category: Fix
Description: When storage was busy, a change that had already been applied could come back as "did not apply" — and whatever asked for it redid the work over the top of it. The owner now confirms the change it applied and finishes writing it out in the background.
Icon: ArrowSyncCheckmark
Order: -20260902
---

# A slow save is no longer reported as a lost write

Every change to a node is applied by the node's owner and then written out to storage. The owner
waits for that write before it confirms the change, so that "saved" has always meant "durable" as well
as "applied". Under heavy load — a bulk install writing hundreds of nodes at once — the write to
storage could queue for longer than the owner was prepared to wait, and at that point the owner gave
up on the write and answered with a *failure*: the change did not apply.

It had applied. The node already carried it, every reader already saw it. What the owner reported was
the fate of the storage write, phrased as the fate of the change — and whatever had asked for the
change believed the report. On the repository check that installs a sibling repository's packages,
the thing asking was the step that adopts a prebuilt build instead of compiling; told it had failed,
the check compiled the type over the build it had just adopted, and reported the adoption as declined
while an identical check minutes later adopted the same build without incident.

Now the owner tells the two apart. Once a change is applied, it is confirmed as applied — a slow
storage write is finished in the background and noted in the owner's log as slow, not converted into
a failure. A storage write that genuinely fails still surfaces as one.
