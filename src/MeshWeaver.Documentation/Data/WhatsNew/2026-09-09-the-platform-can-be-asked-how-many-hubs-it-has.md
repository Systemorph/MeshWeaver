---
Name: The platform can be asked how many hubs it has
Category: Feature
Description: Until now the platform published no metrics of its own — not one. Answering "how many hubs are alive, of what kind, in what state" meant taking a heap dump of a live replica, which suspends it long enough to fail its liveness probe and get it restarted, destroying the state being measured. There is now a meter that answers it continuously and costs nothing.
Icon: DataTrending
Order: -20260909
---

Searching the whole platform for a metric found none:

```
grep -rn "new Meter(" src/                  →  0 results
grep -rln "System.Diagnostics.Metrics" src/ →  0 results
```

Every number any investigation had ever used was a .NET runtime built-in standing in for what was
actually wanted.

## Why that was worse than inconvenient

One investigation asked why roughly 6,900 synchronisation hubs were holding 2.7 GB of a 3.5 GB heap.
Nothing reported how many hubs were alive, how they split between kinds, or how many were still
running versus already dead. The only way to find out was a heap dump against a live replica — and
that suspends every thread for about 106 seconds, against a liveness budget of 90.

So the replica gets restarted, and the restart destroys the state being investigated. Worse, the
freeze scales with heap size: the population only exists on a replica fat enough that measuring it
guarantees losing it. A question that cannot be asked without destroying its own answer is not an
expensive question — it is an unanswerable one.

It also meant a fix could not be checked. One change released about 1,500 dead hubs, and its author
asked readers to *expect the curve to flatten, not vanish* — while nobody could see the curve at all.
A merged fix whose effect cannot be observed is indistinguishable from one that did nothing.

## What there is now

A meter, `MeshWeaver.Platform`, reporting the hubs alive in the mesh — tagged by the kind of address
and by run level. Collectors pick it up automatically.

Two design choices worth stating, because they are what make the number trustworthy:

- **It is measured, not counted.** A counter has to be incremented and decremented at every place a
  hub appears or disappears; miss one and the number drifts from reality while still looking like a
  measurement. This walks the live tree when the collector asks, so it cannot disagree with what is
  actually there.
- **The tag is the kind of address, never the address.** One series per hub would mean thousands of
  them — the very population under investigation — and the meter would become the second thing
  nobody can afford to collect.

It is deliberately a first version: hub counts and nothing else. Streams, queues and pending
callbacks each want their own owner and their own cost, and a meter that tries to answer everything
at once is how instrumentation becomes the thing nobody trusts.
