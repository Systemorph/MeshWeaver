---
Name: Store-module cleanup no longer races a landing
Category: Fix
Description: On a multi-replica deployment, one pod's boot-time cleanup of old module folders could delete a module another pod was landing at the same moment, leaving that module marked installed with no bytes behind it — silently absent until someone re-installed it. Cleanup now waits a few minutes before reclaiming anything unreferenced, closing the race.
Icon: Sparkle
Order: -20260826
---

# Store-module cleanup no longer races a landing

Installing or auto-updating a Store module writes its files first, then records that the module is
installed. Separately, every pod that boots cleans up old module folders nothing refers to any more.
On a deployment with several replicas, those two things could overlap: a cleanup pass on one pod
could read the installed-module list a moment before another pod finished writing its record, see
the just-written files as "nothing refers to this yet", and delete them — a heartbeat before the
other pod's record pointed at them.

The result was a module marked installed and switched on, with its files gone. No restart brought
it back, and nothing said why — the feature it provided (in the reported case, entity edit forms)
simply stopped working on that pod until someone re-installed the package.

## What changed

Cleanup now waits a few minutes before reclaiming a module folder nothing refers to, instead of
reclaiming it the moment it looks unused. A folder that is still unreferenced after that window is a
genuine leftover and is removed as before; one that was actually mid-install survives long enough for
its installation record to catch up.

## What you will notice

Store-installed modules keep working through a rolling restart or a concurrent auto-update — no more
silent, unrecoverable "installed but missing" state that only a re-install could fix.
