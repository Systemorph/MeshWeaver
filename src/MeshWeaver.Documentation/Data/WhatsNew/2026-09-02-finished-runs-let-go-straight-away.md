---
Name: Finished runs let go straight away
Category: Fix
Description: A script, compile or build that finished kept part of the portal awake for another ten minutes. Three of them did — including the most common one of all. Now they release as soon as they end.
Icon: Sparkle
Order: -20260902
---

# Finished runs let go straight away

Every run the portal does for you — a script, a notebook cell, a compile, a build, a test — writes
its progress to an *activity*. While the run is going, the portal keeps a small live connection open
so the page showing you that progress updates as it happens. That connection is not free: it checks
in every 45 seconds, on purpose, to keep the machinery behind it from being cleaned up mid-run.

When the run ends, that connection should end with it. The end of a run is a fact, not a guess, so
there is nothing left to wait for.

For most runs it already worked that way. **Three kinds did not** — including, awkwardly, the most
common one in the whole platform: the ordinary log a script or a test writes as it goes. Those runs
finished, and their connection stayed open for up to **ten more minutes**, until a periodic sweep
noticed nobody was using it. One stale run is nothing. A busy portal doing this all day is carrying
ten minutes of finished work at all times, for no reason.

All three now release the moment the run reaches its end state.

There was a related problem at one of the three, and it is fixed in the same change: when a code run
failed because nothing was there to execute it, the step meant to mark it **failed** could quietly
do nothing at all, depending on the exact shape the data happened to arrive in. The run then sat at
*Running* for ever, showing a spinner for something that had already given up. It is now marked
failed reliably.

One detail worth stating, because it is what makes this safe: a finished run whose page **you are
still looking at** keeps its connection. The release asks first, and simply declines while anyone is
watching.
