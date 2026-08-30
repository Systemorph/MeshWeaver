---
Name: Removing Hand-Woven Concurrency Gates
Category: Architecture
Description: The two shapes that replace a ManualResetEventSlim in a test, why the release must live in a finally, what the 79-site sweep measured, and the ratchet that now holds every root at zero with no allow file.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Removing Hand-Woven Concurrency Gates

A **hand-woven concurrency gate** is `SemaphoreSlim`, `ManualResetEventSlim`, `ManualResetEvent`,
`AutoResetEvent`, `CountdownEvent` or `Monitor.Wait` — anything that parks a thread until another
party signals it. [Asynchronous Calls](../AsynchronousCalls) carries the rule; this page is the
operational companion: **what to write instead**, **why the `finally` is load-bearing**, and **what
the sweep that emptied `test/` actually measured**.

## Why a gate in a TEST is a defect, not a style choice

In product code the failure is obvious: the parked thread is a single-threaded hub action block or
a grain turn, so the message the gate is waiting on can never be processed — deadlock.

In a test the same primitive fails more quietly, and it has bitten here twice:

- **A stranded worker.** `IoPoolResidualNamesItsPoolTest` put its `release.Set()` *after* an
  `await` of the method-timeout token. On timeout that line never ran, so the leaf it had
  deliberately blocked kept a pool thread for two full minutes — **into the next test**, which then
  failed for reasons that had nothing to do with it.
- **A ratchet swap.** The first repair traded the event for an `AsyncSubject` plus `.Wait()`. That
  is an observable→blocking bridge, which parks the thread just the same and trips
  `BlockingBridgeInTestRatchetGuard` instead. Satisfying one guard by violating another is not a
  fix.

## The two shapes, and how to tell them apart

Ask which way the signal travels.

### 1. Producer → test: an `AsyncSubject<Unit>` the producer completes

A handler, a pooled leaf, a render body, a `.Finally(...)`, a `Disposable.Create(...)` — anything
inside the system telling the test "I got here".

```csharp
var entered = new AsyncSubject<Unit>();

pool.InvokeBlocking(_ =>
{
    entered.OnNext(Unit.Default);
    entered.OnCompleted();     // AsyncSubject replays after completion — a late awaiter still sees it
    …
});

await entered.Should().Within(5.Seconds()).Emit("the leaf must be running before the drain");
```

The negative direction — *nothing* should have happened yet — is the same subject with `NotEmit`,
which is the one place a fixed wait is correct because there is no positive signal to await:

```csharp
await fired.Should().NotEmit(300.Milliseconds(),
    "Disposed must not fire while the leaf is still running");
```

🚨 **Never `.Wait()` the subject.** `await …Should()…Emit()` suspends the test; `.Wait()` parks its
thread and is the very defect the other ratchet exists for. Where an assertion helper does not fit,
`await x.Timeout(...).Await(ct)` (`ObservableAwait.Await`, in `MeshWeaver.Messaging.Hub`) is the one
sanctioned wait.

Making the test `async Task` to accommodate this is normal and expected; several `void` tests were
converted in exactly that way.

### 2. Test → a worker it deliberately parks: a volatile flag under a bounded `SpinUntil`

Half of these tests exist *because* something blocks: a wedged action block, "a leaf that ignores
its cancellation token", a subscribe that never returns, a merge turn held open so an ack provably
cannot arrive. **The park stays — it is the subject.** What goes is the kernel handle:

```csharp
var releaseGate = 0;

primary.Update(_ =>
{
    gateEntered.OnNext(Unit.Default);
    gateEntered.OnCompleted();
    SpinWait.SpinUntil(() => Volatile.Read(ref releaseGate) == 1, TimeSpan.FromSeconds(60));
    return null;
}, _ => { });

try
{
    await gateEntered.Should().Within(10.Seconds()).Emit("the turn must be parked before the write");
    …
    Volatile.Write(ref releaseGate, 1);
    …
}
finally
{
    // 🚨 THE LOAD-BEARING LINE
    Volatile.Write(ref releaseGate, 1);
}
```

