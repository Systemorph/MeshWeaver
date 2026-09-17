---
Name: The startup probe stops re-reading the module volume
Category: Fix
Description: A portal's own health endpoint had grown to 6.5-10 seconds per probe against a probe that waits five, because one check re-read the whole module volume on every single call. No new replica could ever finish starting; each was killed after three hours and began again. The platform now answers those questions once per change, exactly as its sibling already did.
Icon: Timer
Order: -20260917
---

A new portal replica is held out of rotation until its health endpoint answers once. That is
deliberate — it keeps the previous version serving while the new one finishes building its content.
It only works while the endpoint can answer inside the few seconds the probe waits.

## What went wrong

One of the checks on that endpoint re-read the **entire module volume on every probe**: the
activation record, every landed-module file beside it, and an existence question for each declared
module — asked again, from scratch, several times a minute, for the life of the process.

On a shared network volume that is not a small cost. Measured on a live portal across all three of
its replicas, the check alone took **6.5 to 10.1 seconds** per probe. The probe waits **five**.

A replica in that state can never finish starting, whatever its actual health. The rollout sat at
"one of two replicas updated" with no outage — the previous version kept serving — and no
explanation. After three hours the replica was stopped and a fresh one began the same thing again.

## Why it had already been fixed once

The same defect, on the volume's other reader, was found and fixed earlier: that reader now reads
the volume **once per change** rather than once per call, by checking three directory timestamps
first. Every writer lands its files by renaming into one of those three directories, so the check is
exact — it is not a cache that can go stale, and there is no timer and nothing to clear.

The required-modules check asked the same volume the same questions and was left doing all of them
per call. It now shares the same answer, rather than keeping a second one of its own that would be
free to disagree about the same files.

## What it deliberately does not do

It does not give the probe a bigger budget. A bigger budget moves the cliff rather than removing it,
and the number was still climbing while this was being written. And the cheap half — which modules
*this process* has actually loaded — is still read fresh on every probe, so a module that loaded a
moment ago stops being reported as waiting immediately.
