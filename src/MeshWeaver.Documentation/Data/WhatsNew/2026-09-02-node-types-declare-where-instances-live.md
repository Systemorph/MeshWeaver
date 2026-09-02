---
Name: A node type can now say where its instances live
Category: Feature
Description: Package authors can declare on a node type which spaces its instances are stored in, so a search for that type asks only those spaces instead of every space on the portal — a step towards faster page loads on large installations.
Icon: Sparkle
Order: -20260902
---

# A node type can now say where its instances live

On a large portal, asking *"show me every item of this type"* has meant asking every single space
on the portal, one by one, even when the answer could only ever be in one or two of them. On our
cloud portal that is close to two hundred spaces per question, and a busy page asks many such
questions at once — which is a large part of why pages have felt slow at peak times.

A node type's definition can now carry a short declaration of **where its instances live** — for
example *"in the Admin menu"* or *"in the A, B or C spaces"*. The portal then asks only those spaces
for that type. The declaration is authored by whoever owns the type and ships with their package,
so nothing central has to be kept in step, and it is entirely safe to get wrong in the generous
direction: naming a space that holds nothing costs an empty answer, nothing more. A type that
declares nothing behaves exactly as before.

Two things are deliberately **not** possible. A declaration on any of the types that make up the
portal's permission system (roles, group memberships, access grants, access policies, and any type
that guards its own subtree) is refused when it is authored, with the reason spelled out: for those
types a shorter answer would mean a permission quietly disappearing, or a revocation quietly not
applying. And this release does not yet make the notification bell, conversations or mail faster —
those live in every user's own space by design, and they get their own, different fix.
