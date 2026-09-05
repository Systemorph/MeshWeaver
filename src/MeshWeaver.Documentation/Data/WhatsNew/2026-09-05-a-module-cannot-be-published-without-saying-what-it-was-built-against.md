---
Name: A module cannot be published without saying what it was built against
Category: Fix
Description: The registry now refuses a module bundle that states no framework identity, instead of storing it and advertising an unknown. Publishing such a bundle answers a named error saying exactly what to change — and installations stop being told "already up to date" about a module they have never actually matched.
Icon: ShieldCheckmark
Order: -20260905
---

# A module cannot be published without saying what it was built against

A module bundle carries the identity of the framework build its bytes were compiled against. That
field is not a label: it is how an installation decides whether the module it already has is the one
being offered. A module's *version* describes its content, so rebuilding unchanged source against a
newer framework republishes under the **same** version — and then the framework identity is the only
thing that tells the rebuild apart from a no-op.

If a bundle arrives without one, the registry used to store it anyway and advertise the blank. Every
installation asking "do I need this?" then got the same answer forever: *already landed — the
identity could not be checked*. Not an error, not a retry; just a module that quietly never updated
again. And it could not be repaired from the receiving end, because the missing information was on
the serving side.

## What changes

Publishing a bundle that states no framework identity is now **refused**, with an error that names
the fault and the remedy rather than only the fault. Nothing is stored, so there is no blank left
behind to clean up later.

Anything that states one is unaffected. Several forms are legitimate — a commit, a build MVID — and
the check does not care which; it only refuses *absence*.

## Why it did not simply always do this

The producers had to be able to satisfy it first. A refusal armed while publishers were still
building bundles that could not state an identity would have stopped every publish in the fleet
instead of stopping the blanks — the opposite of the intended effect. So the check waited behind a
measurement, and was armed once every publisher had actually completed a publish stating its
identity.
