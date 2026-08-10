---
Name: Subscription Ownership
Category: Architecture
Description: A pending timer is a GC root. Its subscription must reach an owner's disposal chain — and holding it in a field is not the same as owning it. The two leak shapes with real before/after fixes, the measured truth table of which primitives actually root, why a green MeshHubDisposalLeakTest proves nothing, and why there is deliberately no analyzer.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2"/><path d="M9 2h6"/></svg>
---

`Observable.Timer`, `Observable.Interval`, `System.Threading.Timer` and `Task.Delay` all park an entry on the process-wide **`TimerQueue`**, and that queue is a **strong GC root**. While the timer is pending it holds its tick closure, and the tick closure holds everything it captured — transitively. When that tail reaches a `MessageHub`, the hub cannot be collected, no matter how correctly it was disposed. Disposed is not the same as unrooted.

So the rule:

> **A timer, interval, or delayed subscription must reach its owner's disposal chain — always, on every path.** "Held in a field" is not ownership. The question is never *is there a variable pointing at the subscription*, it is *does the owner dispose it, on every path, including the paths where the owner is abandoned rather than torn down*.

This page exists because nine sites in `src/` independently rediscovered the same three fixes for this, months apart, each one found by chance when [`MeshHubDisposalLeakTest`](/Doc/Architecture/DebuggingDisposalAndLeaks) sampled a random unrelated PR. Recurrence at that rate is the argument for a written convention. The audit behind it swept the whole tree and is recorded on issue #995.

---

## Sub-shape 1 — the subscription is discarded outright

The tell is a `.Subscribe(...)` whose `IDisposable` goes nowhere. Nothing can cancel the pending timer, so teardown of the owner does not release it, and the closure keeps the owner alive for the full delay past its own disposal.

`ActivityLogLogger` coalesced a burst of script log lines by arming a 100 ms tail-flush:

```csharp
// ❌ BEFORE — the timer's IDisposable is dropped on the floor. A hub torn down within
//    100 ms of the last log call stays rooted: TimerQueue → tick closure → the logger →
//    the IMessageHub it posts to.
if (Interlocked.CompareExchange(ref _publishScheduled, 1, 0) == 0)
{
    Observable.Timer(TimeSpan.FromMilliseconds(ThrottleMs))
        .Subscribe(_ => { Interlocked.Exchange(ref _publishScheduled, 0); Publish(); });
}
```

The fix is ownership, not a shorter timer — a `SerialDisposable` registered on the hub that the closure ultimately reaches:

```csharp
// ✅ AFTER — the pending flush is the HUB's. Teardown disposes the hub's composite,
//    which cancels the timer.
private readonly SerialDisposable pendingFlush = RegisterPendingFlush(hub);

private static SerialDisposable RegisterPendingFlush(IMessageHub hub)
{
    var pending = new SerialDisposable();
    hub.RegisterForDisposal(pending);
    return pending;
}

// …and at the callsite:
if (Interlocked.CompareExchange(ref _publishScheduled, 1, 0) == 0)
{
    pendingFlush.Disposable = Observable.Timer(TimeSpan.FromMilliseconds(ThrottleMs))
        .Subscribe(_ => { Interlocked.Exchange(ref _publishScheduled, 0); Publish(); });
}
```

`SerialDisposable` is the right holder rather than a plain field for two reasons that both matter: assigning a new value **disposes the previous one**, so re-arming can never orphan a pending timer; and a value assigned *after* the `SerialDisposable` has been disposed is disposed on assignment, so there is no race in which teardown and a last-moment arm cross.

The same shape, with a re-establish rather than a flush, is what `ActivityControlPlaneExtensions.SubscribeWithReEstablish`, `ThreadSubmissionServer.InstallServerWatcher` and `ThreadExecution`'s two watchers do: a stream that faults schedules a 1 s retry, and the schedule goes into a `pendingReEstablish` `SerialDisposable` that the watcher's own `Dispose` drops **before** it drops the live subscription — the pending timer is what roots the closure graph, the live subscription is merely what is running.

Same *shape*, not the same *code*: the three thread watchers are hand-rolled loops that share only the scheduling (`ReEstablishSchedule.Arm`, which re-reads the `disposed` flag when the timer fires and sinks a synchronous re-establish throw into the logger). They do **not** route through `SubscribeWithReEstablish` and so do **not** have its terminal fault classification — an own-node-gone `NotFound` or poisoned content re-establishes there rather than stopping. Converting them is open work; don't read the shared shape as shared behaviour.

## Sub-shape 2 — held, but pinning an owner that may never be disposed

This is the shape a static rule cannot see, and it is the majority of the sites that have actually leaked. The subscription **is** assigned to a field. It reads as owned. The defect is that nothing the *owner's* teardown runs ever disposes that field.

