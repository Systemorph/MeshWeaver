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

**An install now owns its package for as long as it runs.** The restart that used to land in the
middle of one — the automatic rebind that follows the package's own type being rebuilt — now
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

It does not make an install immune to every kind of interruption, and it is worth being exact about
which ones remain:

- **Individual types inside a package can still be restarted while their package installs** —
  deliberately, because the install is usually waiting for exactly those rebuilds.
- **An operator restarting a package by hand still goes through a different route**, which does not
  yet consult this. That route is unchanged by this work.
- **Two packages installing into one shared area** can still interrupt each other. Making them wait
  for one another would let each wait for the other for ever, so the right answer there is to run
  them one at a time — a separate change.

What is closed is the case the incident was about: the package's own install is no longer
interrupted by the automatic restart that its own work triggers.
