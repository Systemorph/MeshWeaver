---
Name: Test compiles share the platform's reference set — two shards stop dying on a 6 GiB spike
Category: Fix
Description: Eleven test classes compiled with Roslyn against a reference set they rebuilt per call — about 520 assemblies each time. On CI that produced multi-gigabyte steps that tripped the test host's memory watchdog, whose message then blamed a leak its own trace refutes. The tests now share the process-wide reference set, and the watchdog reports what it measured.
Icon: Gauge
Order: -20260913
---

A test host on CI is killed by its own memory watchdog when its resident size crosses 6 GiB. On
2026-09-12 that happened to two shards of one pull request, in two different test assemblies, with
almost no managed heap in use — and the watchdog's message named the cause as a cumulative
container leak from the mesh test classes.

The trace it writes says something else. Every mesh class on the shard released what it took; the
growth was two steps, of 1.8 and 3.9 GB, each inside a window where no mesh class was running at
all. What was running were plain test classes compiling small assemblies with Roslyn — and each
compile built its reference list from scratch, `MetadataReference.CreateFromFile` over every one of
the ~520 assemblies the process can see, so every compile re-read the whole platform into memory
that only a finalizer gives back. Nineteen compiles in four seconds is 3.4 GB.

Those classes now share the reference set the platform's own NodeType compiler uses, built once
per process. Peak memory of the affected classes fell by two thirds and they run three to five
times faster. The watchdog's message no longer names a cause: it prints what it measured — now
including how much the garbage collector has committed, so a small managed number beside a large
resident one can be read for what it is — and points at the trace and the rule for reading it.

Read [Reading the Memory Watchdog](/Doc/Architecture/ReadingTheMemoryWatchdog).
