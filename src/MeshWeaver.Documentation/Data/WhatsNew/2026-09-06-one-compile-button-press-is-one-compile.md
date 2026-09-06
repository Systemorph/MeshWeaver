---
Name: Pressing Compile while a build is already running no longer queues a second one
Category: Fix
Description: A NodeType that was already compiling for exactly the inputs you asked for used to queue your request behind it and compile again the moment the first finished — so one edit could cost several rebuilds, each one invalidating the page you were looking at.
Icon: ArrowSync
Order: -20260906
---

# Pressing Compile while a build is already running no longer queues a second one

When you press **Compile** on a NodeType — or when anything else asks for a build — the portal
checks whether a compile is already running for the *same* inputs. If it is, your request is
answered by that build rather than queued behind it, because the two would produce byte-for-byte the
same assembly.

That check has existed for a while, but it could only ever say *yes* for one kind of build: the one
started by the Compile button itself. Every other way a build starts — a NodeType compiling for the
first time, recovering after a restart, rebuilding after a platform update, retrying after you fixed
a compile error, healing a record whose assembly had gone missing — started without recording what
it was building. With nothing recorded, the portal could not tell whether the running build matched
your request, so it did the safe thing and queued yours: a second full compile, kicked off the
instant the first one finished.

The visible cost was a page that kept refreshing itself. Each rebuild invalidates the views backed
by that NodeType and raises a *newer build available* prompt, so a single edit could show up as
several rounds of both. It was measured in production as compiles arriving in pairs 65 ms apart, and
seven builds for one merge.

Now every kind of build records what it is building, so the check works for all of them.

## What has deliberately not changed

**If the inputs differ at all, your request still gets its own compile.** Sources edited since the
running build started, a different platform version, a different set of installed modules — any of
these and your request is queued rather than absorbed, because the build in flight would *not*
produce what you asked for. Answering your request with the wrong bytes would be far worse than
compiling twice.

**Compile (force) always compiles.** The explicit force option remains an escape hatch and is never
absorbed into a running build.
