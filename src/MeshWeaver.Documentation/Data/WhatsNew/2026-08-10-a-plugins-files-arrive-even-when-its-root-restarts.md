---
Name: A freshly installed plugin is ready the moment the install finishes
Category: Fix
Description: An install could hand back a package whose root was still restarting, so the first thing to open it got nothing — a page that never rendered, or files that were never written.
Icon: CloudArrowUp
Order: -20260810
---

# A freshly installed plugin is ready the moment the install finishes

Installing a package could leave it in a state where the very next thing to touch
it came away empty-handed. Two ways you might have seen it: a package that ships
files — course videos, posters, images — installed without them, after a pause of
about a minute and a note that its binaries were not being served. Or a freshly
installed plugin's own page came up blank, showing none of the views the package
brought with it.

## What was happening

Installing a package changes what kind of thing its root is, and the portal
restarts that root so it picks up its new behaviour. The install did not wait for
that restart. It started it, then handed back.

So everything that came next — publishing the package's files, the first person to
open one of its pages — could arrive while the root was still on its way down.
Nothing was written, nothing rendered, and because the reply died with the
restarting root, whatever was waiting waited out a full minute before giving up.
Which side of that dead heat a machine landed on is why the same package could
install perfectly on one portal and arrive half-empty on another.

The restart was also happening too early to be useful. It ran before the package's
own code had finished building, so the root that came back could not yet see what
the package had brought — and, having come back once, it kept that empty view.

## What changed

The restart now happens once the package's code is ready, and the install waits for
the root to come back before it finishes. It asks the root a question that can only
be answered *after* the restart, so there is no window left to race.

An install therefore hands back a package that is actually ready: its files are
published, its pages render its own views, and the minute-long pause is gone.
