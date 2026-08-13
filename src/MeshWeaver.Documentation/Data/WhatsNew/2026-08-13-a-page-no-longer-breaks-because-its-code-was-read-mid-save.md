---
Name: A page no longer breaks because its code was read while still being saved
Category: Fix
Description: A freshly built page could be picked up a moment before it had finished saving, which made it look permanently broken even though nothing was wrong with it. Builds now become visible only once they are complete.
Icon: Bug
Order: -20260813
---

# A page no longer breaks because its code was read while still being saved

When you edit the code behind a page, the portal builds it and saves the result so every part
of the portal can use it. The saved file was being created first and filled in immediately
afterwards — a gap of milliseconds, but a real one. Anything that picked the build up inside
that gap got half of it.

Half a build is not a build, so the portal reported it the only way it could: as a failure to
compile. That was the damaging part. A page that had actually built perfectly well was marked
broken, and because a compile failure is treated as a settled answer, it was never retried —
the page stayed broken until someone rebuilt it by hand, and the error message pointed at the
page's code, where there was nothing to find.

It was easiest to hit exactly where it hurt most: on a busy portal, and on installations that
run several copies of the portal sharing one location for builds, where one copy would read
what another was still writing. A page marked broken also holds up the portal reporting itself
as ready, so a single mis-read could slow a restart for everyone.

A build is now written aside and moved into place in one step, so it becomes visible only once
it is whole. There is no longer a moment in which a page can be read half-built.
