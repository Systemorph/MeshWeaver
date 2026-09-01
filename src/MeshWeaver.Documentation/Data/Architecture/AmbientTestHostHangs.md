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
| xUnit `methodTimeout` (30 s) | **written in full** | yes |
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
