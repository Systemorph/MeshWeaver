---
Name: The NodeType progress-area test waits for the area, not for a neighbouring stream
Category: Fix
Description: An intermittent that switched delivery off — the test asserted a snapshot of the Progress area's emissions before the area had emitted, trusting an ordering nothing guarantees. It now waits on the area's own first control.
Icon: Bug
Order: -20260830
---

# The NodeType progress-area test waits for the area, not for a neighbouring stream

`NodeTypeProgressAreaTest.ColdCycle_TriggersCompile_StreamsTransitions_LandsAssemblyOnDisk` failed
on a PR this morning and then on `main` itself, where a red required check turns delivery **off**
until the next green commit.

It waited for the NodeType to reach `Ok` on one stream, then asserted that a queue filled by a
*different* subscription — the Progress layout area — was non-empty. Its own comment claimed the
area "has emitted at least the terminal control by then". The area subscribes to the same state
through its own hop, and nothing orders the two, so the queue was sometimes still empty when the
snapshot was taken.

The test now waits on the area's control stream for its first non-null control — the same wait its
warm-cache path already used — and only then asserts. A wedged area now fails with a bounded wait
that names it, rather than an empty-queue assertion that reads as a race.
