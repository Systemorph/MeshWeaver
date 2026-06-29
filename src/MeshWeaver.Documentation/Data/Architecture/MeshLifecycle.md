---
Name: Mesh Lifecycle — Build Up & Tear Down
Category: Architecture
Description: The exact, symmetric order for standing a mesh up and taking it down — including the TWO-phase drain (DisposalCompleted + I/O-queue) that every teardown MUST await before the service scope dies. Applies to tests, the Orleans silo, and host shutdown.
Icon: ArrowSync
---

# Mesh Lifecycle: Build Up & Tear Down

A mesh is an actor system wired into a DI (Autofac) **service scope**. Standing it
up and taking it down are mirror images, and the tear-down side has ONE rule that
is easy to get wrong and produces a brutal, run-aborting failure when you do:

> **You may not dispose the service scope until every piece of the mesh's work has
> finished — and "work" is TWO things, not one.**

Skip it and a late continuation resolves a service from the already-disposed scope
and throws
`ObjectDisposedException: …LifetimeScope… has already been disposed`. Unobserved,
xUnit reports it as a **"catastrophic failure"** that aborts the whole test
collection — every later test in the run then times out for reasons unrelated to
its own logic.

---

## Build Up

```csharp
var mesh = new MeshBuilder(/* … */)
    .UseMonolithMesh()                       // or UseOrleansMeshServer() on a silo
    .ConfigureServices(s => s.AddSingleton<MyRepository>())   // mesh-scoped singletons
    .AddGraph()
    .Build();                                // builds the Autofac scope + root hub
```

Two invariants make tear-down tractable, so honor them at build time:

1. **Everything stateful is a mesh-scoped singleton**, registered in `MeshBuilder`
   (`ConfigureServices` / `WithServices`). Its lifetime IS the mesh's — it dies with
   the scope, so there is nothing to `Clear()`. No `static` collections/caches (see
   [NoStaticState.md](/Doc/Architecture/NoStaticState)). The
   [`IoPoolRegistry`](/Doc/Architecture/ControlledIoPooling) is exactly this: a
   mesh-scoped singleton owning every `IIoPool`.
2. **Every async/blocking edge goes through `IIoPool`** (see
   [ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling)). This is what makes the offloaded
   I/O *countable* at tear-down — the registry knows how many operations are in
   flight. Bare `Observable.FromAsync` / `Task.Run` work is invisible to tear-down
   and is exactly the leak that throws `ObjectDisposedException`.

---

## Tear Down — drain ALL THREE phases, THEN dispose the scope

`IMessageHub.Dispose()` is **reactive and returns immediately** — it only *kicks off*
the disposal state machine. Completion is signalled later through
`IMessageHub.DisposalCompleted`. But `DisposalCompleted` covers only the **first** of
three kinds of in-flight work; tear-down must drain all three **before** the service
scope is disposed:

| # | In-flight work | Drained by | Why it's separate |
|---|---|---|---|
| 1 | Hub **action blocks** + in-flight **message round-trips** (`hub.Observe`, `GetMeshNode`, …) | `IMessageHub.DisposalCompleted` | Runs on the hub's single-threaded action block; the disposal state machine waits for the response subjects to drain. |
| 2 | **Offloaded I/O** — anything sent through `IIoPool` (DB, blob, HTTP, compile) | `IoPoolRegistry.WhenDrained(timeout)` | Runs on the **ThreadPool**, independent of the action block. `DisposalCompleted` does **not** know about it. |
| 3 | **Async cleanup** a resource cannot finish inside its synchronous `Dispose()` (flush a write queue, await a stream) | `AsyncDisposeQueue.DrainAsync(quiesce)` | `Dispose()` may not block; resources `Enqueue` their async cleanup onto the queue and a TPL `ActionBlock` drains it. |

The async dispose queue is the key to phase 3: **`Dispose()` is synchronous and must
never block**, but some cleanup is genuinely async. So in their sync `Dispose()`,
resources `Enqueue(ct => …)` their async cleanup onto the mesh-scoped
`AsyncDisposeQueue` instead of running or leaking it. A single-consumer `ActionBlock`
drains it in the background; tear-down gives it a bounded quiesce budget to finish.

> **`DrainAsync` completes the block, it does not wait for a version target.** The
> queue is a message stream — under continuous influx, "wait until the drained version
> reaches N" never converges (endless messages). `DrainAsync` instead `Complete()`s the
> block (stops acceptance) and awaits the remainder, so it is bounded even while
> producers are still posting. The `DrainedVersion` counter (one per item) is the test
> hook: enqueue N, drain, assert it advanced by N.

