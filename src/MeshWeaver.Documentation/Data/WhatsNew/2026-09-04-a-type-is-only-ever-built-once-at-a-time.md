---
Name: A type is only ever built once at a time
Category: Fix
Description: Opening a page of a type that was compiling could quietly start a second build of the same type. Two builds finished seconds apart, each recorded where its own copy went, and whichever finished last won — so the page could open against bytes nothing pointed at any more and fall back to the generic view. A request to use a type now waits for the build that is already running instead of starting its own.
Icon: Bug
Order: -20260904
---

# A type is only ever built once at a time

A dynamic type — one whose behaviour is C# stored in the mesh — is compiled on demand, the first
time something needs it. Two things can ask at the same moment: the type's own hub, which starts a
build as soon as it notices there isn't one, and whatever needed the type right now — a page
opening, another pod probing, a package being installed.

Only one of them is supposed to run the compiler. The other is supposed to wait.

## What was going wrong

The waiter could miss the start of the build it was meant to wait for, decide nothing was running,
and compile the type itself. Both builds then finished — seconds apart, sometimes milliseconds —
and each wrote down where it had put its assembly. The second one to finish overwrote the first.

Most of the time that is merely wasteful: two compilations, two copies of the same assembly kept
on disk, two sets of change notifications sent to everything watching the type. But when the
record ended up naming the copy that the *other* build produced, anything opening an instance of
that type looked for bytes that were no longer addressed by anything, found nothing, and rendered
the fallback view — a page with none of the type's own areas on it, and nothing in the log to say
why.

There was a second, subtler consequence. A type reports `Ok` when its build finishes, and that is
the signal everything else waits on. With two builds running, the first `Ok` was not the end of the
story: a further write followed a few milliseconds later. Anything that reacted to that first `Ok`
by writing to the type — recording a decision, marking a version, stamping which framework build it
belongs to — had its write silently replaced by the tail of the second build.

## What changes

A request to use a type now waits for the build that is already in flight, and can no longer be
fooled by a snapshot of the type taken before that build started. One build runs; one assembly is
produced; one record is written; `Ok` means finished.

Nothing changes for a type that needs no build at all — one whose behaviour ships with the
platform, or one that already has a usable assembly. Those answer immediately, as before.