`SpinWait.SpinUntil(predicate, timeout)` returns the same `bool` the event's `Wait(timeout)` did, so
a site that *measured* whether the release arrived (rather than merely waiting for it) keeps its
assertion unchanged. It allocates no handle, needs no `using`, and — like the event — never pumps a
`SynchronizationContext`, which matters where the test's whole point is that the blocked thread must
**not** run a queued continuation.

### 3. "Did that thread finish inside a budget?" → `Thread.Join(timeout)`

Not an event. `Join` returns only on real termination, whereas an event fired from a `finally`
signals *before* the thread has actually ended (#2792).

### 4. No signal back at all, where the block is the whole subject

When a test needs only "this leaf outlives the drain budget", a bounded `Thread.Sleep` **is** the
subject and needs no release — see `IoPoolResidualNamesItsPoolTest`. Prefer this when it does not
cost wall-clock time; prefer the flag when the release must be prompt.

## 🚨 The release goes in a `finally`

Every one of the ~20 converted parks in this sweep writes its release flag in a `finally`, and
several gained a `try` that was not there before. This is not tidiness: without it, an assertion
that throws *before* the release leaves a worker parked for its full bound — 20, 30, 60 seconds — and
that hold outlives the test. Where a helper owns the park (`WriteWaitsForCommitVerdictTest`'s
`OwnerGate`), `Dispose()` releases and the call site uses `using var gate = await Park…(path)`, which
gives the same guarantee.

Idempotence is what makes this safe: a mid-test `Volatile.Write(ref flag, 1)` followed by the same
write in the `finally` is harmless, so the happy path stays readable.

## What the sweep measured

Seeded 2026-08-30 by the guard's own scanner (comments and string literals masked, so a doc comment
naming the ban is not a site):

| | |
|---|---|
| Sites | **79 → 0** |
| Files | 23 |
| Primitive | `ManualResetEventSlim`, every one |
| Largest single file | `test/MeshWeaver.Hosting.Test/IoPoolTest.cs` — 32 |
| Projects | Hosting, Hosting.Monolith, Hosting.Orleans, Messaging.Hub, Layout, Graph, Persistence, Autocomplete |

`src/`, `tools/`, `samples/`, `clients/` and `memex/` were already at zero and stayed there.

## The ratchet after the sweep

`HandWovenGateRatchetGuard` now runs **one tier, not two**. `test` sits in `ProductionRoots`
alongside every other root, held at zero **with no allow file anywhere** — there is nothing to
append a line to, which is the strongest form this rule can take. Two things keep that honest:

- `TheTestTreeAllowFileStaysDeleted` fails if `test/HandWovenGateSites.allow` reappears. A cleared
  inventory that is quietly re-seeded is exactly how one grows back, and the allow-file reader is
  explicit that a *regenerated* file blesses whatever happens to be in the tree.
- `TheScannerSeesWhatItClaimsTo` plants a real temp tree and runs the **real** scan over it —
  proving the scanner sees a genuine gate, sees a `.csx` script, and does **not** see a gate named
  in a comment or a string literal. A guard whose self-test only exercises its regex can lose half
  its scope and stay green.

The only surviving exemptions are the two **verified** `SanctionedGates`: the one sealed inside
`IoPool` (the mesh's single async/IO boundary) and the standalone `ThumbnailGenerator` CLI, whose
exemption is re-checked every run against the premise it rests on — that its csproj references no
MeshWeaver assembly, so there is no hub in that process to park.

## Related

- [Asynchronous Calls](../AsynchronousCalls) — the rule itself, and the no-`async` contract it belongs to
- [Removing Observable-to-Task Bridges](../RemovingObservableToTaskBridges) — the sibling ratchet, and why `.Wait()` is not an escape from this one
- [Controlled I/O Pooling](../ControlledIoPooling) — where concurrency bounding legitimately lives
- [Writing Tests](../WritingTests) — the house rules the converted assertions follow
