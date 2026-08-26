---
Name: Pages stop showing the compile screen after the build has finished
Category: Fix
Description: A page could show the compile-progress screen indefinitely — long after its type had built successfully — and neither recycling it nor its Recompile button cleared it.
Icon: ArrowSync
Order: -20260826
---

# Pages stop showing the compile screen after the build has finished

While a page's type is being built, the page shows a progress screen instead of its content. That
screen could get stuck: on one deployment ten of twelve public package pages showed **⏳
Compiling…** to visitors for more than an hour after the build had finished, and the usual remedies
did not help — recycling the page, recycling the type, even restarting the portal left the same
screen in place.

Two independent reasons, both fixed.

**The page asked the wrong source.** Two parts of the platform decide about a build. The part that
notices a page is stuck reads the build's state from the database and was always right — it kept
correctly restarting the page. The part that decides what the restarted page actually *serves* read
a cached copy instead, and that copy had frozen on an old snapshot. So the page was restarted onto
the same stale answer within seconds, over and over, and settled into re-checking every ten minutes.
The decision now reads the same authoritative source the recovery does, so a page whose type has
finished building shows its content — and a build that really is running still shows the progress
screen, as before.

**The Recompile button did nothing.** The one action offered on that screen silently failed to
write, so a user who clicked it got no error and no effect. It now works, as does the automatic
recompile the platform attempts on a type it believes is stranded.
