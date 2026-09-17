---
Name: A module no longer goes missing because two replicas wrote at once
Category: Fix
Description: An installed module could vanish from a portal replica for the whole life of that pod — no error a user could act on, just a feature that was not there — because the record saying which version to run was published in a way another replica could read half-written. Records are now published by a rename that cannot happen halfway.
Icon: ShieldCheckmark
Order: -20260917
---

# A module no longer goes missing because two replicas wrote at once

A portal runs several replicas that share one disk. When a module is installed, updated or
published, the replica doing the work writes a small record saying which version of that module the
deployment runs; every other replica reads those records when it starts.

A replica that cannot read one of a module's records **does not guess** — it leaves that module out
of the answer entirely, because half a record could mean running an uninstalled module, or an older
version than the one that was installed. That rule is right, and it is the reason the failure was so
quiet: the replica started normally, served normally, and simply did not have the module. The
feature it provides was missing on that one pod until it was restarted, and the only trace was a
line in a pod log.

## What was actually happening

Publishing a record is supposed to be one step: write the file under a temporary name, then rename it
into place, so the name readers watch appears holding the whole record or not at all.

The rename the platform used does not always rename. When it cannot — which happens on the shared
network disk the portals use, while several replicas are writing at once — it silently falls back to
**copying** the file into its final name: the name appears immediately, holds incomplete content
while the copy runs, and is locked against readers for its duration. A replica reading a module's
records in that window sees a record it cannot read, and drops the module.

Measured with the portal's own runtime on a file large enough to watch: during a single such publish,
**21,573** reads failed with *"the file is being used by another process"*, **52,619** read the file
incomplete where the lock is not visible (a reader on a different machine), and **not one** read
found the name missing. That is the shape of the bug — the record was never absent, it was present
and unreadable.

## What changes

Records — and every other file the platform publishes under a name other replicas watch, including
cached assemblies and container layers — are now published with a rename that **cannot** become a
copy. A publish either puts the complete file under its name, reports that another replica got there
first with identical content (which is normal and harmless), or fails loudly having written nothing
at all. There is no longer a state in which a reader can see a half-written record.

Nothing needs to be re-installed and no setting changes. A module that went missing this way was
already restored by the next restart; what is new is that the window that took it away is gone.

Background, with the measurements and the platform detail: [Publishing A File On A Shared
Volume](/Doc/Architecture/AtomicFilePublication).
