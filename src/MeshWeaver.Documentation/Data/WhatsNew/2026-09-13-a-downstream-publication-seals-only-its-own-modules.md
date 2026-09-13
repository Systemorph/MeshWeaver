---
Name: A downstream publication seals only its own modules
Category: Fix
Description: A node repository that composes its upstream's modules for its bake no longer re-seals copies of them into its own publication — one identity now holds one copy of each module, so a portal can no longer be handed two builds of one assembly by two publications of the same identity.
Icon: Layers
Order: -20260913
---

# A downstream publication seals only its own modules

Every satellite bakes its NodeTypes against modules it does not own — `AI`, `Essentials`, `Maps`,
`Stripe` come from the Plugins publication sealed for the same framework identity. Until now the
bake also **re-sealed copies** of those four into the satellite's own publication, so an identity
held five publications each carrying its own copy of `MeshWeaver.AI`. When the upstream resealed the
identity with a newer build, the copies diverged, and a portal reading the identity found two builds
of one assembly — the state `Modules:VersionStrictness` readers answer *"sealed set inconsistent —
nothing rolls"* for, and the reason a page rendered empty on one replica and fine on the next
(MeshWeaver#3732, measured on memex.systemorph.com on 2026-09-08 and again on 2026-09-11).

A publication now seals **only the modules its repository owns**. Upstream modules are still composed
into the bake's compile surface and still checked for one-build-per-name; they are just not
re-published. A satellite that owns no module seals an empty module index — which a reader tells
apart from a publication that predates module sealing — and every consumer composes each package
from the first upstream whose seal lists it, exactly as before.

What this does not change: a portal replica that has not loaded a module it already holds
(`pending_module_activation`, `content-types` on `/health`) is a portal-side reading and is not
addressed here.
