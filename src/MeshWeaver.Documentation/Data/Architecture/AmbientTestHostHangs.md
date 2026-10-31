---
Name: Ambient Test-Host Hangs
Category: Architecture
Description: The family of test hosts that die at a wall-clock cap instead of failing a test — what decides whether an occurrence can be diagnosed at all, which readings of it have already been wrong, and what is still open.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><path d="M12 6v6l4 2"/></svg>
---

# Ambient Test-Host Hangs

A test host that is **killed** rather than failing an assertion leaves almost nothing behind. This
page is the standing record of that family: what its signatures mean, the two readings of it that
have already turned out to be wrong, the one property that decides whether an occurrence is
investigable at all, and what is still genuinely unexplained.

Read it before opening a new investigation. The most recent one began from an issue body whose own
author had retracted its central claim in the comments, and from a root cause the same issue recorded
as reverted after it had been re-fixed by another route. Both were one `gh issue view --comments`
away.

## 🚨 The first rule: re-run the issue's own measurement before acting on it

Every claim below is dated, because they change. Two examples from a single day, 2026-09-01:

- **A hang that was not a hang.** `MeshWeaver.Hosting.Monolith.Test` was reported as starting,
  emitting no output, and never exiting — three times, including on `main`. It was measured
  standalone shortly afterwards: **787 tests, 10 m 32 s, terminated normally**. The suite is #39 of
  48 in a *serial* CI step budgeted at 60 minutes that needs 80–90; 37 suites completed, it never
  finished, and 9 after it never started. A **capacity** defect, not a wedge. The issue was retitled;
  the body's original framing survives above the correction and will mislead anyone who stops
  reading there.
- **A root cause that had already been fixed.** The leading explanation for the family was a dropped
  `OwnerDisposing` NACK. That fix was implemented, measured at a ~10× teardown cost, and reverted —
  and the revert is what the family's issue still records. It was then **re-fixed by a different
  route** (hand the verdict to the waiter through `ILatePatchVerdictSink` instead of posting it and
  hoping), which merged on 2026-08-31 — *after* every occurrence the family's issue lists. Acting on
  "the fix is reverted" would have re-solved a solved problem.

**So: before investigating, re-measure.** Read the comments, not only the body; check whether the
named root has landed since the occurrences; and check the dates on both.

## Silence is not a symptom

`dotnet test` with the console logger prints two header lines per suite and then **nothing until the
final summary**. A suite that is running normally and a suite that is wedged look identical for their
entire duration. A passing 383-test suite in the same run showed 1 m 52 s of unbroken silence.

**"No output for N minutes" therefore distinguishes nothing.** It is the normal shape. Do not offer
it as evidence, and do not accept it as evidence.

The same goes for `Terminate orphan process: pid (…)` in the runner's cleanup. xUnit v3 launches the
test assembly as its own child process, so that line is true of *any* suite killed mid-run. It is not
by itself a leak, a wedge, or a failure to exit.

## 🚨 What decides whether an occurrence can be diagnosed: how the test dies

This is the single most useful fact about the family, and it is a property of *configuration*, not of
the bug:

| How the test dies | Transcript of the hung test | Usable as evidence |
|---|---|---|
| the per-test bound (`methodTimeout`, applied by the execution host) | **written in full**, plus a dump of the stuck thread | yes |
| host wall-clock cap (`exit=124` / `HOST_CRASHED`) | **destroyed** — no trx entry, no captured stdout | no |

The family's defining artefact is destroyed by the family's own failure mode. An investigation that
waits for a CI occurrence and then reads the artifacts is reading everything *except* the thing that
hung: the flood of log lines that survives belongs to the tests that **completed**, so it cannot tell
you whether any of it was co-located with the hang.

Two consequences worth acting on:

1. **Triage should hunt this signature among method-timeout failures**, not only among crashed
   shards. Those instances carry the evidence and are routinely discarded as ordinary flakes.
2. **`methodTimeout` reaching the test host is a diagnosability dependency**, which is why the next
   section is about MSBuild rather than about the mesh.

