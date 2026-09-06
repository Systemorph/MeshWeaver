---
Name: A local install keeps mirroring its mounted checkouts
Category: Fix
Description: A self-registry memex-local installed its mounted repositories once and then stopped tracking them; the packages an operator's install patterns select out of a local checkout now re-assert on every boot, at no cost when nothing changed.
Icon: ArrowSync
Order: -20260906
---

# A local install keeps mirroring its mounted checkouts

A self-registry `memex-local` mounts your checkouts and serves packages from them — but it
installed them once and then never looked again. The mounted repositories sat on the install
lane that seeds a fresh deployment, which by design never re-asserts what it delivered, and
neither mechanism that refreshes an installed package applies to a local install: one needs
GitHub webhooks it never receives, the other a registry it does not have. So the portal quietly
served a week-old course while every boot reported that nothing had changed.

A mounted working tree is standing intent, not a one-time seed. Packages selected out of a
local checkout are now reconciled on every boot: a changed file lands, a new node appears, and
an unchanged tree costs one listing and no writes. Fetched sources keep seeding once, exactly as
before, so a deployed portal whose admin removed a package is still not fought by the next
restart.
