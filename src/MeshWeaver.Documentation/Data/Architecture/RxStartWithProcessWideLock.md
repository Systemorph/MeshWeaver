---
Name: Rx StartWith and Merge Take a Process-Wide Lock
Category: Architecture
Description: Why Rx's StartWith and Merge serialise every subscriber in the process on one Monitor, how a pool-stall capture caught 14 workers queued on it during a hub teardown burst, and the inline operators the hub's hot paths use instead.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>
---

# Rx StartWith and Merge Take a Process-Wide Lock

`source.StartWith(value)` and `a.Merge(b)` look free, but they are not. System.Reactive builds
`StartWith(value)`, `a.Merge(b)`, `Observable.Merge(a, b)` and `sources.Merge()` from
`ToObservable(ImmediateScheduler.Instance)`. On every subscription, `ImmediateScheduler`'s recursive
`Schedule` creates a new `AsyncLockScheduler`. That class is a `LocalScheduler`, and the
`LocalScheduler` constructor calls `System.Reactive.PlatformServices.SystemClock.Register`. `Register`
takes **one process-wide `Monitor`**, on its static `HashSet<WeakReference<LocalScheduler>>`, and adds
an entry to it.

So every subscription of those operators, on every thread of the process, waits for the same lock.

## What it measured

Measured with System.Reactive 7.0.0 on net10.0, in Release. Each operator was subscribed on one thread
while another thread held `SystemClock`'s lock.

| blocks on the lock | does not |
|---|---|
| `StartWith(v)` | `StartWith(Scheduler.CurrentThread, v)`, `Prepend(v)` |
| `a.Merge(b)`, `Observable.Merge(a, b)`, `new[] { a, b }.Merge()` | `new[] { a, b }.ToObservable().Merge()` |
| | `Concat`, `Amb`, `CombineLatest`, `Zip`, `Catch`, `Return`, `Range`, `Generate`, `Repeat`, `Defer` |

Throughput: 640,000 `StartWith` subscriptions on 32 threads take 726 ms, against 28 ms for
`Return(v).Concat(source)`. After 10,000 live `StartWith` subscriptions, 2,570 schedulers were still
registered in the set.

## How it showed up in production (#5799, #5388)

The `[POOLSTALL]` queue-delay capture on memex-cloud `…6b68589447-px94w` was taken on 2026-11-11 at
09:27:09Z. A probe item had waited 5.10 s, and 16,294 items were queued over 39 threads. The capture
printed its largest shape like this:

```
×14:
   at System.Threading.Monitor.Enter_Slowpath(...)
   at System.Reactive.PlatformServices.SystemClock.Register(LocalScheduler)
   at System.Reactive.Concurrency.ImmediateScheduler.Schedule[...]
   at System.Reactive.Linq.ObservableImpl.ToObservableRecursive`1+_[[System.Int64]]...
   …
-> ×14 called from:
   … Amb … Take
   at MeshWeaver.Messaging.MessageHub.StartQuiesceWait(...)
   at MeshWeaver.Messaging.MessageHub.HandleShutdownCore(...)
LOCK waiting to enter a monitor at System.Reactive.PlatformServices.SystemClock.Register(...)
LOCK ×1 candidate holder: … HashSet`1.Add … SystemClock.Register …
```

That is the `StartWith(-1L)` of the Quiescing wait. It ran once per hub during a burst in which 47
hosted `sync/*` hubs were being disposed. Fourteen of the pool's workers were waiting for one lock
inside Rx, and the hubs they were tearing down reported `THE PUMP IS NOT TURNING`.

Holding the lock in a test showed which other hub paths needed it:

- `MessageHub.ApplyTimeout`'s `StartWith`, which runs on **every** request/response;
- the disposal watchdog's `StartWith`;
- the `Merge` calls in `HostedHubsCollection` (the disposal join and the in-flight creation drain),
  in `HubStepsInFlight.Idle` and in `MessageHub`'s dispose legs and subtree progress.

## The rule

On a path that a hub runs per request, per read, per frame or per teardown, use the operators in
`MeshWeaver.Messaging.InlineRxOperators`:

- `source.StartWithInline(value)` instead of `source.StartWith(value)`. It is
  `Observable.Return(value).Concat(source)`.
- `first.MergeInline(second)` and `sources.MergeInline()` instead of `Merge`. These are
  `Return(first).Concat(Return(second)).Merge()` and `sources.Select(Return).Concat().Merge()`.

They behave the same as the Rx operators. The value is emitted synchronously during `Subscribe`, and the
sources are subscribed synchronously and in order. The ordering was measured to be the same even
inside a `CurrentThreadScheduler` trampoline, where a hub turn runs. They are built only from operators
that take no scheduler, so nothing is registered and no lock is taken.

These sites were migrated with the fix:

- `MessageHub`, `HostedHubsCollection` and `HubStepsInFlight`: every site above;
- the `StartWith` calls in `PermissionEvaluator` and `DeclaredPointRead` (the read gate);
- the `StartWith` calls in `MeshNodeStreamExtensions`, `IoPoolRegistry`, `MeshNodeStreamCache`,
  `MeshService` and `RoutingQuiescence`.

**Not migrated.** About 60 `StartWith` calls in layout areas, settings tabs and GitSync views, and
about 130 `Merge` calls across `MeshWeaver.Hosting`, `MeshWeaver.Mesh.Contract`, `MeshWeaver.Data`,
`MeshWeaver.Graph` and others. Each `Merge` call needs its overload checked: a `Merge` of an
`IObservable<IObservable<T>>` takes no lock, and `Merge(maxConcurrent)` is a different operator. In-mesh
NodeType sources are not covered by any `src/` change.

**Test.** `HubPathsTakeNoProcessWideRxLockTest` holds Rx's `SystemClock` lock on a dedicated thread.
While the lock is held, a request/response round trip and a hub's teardown must both complete; with
the lock held they complete within about 50 ms. The negative control is Rx's own `StartWith`, which
must NOT complete under the same hold. Run against the unfixed hub, the test fails on the round trip:
*"a request/response round trip must not need Rx's process-wide SystemClock lock"*.

## Related

- [Which Kind of Silence](../WhichKindOfSilence) — the `[POOLSTALL]` capture that named this, and how to read its group headers.
- [Removing Hand-Woven Concurrency Gates](../RemovingHandWovenGates) — the volatile-flag park the test uses.
