---
Name: The test tree holds zero hand-woven concurrency gates, and the allow file is gone
Category: Feature
Description: All 79 seeded ManualResetEventSlim sites across 23 test files are converted to reactive signals and bounded volatile releases, so `test` now sits in the ratchet's zero-tolerance roots with no allow file left to append a line to.
Icon: Sparkle
Order: -20260830
---

# The test tree holds zero hand-woven concurrency gates, and the allow file is gone

A hand-woven gate — `ManualResetEventSlim` and its relatives — parks a thread until somebody
signals it. In product code that thread is a hub action block or a grain turn, and the deadlock is
immediate. In a **test** the same primitive fails more quietly: when an assertion throws before the
release runs, the worker it deliberately blocked stays blocked for its full bound and bleeds into
the next test. That is not hypothetical — one such site held a pool thread for two minutes and made
an unrelated test fail.

The ratchet added a day earlier froze that debt at a measured **79 sites across 23 files**, every
one a `ManualResetEventSlim`, 32 of them in `IoPoolTest.cs`. All 79 are now converted, and
`test/HandWovenGateSites.allow` is deleted.

Two shapes replaced them, chosen by which way the signal travels:

- **Producer → test** (a handler reporting that it started, a pooled leaf, a `.Finally`, a
  disposal callback) becomes an `AsyncSubject<Unit>` the producer completes, awaited through the
  house assertion helpers — `await x.Should().Within(...).Emit(because)`, or `NotEmit(within)` for
  the negative case. That suspends the test instead of parking its thread, so it cannot self-deadlock
  and cannot strand anything.
- **Test → a worker it deliberately parks** — a wedged action block, a leaf that ignores its
  cancellation token, a merge turn held open so an ack provably cannot arrive — keeps the park,
  because the park *is* the subject, but drops the kernel handle: the worker polls a volatile flag
  under a bounded `SpinWait.SpinUntil`, and the release is written in a **`finally`** so a failing
  assertion can never leave it held.

No assertion was weakened and no timeout widened; several tests gained a `try`/`finally` they did
not have. `test` now sits in the guard's zero-tolerance roots alongside `src/`, `tools/`, `samples/`,
`clients/` and `memex/`, held at zero **with no allow file anywhere** — and a new check fails if the
deleted one ever comes back. The full account of the two shapes, the traps, and what the sweep
measured is in [Removing Hand-Woven Concurrency Gates](/Doc/Architecture/RemovingHandWovenGates).
