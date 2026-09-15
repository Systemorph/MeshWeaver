---
Name: A timed-out test stops in its helpers too
Category: Fix
Description: A test that timed out could still be parked inside one of its own helper methods, because the helper's wait never saw the cancellation token — the analyzer only ever asked the test body for it. Every helper-level wait in the test tree now takes the running test's token, and the five that deliberately do not say why where they sit.
Icon: Timer
Order: -20260915
---

# A timed-out test stops in its helpers too

[The previous entry](/Doc/WhatsNew/2026-09-15-a-timed-out-test-now-stops-waiting) closed the gap
between a test's `Timeout` and the wait it parks in. It closed it for the waits a test reaches
*directly*. This one closes it for the waits a test reaches through **its own helper methods**, which
is where a long test actually spends its time.

**Why the analyzer could not see this.** `xUnit1069` is satisfied by **one** reference to
`TestContext.Current.CancellationToken` anywhere in a test method. A test that hands the token to its
first `await` and then calls `await SeedTheSpace()` — where `SeedTheSpace` polls on a bare
`Task.Delay(300)` or waits on an untokened `.Await()` — is green to the analyzer and still outlives
its verdict: xunit records the timeout, the body stays blocked in the helper for up to that helper's
own deadline, and the runner carries it into the next test. That is the hung-shard behaviour the
rule exists to end, and buying the analyzer's approval without it was the one thing not to do.

**What changed.** Every wait in the test tree was located and attributed to the member that lexically
owns it — `.Await()`, `Task.Delay`, `WaitAsync`, `ReactiveWait.First`, `ObserveCompletion`, and the
reactive assertion terminals `.Emit()` / `.Match()` / `.Complete()` / `.NotEmit()`. Of **2,847** wait
sites, **194 in 110 files sat in a helper and took no token**. **185 of them now take one**, across
106 files: the helper's own `CancellationToken` parameter where it has one, and
`TestContext.Current.CancellationToken` otherwise — the same token the test body holds, and the
documented default of the repository's own test-base primitives.

**The nine sites that deliberately do not, each saying so where it sits** — because a wait that must NOT be
cancelled is a decision, not an oversight:

| left untokened | why |
|---|---|
| `PortalFixture.WaitForPortalAsync` | fixture construction, before any test owns a token |
| `CollectibleUnloadDrain.WaitUntilCollectedAsync` | a teardown drain — teardown lets work finish, it is never forced |
| `QuiescingHubRefusesNewWorkTest.ReleaseAndDispose` | releases the held victim so the fixture tears down cleanly |
| `RxFanOutInversionHarness.BothGatedHandlersComplete` | the delay **is** the deadlock bound; cancelling it would report "deadlock detected" for a test that merely ran out of time |
| `PodHubTransportTest.Settled` | the same shape, worse: its result is read as `.Should().BeFalse(…)`, so a cancelled delay would make a timed-out test **pass** a negative assertion it never finished |
| `MeshTestRunnerTests`' `Sample` cases | they are the in-mesh runner's subject, including one that parks forever so the runner's own timeout can be observed |

**And what is still not fixed, said plainly: 1,292 wait sites inside test bodies still take no
token.** Each is analyzer-compliant (the method references the token somewhere else) and bounded by
its own `.Timeout(…)`, so none can hang a run — but a test with several waits can still be sitting in
its third one when the verdict is written. Same defect, larger population, not fixed here. Claiming
otherwise would be the failure this change exists to remove.

## Two tooling facts, measured rather than asserted

The move to Microsoft.Testing.Platform raised a question about the reusable CI lanes that run
satellite repositories' suites. `dotnet test` picks its runner from the first `global.json` found
walking **up from the current directory** — measured on the .NET 10.0.400 SDK with `dotnet test
--help` as the instrument: a `global.json` in a **sibling** directory does **not** apply, one in the
cwd or an ancestor does. Both lanes run with the *caller's* checkout as the working directory and
keep the platform in a sibling checkout, so the platform's `global.json` was never going to reach
them — and the caller's own always would.

So rather than leave a trap for the first repository that opts in, `node-repo-module-pack.yml` and
`node-repo-platform-canary.yml` now **read the caller's `global.json` and choose their own flags**:
VSTest when it selects nothing, xunit's MTP trx reporter when it selects Microsoft.Testing.Platform.
Nothing changes for a caller that has not moved. The failure this avoids is a quiet one — under MTP
both `--logger` and `-warnaserror` were measured to end a run as `Zero tests ran`, exit 5, which the
module lane would have recorded as *"the module's own suite failed"*.

The canary's build-failure classifier widened in the same change: it recognised only `CS`/`FS`/`BC`
diagnostics, so an **analyzer** error — exactly what a pin move onto the 4.x line produces, in the
thousands — was filed as "comparison incomplete" instead of the `BUILD FAILED at main` the lane
exists to report. `MSB`, `NU` and `NETSDK` stay classified as infrastructure, so a restore outage is
still never reported as "the platform broke you".
