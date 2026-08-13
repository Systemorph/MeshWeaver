---
Name: Compiled code is filed under the name it is looked up by
Category: Fix
Description: After compiling code that lives inside the portal, the portal sometimes wrote down the wrong location for the result. Later it looked there, found nothing, and quietly fell back to a blank configuration — so the page rendered empty and the pre-update readiness check called a perfectly good build missing.
Icon: Sparkle
Order: -20260813
---

# Compiled code is filed under the name it is looked up by

Code that lives inside the portal is compiled by the portal itself, and the result is filed away so
that everything which needs it later — opening a page, activating content, checking before an update
that everything still builds — can fetch the same result instead of compiling it again.

Filing it away means writing down two things: the compiled code, and a note saying where it was put.
The note has to match. If it does not, every later lookup goes to an empty shelf.

Two different parts of the portal write that note, and one of them was writing the wrong location. It
recorded where the content stood *at the moment it finished writing* rather than where the compiled
result had actually been filed — and those drift apart, because the act of recording the compile
moves the content on. Which of the two wrote last was decided by nothing more than timing, so the
same compile could produce a correct note or a broken one on different runs of the same code.

When the broken note won, nothing announced it. The compile had genuinely succeeded, and everything
you could see said so: the status read fine, the timestamp was fresh, the build was real and sitting
exactly where it had been put. Only the note pointed elsewhere. What you saw instead was a page for
that content rendering empty — the portal had looked for the compiled code, not found it, and fallen
back to a configuration that does nothing, without saying why. The check that runs before a platform
update was misled the same way: it asked whether each piece of in-portal code had a usable build,
followed the note, found the empty shelf, and reported a healthy build as missing.

Both writers now record the location the result was actually filed under. A note that cannot be
trusted to point at the thing it names is worse than no note at all, so this is also now pinned by a
test that reads the note and insists the shelf it names is not empty.
