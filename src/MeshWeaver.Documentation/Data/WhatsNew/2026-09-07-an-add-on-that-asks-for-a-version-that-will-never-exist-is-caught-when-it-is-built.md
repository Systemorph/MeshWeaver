---
Name: An add-on that asks for a version that will never exist is caught when it is built
Category: Fix
Description: An add-on can state the oldest platform it works on. Nothing checked that such a statement could ever be met, so an impossible one quietly held the add-on back for ever — and the message explaining the hold named two versions that looked like they were in the right order.
Icon: ShieldCheckmark
Order: -20260907
---

# An add-on that asks for a version that will never exist is caught when it is built

An add-on can state the oldest platform version it works on, and an installation holds it back until
it is running something at least that new. That is the right behaviour when the platform simply has
not caught up yet.

Nothing checked whether the stated version could ever be reached. If it named a version line that
was retired — or one that was never published at all — the hold was permanent, and looked exactly
like a hold that was about to clear. The message even read as though the two versions were in the
right order:

> the module requires platform 3.0.0-rc8 or newer but this deployment runs 3.0.0-ci.7989

They are not comparable the way they look. Version suffixes are ordered as text, so a build
numbered `ci.7989` counts as *older* than `rc8`, and always will, no matter how high the build
number goes. On 7 September that arithmetic was holding every pending update on both hosted portals:
forty-two add-ons, each waiting for a platform build that no longer exists.

The values themselves have been corrected. What changes here is that the next one cannot be written
silently: when an add-on is built, its stated minimum is now measured against the platform it is
actually being built against, and a requirement that platform cannot meet fails the build with the
add-on's name, the version it asked for and the version it got. An unreadable platform version fails
too — a check that cannot tell you the answer must not report that everything is fine.
