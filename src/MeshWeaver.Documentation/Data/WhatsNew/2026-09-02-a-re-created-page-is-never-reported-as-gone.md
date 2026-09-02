---
Name: A re-created page is never reported as gone
Category: Fix
Description: Fixed a fault where a page opened right after a node had been deleted and re-created at the same address could be told, definitively, that it no longer existed — and would stop trying.
Icon: Sparkle
Order: -20260902
---

# A re-created page is never reported as gone

Deleting a node and re-creating it at the same address is an everyday shape: a re-import, a repair
migration, a node moved away and back. When a node is deleted, the platform remembers that for a
short while so that anyone still asking for it gets a clear, final answer — *this address is gone
and will not come back* — instead of waiting on a node that will never reappear.

That final answer used to outlive the re-creation. The record of the delete was only cleared as a
side effect of the node's own machinery noticing the re-creation, which under load could happen
late — or not at all, because the delete had just shut that machinery down. A page opened in that
window, even after the re-creation was already visible everywhere else, could be told the node was
gone for good, and because that answer is meant to be final, it stopped asking.

The re-creation now retires the delete record in the same breath as it is stored, before anyone is
told about the new node. A page opened after a re-creation always sees the re-created node.