`MeshNodePickerView`'s search debounce is the clean example:

```csharp
// ❌ BEFORE — assigned to a field, so any "discarded Subscribe" analyzer passes this
//    file clean. But the ONLY code that ever disposed _debounceSub was the NEXT
//    keystroke. Type into the picker, navigate away, and the last 200 ms timer stays on
//    the TimerQueue holding the component → its injected IMeshService → the mesh services.
private IDisposable? _debounceSub;

private void OnSearchInput(ChangeEventArgs e)
{
    _debounceSub?.Dispose();
    _debounceSub = Observable.Timer(TimeSpan.FromMilliseconds(DebounceMs))
        .Subscribe(_ => LoadResultsAsync());
}
```

```csharp
// ✅ AFTER — the handle is registered ONCE into the framework's existing component
//    disposal list, which DisposeAsync releases. The per-keystroke semantics are
//    unchanged; what changes is that teardown cancels the LAST one too.
private readonly SerialDisposable _debounceSub = new();

protected override void OnInitialized()
{
    base.OnInitialized();
    Disposables.Add(_debounceSub);
}

private void OnSearchInput(ChangeEventArgs e)
{
    _debounceSub.Disposable = Observable.Timer(TimeSpan.FromMilliseconds(DebounceMs))
        .Subscribe(_ => LoadResultsAsync());
}
```

Two details in that fix generalise. **Register in `OnInitialized`, not in `BindData`** — `BindData` re-runs on every binding-relevant parameter change, which would both duplicate the entry and cancel a debounce the user is mid-typing; `OnInitialized` runs once and is guaranteed by Blazor to run before any event handler can fire, so no keystroke can arm a timer outside the registration. And **use the framework's existing list rather than a bespoke `DisposeAsync` override**: `BlazorView.Disposables` is already released at teardown, and the MAUI view pack's `Disposables.Add(_searchSub)` is the sibling of exactly this pattern.

### When the owner might never be torn down

`Disposables.Add` / `RegisterForDisposal` are enough for an owner whose disposal is guaranteed. Some owners' disposal is not guaranteed: a hub created at `RunLevel = 1` that never completes activation is *abandoned*, never disposed, so its dispose hooks never run. For a **diagnostic or keep-alive** timer — something that must never be the reason its subject stays alive — hold the subject weakly and self-dispose when it is gone:

```csharp
// MessageHub.InstallStaleCallbackScanner — a diagnostic must never keep its hub alive.
var weakSelf = new WeakReference<MessageHub>(this);
var sub = new SingleAssignmentDisposable();
sub.Disposable = Observable.Interval(StaleCallbackScanInterval)
    .Subscribe(_ =>
    {
        if (!weakSelf.TryGetTarget(out var self))
        {
            sub.Dispose();   // captured directly — needs no reference back to the dead hub
            return;
        }
        self.ScanStaleCallbacks(thresholdMs);
    });
staleCallbackScannerSub = sub;   // ALSO registered, so a normal teardown kills it promptly
```

A live hub is held by its real owners (parent hosted-hubs, DI), so the weak reference always resolves while the hub matters; an abandoned one becomes collectable and the next tick tears the timer down. `JsonSynchronizationStream`'s 45 s heartbeat and `KernelContainer`'s idle-disconnect `Timer` (a `WeakReference` plus a **`static`** callback, so the closure captures nothing) use the same idiom. Note that the weak version is *in addition to* registering for disposal, never instead of it — the weak reference bounds the damage on the abandoned path, the registration releases the queue entry promptly on the normal one.

The third idiom is the **severable cell**, for the case where disposing the timer is not enough. Disposing a one-shot `System.Threading.Timer` does not immediately remove it from the `TimerQueue`; the queue can keep the `TimerQueueTimer`, and therefore its callback's captured state, until the due time passes. `MeshNodeTypeSource`'s debounce reaches its source through a mutable cell that teardown nulls out, with a `static` callback so nothing else is captured:

```csharp
var cell = _flushCell;                       // FlushOnDispose sets cell.Target = null
_debounceTimer = new Timer(static state =>
{
    if (state is FlushCell { Target: { } src }) src.RunDebouncedFlush();
}, cell, DebounceInterval, Timeout.InfiniteTimeSpan);
```

Measured at the time: with `this` captured, the ClrMD probe found **9** disposed-but-pinned hubs immediately after teardown and 0 once the debounce interval had elapsed; through the cell it found 0 immediately.

## `Task.Delay` as a gate

The reactive shape is preferred and is what `MessageHub`'s disposal watchdog uses — `Observable.Timer(timeout).TakeUntil(disposalCompleted)`, so a normal fast disposal releases the queue entry the instant disposal finishes. (Its ancestor, an uncancelled `Task.Delay(25 s)`, rooted the entire hub graph for 25 s after **every** dispose, fast ones included.)

