---
Name: An update is never read while it is being written
Category: Fix
Description: Installations read the platform's prepared plugin content out of a folder that was being overwritten in place while they read it. Each update is now written into its own folder and only pointed at once it is complete, so a read can no longer catch an update half-finished.
Icon: Checkmark
Order: -20260914
---

# An update is never read while it is being written

When the platform prepares plugin content for an installation, it publishes a set of files —
several dozen of them — that every installation running that platform version then reads. Until
now, publishing an update meant **overwriting that set in place**: remove the "this set is
complete" marker, upload the new files over the old ones, put the marker back.

For the minute and a half that took, the folder held some old files and some new ones. Anything
reading it in that window read a mixture.

## What went wrong

Two different pipelines publish this particular set, and neither can see the other. Interleave them
and the folder ends up marked complete while holding files from **two** different preparations —
self-consistent to everything that reads it, and wrong. The first symptom is an installation that
renders nothing, with a mismatch reported deep in a log.

A byte-level check added earlier turns most of those into a loud refusal rather than a silent mix,
but it is a check performed *after* the fact. It cannot stop the overwrite; it can only notice it.

## What changes

**Each publication is now written into its own folder**, checked there, and only then pointed at.
A one-line pointer says which folder currently applies, and it is moved as the very last step —
after everything in the new folder is verified complete.

So a reader never looks inside a folder that is still being filled in. It follows the pointer,
which either names the previous complete set or the new complete set, and never anything in
between. Two pipelines publishing at the same time now write to two different folders, so there is
no longer a mixture to produce.

**Nothing has to be upgraded to benefit.** A copy in the old flat layout is still published beside
the new one, so an installation that predates this change reads exactly what it read before. If the
pointer is ever missing or unreadable, every reader falls back to that copy — the worst case is the
behaviour of the day before yesterday, not a failure.

## What this does not change

**Older folders are kept.** Nothing is removed until it has been superseded, is named by no
pointer, and is more than a month old.

**One copy still races.** The compatibility copy kept for installations that predate the pointer is
still written in place, so an overlap still costs that copy and is still reported. Retiring it is
the next step, once no installation needs it.