🚨 **For a long time the top row did not exist for a plain `[Fact]`.** xunit.v3 ignores the
`methodTimeout` key, so only a test declaring `[Fact(Timeout = …)]` ever died that way, and every other
wedge took the bottom row (MeshWeaver.Plugins#4424). The execution host now applies the key; see
[Writing Tests](../WritingTests), "The per-test bound".

## The guard that could not reach the fleet

`test/Directory.Build.props` selects **one of two** `Content` branches for `xunit.runner.json` and
for `appsettings.json` — the shared default, or a project-local override. If a rename or a reworked
condition ever makes it select *neither*, the build still succeeds and the tests still run: under
xUnit's **own** defaults, where `parallelizeTestCollections` is true, the thread count is the
machine's core count, and **there is no `methodTimeout` at all**.

That last one turns every wedge in the affected assembly from a bounded, attributable failure that
carries its own transcript into a host kill that destroys its evidence — the top row of the table
above silently becoming the bottom row.

`VerifyXunitRunnerConfigCopied` exists to make that regression red. It used to live in
`test/Directory.Build.targets`. **MSBuild imports `Directory.Build.props` and
`Directory.Build.targets` independently**, and every satellite that consumes the plumbing imports
the *props* explicitly from its own `src/Directory.Build.props` — MeshWeaver.Plugins (78 `*.Test`
projects) and MeshWeaver.SocialMedia (1) — while **neither imports the targets**. Measured
2026-09-01: **zero of the five satellite checkouts** (.Plugins, .Education, .Reinsurance,
.SocialMedia, .Manufacturing) reference `test/Directory.Build.targets` at all. They inherited the
plumbing without the assertion that it had worked: **79 test projects without the guard, against
core's ~15 with it.**

**The guard now lives in `test/Directory.Build.props`, in the same file as the includes it
validates**, so importing the mechanism cannot leave the check behind — no satellite change required.
`DirectoryBuildGuardTravelsWithItsSubjectTest` fails if it drifts back apart.

This is the rule AGENTS.md states for CI gates, one layer down: **a gate must never be separable from
the input it validates**, because a gate that never runs is indistinguishable from a gate that
passed. See [Reading CI Signals](../ReadingCiSignals) for the same failure at the workflow level.

## Signatures, and what they are worth

The line most often quoted for this family is:

```
Dropping StreamEndedEvent … no synchronization hub found on this hub or any parent
  — the target stream is gone
```

**On its own it is worth nothing.** It has been measured in at least two *unrelated* defects, and a
hub that is going down for any reason emits it. Grouping by this string merges populations that need
separating.

🚨 **That exact string no longer exists, so re-running the old grep returns a FALSE NEGATIVE.** On
2026-09-01 the drop diagnostic was split by message type: a `StreamEndedEvent` to an already-departed
subscriber is a semantic no-op (its whole meaning is "nothing more comes"), so it moved to `Debug`
**and got different wording** — *"the target stream is already gone, and a terminal notice to a
departed subscriber is a no-op"*. It no longer contains "no synchronization hub found on this hub or
any parent" at all. Two independent reasons a search for the historical line now finds nothing:
**the level and the text**. Every *other* dropped `StreamMessage` keeps the original warning, because
a lost `Full` renders a region blank.

Searching a post-2026-09-01 capture, match on `Dropping StreamEndedEvent` alone and make sure the
capture includes `Debug` for `MeshWeaver.Data.DataExtensions`.

The discriminator that has actually separated cases is whether
`[UpdateQueue] ADVANCE_WITHOUT_HANDOFF … bound=5000ms` appears **inside the hung test's own window**,
within milliseconds and on the same path. Present ⇒ the update queue advanced because the owner never
acknowledged a write, which is the missed-observation mechanism. Absent ⇒ a different mechanism
wearing the same log line. It has returned both answers on real data, which is what makes its
positive answer worth something.

A second family of evidence that reads as damning and is not: `teardown-stragglers.log` files full of
`ObjectDisposedException` with `TeardownOrderedScopeDisposal.CloseWhenDrained` on top. Those are
**first-chance** exceptions, and that call site catches exactly that type:

```csharp
catch (ObjectDisposedException)
{
    // The root itself is gone — the drains ran (or nothing will run them). Close now.
    closeScope();
    return;
}
```

They are a **clock, not a cause** — and they arrive in bursts at a handful of distinct instants, which
is the shape of successive test classes disposing their meshes, i.e. progress. On one captured run the
last straggler landed **7 m 53 s before** the kill.

## A host that deadlocks on its own stdout

This one is solved. Its signature is worth knowing because it reads as a teardown stall.

**What it looked like.** Core PR #7128, shard 0 (run 40102602726, attempt 1): the
`MeshWeaver.Hosting.Orleans.Test` host's console output **stopped in the middle of one test's
captured-output block** at 14:39:26, and it printed nothing more. There was no `[PASS]` line for
tests that the trace log shows finishing later, and no `[Long Running Test]` line, although
`-longRunning 60` prints one every minute while any test runs. Meanwhile
`collected-logs/_meshweaver-test-trace.log` went on recording until 14:42:58. The host was killed
at the 8-minute cap (`exit=137 TIMEOUT`).

The discriminator is **the trace log still advancing while the job log has stopped, and the
`[Long Running Test]` reporter silent too.** A test that is merely slow keeps the reporter printing.
Only a blocked console stops it.

**What it was.** A local loop of the host with a 90-second output-silence detector caught it on the
9th run. `dotnet-dump analyze <core> -c syncblk` named two `SyncTextWriter`s, each owned by one
thread and wanted by the other:

- xUnit's reporter thread had captured `Console.Out` at startup. It held that original writer and,
  inside its flush, entered `ConsolePal.WriteFromConsoleStream`. On Unix that method takes
  `lock (Console.Out)`.
- By then a kernel cell had installed `CapturingTextWriter` with `Console.SetOut`, so
  `Console.Out` was the hook's synchronized wrapper. The Microsoft console-logger thread held that
  wrapper, and its write fell through the hook to the fallback, which was the ORIGINAL writer.

After that, every other console writer queued behind the two: the long-running reporter, the
end-of-run summary, and `OrleansMeshPool`'s receipt line. So the host could never exit. The same
inversion is reachable in a portal, where any component that kept the startup `Console.Out` plays
the reporter's part.

**The fix.** The hook's fallback is a writer over the raw standard stream, never the writer it
replaced. A write through the hook then holds only the wrapper's lock, and `ConsolePal` re-enters
that same lock. `ConsoleHookTakesNoLockOfTheWriterItReplacedTest` holds the replaced writer's lock
the way the reporter does and requires a write through the hooked console to finish anyway. Against
the old fallback both of its cases fail after their 10-second bound, and the host exits normally.
The test asserts the property and never builds the cycle, so its negative control cannot wedge the
host. With the fix, 25 consecutive local runs of the whole host finished with no stall.

**The "3 minutes after the last test" was not teardown.** The triage that led here read main's shard
0 (run 40105736575) as a host that idled 3 min 19 s after its last test. The last test was still
running: `CrossGenerationManifestExchangeTest.WithoutTheLegacyPath_…` waits through a full
`TestTimeouts.CrossSilo` window of silence by design. That is (30 s + 1 s + 5 s) × 3 × 2 = 216 s on CI.
It has no `TEST START` marker because it does not derive from a mesh test base, so it does not show
up in a `TEST_START`/`TEST_END` census. Its own `[Long Running Test]` lines name it. Across 35
recent CI runs the host took 261–279 s every time except the one deadlock, and it exits the moment
that test reports.

## A module suite that wedges: ended below the job cap, with a dump

Core's shards always bounded each host below the job cap (`timeout 8m` in `dotnet-test.yml`). The
satellites' module lane (`node-repo-module-pack.yml`) did not. Its `dotnet test` ran unbounded, so a
wedged suite ran into the job's 45-minute cap. A job the cap cancels runs no further step, so it kept
no trx, no phase trace and no dump. The leg read `cancelled`, which looks like infra.

That is what happened on MeshWeaver.Plugins PR #4155 (run 40115238778, `Module tests
(MeshWeaver.AI)`, platform `c784cdb0e`, which predates the stdout fix above). The AI host printed its
last skip about 70 s in and then nothing for 35 minutes. That silence does **not** carry the
stdout-deadlock signature by itself: under Microsoft.Testing.Platform the console prints only skips,
failures and the summary. A green run of the same suite (run 40120811982) is silent from that same
skip until its summary 3.5 minutes later, and xunit's long-running notices do not reach the MTP
console even with `--long-running` and `--xunit-diagnostics on` (measured locally). So nothing in
that log separates the #7137 deadlock, a test that blocks synchronously, and a run-teardown wedge.
Each of them withholds the summary too. 20 local runs of the suite on that platform did not stall.

Each suite now runs under `.github/scripts/run-bounded-suite.sh`. Its deadline is whatever the job
has left, less a reserve for the steps after it: a stuck detector derived from the cap, not a budget.
On the deadline it:

1. names every phase in `/tmp/meshweaver-test-trace.log` that opened and never closed
   (`TEST_START` without `TEST_END`, `INIT_START` without `INIT_DONE`, `DISPOSE_START` without
   `DISPOSE_DONE`). If every phase closed, it says the host is stuck outside a test.
2. asks each .NET process in the tree to dump **itself**, through its diagnostics socket
   (`.github/scripts/diag-dump.py`). This needs no ptrace and no tool installed at the moment of the
   wedge. The runtime spawns its own `createdump`, and its diagnostics server answers from its own
   native thread while managed threads are deadlocked. The dumps are mini dumps: every managed stack,
   about 100 MB for the AI host. A heap dump of the same host is 1.7 GB, which would cost the
   artifact store a lot per wedge. The stacks name a lock-order deadlock's two threads. `syncblk`
   needs the heap, so a lock-owner question is answered from a local heap dump.
   The whole dump phase shares **one** budget (`--dump-budget`, 180 s by default) that sits inside
   the caller's reserve. Each request gets only what is left of it (`diag-dump.py --timeout` is one
   deadline over connect, send and every read), and a process still unasked when the budget is
   spent is skipped and said. So a runtime that accepts the request and then stalls cannot push the
   job past its cap before the evidence is uploaded.
3. ends the process tree and exits 124. The lane then records the suite as a TIMEOUT, not as
   "the suite failed", and the existing evidence step keeps the dumps and the trace under
   `test-evidence/<module>`.

A suite whose turn comes after the window is spent (less than 60 s left before the reserve) is
**not started**. Starting it would put it inside the reserve, where it could run into the cap. It
is recorded as a failure, never a pass, and the step names every unstarted suite in one annotation.

Measured end to end on the AI suite with a 75 s deadline: exit 124, the four tests and two class
inits in flight named by pid, a 94 MB host dump with 78 readable managed stacks, and no process left.

## What is still open

**What ends a live node's synchronization streams roughly five seconds into an operation that is
still running.** The caller-side consequences are understood and fixed — the verdict now reaches the
waiter rather than being posted into a hub that has stopped listening, and the queue's 5 s advance
bound is deliberately independent of the caller's terminal (failing the caller there would abandon
writes that are about to land). What remains unexplained is the owner going away mid-run.

Both suites that witness it reproducibly live in MeshWeaver.Plugins, and the class is **bulk-only** —
it passes in isolation and needs the co-tenancy of a full run. So the productive route is a **local
bulk run with the process inspected while it is still wedged**, not another artifact dive.

## 🚨 Measuring a hang on a loaded machine measures the machine

CPU starvation produces this family's exact signature: a host that starts, emits nothing, and sits
there. A "reproduction" observed on a saturated box is indistinguishable from the real thing.

- **Record the load average with every observation** and put it beside the result. A hang at load 90
  on 18 cores is not data; the same hang on an idle box is.
- **Prefer a zero-CPU discriminator to a wall-clock one.** A starved process still accumulates CPU
  time; a genuinely wedged one does not. Watch whether `%CPU` stays pinned at `0.0` while elapsed
  time grows — that separates *wedged* from *slow* in a way no timeout can.
- If the machine is busy, **say so and stop** rather than concluding. Never raise a bound to get past
  it: raising the cap only moves where the same thing is observed, and buys another hour of runner
  time per run to arrive there.

## Related

- [Reading CI Signals](../ReadingCiSignals) — why a skipped or absent required check reads as green.
- [Debugging Message Flow](../DebuggingMessageFlow) — the trace tags, and the discriminator between
  a lock and a missed observation (a burst of work then total silence is a dropped emission, not a
  lock).
- [Writing Tests](../WritingTests) — the house rules, including why a test is never re-run to see
  whether it was a flake.