Where a `Task.Delay` gate already exists, it is safe under exactly two conditions:

- it is one arm of a **`Task.WhenAny`**, or
- it carries a **`CancellationToken` that is always cancelled**, on every path.

`DataContext.OpenInitializationGate` is the first shape — `Task.WhenAny(allInit, Task.Delay(120 s))`. `MessageService`'s deferred-delivery timeout is the second — `Task.Delay(30 s, cts.Token)` where the token is cancelled both on drain and on `Dispose`; note that the abandoned-owner path is exactly the one where "always cancelled" gets interesting, which is why it is worth checking rather than assuming.

## Which primitives actually root — measured, not reasoned

The #995 audit did not reason about this. It built a probe that captures a payload behind each shape, forces a Gen2 collection while the timer is still pending, and reports whether the payload survived. The table is short, it was cheap to produce, and it settles the questions that otherwise get argued from memory:

| Shape | Captured object after a forced Gen2 GC |
|---|---|
| `Observable.Timer(long).Subscribe(…)` — result **discarded** | **ROOTED** |
| `Observable.Timer(long).Subscribe(…)` — held + `Dispose()` | free |
| `Observable.Interval(long).Subscribe(…)` — held, **not** disposed, strong capture | **ROOTED** |
| `Observable.Interval(…)` capturing through a `WeakReference` | free |
| `Task.WhenAny(winner, Task.Delay(long)).ContinueWith(…)` — winner completes after hookup | free |
| `Task.WhenAny(alreadyCompleted, Task.Delay(long)).ContinueWith(…)` — fast path | free |
| `Task.Delay(long).ContinueWith(…)` — no token | **ROOTED** |
| `Task.Delay(long, token).ContinueWith(…)` — token **cancelled** | free |
| `Task.Delay(long, token).ContinueWith(…)` — token **not** cancelled | **ROOTED** |
| `new Timer(cb, …)` one-shot — `Dispose()` before due | free |
| `new Timer(cb, …)` one-shot — not disposed, strong capture | **ROOTED** |
| `new Timer(static cb, cell)` — not disposed, cell **severed** | free |

Three rows are worth reading twice, because they are the counter-intuitive ones.

**`Task.WhenAny` removes its continuation from the losing task, so it does not root.** By inspection, `DataContext.OpenInitializationGate` arms an uncancelled `Task.Delay(120 s)` on every hub that has a `DataContext` — visibly the largest find in the tree and the same shape as the original `MessageHub` watchdog. It is not a leak, and reading alone would have reported a 120 s hub-wide leak that does not exist. Two further sites (`McpMeshPlugin`, `MessageHubGrain`) are cleared by the same measurement.

**A *fired* `Observable.Timer(…).Subscribe(…)` handle, still held, does not retain its callback closure** — Rx's sink detaches its observer on completion. This is what makes `RegisterForDisposal(pendingFlush)` cheap rather than a trade of one leak for another: the hub retains the `SerialDisposable` (tens of bytes), and a settled logger with its message list stays collectable while the hub lives on.

**A bare `Task.Delay(…).ContinueWith(…)` does root** — that is the shape that pinned the whole hub graph after every disposal until the watchdog was rewritten reactively.

The same detach-on-completion fact is why the mandated `stream.Update(...).Subscribe(...)` shape from [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) is safe to discard: those observables complete promptly, and the `IDisposable` of a completed sequence roots nothing. *Pending* is the whole difference. That is also why an analyzer cannot be written cheaply — see below.

## Pinning a fix — and why `MeshHubDisposalLeakTest` is not the pin

`MeshHubDisposalLeakTest` is a **discovery** probe. It is what found all nine of these, and it is worth keeping for that. It is not a regression test, and **a green run from it is not evidence**:

