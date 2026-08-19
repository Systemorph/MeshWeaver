---
Name: A freshly installed package is readable the moment it appears
Category: Fix
Description: Landing on a new package's page right after it installed no longer showed "access denied" for the first few seconds — its public read is now published before its content.
Icon: LockOpen
Order: -20260818
---

# A freshly installed package is readable the moment it appears

For a short window after a package finished installing, opening one of its pages — including the
paywall landing that is meant to be public — could answer *"Access denied: you lack Read permission
on '{Package}'"*. Refreshing a few seconds later worked, which is what made this so easy to dismiss
as a hiccup.

It was not a permissions bug. The package became **reachable** as soon as its root landed, but the
grants that say *"everyone may read this"* were written at the very END of the install — after every
content node, every type and every compile. On a busy instance that gap was measured at **12 to 17
seconds**, during which the permission check was doing exactly the right thing with the information
it had: the grants were simply not there yet.

Publishing a partition's access is now a **phase of the install**, placed immediately after the
package's root and before its first content node. A package can no longer be observable before it is
readable — the window is gone rather than shortened.

Two things deliberately did not change:

- **Nothing was opened up.** A commercial package still installs gated, and a package that publishes
  only some of its pages still gates the rest. Those gates are now written *before* the pages they
  cover exist, so a protected page is protected from the moment it lands rather than a moment after.
- **A package that ships its own access policy still wins.** Its policy is written in the same phase
  and just ahead of the declared one, so the installer never overwrites it — not even briefly.

The install also now checks its own work: if the partition somehow ends up unreadable, that is
reported as an error naming the package instead of finishing quietly.
