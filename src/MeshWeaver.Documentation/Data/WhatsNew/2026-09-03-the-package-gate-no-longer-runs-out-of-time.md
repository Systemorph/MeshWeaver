---
Name: The package gate no longer runs out of time
Category: Fix
Description: The check that installs every package and runs its tests took so long that it was being cut off part-way through — and a cut-off check looked exactly like one somebody had cancelled on purpose. It can now be spread across several machines at once.
Icon: Timer
Order: -20260903
---

# The package gate no longer runs out of time

Before a change to a package collection is published, one check does the heavy work: it installs
**every** package into a real, throwaway mesh, compiles each type in it, renders it, and runs its
tests. Nothing else proves that what will ship actually works.

It runs them one after another, and on a large collection — 59 packages — that had grown to
between 18 and 30 minutes. The check is allowed 30. So it was being **cut off part-way through**,
on one full run in five.

**A cut-off check does not report a failure.** It reports as *cancelled*, which is exactly what a
check somebody deliberately stopped reports. Neither the person reading the result nor the
automation could tell the two apart, so the check quietly stopped counting for anything: changes
were published with it still unfinished, and the record of which tests had run went with it.

Giving it more time was not the answer. There is a firm ceiling on how long any check may run, so
it would have bought a few minutes against a problem that keeps growing — and being cut off at the
larger number still reports the same unreadable *cancelled*.

**The check can now be spread across several machines at once.** Each one takes a share of the
packages, installs the ones its share depends on so they can be judged properly, and reports on its
own share only — every package still judged exactly once, by exactly one machine. On that
59-package collection this takes the whole check from about 22 minutes to about 11, well clear of
the limit, so a slow day no longer produces a verdict nobody can read.

Two things deliberately did not change. The result is still **one** check with the same name, so
nothing that depends on it has to be re-pointed — the machines' findings are folded back into a
single report before anyone reads it. And the fold refuses to report at all unless the shares add
up: if one machine's findings are missing, or two of them judged the same package, or a package
fell between the shares, it fails and says which. A faster check is only worth having if it still
cannot pass on nothing.

Collections keep the single-machine behaviour until they opt in, so nothing changes for a small one
where the check was never near the limit.
