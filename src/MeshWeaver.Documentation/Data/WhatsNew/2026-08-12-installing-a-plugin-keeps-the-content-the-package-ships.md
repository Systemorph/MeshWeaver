---
Name: Installing a plugin keeps the content the package ships
Category: Fix
Description: A package whose content type shares a name with another package's no longer installs stripped, no longer rewrites unchanged nodes on every sync, and no longer reads as another package's data.
Icon: Sparkle
Order: -20260812
---

# Installing a plugin keeps the content the package ships

Two packages are allowed to name a thing the same way — a reinsurance space and an accounting space
can both have a "Currency", and a customer catalogue that ships both had twelve such names. The
platform, though, kept one list of those names for the whole mesh, so the second package to load
quietly took the first one's place. Whichever had been compiled most recently became "the" Currency
for everybody.

The consequences were all silent. Installing a package could store its data under the other
package's shape: fields the other shape does not have were dropped, so a sample record could land
with its entire content missing, and fields it does have were filled in with the other package's
defaults. Every following sync then saw a difference that was never authored, rewrote nodes nobody
had touched, and recompiled types that had not changed — and because the winner depended on the
order things happened to compile, the same install produced a different result each time it ran.
Elsewhere in the portal a page could read a record as the wrong kind of thing and render empty.

Now an install writes exactly the content the package ships, and a name two packages claim is
treated as what it is — ambiguous. Content carrying such a name is read by the part of the mesh that
knows which package it belongs to, rather than guessed from the name, and the log names the
collision once so it can be resolved by renaming one of the two. Repeat syncs of unchanged content
now write nothing at all.