The canonical helper does all three:

```csharp
// MeshWeaver.Mesh.MeshTeardownExtensions
await mesh.TeardownAsync(timeout);   // Dispose() → DisposalCompleted → IoPool drain → AsyncDisposeQueue drain
// ONLY NOW is it safe to dispose the Autofac scope:
await ((IAsyncDisposable)mesh.ServiceProvider).DisposeAsync();
```

`TeardownAsync`:

1. captures the `IoPoolRegistry` + `AsyncDisposeQueue` **while the scope is still alive**
   (never resolve DI once disposal has begun — see the note in `MessageHub.DisposeImpl`),
2. calls `mesh.Dispose()` (resources enqueue their async cleanup during this reactive disposal),
3. awaits `DisposalCompleted` (phase 1),
4. awaits `IoPoolRegistry.WhenDrained` (phase 2), then
5. after all the sync stuff is disposed, awaits `AsyncDisposeQueue.DrainAsync` (phase 3).

If a caller drives `Dispose()` itself and keeps its own progress/diagnostic loop
around `DisposalCompleted` (the monolith test base does), it uses the wait half
directly — pass the services captured *before* `Dispose()`:

```csharp
var ioPools = mesh.ServiceProvider.GetService<IoPoolRegistry>();        // capture first
var disposeQueue = mesh.ServiceProvider.GetService<AsyncDisposeQueue>();
mesh.Dispose();
await WaitWithProgressAsync(...);                                       // phase 1 (DisposalCompleted)
if (ioPools is not null)
    await ioPools.WhenDrained(timeout).FirstAsync().ToTask();           // phase 2 (I/O queue)
if (disposeQueue is not null)
    await disposeQueue.DrainAsync(timeout);                             // phase 3 (async cleanup)
// THEN dispose the scope
```

Each wait is **bounded** by a timeout. A wait that times out means a real bug — a
wedged action block (surfaced separately by `AnyHubQuiescingTimedOut`), a leaked
I/O slot (a non-zero `IoPoolRegistry.TotalInFlight` after the wait), or a wedged async
cleanup. The timeout keeps tear-down from hanging; it does **not** paper over the leak
— log it and fix the leak, never just widen the timeout (see [the no-band-aids rule](/Doc/Architecture/ControlledIoPooling)).

### The order is the whole point

```
Dispose()  →  DisposalCompleted  →  IoPool drain  →  AsyncDisposeQueue drain  →  dispose scope
 (enqueue async    (action blocks)    (ThreadPool I/O)   (async cleanup)
  cleanup here)  └──────────────── nothing may resolve DI after this ────────────────┘
```

Dispose the scope one step early — before phase 2 or 3 — and any straggler `IIoPool`
continuation or un-run async cleanup that resolves a service hits a dead scope. That is
THE catastrophic `ObjectDisposedException`.

---

## Where this is wired

| Context | Tear-down site |
|---|---|
| Monolith tests | `MonolithMeshTestBase.DisposeAsync` — `WaitWithProgressAsync` (phase 1) + `IoPoolRegistry.WhenDrained` (phase 2) + `AsyncDisposeQueue.DrainAsync` (phase 3) before `base.DisposeAsync` releases the per-`[Fact]` provider. |
| Host shutdown (prod) | Same shape — `TeardownAsync` (or the three phases by hand) before the host disposes the root scope. |
| Orleans test cluster | **Exception — do NOT hand-roll this.** The whole `TestCluster.DisposeAsync` is handed to a background pool (`OrleansClusterDisposal.DisposeInBackground`) because awaiting *any* part of silo shutdown on the xUnit teardown thread deadlocks — silo shutdown drives continuations that the blocked thread owns. So you must **not** manually `Dispose()`/`TeardownAsync` a live silo's root mesh hub at fixture teardown (double-dispose + the same deadlock). The "offloaded work draining against a disposed scope" race on the silo side is absorbed by `OrleansShutdownRaceSuppressor`, not by an inline drain. |

See also: [TestStateIsolation.md](/Doc/Architecture/TestStateIsolation) (per-test disposal + static
seed), [ControlledIoPooling.md](/Doc/Architecture/ControlledIoPooling) (the I/O pool the drain waits
on), [NoStaticState.md](/Doc/Architecture/NoStaticState) (why everything is mesh-scoped).
