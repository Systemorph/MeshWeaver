---
Name: A repository sync no longer deletes a type your records still use
Category: Fix
Description: When a repository retired a record type before the records of that type on your portal had been moved to its replacement, the next sync deleted the type anyway and left those records unreadable — and then held every update back for a "regression" that was really the retirement. The sync now keeps the type until the last record has moved, says so, and the update proceeds.
Icon: ShieldCheckmark
Order: -20260908
---

# A repository sync no longer deletes a type your records still use

A record on your portal — a mail, a proposal, a course — is of a *type*, and the type is what knows
how to show it. Types come from repositories, and a repository can retire one: replace it with
something better and stop shipping the old one. The intended way to do that is to move the existing
records to the replacement first, on every portal, and only then retire the type.

**That order is not always what arrives.** A portal that had not synced with the repository for a
while received the retirement before the records on it had been moved. The sync looked at the
type, saw that one record still used it, wrote a warning saying so — and deleted the type in the same
step. The record was still there, but nothing could show it: it opened as *Unavailable*, blank, with
no message naming what was missing. And because the portal's own start-up check then saw a type
that had "stopped working", it treated that as damage the new version had caused and refused to
finish the update — for a type that no longer existed, which nothing could ever repair.

**Now the sync keeps the type for as long as any record still uses it.** The type stays, the record
keeps opening, and the sync activity says exactly what it is waiting for:

> ⏸ Held Crm/Mail — the repository no longer carries this NodeType, but the mesh still has 1
> instance(s) of it (PartnerRe/Esl/DueDiligenceMail). Not pruned: retype or delete the instances and
> the next sync completes the retirement.

The same note appears on the repository's settings tab, so you can see at a glance that your portal
holds content the repository does not. Move the record to the replacement type, or delete it, and
the next sync finishes the retirement on its own — nothing to trigger, nothing to remember. A type
that no record uses is retired at once, as before.

The start-up check has learnt the difference too. A type its repository has retired — held for its
last records, or already gone — is listed in the portal's health page as *retired by its repository*,
not counted as a failure of the new version, and does not hold an update back.

Two things did not change: a type nobody uses is still removed when its repository drops it, and a
genuine compile failure on a type that still exists still stops a bad update from going live.

See [Retiring a NodeType](/Doc/Architecture/RetiringANodeType) for the retirement procedure and
[Dangling NodeTypes](/Doc/Architecture/DanglingNodeTypes) for the mechanism.
