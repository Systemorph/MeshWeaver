---
Name: A poisoned compile process now names itself in the log CI keeps
Category: Fix
Description: When one Roslyn emit leaves a process unable to compile anything else, the line that says so — and tells you not to blame the change under test — reached only a log that records failed tests. It now reaches the complete one.
Icon: DocumentError
Order: -20260906
---

Sometimes a single Roslyn `Emit` throws and, from that moment, **every** later compile in the same
process fails identically. Parsing and binding keep working perfectly; only the emit is dead. One
such event surfaces as up to a dozen unrelated test failures, and each time it has cost a fresh —
and always identical — misdiagnosis of whatever change happened to be under test.

The compile pipeline already detects this and logs one loud line saying so: *"every later compile in
this process will fail the same way — attribute the failures that follow to this line, not to the
change under test"*. But it logged that line **without the exception object**, and the complete log
a CI run retains only records entries that carry one. So the loudest statement the pipeline produces
was also its least durable: absent from that log by construction, and absent from the per-test output
whenever the test that happened to be running had passed. Measured on two real occurrences: 25 copies
of the line in the per-test output, **zero** in the retained log, both times.

Passing the exception is the whole fix — the same thing the neighbouring call already did.

The self-diagnosis that runs at the first failed emit also grew a read it was missing. It checked
that a nested type's container reads back correctly, but never that a **top-level** type's container
reads back as *absent* — which is precisely the check the metadata writer makes before the call that
crashes. A process broken in that direction would have been reported healthy. It now reads both
directions, and says which one it read.
