---
Name: Node writes no longer queue behind one another
Category: Fix
Description: Every node write in a mesh was executed by a single hub, one at a time, so a slow create or delete held every other one behind it — a bulk install or an import could stall ordinary edits for tens of seconds while the portal looked frozen. Node writes now proceed in parallel.
Icon: Flash
Order: -20260908
---

# Node writes no longer queue behind one another

Creating, deleting, moving or copying a node no longer waits for whatever other node write happens
to be in progress. Until now every node write in a mesh was executed by a single hub, one at a time,
and a slow write held every other one behind it — a bulk install or an import could stall ordinary
edits for tens of seconds, and the portal looked frozen while it did.

The cause was not in the node-write path. When a hub waits for a reply, the continuation used to run
on that hub's own message loop, inside the turn that delivered the reply — so a write that waited on
anything internally kept the loop busy for as long as the whole operation took, and nothing else
could be processed. Continuations now run off the message loop, so the loop stays free and writes
proceed in parallel.
