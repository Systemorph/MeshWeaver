---
Name: An installed module built for another platform no longer displaces the one your portal ships
Category: Fix
Description: An installed copy of a module your portal also ships with took over on the strength of one fact — its files were on disk. When that copy had been built for a different platform it still loaded, and the views inside it no longer matched what the portal draws, so panels rendered as raw text like "StackControl { … }". The copy that came with your portal now wins, and the installed one is set aside with a line saying why.
Icon: Box
Order: -20260913
---

Some modules exist in two copies: the one your portal ships with, and one installed from a
registry. Until now the installed copy always won — on one fact, that its files were on disk. There
was a check, but it asks a different question: *does the code this module refers to exist here?* A
view pack built a fortnight earlier passes that check comfortably, because the types it refers to
are all still there. What has changed is what the portal **draws**, and the views inside that copy
were registered for the older shapes.

The result was a page that renders its own internals. On one installation a panel came out as
`StackControl { … }` — the raw text of the thing that should have been drawn — because the module
that was asked to draw it had no view for that shape any more. Nothing errored, no module reported
itself unhealthy, and the installed copy went on reporting itself installed and current.

## What changes

**The copy that came with your portal wins when the installed copy was built for a different
platform.** Every module records which platform build it was compiled against, and your portal
knows its own. When those two differ and your portal ships the same module, its own copy is the one
that runs — it was compiled with the platform it is running on, so it fits by construction.

**The set-aside copy says so, in the startup log, naming both builds.** A copy that is quietly
absent and a copy that was never installed look identical from the outside; this one does not. The
line names what the installed copy was built for, what your portal runs, and that no action is
needed.

**It fixes itself.** As soon as the registry serves that module built for the platform you are
running, your portal picks it up on its next check and the installed copy wins again — no
re-install, nothing to clean up.

## What this does not change

**A module your portal does not ship stays exactly as it is.** If the installed copy is the only
copy, it loads whatever it was built against — setting it aside would turn a module that works into
one that is missing, which is worse.

**A module that records no platform build is untouched.** Not knowing which platform a copy was
built for is not the same as knowing it was a different one, and it is never read as one.

**Installing a module built elsewhere is still normal.** Nothing is removed, nothing is refused,
and no module is ever left absent by this: the only thing that happens is a choice between two
copies you already have.
