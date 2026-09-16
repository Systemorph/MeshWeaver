---
Name: A page whose data never loads says what it was waiting for
Category: Fix
Description: When a node's data could not finish loading within two minutes, the error guessed at the cause — "likely a stuck NodeType compile" — a cause that cannot produce it. It now names the data source and the exact piece of loading it was still waiting on, and every view bound to that node is told at once instead of each timing out separately.
Icon: Clock
Order: -20260916
---

# A page whose data never loads says what it was waiting for

Every node that carries data loads it when it starts. If that load has not finished after two
minutes, the node gives up and refuses further requests, so a page does not hang forever.

## What went wrong

The error it recorded ended with a guess:

> DataContext initialization did not complete within 120s — likely a stuck NodeType compile, or a
> data source that never initialised.

The first half of that guess cannot happen at this point — a type's compile is finished or reported
before the node's data starts loading — and neither half said which data source, or which part of
it, was stuck. Every stuck load produced the same sentence, so unrelated problems piled up in one
report for five weeks and none of them could be told apart.

It also told the wrong views. When the load failed, only one of the node's data streams was marked
as failed. Views bound to its other streams kept waiting and each reported its own timeout later, so
one stuck load showed up as several different-looking errors — in one case a learner's quiz, course
navigation and activity tracking all broken at once, each reporting a different cause. On some data
sources, reporting the failure even started new loads against the thing that had just failed to
answer.

## What happens now

The error names what it measured: which data source had not finished, which of its streams never
received data, and which part of the load was still outstanding. Every stream the node holds is
marked as failed at the same moment, so every bound view learns of it immediately, and reporting
the failure starts nothing new.

The two-minute limit itself is unchanged.

How the wait is structured, and what is still open, is written up in
[What the DataContext Init Time-Box Bounds](/Doc/Architecture/DataContextInitializationTimeout).
