---
Name: Two more tests measure the condition, not a stopwatch
Category: Fix
Description: A self-update test waited up to 30 seconds for a hold to appear and read its absence as a wrong verdict; a data-context test read "slow" as "never answered". Both now wait on the thing they are actually testing. One of them had already ejected a release from the merge queue.
Icon: Timer
Order: -20260830
---

# Two more tests measure the condition, not a stopwatch

A test bound is not an assertion. When a test waits *up to N seconds* for something to appear and
then reports its absence as a failure, it has quietly asserted two different things at once: that
the behaviour is wrong, and that the machine was fast enough. On a loaded runner only the second one
is ever false — and the failure it prints describes the first.

That is not hypothetical any more. Since the merge queue went live, every entry is built on a
runner under load *by construction*, and three separate releases have been ejected by three
different tests of this shape. Two of them are fixed here.

**The self-update availability gate** waited 30 seconds for a "held" marker to land on the policy
node. But the service that writes it is event-driven, so "the hold is not there yet" and "the first
check has not run yet" are indistinguishable from outside — and on a busy shard it is always the
second. The service now publishes each completed check, and the test waits for *the service to have
evaluated* before asserting on what it produced. No timer, no race.

**The data-context watchdog** proves that a hub whose initialisation threw answers requests with an
error rather than hanging — *and answers fast*. One 15-second bound was asserting both of those, and
they want opposite values: generous for "was it answered at all", tight for "was it answered
quickly". A runner that merely delayed the rejection produced a timeout, and the test reported
*"the faulted arm left the gate shut"* — the defect's own signature, for a defect that was not
there.

Simply widening the bound would have deleted the other half in silence: the test would have kept
passing if the answer took 55 seconds, which is exactly what its name says it guards. So the two
are separated. The wait is now generous, so a timeout genuinely means **never answered**; and the
elapsed time is asserted on its own, against a threshold taken from the mechanism rather than from
a feeling of "fast" — the hub's own request budget, which is what a request that *fell through* the
faulted arm would wait out. A slow answer and an absent one now fail with different messages,
because they are different defects.

Neither change weakens what is being tested. One replaces a bound with the condition itself; the
other splits a bound that was quietly standing in for two.
