---
Name: The portal stops hoarding every build of its own code
Category: Fix
Description: Each platform update left behind a full set of compiled in-portal code and none was ever removed — 93 sets on the busiest portal, filling a disk shared with the keys that keep you signed in. The portal now tracks which sets are still in use and reports the rest.
Icon: Sparkle
Order: -20260813
---

# The portal stops hoarding every build of its own code

Code that lives inside the portal is compiled by the portal itself, and the result is kept on disk so
the next start does not have to compile it again. That store is keyed by which version of the
platform did the compiling, because compiled code from one platform version cannot safely be loaded
by another — the day that rule was missing, a routine update took the whole portal down.

So a platform update correctly produces a complete new set. What was missing was anything that ever
threw an old set away. On the busiest portal that had reached ninety-three sets — nearly eight
thousand files, three gigabytes — of which about one percent could still be loaded by the running
platform. The rest could never be loaded by anything again.

That disk is not private to the compiler. It also holds the keys that keep your sign-in valid, so
letting it fill would not have been a tidy, contained failure: it would have taken sign-in down at
the same moment, and the error you saw would have pointed somewhere else entirely.

The portal now keeps a running note of which platform version it is actually using, refreshed while
it runs, and every portal in a group can read every other's. A set is only ever a candidate for
removal if nothing anywhere still says it is in use — and, as a second and third safety net, only if
it is neither one of the most recent few nor recently written. Anything the compiler did not put
there is left strictly alone.

For now it measures and reports rather than removes: it writes down exactly what it would delete and
deletes nothing. Removing files is the one thing here that cannot be undone, so turning it on is a
deliberate decision taken against a real report rather than a default nobody has read.
