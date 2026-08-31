---
Name: A stalled package install now says what it was waiting for
Category: Fix
Description: Installing a package waits on several things, and each wait gives up after a while. Every one of them reported doing so except one, which gave up in complete silence — so a thirty-second stall left no trace and could not be explained afterwards.
Icon: Sparkle
Order: -20260831
---

# A stalled package install now says what it was waiting for

Installing a package involves several waits: for a component to warm up, for permissions to settle,
for a restarted component to come back. Each gives up after a while and carries on, which is the
right behaviour — an install should not hang forever because one step was slow.

Each of those waits also reports when it gives up, naming itself and what it had been waiting for.
That report is what makes a slow install explainable after the fact, which matters because these
stalls show up on a running system rather than on anyone's machine: pulling a full set of packages
has been observed taking half an hour when the actual work totalled well under a minute.

**One of the waits reported nothing at all.** It waits for a component that is restarting to finish
shutting down, and its own note says it is only ever reached when that shutdown has wedged — so
reaching it is the most interesting thing that can happen on that path, and it was the one thing
never written down. Each occurrence cost thirty seconds that appeared in no log, which is exactly
the kind of gap that makes a stall impossible to account for later.

It now says so, naming the component and how long it waited, while still carrying on as before. The
behaviour is unchanged; what changed is that the next stall can be explained instead of guessed at.
A check now keeps every one of these waits reporting, so none can go quiet again.