- **It samples.** A root that is live only for a bounded window — 1 s for the watcher re-establish, 100 ms for the log flush — is caught only if the probe's forced GC lands inside that window. Fire first, get collected, go green, with the defect fully present.
- **It cannot attribute.** It reports the first `MessageHub` reachable from *any* non-stack root. A red run names whatever chain it happened to walk, which may be a different defect than the one you are chasing; a green run says only "no hub was reached within the visit budget".
- **It is inconclusive off Linux.** ClrMD snapshot-attach throws on macOS, so a surviving hub SKIPs (#674) — locally a developer learns nothing either way.

The right pin is a **targeted, timing-free ownership test** next to the code that owns the subscription. Assert the ownership property directly — after the owner is disposed, the handle holding the pending timer is disposed too — which holds whether or not the timer has already fired, so the test can neither flake nor pass by accident. A `WeakReference` probe would instead be a sampling test of a 100 ms window, i.e. the thing that pins nothing.

Then **run the negative control**: revert only the ownership line, leave the signature alone, and confirm the test fails with the message you expect.

| Reverted | Expected result |
|---|---|
| `hub.RegisterForDisposal(pending)` in `ActivityLogLogger.RegisterPendingFlush` | `DisposingTheHub_CancelsThePendingTailFlush` fails: *"…tearing down the hub must cancel a tail flush that is still pending … but found False"* |
| `Disposables.Add(_debounceSub)` in `MeshNodePickerView.OnInitialized` | `DisposeAsync_CancelsTheLastPendingDebounce` fails: *"…OnInitialized must register the debounce handle in BlazorView.Disposables … but found 0"* |

Assert **both halves** of the ownership, not just the registration: that teardown disposes the handle, *and* that the working path arms into that same handle. Without the second assertion the registration could guard a slot nothing ever writes to and the test would still pass.

## Why there is deliberately no analyzer

This gets proposed roughly every time the class resurfaces. The audit measured it, and the numbers say no.

A Roslyn rule for sub-shape 1 — "`Observable.Timer`/`Interval` whose `.Subscribe(...)` result is discarded" — would have flagged **1 of the 46** timer/gate sites in `src/` and **1 of the 6** findings. The other live defect was held in a field and reads as correct to any static rule, as do all four of the ambiguous sites, as does the majority of the prior art: sub-shape 2 is a capture-lifetime question about whether an owner is disposed on every path, and it is not statically decidable here.

Widening the rule is worse, not better. An unrestricted "discarded `Subscribe` result" rule fires on **388 of the 697** `Subscribe`/`Connect` invocations in `src/`, and roughly 386 of those are the cold-observable `stream.Update(...).Subscribe(...)` pattern that this codebase **mandates** — safe for the measured reason above. Scoped down to timers it finds one 100 ms bug.

So the cost is not the build time. It is that a mechanical check implies the class is handled while covering the shape that does *not* recur and missing the one that does — which is the worse failure mode, because it stops people reading the field.

The same reasoning rules out a committed test that enumerates the sites. It would need a baseline allowlist (the `test/` tree has deliberate discarded-timer constructs that are the subject under test — `ActivityOutlivesDisposeTest`, `IoPoolTest`), and a stale allowlist is how an enumerating test rots into noise. It would also need the source tree at test time, coupling a unit test to repo layout.

## Re-auditing the tree

The scanner that produced these numbers is about 130 lines of `Microsoft.CodeAnalysis.CSharp`: syntax-only, no `MSBuildWorkspace`, classifying every `Subscribe`/`Connect` invocation by whether its value is consumed plus the text of its receiver chain, over `src/` and `test/` in about a second. It is what makes a discarded `IDisposable` visible when it is the tail of a fluent chain, which a text search misses.

**It is deliberately not in the repository.** It is a one-off script, re-written or re-run at audit time and thrown away — the decision is recorded here so the next person does not re-litigate it. An audit is three passes and only the first is mechanical:

1. **The syntax scan** — every `Subscribe`/`Connect`, consumed or discarded, with its receiver chain.
2. **A manual read** of every `Observable.Timer`/`Interval`, `System.Threading.Timer` and `Task.Delay` hit: capture tail and owner-disposal path for each.
3. **A measured GC-root probe** for any shape whose rooting behaviour you are about to assume. Rebuild the table above rather than trusting it if the runtime has moved.

Steps 2 and 3 are the ones that find things, and neither can be committed as a check. What *is* committed is per-site: a targeted ownership test with a negative control, next to the code that owns the subscription.

## The four clauses

Applied to a new subscription, the rule fits in four lines. These cover all nine sites already fixed and both live defects the audit found:

1. A timer subscription is **assigned into something the owner disposes**, or composed into a stream that is.
2. A timer that **can outlive its subject** holds that subject weakly, or reaches it through a severable cell — and still registers for disposal, for the normal path.
3. A `Task.Delay` used as a gate is either inside a `Task.WhenAny` or carries a token that is **always** cancelled. Prefer `Observable.Timer(...).TakeUntil(...)`.
4. Pin it with an **ownership** test plus a **negative control**. Never with `MeshHubDisposalLeakTest`.

---

**See also:** [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — the no-`await` rulebook, and the "Subscribe is mandatory" rule that makes this class easy to get wrong · [Hub Disposal Model](/Doc/Architecture/HubDisposalModel) — what teardown actually runs, and when `RegisterForDisposal` fires · [Debugging Disposal & Leaks](/Doc/Architecture/DebuggingDisposalAndLeaks) — the ClrMD root probe, for when you need to find one of these rather than avoid it · [Controlled I/O Pooling](/Doc/Architecture/ControlledIoPooling) — where genuinely async leaves belong · [Writing Tests](/Doc/Architecture/WritingTests) — timing-free assertions.
