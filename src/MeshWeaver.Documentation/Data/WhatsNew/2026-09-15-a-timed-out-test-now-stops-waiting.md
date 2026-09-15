---
Name: A timed-out test now stops waiting
Category: Feature
Description: The test framework moved to xunit.v3 4.0.1, and every test that declares a Timeout now hands the test's cancellation token to the wait it is parked in — so when the timeout fires, the wait ends and the run moves on, instead of the verdict being written over a body that is still blocked.
Icon: Timer
Order: -20260915
---

# A timed-out test now stops waiting

A test that declares a `Timeout` promises to be over by then. Until now that promise was only half
kept: when the timeout fired, xunit recorded the failure, but the test's body was still parked in
whatever it had been waiting on — a mesh stream that never emitted, a poll against a portal that
never came up. The runner carried that blocked body along to the next test, and a wedge in one
place surfaced as a slow or hung shard somewhere else.

**The platform's test packages moved to the xunit.v3 4.x line** (`xunit.v3`, `xunit.v3.assert`,
`xunit.v3.extensibility.core` and `xunit.v3.runner.console` to 4.0.1; the VSTest adapter had already
moved to 4.0.0). Its analyzer treats a test that declares a `Timeout` without consuming
`TestContext.Current.CancellationToken` as a build error, and the repository builds with warnings as
errors — so every one of those tests, 707 of them across 260 files, now passes the token into the
call it waits on:

- `observable.Await(TestContext.Current.CancellationToken)` — the one sanctioned way to wait for an
  observable already took a token; it is now passed everywhere a test has a deadline.
- The reactive assertion chain — `.Should().Within(t).Emit(…)`, `.Match(…)`, `.Be(…)`,
  `.Complete(…)` and `.NotEmit(…)` — gained a trailing `cancellationToken` parameter, threaded down
  to the wait itself. A cancelled wait is reported as a cancellation, never folded into "it did not
  emit" or, for `NotEmit`, into a pass the test never finished.
- Helpers a test waits on (`Seed`, `InstallPackage`, an E2E fixture's login and readiness polls)
  take the token and hand it to their own waits.

A synchronous test with a `Timeout` has nothing to hand the token to; it observes the token at its
first statement, which is the analyzer's documented form for that shape.

Nothing was suppressed to get here: neither rule was added to `NoWarn`, and no test's timeout,
assertion or wait was changed. One fixture had two public constructors, which xunit 4.x refuses; the
shape-driven one is now internal to the assembly that calls it.

One consequence is worth knowing before it surprises someone: the 4.x line ships
Microsoft.Testing.Platform v2, which **refuses the VSTest target on the .NET 10 SDK**, so
`dotnet test` now runs through Microsoft.Testing.Platform (opted into by a root `global.json`).
Project paths, `-c`, `--no-build` and `--filter "FullyQualifiedName~…"` are unchanged; `-l:trx`,
`--logger`, `--blame-hang-*` and `-warnaserror` are gone, and a local run no longer drops a `.trx`
unless it is asked for. CI is untouched — its shards launch each project's native xunit host
directly and never call `dotnet test`. [Writing Tests](/Doc/Architecture/WritingTests) carries the
new flags.
