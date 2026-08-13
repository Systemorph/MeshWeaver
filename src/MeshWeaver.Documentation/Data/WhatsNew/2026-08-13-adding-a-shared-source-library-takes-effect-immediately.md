---
Name: Adding a shared source library now takes effect without a restart
Category: Fix
Description: Pointing a type at code that lives somewhere else used to be ignored until the portal restarted, and the type would fail to build complaining about code that was plainly there.
Icon: Sparkle
Order: -20260813
---

# Adding a shared source library now takes effect without a restart

A type can borrow code from somewhere else instead of duplicating it — you point it at another
type's source folder and both build from the same files. On a portal that had already been running,
that instruction was quietly ignored.

The type kept building from the set of files it had when it first started up. The borrowed code was
never included, so the build failed complaining that a class "does not exist" — naming code sitting
right there in the mesh, which you could open and read. Nothing said the instruction had not been
picked up, and the same failure repeated on every retry. The only thing that helped was restarting
the portal, and the failure was invisible to every check beforehand: on a freshly started instance
the instruction works, so a change could pass every gate and only break where it mattered.

A type failing to build this way is not a private inconvenience — until it is fixed, every page
belonging to it shows the build-error notice instead of its content.

The source list is now re-read whenever it changes, so adding, removing or repointing a shared
library takes effect on the next build. If the borrowed code genuinely cannot be found, the error
now means what it says.
