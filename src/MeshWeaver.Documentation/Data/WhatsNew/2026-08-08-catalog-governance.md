---
Name: Commercial plugins need a Global Admin, and orphaned install records can finally be removed
Category: What's New
Description: Free packages install and update with no special permission; commercial ones now require a Global Admin on every path, including the unattended one — and a package that left the registry no longer leaves an unremovable phantom record behind.
Icon: ShieldCheckmark
---

# Commercial plugins need a Global Admin, and orphaned install records can finally be removed

Two things about the plugin catalog were drawn in the wrong place. Both are fixed.

## Free syncs freely, commercial needs a Global Admin

A **free** package — no price, or a price of 0 — installs and auto-updates with no special
permission. That is what lets a fresh installation pick up the platform baseline without an
administrator in the loop.

A **commercial** package now requires **Global Admin** on the installing instance to be installed or
updated at all. Previously only the catalog *screen* was admin-only, while the machine paths — the
unattended install at startup and the auto-update that reacts to a plugin repo's green build —
applied priced packages with no permission check whatsoever.

The check now sits on the **action**, so every path is covered identically:

- installing from the catalog is authorized by the admin who clicked it;
- the install record remembers who authorized it, and an unattended update re-verifies that the same
  principal is *still* a Global Admin — revoking the admin stops the syncing;
- an update that cannot be authorized is not silently skipped: it raises a notification on the
  install record and says why, and the manual Update button remains.

A viewer who is not a Global Admin now sees "Requires Global Admin" on a commercial package's card
instead of a button that would refuse the click.

## An orphaned install record can be removed

When a package leaves the registry — most often because it was renamed and became a new product —
its install record used to be stuck in the mesh forever. The install-records partition denies writes
to every user identity by design (only the installer writes there, as the system), and the only
removal action lived on a catalog card that a departed package no longer has.

The catalog now lists those records under **Orphaned install records**, with a removal action for
Global Admins that runs the same system-identity path the installer uses. Removing the record does
not touch the content it installed, and the list only appears when the registry actually answered —
an unreachable registry never offers to remove everything.
