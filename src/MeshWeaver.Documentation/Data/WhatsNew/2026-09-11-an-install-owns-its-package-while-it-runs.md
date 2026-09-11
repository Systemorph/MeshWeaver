---
Name: An install owns its package while it runs
Category: Fix
Description: Installing a package no longer risks the package being restarted underneath it. A restart aimed at a package that is being installed now waits for the install to finish and then happens, instead of cutting the install off and leaving it to give up ten minutes later.
Icon: BoxCheckmark
Order: -20260911
---

Installing a package writes a lot of things into it, and each of those writes is waiting to be told
it landed. If the package itself is restarted while that is happening, the parts that owed those
confirmations disappear — so the confirmations never arrive, the install keeps waiting for an answer
nobody is left to give, and ten minutes later it reports a timeout against a package whose files had
all been written minutes earlier.

That happened six times, and it cost four publishes.

## What changed

**An install now owns its package for as long as it runs.** Anything that would restart that package
— an automatic rebind after the package's own type is rebuilt, an operations action, a reconcile —
**waits**, and happens the moment the install finishes.

Nothing is dropped and nothing is retried on a clock. The wait ends on the install ending, which is
a thing that always happens: an install that succeeds ends, an install that **fails** ends, and an
install whose caller gives up ends. All three release the package, which is what makes waiting safe
— a restart parked behind an install that crashed would be far worse than the original problem.

## What did NOT change

**An ordinary restart still happens immediately.** That is by far the common case — a package is
restarted after its type is rebuilt every time anything changes — and a fix that made every restart
wait would have quietly broken the thing restarts exist to do: pick up a package's real
configuration instead of the placeholder it started with.

The install's own, deliberate restart of the package it has just rebuilt is also unchanged. That one
is part of the install, it is ordered, and the install waits for the package to come back before
carrying on. What no longer happens is a *second*, unplanned restart landing in the middle —
including one landing on top of the fresh start the install was waiting for.

## What this does not do

It does not make an install immune to every kind of interruption. Individual types inside a package
can still be recycled while their package installs — deliberately, because the install is often
waiting for exactly those rebuilds. What is closed is the case the incident was about: nobody but
the install itself takes the package down while the install is writing into it.
