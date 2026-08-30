---
Name: Hand-woven concurrency gates cannot come back
Category: Feature
Description: A SemaphoreSlim or ManualResetEventSlim parks a thread — on a hub action block that is a deadlock the mesh cannot recover from. Production is now held at zero outside the one sealed inside IoPool, and the test tree's remaining inventory can only shrink.
Icon: ShieldCheckmark
Order: -20260830
---

# Hand-woven concurrency gates cannot come back

A `SemaphoreSlim`, `ManualResetEventSlim`, `AutoResetEvent` or `CountdownEvent` parks a thread. On
this mesh that thread is usually a single-threaded action block or a grain turn, so the message the
gate is waiting for can never be processed — a deadlock nothing recovers from.

Serialization already belongs to the hub, and concurrency bounding already belongs to the I/O pool.
A new guard makes that enforceable rather than merely written down.

## Two tiers

- **Production is ZERO** — `src/`, `tools/`, `samples/`, `clients/`, `memex/`, with **no allow
  file**. The only permitted gates are the entries in a small **verified register**, re-checked on
  every run: an entry whose subject moved, or which no longer contains a gate, **fails** and tells
  the next author to delete it. It is not an allow list wearing a different hat.
- **The test tree may only shrink** — a seeded inventory of what exists today, frozen so the debt
  cannot grow while the sweep that clears it is written.

## Why the test tree counts too

In a test the same primitive fails more subtly: it strands a blocked worker when an assertion
throws before the release runs. Measured this week — a pool test put its release *after* an `await`
of the method-timeout token, so on timeout the release never ran and a blocked leaf held a pool
thread into the next test. The replacement is an observable the producer completes, released in a
`finally`.

## Proven by breaking it

The guard's self-test plants a real directory tree and runs the **real** scan over it, rather than
matching its pattern against strings — a guard whose self-test never calls its own scanner can lose
half its scope and stay green. Each arm was then verified by injecting a violation and watching it
go red: a new gate in `src/`, a new file in `test/`, and a listed file whose count grows.
