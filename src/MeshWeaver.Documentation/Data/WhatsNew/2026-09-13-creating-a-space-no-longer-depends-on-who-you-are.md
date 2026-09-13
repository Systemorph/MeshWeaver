---
Name: Creating a space no longer depends on who you are
Category: Fix
Description: The partition record a new space needs is now announced to the running mesh as platform infrastructure, so an ordinary member's space is set up exactly like an administrator's instead of intermittently coming up half-registered.
Icon: ShieldCheckmark
Order: -20260913
---

# Creating a space no longer depends on who you are

Creating a space does two things: it writes the space itself, and it registers the space's
partition with the running system so every part of the platform can route to it. That second step
was being checked against *your* permissions — and the partition register is administrator
territory, so for an ordinary member it was refused. Whether it was refused varied from one create
to the next, because the step happened to inherit a privileged context from the grant that makes
you the owner of your new space, and only when that context had not yet been torn down.

The result was a space that looked created and was not fully registered: adding the first page to
it could come back as "Create permission required", and deleting a space and re-creating it under
the same name could fail the same way.

Registering the partition is the platform's own bookkeeping — the record has already been written
by the time it happens — so it now says so explicitly instead of borrowing whoever asked. Creating
a space behaves identically for a member and for an administrator, and nothing about who may
create, read or change a space has changed.
