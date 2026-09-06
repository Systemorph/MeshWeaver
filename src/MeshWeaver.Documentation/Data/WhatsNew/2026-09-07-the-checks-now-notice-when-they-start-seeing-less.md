---
Name: The checks now notice when they start seeing less
Category: Feature
Description: Every cross-repo surface check reads one index of the platform's public API. If that index quietly shrinks, the checks stay green — because they are looking at a smaller world, not because the change was safe. They now measure that directly.
Icon: Ruler
Order: -20260907
---

# The checks now notice when they start seeing less

A gate can be green for two reasons that look exactly alike from outside: *the change was safe*, or
*the thing that would have failed was never in the list.*

The platform's cross-repo checks — the ones that stop a change removing a public type here from
breaking a repository that uses it — all ask their question of one index: every public type and
public member declared under `src/`. The index is built by reading the source files. So the checks
are only ever as good as that reading, and a reading can go wrong in a way that produces no error,
no warning and a perfectly plausible number.

## What happened

Three invisible bytes at the start of a file — a byte-order mark, which many editors add without
being asked — put the very first character out of reach of a pattern anchored to the start of a
line. 310 of the platform's 1 271 compiled source files carry one.

The effect was not that the index failed. It was that the index held **1 955 types where the truth
was 1 971**: sixteen public types were in it nowhere at all, and a hundred and twenty-four more were
filed under the wrong name. The checks read that number, found it healthy, and answered
confidently.

Nothing slipped through — that was measured rather than hoped for, two different ways, across every
one of the 754 changes made while the gap was open. But it was a reprieve, not a defence: the checks
had simply not yet been asked the question they would have got wrong.

## What changed

The reading now has to prove it did not get worse.

Whenever a change touches the code that builds the index, the platform runs **the previous version
of that code and the new version over the identical set of files**, and compares what each one
found. Because both are reading exactly the same files, any difference is the reading — not the
codebase.

- Seeing **fewer** things than before stops the change, and says which counter dropped and by how
  much. If the old number really was too high, that gets written down as a sentence in the change
  itself, per counter, with a reason.
- Seeing **more** is a fix, and passes. The repair that started all this measures **+16 types and
  +111 members** over an identical set of files — which is the entire point of it.
- Seeing the same is printed with both numbers, so a pass says something instead of nothing.

There was already a check that the index is not *empty*. That one catches "nothing was read". This
one catches "less was read than last time", which is the failure that actually happened, and which
no threshold on a single number can see: the gap was under one percent of the total.
