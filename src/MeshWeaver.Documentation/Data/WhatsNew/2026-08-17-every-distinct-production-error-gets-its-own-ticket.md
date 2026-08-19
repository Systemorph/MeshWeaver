---
Name: Every distinct production error gets its own ticket
Category: Fix
Description: The red-log watcher collapsed thousands of lines a minute onto one to three tickets — thirteen NodeTypes parked at CompileError were never reported at all. Distinct errors are now distinct incidents, and a window the watcher could not fully read says so instead of passing in silence.
Icon: Sparkle
Order: -20260817
---

# Every distinct production error gets its own ticket

The incident list is supposed to hold one entry per distinct error a portal reports. It was holding
one entry per *component*: the identity of an incident was built from where the fault was raised and
what type it was, and nothing else — so thirteen NodeTypes failing to compile, each with its own
compiler errors and its own fix, arrived at the same stack frame with the same exception type and
became a single ticket. None of the thirteen was reported.

An incident is now identified by the fault's own words as well: the exception's message, with
everything that varies between occurrences masked out — node paths, ids, counts, elapsed times, and
any name the message itself uses as a path. A thousand repeats of one error are still one entry with
a rising count; two genuinely different errors are two entries with names you can tell apart. When
one log source produces so many different shapes that the masking is clearly missing something, they
fold into a single entry that says how many it stands for, rather than flooding the list.

A window the watcher could not read completely now reports that as a finding of its own, as does a
stretch skipped after a long outage. Silence from the log store is no longer mistaken for quiet.
