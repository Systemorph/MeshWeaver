---
Name: One change rebuilds a type once, not several times
Category: Fix
Description: A single edit or install could rebuild the same type several times over, interrupting anyone viewing a page built from it.
Icon: Sparkle
Order: -20260828
---

# One change rebuilds a type once, not several times

A single change — saving an edit, pushing, installing a package — could rebuild the same type
several times in a row. Each rebuild interrupted whatever was open: pages built from that type
reloaded, and the "a newer build is available" prompt appeared again and again. A slide deck being
presented could rebuild while it was on screen.

Repeat rebuilds that would produce exactly the same result are now recognised as such and skipped.
If the content really has changed in between, it still rebuilds — and asking for a rebuild
explicitly always does one.
