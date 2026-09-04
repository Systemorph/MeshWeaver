---
Name: Asynchronous Calls
Description: "Patterns and hard rules for composing async work safely in hub handlers: why await deadlocks, IObservable<T> as the universal return type, the subscribe-all-upfront cell-loading pattern, and when each primitive is correct."
---

# Asynchronous Calls in MeshWeaver

> **For GUI rendering, see [Data Binding](/Doc/GUI/DataBinding) — that is the authoritative pattern.** Layout areas declare bindings; the Blazor view subscribes via `Hub.GetMeshNodeStream(path)` (the shared `IMeshNodeStreamCache` handle). The rules on this page cover hub-handler and service code, where you still need to compose async work safely.

---
<svg viewBox="0 0 760 320" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;" font-family="sans-serif" font-size="13">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#90a4ae"/>
    </marker>
    <marker id="arr-red" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#e53935"/>
    </marker>
    <marker id="arr-green" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="#43a047"/>
    </marker>
  </defs>
  <text x="190" y="22" text-anchor="middle" fill="#e53935" font-weight="bold" font-size="13">❌ await — DEADLOCK</text>
  <text x="570" y="22" text-anchor="middle" fill="#43a047" font-weight="bold" font-size="13">✅ IObservable — Safe</text>
  <line x1="380" y1="10" x2="380" y2="310" stroke="currentColor" stroke-opacity="0.2" stroke-width="1" stroke-dasharray="4,4"/>
  <rect x="20" y="38" width="150" height="40" rx="10" fill="#1e88e5"/>
  <text x="95" y="54" text-anchor="middle" fill="#fff" font-weight="bold">Hub ActionBlock</text>
  <text x="95" y="70" text-anchor="middle" fill="#fff" font-size="11">(single-threaded)</text>
  <rect x="20" y="108" width="150" height="36" rx="10" fill="#5c6bc0"/>
  <text x="95" y="122" text-anchor="middle" fill="#fff">Handler runs</text>
  <text x="95" y="138" text-anchor="middle" fill="#fff" font-size="11">await GetMeshNode(path)</text>
  <line x1="95" y1="78" x2="95" y2="105" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="20" y="168" width="150" height="36" rx="10" fill="#7b1fa2"/>
  <text x="95" y="184" text-anchor="middle" fill="#fff">ActionBlock</text>
  <text x="95" y="200" text-anchor="middle" fill="#fff" font-size="11">BLOCKED — waiting</text>
  <line x1="95" y1="144" x2="95" y2="165" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="20" y="228" width="150" height="36" rx="10" fill="#546e7a"/>
  <text x="95" y="244" text-anchor="middle" fill="#fff">Response arrives</text>
  <text x="95" y="260" text-anchor="middle" fill="#fff" font-size="11">→ queued behind block</text>
  <line x1="95" y1="204" x2="95" y2="225" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="40" y="282" width="110" height="28" rx="8" fill="#b71c1c"/>
  <text x="95" y="301" text-anchor="middle" fill="#fff" font-weight="bold">🔴 DEADLOCK</text>
  <line x1="95" y1="264" x2="95" y2="279" stroke="#e53935" stroke-width="1.5" marker-end="url(#arr-red)"/>
  <rect x="410" y="38" width="150" height="40" rx="10" fill="#1e88e5"/>
  <text x="485" y="54" text-anchor="middle" fill="#fff" font-weight="bold">Hub ActionBlock</text>
  <text x="485" y="70" text-anchor="middle" fill="#fff" font-size="11">(single-threaded)</text>
  <rect x="410" y="108" width="150" height="36" rx="10" fill="#5c6bc0"/>
  <text x="485" y="122" text-anchor="middle" fill="#fff">Handler runs</text>
  <text x="485" y="138" text-anchor="middle" fill="#fff" font-size="11">hub.Observe(...)</text>
  <line x1="485" y1="78" x2="485" y2="105" stroke="#90a4ae" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="410" y="168" width="150" height="36" rx="10" fill="#26a69a"/>
  <text x="485" y="184" text-anchor="middle" fill="#fff">Returns Processed()</text>
  <text x="485" y="200" text-anchor="middle" fill="#fff" font-size="11">ActionBlock FREE</text>
  <line x1="485" y1="144" x2="485" y2="165" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <rect x="590" y="168" width="150" height="36" rx="10" fill="#f57c00"/>
  <text x="665" y="184" text-anchor="middle" fill="#fff">Observable chain</text>
  <text x="665" y="200" text-anchor="middle" fill="#fff" font-size="11">runs concurrently</text>
  <line x1="560" y1="186" x2="593" y2="186" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <rect x="590" y="228" width="150" height="36" rx="10" fill="#43a047"/>
  <text x="665" y="244" text-anchor="middle" fill="#fff">Response arrives</text>
  <text x="665" y="260" text-anchor="middle" fill="#fff" font-size="11">→ onNext fires</text>
  <line x1="665" y1="204" x2="665" y2="225" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
  <rect x="600" y="282" width="130" height="28" rx="8" fill="#1b5e20"/>
  <text x="665" y="301" text-anchor="middle" fill="#fff" font-weight="bold">✅ No deadlock</text>
  <line x1="665" y1="264" x2="665" y2="279" stroke="#43a047" stroke-width="1.5" marker-end="url(#arr-green)"/>
</svg>

*Hub ActionBlock threading: `await` blocks the single-threaded inbox so the response can never be processed — `IObservable` compose-and-subscribe returns immediately and lets the chain continue on a free thread.*

## 🚨🚨🚨 `await hub.GetMeshNode(...)` (or any hub round-trip) IS A 100% DEADLOCK 🚨🚨🚨

Bridging any hub round-trip back to a `Task` and awaiting it deadlocks the mesh hub. This is not a "depends on the scheduler" or "usually safe" rule — it is **100% reproducible** under load, and is the reason the framework moved to `IObservable<T>` end-to-end.

**Forbidden patterns — every line below deadlocks:**

```csharp
// ❌ Direct .ToTask() bridge then await.
var node = await hub.GetMeshNode(path, TimeSpan.FromSeconds(10)).ToTask(ct);

// ❌ .FirstOrDefaultAsync() is just .ToTask() under a different name.
var node = await hub.GetMeshNode(path).FirstOrDefaultAsync();

// ❌ Observable.FromAsync does NOT help.
//    The Func<Task<T>> still re-invokes on each Subscribe and the inner
//    await still bridges back to the calling scheduler.
return Observable.FromAsync(async ct =>
{
    var node = await hub.GetMeshNode(path).ToTask(ct);  // ← still deadlocks
    return Process(node);
});

// ❌ Same deadlock behind a new method boundary.
private async Task<X> Resolve(string path, CancellationToken ct)
{
    var node = await hub.GetMeshNode(path).ToTask(ct);  // ← still deadlocks
    return Process(node);
}
```

**The only correct shape** — compose into the observable chain with `.Select` / `.SelectMany`:

```csharp
// ✅ Composable, no Task surface, no scheduler bridge.
return hub.GetMeshNode(path, TimeSpan.FromSeconds(10))
    .Select(node => Process(node));

// When the next step is itself an observable:
return hub.GetMeshNode(path, TimeSpan.FromSeconds(10))
    .SelectMany(node => DoNextThing(node));    // returns IObservable<...>
```

### Return type must be `IObservable<T>` — never `Task<T>`

Every public method on a service, handler, helper, or extension that participates in mesh work returns `IObservable<T>`. Not `Task<T>`. Not `ValueTask<T>`. Not `async Task<T>`. The instant a public method returns a Task, the next caller will `await` it — and that's the deadlock.

```csharp
// ❌ Task on the public surface invites await at every call site.
public Task<X> ResolveSomethingAsync(...) { ... }

// ✅ IObservable surface forces the caller to compose with .Subscribe / .Select.
public IObservable<X> ResolveSomething(...) { ... }
```

This is a **hard contract**, not a style preference:

- New service methods: `IObservable<T>` only.
- Refactoring an existing `async Task<T>` that is hub-reachable: change the signature; update every caller.
- Don't paper over it with default-interface Task shims, and don't keep a Task overload "for tests": 🚨 **`.ToTask()` is forbidden in tests too** (maintainer, 2026-08-30). A test awaits the observable directly with a `.Timeout(...)`. The production interface stays Task-free.
- Don't introduce `Observable.FromAsync(ct => SomeAsyncMethod(...))` to "make it observable" while the inner method still awaits a hub round-trip — that's the same deadlock with one extra layer of indirection.

🚨 **There is no sanctioned `.ToTask()` boundary — not even at the test edge** (maintainer, 2026-08-30: *"no ToTask ever"*). The mechanism below is the reason: a Task completed inside an Rx pipeline resumes its awaiter INLINE on the signalling thread, still inside the trampoline, and everything the continuation does inherits that flag — which is why this was never a test-only concern. Await the observable directly (`await …FirstAsync().Timeout(…)`); in framework lifecycle hooks (`OnActivateAsync` etc.) subscribe and return `Task.CompletedTask`. The one place a bridge may work is inside an ACTIVITY, where nothing mesh-side runs after the await — and even there, prefer the reactive shape.

**The same rule applies to every hub round-trip primitive:**

- `hub.GetMeshNode(path)` — never awaited, always composed.
- `hub.Observe(request, options)` — never awaited or `.ToTask()`'d in production; subscribe.
- `meshService.QueryAsync(...)` — never inside hub-reachable code; use `meshService.Query(...).Subscribe(...)`.
- `workspace.GetRemoteStream<T, TRef>(addr, ref)` — subscribe; never `.Take(1).ToTask()` to fake a fetch.

---

## 🚨 No bare `Observable.FromAsync` in adapters — bridge I/O only through `IIoPool`

`Observable.FromAsync(asyncFn).SubscribeOn(TaskPoolScheduler.Default)` looks safe but **deadlocks under a blocking subscriber**. `SubscribeOn` only moves the *subscribe* onto the pool; the `await` continuations *inside* `asyncFn` (including each `MoveNextAsync` of an `await foreach`) resume on whatever scheduler the awaited task captured. When the leaf is consumed by a blocking subscriber — a hub/grain `ActionBlock`, or a test's synchronous `Should().Within(...).Match(...)` wait — that continuation can be queued behind the very thread that is blocked waiting for it. This is the recurring **"search / snapshot query hangs"** failure, and it is invisible on the happy path (an unscoped, single-provider query that emits synchronously never exercises the captured continuation; stack a `CombineLatest` fan-out plus a blocking wait on top and it wedges).

**The fix is the `IIoPool` (`MeshWeaver.Mesh.Threading`)** — the single sealed boundary between the turn-based hub schedulers and the genuinely-async I/O leaves. It runs the work behind a concurrency gate with `ConfigureAwait(false)` on every await, so the continuation can never hop back to a captured scheduler. Use the fluent `IoPoolExtensions` helpers, which push the leaf onto the pool eagerly and replay its results through a `ReplaySubject` — a blocking subscriber attaches late and still observes the full result, because the leaf never depends on the subscriber's thread to progress:

```csharp
// ❌ WRONG — deadlocks when a blocking subscriber (ActionBlock / test wait) is downstream.
return Observable.FromAsync(async ct =>
    {
        var rows = new List<Row>();
        await foreach (var r in QueryStream(ct))   // continuation captures the caller's scheduler
            rows.Add(r);
        return rows;
    })
    .SubscribeOn(TaskPoolScheduler.Default);        // moves the SUBSCRIBE, not the await continuation

// ✅ RIGHT — the leaf runs entirely inside the pool; results replay to any subscriber.
private readonly IIoPool _ioPool =
    ioPoolRegistry?.Get(IoPoolNames.FileSystem) ?? IoPool.Unbounded;   // DI-less fallback still pools

return _ioPool.Run(async ct =>
{
    var rows = new List<Row>();
    await foreach (var r in QueryStream(ct).ConfigureAwait(false))
        rows.Add(r);
    return rows;
});

// Streaming leaf (emit each item rather than accumulate):
return _ioPool.RunStream(ct => QueryStream(ct));   // IObservable<Row>, one OnNext per item
```

Obtain a pool with `hub.ServiceProvider.GetService<IoPoolRegistry>()?.Get(IoPoolNames.X) ?? IoPool.Unbounded` (the `Unbounded` fallback still offloads to the ThreadPool with `ConfigureAwait(false)`, so it is never worse than the bare `FromAsync` it replaces). Pool names live on `IoPoolNames` — `FileSystem`, `Blob`, `Http`, `Ai`, `AgentStore`, `Query`, `Layout`, `Routing`, `Compile`, `Process`, plus the per-adapter prefixes `pg:`/`pg-read:`/`sf:`/`sf-read:` (`pg:{adapter}` is capped at 1, so the gate *is* the single Npgsql connection) — and pick the concurrency cap per resource class. Read the XML docs on `IoPoolNames` before picking one: several exist to make nesting acyclic (`AgentStore` is deliberately NOT `Ai`, because a store call runs inside a tool call that already holds an `Ai` slot) or to make a subscribe drainable at teardown (`Query`, `Layout`) rather than to throttle. For idempotent one-shots, cache the eager `pool.Run(...)` observable in an instance `ConcurrentDictionary` (the **promise-cache** — canonical: `PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned`).

**The only sanctioned `Observable.FromAsync` lives inside `IoPool` itself** — it is the one place that owns the gate + `ConfigureAwait(false)` + `SubscribeOn` discipline. Every adapter / provider / service that bridges an `async`/`IAsyncEnumerable` leaf goes through `IIoPool`, never `Observable.FromAsync` directly. This is the same litmus test as rule #9 in the absolute rules below: name the I/O the leaf awaits, run it in the pool, and return `IObservable<T>`.

### Every `Observable.FromAsync` converts — the only one that survives is inside `IoPool`

🚨 **`Observable.FromAsync` is forbidden in `src/`** (see [ControlledIoPooling](/Doc/Architecture/ControlledIoPooling)). There is no "keep it" category — the single sanctioned occurrence is sealed inside `IoPool`. The earlier "Postgres owns a pool, keep `FromAsync`" carve-out is **rescinded**.

| Category | Examples | Action |
|---|---|---|
| **I/O leaf, no pool of its own** | file-system / embedded-resource / content reads, version stores, Azure **blob** I/O, the storage-adapter scope-walk query | **`IIoPool`** (`FileSystem` / `Blob` pool). |
| **I/O leaf with a driver pool** | Postgres / Cosmos adapters & query leaves | **Per-adapter `IIoPool`** — `pg:{adapter}`, cap 1, so the gate *is* the single Npgsql connection ("hook into the pg pool"). The hot query/storage `FromAsync` sites are **migration debt, not a sanctioned pattern**; new code (e.g. `EnsurePartitionProvisioned`) is pooled from day one. |
| **Not an I/O leaf** | message routing, lock acquisition, async view generators, hub handler bridges | Not a leaf — but still never `FromAsync`. Compose reactively or use the framework lifecycle hook. Any surviving `FromAsync` here is legacy to migrate, never a template to copy. |

The litmus test: *name the I/O the leaf awaits.* DB / file / blob / HTTP / compile / process → the matching `IIoPool` (DB → the per-adapter `pg:{adapter}` pool). No nameable I/O (routing, a lock, a view render, a handler hop) → it isn't a leaf; make it reactive without `FromAsync`. Either way **you never type `Observable.FromAsync` yourself** — only `IoPool` does.

### The consumer side — bind whole collections, never `await foreach`

The flip side of the producer rule: code that *consumes* a query/search result binds the **whole collection per emission** off an `IObservable<IReadOnlyCollection<T>>`; it never drives a UI off an `IAsyncEnumerable` / `await foreach` / `Channel`. The portal search box is the canonical example — a `Subject` of typed terms, debounced, `Switch`ed to the latest term's progressive suggestion stream, bound whole:

```csharp
// IMeshService.Query(request) / .Autocomplete(...) return IObservable<IReadOnlyCollection<QueryResult>>:
// every provider stream is seeded .StartWith(empty) + CombineLatest, so the snapshot emits as soon as
// the FIRST source converges (source B), then re-emits B+A re-ordered by score as A returns.
_searchSubscription = _terms
    .Throttle(TimeSpan.FromMilliseconds(250))     // debounce — reactive, not Task.Delay
    .DistinctUntilChanged()
    .Select(term => MeshSearch.Suggestions(_meshService, term, contextPath, MaxResults))
    .Switch()                                     // cancel the previous term's stream
    .Subscribe(list => InvokeAsync(() =>          // bind the WHOLE collection, not per-item inserts
    {
        suggestions = list.ToArray();
        StateHasChanged();
    }));
```

No `SearchHub`, no `Channel`, no `await foreach`, no per-item insertion: one subscription, the whole set rebinds on every progressive emission. The old fire-and-forget `Channel`/`IAsyncEnumerable` streaming search (where the handler's `finally` completed the writer before the pool-scheduled query emitted, dropping every result) is exactly what this replaces.

---

## 🚨 `IEnumerable.ToObservable()` on a read path — the emission that is queued and never runs

**On a storage read or a query walk, never call the parameterless `IEnumerable<T>.ToObservable()`. Use `ToInlineObservable()`** (`MeshWeaver.Mesh.Services.InlineObservableExtensions`).

Rx defaults `ToObservable()` to `SchedulerDefaults.Iteration`, which is `CurrentThreadScheduler` — and that scheduler does **not** mean "run it here, now". It means "run it on *this thread's* Rx trampoline". `CurrentThreadScheduler` keeps a `[ThreadStatic] bool` for "a trampoline is already running on this thread"; while it is set, `Schedule` **only enqueues and returns**, leaving the item for whoever owns that outer trampoline to drain.

**Why an ordinary caller is inside a foreign trampoline — and why it is not a test-only concern.** Rx runs *every* operator subscription through that trampoline (`Producer.SubscribeRaw`), so the flag is set far more often than it looks. Worse, it escapes the pipeline: a `Task` completed from inside an Rx pipeline — exactly what `FirstAsync().ToTask()`, an `AsyncSubject`, or a `TaskCompletionSource` resolved on an `OnNext` does — resumes its awaiter **inline on that thread, still inside the trampoline**. Everything that continuation goes on to do inherits the flag. The captured 558-frame stack from a reproduction reads, bottom-up:

```
MessageService.DrainOne()                       // the hub's own pump, on a ThreadPool thread
 → Producer.SubscribeRaw
   → CurrentThreadScheduler.Schedule            // trampoline OPENED here; the flag is now set
     → … ~500 Rx frames …
       → ToTaskObserver.OnCompleted → TaskCompletionSource.TrySetResult   // a .ToTask() resolves
         → AwaitTaskContinuation.RunOrScheduleAction(allowInlining: true) // awaiter resumes INLINE
           → … the awaiting code, and everything it goes on to call …
```

The trampoline's owner is the **hub message pump**, not anything test-specific.

**The failure it produces has no signature.** If such a frame subscribes a query and then blocks waiting for its first result, the walk it enqueued can only run once the frame returns, and the frame only returns once the walk runs:

```csharp
// ❌ WRONG — the walk is queued on whatever trampoline happens to own this thread.
var nodePaths = level.NodePaths.ToObservable();

// ✅ RIGHT — ImmediateScheduler; no ambient per-thread state, iterates during Subscribe.
var nodePaths = level.NodePaths.ToInlineObservable();
```

**No exception, no completion, no row — forever.** In the portal that is a live children listing (chat token chip, notification bell, folder view) that opens and silently stays empty, while a point read of the same content returns immediately. Issue #2377: the pedestrian scope walk had this shape, and it made `LiveQueryHandoffDropTest` fail ~23% of cold whole-assembly runs on a 4-CPU Linux runner (its 30 s warm-up wait was the block) while every warm or single-test run passed. Under xUnit the reach was wider still — the inlined continuation in that stack was a mesh-teardown `await`, so the runner carried on *on that stack* and started subsequent tests inside the trampoline.

`ImmediateScheduler` carries no such state: it invokes directly, and its recursive form is trampolined through a per-call `AsyncLock`, so a long sequence iterates without growing the stack. `LiveQueryForeignTrampolineTest` pins the contract deterministically — it subscribes from inside a real trampoline and blocks there, which is exactly the shape that used to strand.

> This is *not* a licence to sprinkle `ImmediateScheduler` around. It applies where the code already promises to be synchronous on the subscribing thread: `IStorageAdapter` reads and the pedestrian query walk. Anything that genuinely does I/O still goes through `IIoPool` (above).

---

## 🚨 Waiting for a completion signal: `Subscribe`, never `.ToTask()`

Disposal, teardown, a drain report, a grain deactivation — every "wait for X to finish" in the mesh
is an `IObservable<T>` that fires once and terminates. **Waiting for one means
`signal.Subscribe(onNext, onError)` and nothing else.** No poll, no `SpinWait`, no
`.Wait()`/`.Result`, no `Timeout` operator raced against the signal, and no `Task` gate.

### The reason is DEADLOCK, not a lost exception

**These completions are produced BY THE VERY SCHEDULER the waiter is occupying.** A hub's disposal
finishes when its single-threaded action block drains; a grain's deactivation finishes when its turn
scheduler is free. Wait on one of those from that same scheduler and the thing you are waiting for
can never happen — the Task never settles, and the only thing that ever ends the wait is whatever
timeout was raced against it. This is [Rule 1 of the `/async` skill](/Doc/Architecture/AsynchronousCalls)
applied to lifecycle signals.

🚨 **You do not have to write the block to get it — Rx hands it to you.** `ToTask()` completes its
`TaskCompletionSource` from inside the pipeline **without `RunContinuationsAsynchronously`**, so
`TrySetResult` resumes the awaiter **inline on the signalling thread**. Everything after that
`await` — the rest of your method — then runs on the hub's disposal thread or the grain's turn
scheduler. And it is sticky: with no `SynchronizationContext`, `await` captures
`TaskScheduler.Current`, so **one** inline resumption routes **every later await in that method**
onto the same scheduler.

That is issue #2301. `OrleansGrainTeardownStragglerTest` resumed inline on the deactivating grain's
scheduler and then held it, waiting for that grain's activation to leave the silo catalog — which
needed the scheduler it was holding. It failed at **exactly 30 s**, its `Timeout` budget, every
time, while a healthy activation leaves the catalog in **0.10 s**. A number that is always the
budget rather than a distribution around it is the signature of a deadlock, not of contention.

### The lost fault is the second-order effect

When that timeout finally fires it settles the Task, and a fault still travelling the chain has **no
observer left** — an unobserved exception, surfaced on the finalizer as
`UnobservedTaskException`, which xUnit v3 escalates into a Catastrophic failure that poisons the
*next* test class. That is #2301's `HOST_CRASHED` marker: what the deadlock does on its way out.

`IMessageHub.DisposalCompleted`'s own documentation used to recommend the broken bridge (*"at a
genuine async edge (test teardown, grain deactivation) bridge once with
`DisposalCompleted.FirstOrDefaultAsync()` / `.ToTask()`"*), which is why four attempted fixes all
aimed at the timeout. That sentence is gone.

```csharp
// ❌ WRONG — resumes the caller INLINE on the hub's disposal thread, then holds it; and the
//    Catch DISCARDS a faulted disposal on top.
await hub.DisposalCompleted
    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
    .FirstOrDefaultAsync()
    .ToTask()
    .WaitAsync(timeout);                                            // …racing what it waits for

// ✅ RIGHT — the turn returns immediately and the follow-on work belongs to the signal. The error
//    arm is fluent (.Catch) because a fault arrives LATER and on ANOTHER thread: a try/catch
//    around the Subscribe CALL cannot see it. This is MessageHubGrain.OnDeactivateAsync.
hub.DisposalCompleted
    .Take(1)
    .Catch<Unit, Exception>(ex => { logger.LogError(ex, "… KEEPING its load context"); return Observable.Empty<Unit>(); })
    .Subscribe(_ => UnloadContextIfSafe(reason, grainId));
return Task.CompletedTask;
```

### Where an API genuinely demands a `Task`

`ILifecycleObserver.OnStop`, `IHostedService`, an `async Task` test method. Bridge with
`ReactiveCompletion.ObserveCompletion`, which subscribes, completes its task with
`RunContinuationsAsynchronously` — so the caller is **never** resumed on the signalling scheduler —
and keeps its error arm attached after the task settles. Two rules go with it:

- **Put the bridge at the OUTERMOST edge.** Return the Task to whoever demanded it; never wrap it
  around a wait whose completion needs the thread you are holding. `IoPoolSiloTeardown.OnStop` is
  the reference: it composes and **returns**, blocking nothing.
- **The bound belongs to the edge, not to the signal.** `[Fact(Timeout = …)]`, a host's shutdown
  token, a `CancellationToken` — never a `Timeout` operator spliced into the wait, which settles the
  waiter while the work it bounded is still in flight.
- **A poll is the same defect wearing a loop.** If you are sampling state on an interval because
  there is no completion signal to subscribe to, publish the signal. #2301's test polled
  `IManagementGrain.GetDetailedGrainStatistics()` every 100 ms against a 30 s `Timeout` because
  nothing exposed "this activation is fully gone"; the fix was `GrainDeactivationCompleted`, which
  the grain publishes from Orleans' own `IGrainContext.Deactivated`.

Both properties are pinned by `DisposalWaitBridgeTest` (test/MeshWeaver.Messaging.Hub.Test), whose
control test asserts that `.ToTask()` really does resume inline on the signalling thread.

Related: issue #2488 catalogues the remaining poll / timeout-escape sites.

## 🚨 Cold observables: Subscribe is mandatory

Every method that performs a write or side effect returns a cold `IObservable<T>` — **the side effect runs on `Subscribe`, not on call.** Forgetting to subscribe means the work silently never happens.

```csharp
// ❌ WRONG — fire-and-forget. GetMeshNodeStream().Update(...) is cold;
//   the update only runs on Subscribe. This was the "chat doesn't work in prod"
//   root cause: AppendUserInput discarded the IObservable, so the thread
//   state never changed and the watcher never dispatched.
workspace.GetMeshNodeStream().Update(node => node with { Content = … });

// ✅ RIGHT — subscribe with explicit success / error handlers.
var logger = workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
    ?.CreateLogger("MyComponent");
workspace.GetMeshNodeStream().Update(node => node with { Content = … })
    .Subscribe(
        _ => { /* optional success follow-up */ },
        ex => logger?.LogWarning(ex, "Update failed for {Path}", path));
```

When the next step depends on the commit completing, chain with `SelectMany`:

```csharp
meshService.CreateNode(satelliteCell)
    .SelectMany(_ => workspace.GetMeshNodeStream().Update(node => CommitState(node)))
    .Subscribe(
        committed => hub.Post(new NextStepRequest(committed.Id), …),
        ex => onFailure(ex));
```

### Detecting fire-and-forget at runtime

`workspace.GetMeshNodeStream().Update(...)` returns a `RequireSubscribeObservable<MeshNode>` that logs a warning at GC if `Subscribe` was never called:

> *Fire-and-forget callsite detected: 'MeshNodeStreamHandle.Update(path='…')' returned a cold IObservable that was never subscribed — the side effect did NOT run. Add .Subscribe(_ => { }, ex => logger.LogWarning(ex, ...)) at the callsite.*

Treat that warning as a hard failure. Search the log channel `MeshWeaver.Mesh.RequireSubscribe` after every test or CI run.

### Compile-time signal

The legacy `workspace.UpdateMeshNode(update)` extension is `[Obsolete]` and points to the new API. Any obsolete-warning hit on a build is a missing-Subscribe bug — fix the callsite, don't suppress the warning.

### Subscribe is mandatory on every cold-write surface

- `meshService.CreateNode(node)` / `UpdateNode(node)` / `DeleteNode(path)` — cold; subscribe to commit.
- `meshService.MoveNode(...)` / `meshService.CopyNode(...)` — cold; subscribe.
- `remoteStream.Update(current => updated, ex => …)` — the `ex` callback fires on the stream's hub; the returned `void` IS the subscription.

### …but a subscription that stays PENDING needs an owner

Discarding the `IDisposable` from `stream.Update(...).Subscribe(...)` is fine — those observables complete promptly, and the handle of a *completed* sequence roots nothing (Rx detaches its observer on completion). A **timer** subscription is the opposite case: `Observable.Timer` / `Observable.Interval` / `Task.Delay` park an entry on the process-wide `TimerQueue`, which is a **strong GC root**, so while the timer is pending it holds the tick closure and everything that closure captured — past its owner's disposal.

```csharp
// ❌ The timer's IDisposable goes nowhere — nothing can cancel a flush still pending
//    when the hub tears down, so the closure roots the hub for the whole delay.
Observable.Timer(TimeSpan.FromMilliseconds(100)).Subscribe(_ => Publish());

// ✅ The pending timer is the HUB's: teardown disposes the composite, cancelling it.
pendingFlush.Disposable = Observable.Timer(TimeSpan.FromMilliseconds(100))
    .Subscribe(_ => Publish());          // pendingFlush: a SerialDisposable passed to
                                         // hub.RegisterForDisposal(...) at construction
```

Holding it in a field is **not** the same as owning it — the field has to be disposed by the owner's teardown, on every path. Full treatment, with both leak shapes, the measured truth table of which primitives actually root, and why there is deliberately no analyzer: [Subscription Ownership](/Doc/Architecture/SubscriptionOwnership).

### 🚨 …and a subscription on a path that can FAULT needs an error arm — omitting it kills the process

`Subscribe(onNext)` is not "subscribe and ignore errors". Rx's default `onError` for the one-argument overload is `Stubs.Throw`, which **rethrows the fault on whatever thread carried it** — and that thread is chosen by whatever produced the error, not by you. A timeout raises `OnError` from a `CancellationTokenSource` callback on a **`TimerQueue` thread**, where there is no `catch` anywhere above the frame. The result is an unhandled exception and an aborted process, not a logged error.

```csharp
// ❌ No error arm. A provider timeout here ends the PROCESS on a timer thread.
typeSource.GetStreamUpdates().Subscribe(instances => Apply(instances));

// ✅ Reported, on the thread the fault arrived on, and the process survives.
typeSource.GetStreamUpdates().Subscribe(
    instances => Apply(instances),
    error => Logger.LogError(error, "…{Collection} faulted; it is frozen at its last emission", name));
```

Two corollaries worth stating separately:

- **A `try`/`catch` around the `Subscribe` CALL catches nothing.** The fault travels through the stream and arrives later, on another thread; the `try` block has long since exited. Fault handling is fluent — an error arm, or `.Catch(...)` — never `try`.
- **An error arm is not a swallow.** It must SAY what stopped working. Where another subscriber already owns the verdict (a data source's initialization observing the same `Replay(1)`, which fails the hub's startup), the arm's job is to report and let that path decide — its existence is what stops a fault from taking the host with it.

This is the defect behind [#2468](https://github.com/Systemorph/MeshWeaver/issues/2468): a virtual data source's live-update subscription had no error arm, so a content loader's `GetMeshNode` timeout aborted the CI content gate — which then reported *"failed before it produced a verdict — no check was judged"*. A gate that dies before judging is worse than a gate that fails.

---

## 🚨 `MeshNode.Content` is always typed at the `GetMeshNodeStream` boundary

`MeshNodeStreamHandle.Subscribe` and `MeshNodeStreamHandle.Update` round-trip `node.Content` through the workspace's `JsonSerializerOptions`. The Subscribe path runs a `TypedContentObserver` that deserialises any `JsonElement` to its registered domain type before delivery; the Update path wraps the caller's lambda so the input is already typed and the output is re-serialised before the patch hits the wire.

This eliminates the silent null-fallback corruption class:

```csharp
// ❌ Before — silently lossy when Content arrived as JsonElement.
//    `as MeshThread` returns null, the `?? new MeshThread()` fallback
//    overwrites every other field with defaults — silent data corruption.
workspace.GetMeshNodeStream().Update(node =>
{
    var t = node.Content as MeshThread ?? new MeshThread();
    return node with { Content = t with { PendingUserMessages = pending } };
});

// ✅ After — the framework delivers a typed MeshThread regardless of
//    underlying storage. If Content is genuinely null/wrong-shaped, the
//    cast fails and the lambda returns `node` unchanged — no overwrite.
workspace.GetMeshNodeStream().Update(node =>
{
    if (node.Content is not MeshThread t) return node;
    return node with { Content = t with { PendingUserMessages = pending } };
});
```

Full treatment with the read-side rule and helpers (`EnsureTypedContent`, `EnsureSerialisedContent`): [CqrsAndContentAccess.md → "Content is always typed at the GetMeshNodeStream boundary"](/Doc/Architecture/CqrsAndContentAccess).

---

## 🚨 `No handler found for message type X` is almost always a type-registry mismatch

When a routed `IRequest<T>` comes back as a `DeliveryFailure` saying *"No handler found for message type X"*, the handler usually IS registered via `WithHandler<X>(...)`. The framework's `FinishDelivery` only emits this when the message arrived deserialized as a different CLR type — or as a raw `JsonElement` — because the receiving hub's `ITypeRegistry` is missing the `WithType(typeof(X), nameof(X))` entry that the sender used as the `$type` discriminator.

**Triage in this order — don't skip steps:**

1. Verify `X` is registered on **both** sender and receiver via `config.TypeRegistry.WithType(typeof(X), nameof(X))` — typically through a module-level `AddXxxTypes(this ITypeRegistry)` extension.
2. For Orleans / cross-process: confirm the registration is wired into **both** the silo's mesh/hub config AND any client/portal hub that posts the request.
3. Only after ruling out (1)–(2): suspect an actual missing handler or wrong target address.

It's almost never a missing `WithHandler<X>` line. A message that deserialises into the wrong CLR type can never match the filter `d is IMessageDelivery<X>`.

---

## 🚨 Subscribe callbacks post to the hub — don't do work directly in `Subscribe`

When a long-lived `IObservable<T>` (workspace stream, synced query, change feed) drives action on a hub, the `Subscribe` callback fires on **whatever scheduler the upstream emits on** — often the workspace's emission thread, sometimes a thread-pool task, occasionally the hub's own ActionBlock. Putting non-trivial work in that callback couples it to an unpredictable thread and routinely deadlocks: the callback walks into `workspace.GetQuery(...)` (cold cache → upstream Subscribe), or `meshService.CreateNode(...)` (posts to the mesh hub and waits on the same hub's ActionBlock that is now blocked).

**Rule:** the `Subscribe` callback does ONE thing — `hub.Post(new TriggerMessage(...))`. A registered handler on that hub picks the message off the ActionBlock and runs the logic there. The ActionBlock is the single-threaded actor; serialisation, re-entrancy, and ordering are all handled by the inbox.

```csharp
// ❌ WRONG — fires on workspace emission scheduler, does dispatch in-line.
ownStream
    .Where(node => node.Content is NodeTypeDefinition def
                   && def.CompilationStatus == CompilationStatus.Pending)
    .Subscribe(pendingNode =>
    {
        workspace.GetMeshNodeStream().Update(curr => ... Compiling ...)
            .Subscribe(_ =>
                NodeTypeCompilationActivity.Start(hub, hubPath, logger)
                    .Subscribe(activityPath =>
                        hub.Post(new RunCompileRequest(hubPath, snapshot),
                                 o => o.WithTarget(new Address(activityPath)))));
    });

// ✅ RIGHT — subscription posts a trigger; handler runs on the ActionBlock.
ownStream
    .Where(node => node.Content is NodeTypeDefinition def
                   && def.CompilationStatus == CompilationStatus.Pending)
    .Subscribe(pendingNode =>
        hub.Post(new DispatchCompileTrigger(pendingNode), o => o.WithTarget(hub.Address)));

// Handler is registered on the per-NodeType hub in MeshDataSource:
//   .WithHandler<DispatchCompileTrigger>(NodeTypeCompilationHelpers.HandleDispatchCompile)
// The handler owns the Pending→Compiling transition, activity dispatch, and fallback.
// Running on the ActionBlock, the status check + Update is implicitly atomic.
internal static IMessageDelivery HandleDispatchCompile(
    IMessageHub hub, IMessageDelivery<DispatchCompileTrigger> request)
{
    var workspace = hub.GetWorkspace();
    workspace.GetMeshNodeStream().Update(curr =>
        curr.Content is NodeTypeDefinition def
        && def.CompilationStatus == CompilationStatus.Pending
            ? curr with { Content = def with { CompilationStatus = CompilationStatus.Compiling } }
            : curr)
        .Subscribe(...);
    return request.Processed();
}
```

**Why the workspace emission thread is dangerous:** the workspace's `MeshNodeReference` reducer emits on the thread that applied the change. When that change came from a hub message, it's the hub's ActionBlock; when it came from a remote stream it's the workspace emission scheduler. The callback inherits whichever — and an `Update` chained off it inherits again. Anything downstream that needs a different scheduler starts on a thread pool but its continuation captures the calling context. By the time you've chained three `.Subscribe`s deep, the scheduler graph is impossible to reason about.

`hub.Post` breaks the chain: the post returns immediately, the Subscribe callback completes, and the handler runs on the well-defined ActionBlock thread. Reasoning becomes local again.

**When direct Subscribe work is OK:** read-only display work that emits to a `BehaviorSubject` (no upstream calls), or a closure that simply tears down a disposable on a terminal event. Anything that touches `workspace.GetQuery`, `workspace.GetMeshNodeStream(remotePath)`, `meshService.CreateNode/UpdateNode`, or a `hub.Post` whose response the continuation awaits — move it behind a `hub.Post` + handler.

---

## 🚨 Subscribe-all-upfront cell loading — `Observable.CombineLatest`, never `.Concat()`

Loading N node contents in parallel needs **N hub activations running concurrently**, not one at a time. The shape that gets this wrong is a serial fold:

```csharp
// ❌ WRONG — sequential; total wall-clock = Σ(t_i).
// .Concat() subscribes to stream #1, waits for it to complete (Take(1) + Timeout),
// THEN subscribes to #2, etc. Ten cold cells at 200 ms each = 2 s wall-clock.
var cellLookups = cellIds.Select(id =>
    workspace.GetMeshNodeStream($"{threadPath}/{id}")
        .Take(1)
        .Timeout(TimeSpan.FromSeconds(5))
        .Catch<MeshNode, Exception>(_ => Observable.Empty<MeshNode>()));

return cellLookups.Concat()              // serial fold
    .ToList()
    .Select(cells => Aggregate(cells));
```

```csharp
// ✅ RIGHT — Observable.CombineLatest subscribes to ALL N inputs simultaneously.
// The N per-node hub activations and initial-frame round-trips happen CONCURRENTLY,
// so total wall-clock is ≈ max(t_i) instead of Σ(t_i).
//
// Per-cell Catch returns a sentinel null so CombineLatest still fires when one
// cell times out. Without the sentinel, CombineLatest waits forever (it requires
// at least one emission from every input).
var cellLookups = cellIds.Select(id =>
    workspace.GetMeshNodeStream($"{threadPath}/{id}")
        .Take(1)
        .Timeout(TimeSpan.FromSeconds(5))
        .Select(n => (MeshNode?)n)
        .Catch<MeshNode?, Exception>(_ => Observable.Return<MeshNode?>(null)));

return Observable.CombineLatest(cellLookups)
    .Take(1)
    .Select(cells => Aggregate(cells.Where(c => c != null).Cast<MeshNode>().ToList()));
```

**Why `CombineLatest` and not `Merge`+`Distinct`:** both subscribe to all inputs upfront, so both achieve the parallel-activation goal. `CombineLatest` produces a positional list (cell #i at index i), which is what the aggregator wants. `Merge` produces values in arrival order — you'd then need `Distinct` + `Take(N)` + `ToList` to recover the set. Both work; `CombineLatest` is simpler when each input emits exactly one value.

**Why this smears load over infra:** each `workspace.GetMeshNodeStream(path)` triggers an `IMeshNodeStreamCache` lookup, possibly a permission check, possibly cold-activation of the per-node hub, possibly a database read. Running all N in parallel lets the cache, access pipeline, database, and activation scheduler all be busy at once instead of idle for `(N-1)/N` of the wall-clock.

**Lazy chain, eager subscribe:** building `cellLookups.Select(...)` does NOT subscribe — these are cold observables. Subscription happens when `Observable.CombineLatest(cellLookups)` is itself subscribed. At that moment, all N inputs subscribe simultaneously. Don't touch each input sequentially before handing the collection to `CombineLatest` — that reintroduces the serial pattern.

**Canonical callsites:**

- `ThreadExecution.LoadFullConversationHistoryFromMesh` — N prior-cell loads for the agent's chat history per round.
- `ThreadExecution.LoadPriorUserMessagesFromMesh` — post-restart resume after `AgentChatClient` cache miss.

**When NOT to fan-out at all:** if the consumer only needs a preview (a thumbnail card), don't load every cell to render a 60-char string. Return the synchronous data (title, count, last-modified) from the own node and delegate the preview to a `LayoutAreaControl` — the child hub activates lazily on the Blazor side when the tile becomes visible. Canonical: `ThreadLayoutAreas.Thumbnail` returns title + count immediately and embeds a `LayoutAreaControl(lastCellPath, "Streaming")` for the preview.

---

## Streams are reactive — subscribe, don't snapshot

`ISynchronizationStream<T>` is consumed via `.Select(...)` / `.Where(...)` / `.Subscribe(...)`. The framework's snapshot accessor is `internal` — application code can't see it, so the temptation to `.Current?.Value` doesn't exist. If a sync handler needs a value it can't subscribe for, the handler is wrong: derive it from the request payload, or defer the work to a follow-up message posted from inside `Subscribe`.

---

## One-shot reads compose on `GetMeshNodeStream` — the cache makes them cheap

`workspace.GetMeshNodeStream(path)` is backed by the process-wide `IMeshNodeStreamCache`: **one shared upstream handle per path**. A `.Take(1)` completes *your* subscription; the upstream stays alive for every other reader (and the writer). So a one-shot read is just the same stream, completed after the first useful emission:

```csharp
workspace.GetMeshNodeStream(path)
    .Where(node => node is not null)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(10))
    .Subscribe(node => /* snapshot */, ex => logger.LogWarning(ex, "read failed"));
```

(The old "don't `.Take(1)` a stream" warning applies to **per-call** subscriptions like `GetRemoteStream<MeshNode, …>` — which is exactly why that surface is discouraged for MeshNode reads; see [CQRS](/Doc/Architecture/CqrsAndContentAccess).)

**Decision matrix for reading mesh state:**

| What you need | Primitive |
|---|---|
| **Single node, live** (view re-renders on changes) | `workspace.GetMeshNodeStream(path)` — **stay subscribed** (no `.Take(1)`) |
| **Single node, one-shot** (handler, helper, click) | `workspace.GetMeshNodeStream(path).Where(n => n is not null).Take(1).Timeout(...)` |
| **Set / listing, live** (dashboard, autocomplete) | `meshService.Query<T>(MeshQueryRequest.FromQuery(...))` — emits initial set + deltas |
| **Set / listing, one-shot** (MCP tool, CLI, HTTP endpoint) | `meshService.QueryAsync<T>(...)` — ONLY when the caller exits after the snapshot |

🚨 **Never `.Take(1)` a display stream** — a live-bound view that snapshots freezes on the first emission (rule 8 below). `.Take(1)` is for genuine one-shot reads and read-modify-write chains only.

### When `.Take(N)` is the right primitive: read-modify-write inside `SelectMany`

There is exactly one shape where `.Take(N)` on a workspace stream is correct: a **read-modify-write** chain inside a hub handler. The handler needs the current snapshot once to build a follow-up message; the stream keeps emitting after that, but the handler doesn't care about the rest. `.Take(N)` snapshots N values then completes — pure reactive composition, no `.ToTask()`, no `await`.

```csharp
// Read the current entity, then issue the deletion.
return stream
    .Timeout(TimeSpan.FromSeconds(30))   // bound the wait — handler must not hang
    .Take(1)                             // one snapshot, then complete
    .SelectMany(entityValue =>
    {
        if (entityValue.Value == null)
            return Observable.Return(DeleteUnifiedReferenceResponse.Fail(...));

        var changeRequest = new DataChangeRequest { Deletions = [entityValue.Value], ... };

        // RequestChange issues the write eagerly and REPORTS through its observable:
        // one ActivityLog once every affected stream applied its part, then completes.
        return workspace.RequestChange(changeRequest, null)
            .Select(log => new DataChangeResponse(hub.Version, log).Status == DataChangeStatus.Committed
                ? DeleteUnifiedReferenceResponse.Ok()
                : DeleteUnifiedReferenceResponse.Fail(log.Messages.LastOrDefault()?.Message ?? "Delete failed"));
    });
```

Why this is fine — and why the rules above still apply:

- The stream comes from `workspace.GetStream(entityRef, x => x.ReturnNullWhenNotPresent())`, **not** from `GetRemoteStream` / `GetMeshNodeStream`. Same workspace, no `SubscribeRequest` round-trip — `.Take(1)` is just "next emission".
- `.Take(N)` is composed inside `SelectMany`. The chain is one observable — there is no `await`, no `Task`, no `ToTask`. The handler's `Subscribe(...)` consumes the whole pipeline.
- It's read-modify-**write**. The point of `.Take(1)` is to snapshot input for the next message, not to display anything.

If you find yourself using `.Take(N)` outside a `SelectMany` that immediately produces a follow-up message, go back to the decision matrix above.

---

## 🚨 The absolute rules (no exceptions outside tests)

> These rules are non-negotiable. Treat any violation as a bug, not a style issue.

1. **No `Task<T>` / `async` / `await` in mesh-reachable code.** Public methods on services, handlers, layout areas, and click actions return `IObservable<T>` (or `void`). An `async Task` method that awaits a hub operation deadlocks the hub ActionBlock.

2. **No `*Async` extension shims on `IMeshService`.** Use `meshService.CreateNode(node)` / `UpdateNode(node)` / `DeleteNode(path)` — these return `IObservable<MeshNode>`. Never use `.CreateNodeAsync(...)` / `.UpdateNodeAsync(...)` / `.DeleteNodeAsync(...)` — those extensions bridge to Task via a `TaskCompletionSource` and deadlock every time they are reached from a hub handler.

   🚨 **They are still on the shipped contract, and the reason is worth knowing before you try to delete them.** `MeshServiceExtensions`' own doc comment claims "~180 existing callers"; measured 2026-08-27 that is **zero** in `src/`, `samples/`, `memex/` and `content/` — every remaining caller in THIS repo is a test. (Measured twice, by different means: a tree grep here, and an org-wide GitHub code search, which is what reaches the satellite repos AND their in-mesh source.) What blocks the deletion is a repo no compiler here can see: **`MeshWeaver.Reinsurance` has 58 call sites across 22 in-mesh `Source/*.cs` files** (`UWDeepfield/TreatySubmission`, `UwPortfolio`, `CatScenario`, `UWDeepfieldHome`, `UwLossWatch`). In-mesh source compiles at RUNTIME in the portal, so deleting the shim would turn 22 NodeTypes `CompileError`, and a `CompileError` NodeType refuses portal readiness — see [NodeTypeCompilation](../NodeTypeCompilation).

   So this is a two-step, in this order (tracked as Reinsurance issue #102): **port those 58 sites to `CreateNode(...).Subscribe(...)`** — which is a real fix, not paperwork, because every one of them is an `await` on hub-reachable layout-area code — **then** move the shim into `MeshWeaver.Fixture` beside the test-only `QueryAsync` bridge. Until then `MeshServiceHasNoTaskShimGuard` (test/MeshWeaver.Documentation.Test) ratchets it: the three methods may not gain a fourth, and no second assembly may add one.

3. **`hub.Observe(...)` is the ONLY request/response primitive.** There is no Task-returning request/response API left on `IMessageHub` — `RegisterCallback` and `AwaitResponse` were **deleted**, not deprecated, so code that names them no longer compiles. `hub.Observe(request, options?)` returns `IObservable<IMessageDelivery<TResponse>>` backed by an `AsyncSubject`; `DeliveryFailure` flows via `OnError` as a `DeliveryFailureException`. No `TaskCompletionSource`, no callback registry, no Task-await deadlock surface.

   **🚨 3a. ALWAYS the pre-registering `hub.Observe(request, options)` — NEVER `hub.Post(...)` then `hub.Observe(delivery)`.** The two are not interchangeable. `Observe(request, options)` registers the response `AsyncSubject` **before** posting; `Observe(delivery)` registers it **after**, and `HandleCallbacks` **DROPS** any response whose correlation id has no registered subject yet ("No subject found for response … treating as processed"). A reply that lands in that window is consumed and gone — the caller's callback then sits pending forever (surfacing as a hang to its timeout, or as a leaked callback at teardown).

   The window is real, not theoretical: `PostImplGeneric` runs `ScheduleNotify` **synchronously**, so by the time `Post` returns the delivery is already enqueued and its turn scheduled onto `turnScheduler` on another thread. Preempt the posting thread — routine under CI thread-pool contention — and the handler answers first. A warm owner answers in sub-millisecond time.

   `Observe(delivery)` is safe ONLY when the response provably cannot have arrived yet, and "the delivery is already posted" is *not* that proof. If you already hold a posted delivery, that is the smell — restructure so the request is handed to `Observe(request, options)` instead. Prior occurrences: `MeshOperations` (four verbs), `MeshNodeStreamExtensions.GetMeshNode`, `HubStreamProviderFactory`, and `CreateOrUpdateNodeRequest`'s inner create (#981). Pinned by `WorkspaceCacheEvictionTest.GetMeshNode_WarmOwner_DropsResponse_WhenSubjectRegisteredAfterPost` and `UpsertInnerCreateObservationTest` — both built as two arms that are each other's [negative control](/Doc/Architecture/NegativeControls).

   A dropped response is one of the **two** ways a caller waits forever. The other is an upstream that completes without emitting, which no `.Timeout` catches — see [Silent Completion](/Doc/Architecture/SilentCompletion).

4. **Never `.QueryAsync<MeshNode>($"path:X").FirstOrDefaultAsync()` to read a known node.** Queries go through a lagged read-side index. For a known path: live = `workspace.GetMeshNodeStream(path).Subscribe(...)`; one-shot = the same stream with `.Where(n => n is not null).Take(1).Timeout(...)` (or the `hub.GetMeshNode(path, timeout?)` convenience, which wraps it null-on-absent).

5. **Never wrap a Task-returning query in `Observable.FromAsync(() => query.QueryAsync(...).FirstOrDefaultAsync().AsTask())`.** This is fake-reactive — it runs through the lagged index and returns stale content.

6. **🚨 Never bridge a request/response round-trip back through a `Task`.** `Observable.FromAsync(() => SomethingThatAwaitsAHubReply())` re-introduces the deadlock the observable surface removed: the continuation captures the calling sync-context. `hub.Observe(...)` already *is* the observable — subscribe to it, never wrap it.

7. **`ISynchronizationStream<T>.Update` callbacks must be synchronous.** Don't use the `Func<T?, CancellationToken, Task<ChangeItem<T>?>>` overload from hub-reachable code — it hides an `await` inside the stream update.

8. **🚨 NO `.Take(1)` on display streams.** A `.Take(1)` snapshots and unsubscribes — the view freezes on the first emission and **stops updating**. For display, stay subscribed. The only `.Take(1)` that is ever right is a one-shot read in a read-modify-write chain (see above).

9. **🚨 The async boundary lives at the real I/O edge — defer it as deep as possible.** `async` / `await` / `IAsyncEnumerable` bridge across a *genuine* I/O wait (Postgres, file-system, network). In-memory work is never async: anything that only touches in-process state projects synchronously and lifts to `IObservable<T>` via `IEnumerable<T>.ToObservable()`. An `async IAsyncEnumerable` method that never awaits I/O is a bug. Only the leaf that actually performs I/O is allowed to be async, and it bridges back to the observable contract at one sealed point (`Observable.Create` + `await foreach`), pooling at that edge via the shared **`IIoPool`** governor (`MeshWeaver.Mesh.Threading`). Litmus test: before writing `async`, name the I/O it awaits — if you can't, delete it and return `IObservable<T>`. Full treatment: [Aggregating Providers → "The async boundary lives at the I/O edge"](/Doc/Architecture/AggregatingProviders).

10. **🚨 No hand-woven async/concurrency primitives — the actor model does NOT tolerate `SemaphoreSlim`.** A `SemaphoreSlim` / hand-rolled async gate / lock-for-async / `TaskCompletionSource`-as-a-signal / `ManualResetEventSlim` / `Task.Delay`-timeout-race anywhere in `src/` **or `test/`** is FORBIDDEN, outside the one sealed inside `IoPool`. `WaitAsync()` blocks/parks a thread and its continuation captures the awaiting scheduler — on a hub it parks the single-threaded action block (or grain turn) so the message you're waiting on can never be processed → **deadlock** (the lock-shaped twin of rule 1). Serialization ("one at a time") is what the hub action block gives for free — channel through it: `Subject<T>` + `.Select(Run).Concat().Subscribe(...)` (canonical: `KernelExecutor`'s REPL queue, which *replaced* a `SemaphoreSlim`; same shape: `RoutingServiceBase.ActivationSerializer`), or `GetMeshNodeStream(path).Update(...)`. Concurrency bound / one-shot init / connect handshake → `IIoPool` + the promise-cache (`pool.Run(...)` in an instance `ConcurrentDictionary`), never a `SemaphoreSlim(1,1)` `_initLock`/`_connectGate`. The ONLY sanctioned `SemaphoreSlim` is sealed inside `IoPool` (the off-hub I/O boundary). **In a TEST** the same primitive strands a deliberately blocked worker whenever an assertion throws before the release runs, so `test/` is held at zero too, with no allow file: a producer→test signal is an `AsyncSubject<Unit>` awaited through the assertion helpers, and a release INTO a worker the test parks on purpose is a volatile flag polled under a bounded `SpinWait.SpinUntil`, written in a `finally`. Full treatment: [ControlledIoPooling](/Doc/Architecture/ControlledIoPooling) · [Removing Hand-Woven Concurrency Gates](/Doc/Architecture/RemovingHandWovenGates).

---

## 🚨🚨🚨 NEVER USE `QueryAsync` TO OBTAIN A `MeshNode` 🚨🚨🚨

`IMeshService.QueryAsync` is for searching and listing — it runs through a **lagged, eventually-consistent read-side index** that can return stale content right after a write. It is **never** the right tool for reading the current committed state of a specific node.

### ❌ Wrong — every line below is a bug

```csharp
// ❌ Lagged index — stale after writes.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();

// ❌ Same bug wrapped in Observable.FromAsync to look reactive.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ❌ Even with a path: filter, this is still a query. Still lagged.
await foreach (var n in mesh.QueryAsync<MeshNode>($"path:{path}")) { node = n; break; }

// ❌ Snapshot may be null before first emission.
var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;
```

### ✅ Right — the one way to obtain a known MeshNode

```csharp
// Direct subscription to the owning hub via the shared per-path handle.
// Authoritative, live, no staleness, no query index involved.
var workspace = hub.GetWorkspace();
return workspace.GetMeshNodeStream(path)
    .Where(node => node is not null)            // skip pre-init frame
    .Take(1)                                    // one emission then complete
    .Timeout(TimeSpan.FromSeconds(10));         // bound the wait
```

This is also how you **wait for work to finish** — subscribe until a field in the node's content flips to a completion state, then `.Take(1)`. No polling loop. No repeated queries.

### Sets / listings — prefer `Query`, not `QueryAsync`

Even when a query is the right idea (listings, filters, existence checks), **do not `await` the `IAsyncEnumerable<T>`** version — use `IMeshService.Query<T>`. It returns `IObservable<QueryResultChange<T>>` with an initial full set and then incremental deltas, composing with `Select` / `Where` / `Subscribe` like every other mesh observable.

> **`IMeshService.Query` inside a layout-area render is now SAFE — the framework subscribes every render off the owning hub's action block.** It was once the canonical query-in-render deadlock (see [Query-in-render is safe](#-query-in-render-is-safe--the-framework-subscribes-every-render-off-the-hub-turn) below and [OrleansTaskScheduler](/Doc/Architecture/OrleansTaskScheduler)); the render pipeline now hops the subscribe onto `TaskPoolScheduler.Default`, so a generator that composes `IMeshService.Query(...).Select(...)` no longer wedges the grain. For *displaying* a single node's values a binding is still cleaner — declare it and let the Blazor view subscribe (see [Data Binding](/Doc/GUI/DataBinding)) — but query-in-render is no longer a trap.

**`QueryAsync` breaks the update flow.** It is a one-shot snapshot — the view freezes. If a row is added, removed, or mutated, the list doesn't change. `Query` emits the initial set plus a delta for every subsequent change, so the downstream chain stays live.

```csharp
// ❌ WRONG — IAsyncEnumerable + await — hub ActionBlock blocks on query pump.
var items = await meshService.QueryAsync<MeshNode>("nodeType:Post").ToListAsync();

// ✅ RIGHT — reactive, live, auto-updates on mesh changes.
meshService.Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Post"))
    .Select(change => change.Result)
    .Subscribe(nodes => { /* render, react */ });
```

**Valid uses of `Query`:**

- Listing children of a namespace (`path/*`)
- Filtering by predicate across the mesh (`nodeType:X`, `name:*sales*`)
- Existence tests across the mesh
- Autocomplete / browsing / search UIs
- Layout areas that render a list and want live updates

### The one case where `QueryAsync` is correct: one-shot lookups that exit the process

`QueryAsync` is correct for request/response call sites that return **once** and then the caller is gone:

- **MCP tool handlers** — tool returns a payload and the session ends.
- **Export / import CLI services** — pull-and-leave jobs that dump to disk.
- **HTTP endpoints that render once and close** — e.g. a CSV download.

Rule of thumb: **if any downstream code re-renders or re-computes when data changes, you need `Query`.** `QueryAsync` is only safe when the caller serialises the snapshot and walks away.

---

## 🚨 Query-in-render is SAFE — the framework subscribes every render OFF the hub turn

> **This is the fix for the deadlock that took down multiple production meshes.** A layout area may
> now do `IMeshService.Query(...)` (or any mesh round-trip — `hub.Observe`, `GetRemoteStream`, a
> workspace query) *directly in its render*, and it will render instead of wedging the hub. The
> safety is in the **portal/framework binary**, not in node source — so **already-deployed nodes
> whose cached assemblies won't recompile become safe automatically.**

### The trap it closes

A layout area's view generator returns an `IObservable<UiControl?>`. The framework's render pipeline
(`LayoutAreaHost.BuildInitialization`) **subscribes** to it. Before the fix that subscribe ran on the
layout-area's own synchronisation-stream hub **action block** — a single-threaded actor
(`MaxDegreeOfParallelism = 1`), and for a node hosted as an Orleans grain, grain-affined (the ROOT
GRAIN HUB runs on the grain scheduler; see [OrleansTaskScheduler](/Doc/Architecture/OrleansTaskScheduler)).
So the generator body — and the subscribe to whatever observable it returned — ran **on the hub
turn**. A generator that did `IMeshService.Query(...)` opened that subscription while the turn was
held; the query must route through Orleans and come back to **this** hub, but the turn could not
advance to process the response → **the hub DEADLOCKED**. On startup prerender many grains blocked at
once → thread-pool starvation → the whole silo wedged (even `/healthz`). Confirmed offenders:
`Doc/DataMesh/SocialMedia/Post` (List area) and `Doc/DataMesh/PythonPandasNode/PandasExplorer` — each
queried in-render.

### The fix — one reactive seam, `SubscribeOn(TaskPoolScheduler.Default)`

`LayoutAreaHost` now hops the render subscription (top-level AND every nested container / dialog /
editor sub-area) onto the thread pool with `.SubscribeOn(System.Reactive.Concurrency.TaskPoolScheduler.Default)`
— the framework's **own designated reactive off-hub move**, the same one `MeshQuery.Query` and
`IMeshNodeStreamCache.GetQuery` already make (see
[OrleansTaskScheduler → "SubscribeOn inside a grain-hosted service"](/Doc/Architecture/OrleansTaskScheduler)).
The generator, and every observable it subscribes in-render, now runs **OFF the hub turn** — which is
immediately free to route and answer the round-trip. **100% reactive: a pure Rx scheduler operator,
no `async` / `await` / `Task`.**

**Why it can't reintroduce ordering bugs.** The render *output* never touches the hub directly —
`PushRenderResult` / `UpdateArea` call `Stream.Update(...)`, which posts an `UpdateStreamRequest` to
the hub's action block (`hub.Post` is the actor inbox: safe from any thread, re-serialised in order).
So an emission arriving on a pool thread is re-marshalled onto the owning hub's single-threaded turn
exactly as before — the offload moves only the **subscribe-time** work off the hub, never the state
writes. Data-before-control ordering (the queued `UpdateData` → control sequence) is preserved.

### What this does NOT license

Rendering code should still be **reactive and side-effect-free**: compose with `.Select` / `.Where` /
`.SelectMany`, return the `IObservable<UiControl?>`, never bridge a hub round-trip back to a blocking
`Task` (`.Result` / `.Wait()` / `.ToTask()` + wait). The framework subscribes you off the hub turn;
it does not make a **blocking** subscribe safe — a generator that *blocks its subscribing thread* on a
round-trip still burns a pool thread (and under enough concurrency, the pool). Stay reactive. The rule
that changed is narrow and important: **a normal reactive `IMeshService.Query` composed into the
render no longer deadlocks the hub.**

---

## The T-Shirt Analogy

When you order a t-shirt online, you don't stand at the mailbox waiting for it to arrive. Your life continues, and you deal with it when it shows up.

**Truly async (MeshWeaver pattern):**

```csharp
// Post + observe in one go — emits exactly one IMessageDelivery<MyResponse>.
// DeliveryFailure / Timeout flow through onError; no callback ever silently no-ops.
hub.Observe(new MyRequest(), o => o.WithTarget(address))
    .Subscribe(
        resp => { /* handle resp.Message — your "mailbox notification" */ },
        ex   => { /* DeliveryFailureException, TimeoutException, etc. */ });

// Your code continues immediately — no blocking
return delivery.Processed();
```

**Fake async (C# async/await — DO NOT do this in production):**

```csharp
// You ARE standing at the mailbox — deadlocks the hub ActionBlock.
var response = await hub.Observe<MyResponse>(request).FirstAsync().ToTask();
```

(The framework no longer offers a Task-returning request/response method at all — the only way
to write this bug today is to bridge the observable back to a Task yourself, as above.)

In tests, `await MonolithMeshTestBase.AwaitResponseAsync(request, ...)` is the sanctioned helper — and 🚨 it must NOT bridge through `.ToTask()`: it awaits `hub.Observe(...).FirstAsync()` directly under the test's timeout.

---

## Why `await` Deadlocks in Hub Handlers

The message hub processes messages sequentially through a single-threaded `ActionBlock`. When a handler calls `await`, it blocks the ActionBlock waiting for a response. That response is itself a message that needs to be processed by the same ActionBlock — which is blocked. **Deadlock.**

```
Handler runs on ActionBlock
    → await <anything that needs a reply>
        → ActionBlock is blocked waiting
            → Response message arrives
                → Cannot be processed — ActionBlock is busy
                    → DEADLOCK
```

This applies to all of the following:

- `await hub.Observe(...).FirstAsync().ToTask()` — blocks the hub on its own reply
- `await someTask` — blocks the hub scheduler
- `hub.InvokeAsync(...)` — schedules work on the blocked scheduler
- `workspace.GetStream().Subscribe(...)` — if the stream observes on the hub scheduler, the emission is queued behind the blocked handler

---

## The Observable Pattern

Use `IMeshService` to enter reactive / observable contexts. Observables are inherently truly async — you subscribe and get notified when data is available.

### Creating Nodes (Non-Blocking)

State updates go in the **handler body** (runs on the grain scheduler), not in the Subscribe callback:

```csharp
// Fire-and-forget — no callback needed for state updates
meshService.CreateNode(new MeshNode(id, namespace)
{
    NodeType = "MyType",
    Content = new MyContent { ... }
}).Subscribe(
    _ => logger.LogInformation("Node created"),
    error => logger.LogError(error, "Node creation failed"));

// State update in the handler body (grain scheduler) — safe
hub.GetWorkspace().GetMeshNodeStream().Update(node => node with
{
    Content = content with { Messages = content.Messages.Add(id) }
}).Subscribe(_ => { }, ex => logger.LogWarning(ex, "Update failed"));

return delivery.Processed();
```

### Never do state updates in Subscribe callbacks

Subscribe callbacks run on **arbitrary threads**. Direct workspace mutation requires the hub's scheduler. Mixing these causes deadlocks — this is not framework-specific; you don't control which thread a callback runs on.

```csharp
// WRONG — callback runs on unknown thread, direct mutation needs hub scheduler:
meshService.CreateNode(node).Subscribe(_ =>
{
    /* direct workspace mutation here */ // ← deadlock: wrong thread
});

// CORRECT — separate concerns: fire-and-forget for I/O, state update composed
// (GetMeshNodeStream().Update is itself a cold observable — chain, don't nest):
meshService.CreateNode(node)
    .SelectMany(_ => workspace.GetMeshNodeStream().Update(n => ...))
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "create+update failed"));
```

The principle: **I/O is fire-and-forget; state changes happen where you control the thread.**

---

## UI Click Actions — Same Rules Apply

Blazor button clicks flow through the layout area host, which is backed by a message hub. The same rule applies: **no `await` on mesh-backed operations inside the click handler.**

### The canonical reactive click handler

```csharp
.WithClickAction(ctx =>
{
    // 1. Immediate optimistic feedback.
    ctx.Host.UpdateData(resultId, "<p>Working…</p>");

    // 2. Read form data via Subscribe (NOT await FirstAsync).
    ctx.Host.Stream.GetDataStream<Dictionary<string, object?>>(formId)
        .Take(1)
        .Subscribe(data =>
        {
            var label = data?.GetValueOrDefault("label")?.ToString() ?? "";
            if (string.IsNullOrEmpty(label))
            {
                ctx.Host.UpdateData(resultId, "<p>Please enter a label.</p>");
                return;
            }

            // 3. Call reactive services — IObservable<T>, not Task<T>.
            myService.DoWork(label).Subscribe(
                result => ctx.Host.UpdateData(resultId, $"<p>Done: {result}</p>"),
                ex     => ctx.Host.UpdateData(resultId, $"<p>Error: {ex.Message}</p>"));
        });

    return Task.CompletedTask;  // click handler itself is synchronous
})
```

### Writing reactive services

Expose `IObservable<T>` from any service called from a click handler or hub handler. Compose with `SelectMany`, `Select`, `FirstOrDefaultAsync` (the Rx operator, not the `IAsyncEnumerable` extension).

```csharp
public IObservable<TokenResult> CreateToken(string label)
{
    var userNode = new MeshNode(...);
    return nodeFactory.CreateNode(userNode)                  // IObservable<MeshNode>
        .SelectMany(created =>
        {
            var indexNode = new MeshNode(...);
            return nodeFactory.CreateNode(indexNode)
                .Select(_ => new TokenResult(rawToken, created));
        });
    // No await. Consumer calls .Subscribe(onNext, onError).
}

// ❌ WRONG — fake-reactive wrapper over the lagged read-side index.
// ✅ CORRECT — read committed content directly from the owning hub:
public IObservable<bool> DeleteToken(string path)
{
    var workspace = hub.GetWorkspace();
    return workspace.GetMeshNodeStream(path)
        .Take(1)
        .Timeout(TimeSpan.FromSeconds(10))
        .SelectMany(node => node is null
            ? Observable.Return(false)
            : nodeFactory.DeleteNode(path));
}
```

See *[CQRS — Queries vs. Content Access](/Doc/Architecture/CqrsAndContentAccess)* for the full rule.

### Static handlers compose — don't wrap them in a service for "DI cleanliness"

Hub request handlers that **just compose I/O** (read inputs, fan out, render, post the response) belong in a **static class with private static helpers**. Do not extract them into an `IFooService` + instance class just because the body grew:

- The hub already carries the service provider (`hub.ServiceProvider.GetRequiredService<T>()`).
- DI services exist to hold **state**. A static handler holds none.
- Adding an interface forces every call site through DI registration, creates a "boundary" with no observable difference, and increases the surface tests have to mock around.

```csharp
public static class ExportDocumentHandler
{
    public static MessageHubConfiguration AddExportDocumentHandler(this MessageHubConfiguration config) =>
        config.WithHandler<ExportDocumentRequest>(Handle);

    private static IMessageDelivery Handle(
        IMessageHub hub, IMessageDelivery<ExportDocumentRequest> delivery)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var brandingResolver = hub.ServiceProvider.GetRequiredService<BrandingResolver>();
        var request = delivery.Message;

        hub.GetMeshNode(request.SourcePath, TimeSpan.FromSeconds(15))
            .SelectMany(root => brandingResolver.Resolve(request.Options.BrandNodePath)
                .Zip(CollectChapters(meshService, request, root),
                     (branding, chapters) => Render(request, root, chapters, branding)))
            .Subscribe(
                bytes => hub.Post(new ExportDocumentResponse(...), o => o.ResponseFor(delivery)),
                ex    => hub.Post(new ExportDocumentResponse(..., Error: ex.Message), o => o.ResponseFor(delivery)));

        return delivery.Processed();
    }

    private static IObservable<List<(string, string)>> CollectChapters(...) { /* ... */ }
    private static byte[] Render(...) { /* ... */ }
}
```

When scripts want to reuse the same building blocks, they call the **public renderer types directly** — they don't need a service interface. Reach for an instance service only when there's actual state to hold (a cache, a per-circuit context, a plugin registry).

### Anti-patterns in click handlers

```csharp
// ❌ async click handler with await — deadlocks under load.
.WithClickAction(async ctx =>
{
    var data = await ctx.Host.Stream.GetDataStream<T>(id).FirstAsync();
    var result = await myService.DoWorkAsync(data);
    ctx.Host.UpdateData(resultId, result);
})

// ❌ Task.Run as a "fix" — AccessContext doesn't flow, exceptions vanish.
.WithClickAction(ctx =>
{
    _ = Task.Run(async () => { await myService.DoWorkAsync(); });
    return Task.CompletedTask;
})
```

---

## Post + Observe Pattern

For request-response flows where you need the result but can't block:

```csharp
// One call: posts the request and returns IObservable<IMessageDelivery<TResponse>>.
// onError fires for DeliveryFailureException / TimeoutException — never silent.
hub.Observe(new CreateNodeRequest(node), o => o.WithTarget(address))
    .Subscribe(
        resp => DoSomething(resp.Message),
        ex   => logger.LogWarning(ex, "CreateNode failed"));

return delivery.Processed();
```

**How it works:** `Observe` stores an `AsyncSubject<IMessageDelivery>` under the request's correlation id and returns it; `HandleCallbacks` — the first rule in the hub's chain — looks the id up when the response arrives and pushes onto that subject (`subject.OnError(new DeliveryFailureException(failure))` for a `DeliveryFailure`, `OnNext` + `OnCompleted` otherwise). No Task, no `TaskCompletionSource`, no callback registry: a failure cannot be delivered to a Task nobody awaits, which is what used to turn a routing failure into a silent infinite hang.

### 🚨 Never post first and observe afterwards

```csharp
// ❌ WRONG — the response subject is registered AFTER the post. HandleCallbacks DROPS a
//    response whose id has no subject yet, so a reply that lands in this window is gone
//    and the callback hangs forever. Post returns only after ScheduleNotify has already
//    enqueued the delivery and scheduled its turn on another thread — the handler can and
//    does win this race under load.
var delivery = hub.Post(request, o => ...);
hub.Observe(delivery).Subscribe(onNext, onError);

// ✅ RIGHT — one call; the AsyncSubject is registered BEFORE the post, so however early the
//    reply lands it is buffered and replayed to the subscriber.
hub.Observe(request, o => ...).Subscribe(onNext, onError);
```

See rule 3a. If some intermediate code needs the delivery id up front, pass an explicit
`o.WithMessageId(id)` through the same `Observe(request, options)` call — do not split it
into a post and a later observe.

### Inside an `IObservable<T>` chain

```csharp
public IObservable<TResult> DoOperation(...)
{
    return hub.Observe(new MyRequest(...))
        .SelectMany(resp => hub.Observe(new SecondRequest(resp.Message.X)))
        .Select(secondResp => Project(secondResp.Message));
    // Neither Post fires until the caller subscribes.
}
```

---

## 🚨 Node mutations land on the owning hub — `stream.Update` routes there for you

A MeshNode's authoritative copy lives in its **owning per-node hub**'s workspace (loaded by
`MeshDataSource` at init via `MeshNodeReference`); the mesh hub does not hold it. A content
mutation must reach that owning hub — but you do **not** write a forwarding handler for it.
The canonical write routes there automatically:

```csharp
// Any hub — external OR own path. The cache routes to the owner; no hand-written forward.
workspace.GetMeshNodeStream(path)
    .Update(current => current with { Content = … })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "Update failed for {Path}", path));
```

- `path == this hub's address` ⇒ writes through the local data source (`UpdateOwn`).
- `path != this hub's address` ⇒ the process-wide `IMeshNodeStreamCache` opens a sync
  subscription to the owner and posts an RFC 7396 JSON-merge `PatchDataRequest`. The owner
  enforces `Permission.Update` via the `[RequiresPermission(Update)]` pipeline (a denial posts
  a `DeliveryFailure(Unauthorized)`), merges the diff against its OWN current state, **stamps
  auditing** (`LastModified`/`LastModifiedBy`), **persists durably**, and acks. `UpdateRemote`
  drives the caller's terminal emission off that owner response, so a subsequent read-after-write
  sees the commit. An RLS denial surfaces as `UnauthorizedAccessException`; a
  deserialization/validation rejection surfaces as `MeshNodeStreamException`. 🚨 **The terminal is
  the owner's verdict and nothing weaker (#2661)** — a bound expiring is not a commit, so a busy
  owner makes the caller *wait* rather than be told "saved" optimistically; a verdict arriving after
  the short response bound is delivered through `LatePatchResponseRegistry` (which watches
  `DeliveryFailure` as well as `PatchDataResponse`), and an owner that produces no terminal at all
  within `LateResponseWatchBound` faults the write as unconfirmed. (App-integrity `INodeValidator`s for Update — version, name — run
  client-side in `IMeshService.UpdateNode`; the owner-enforced RLS/partition validators are
  marked `IOwnerEnforcedNodeValidator` and skipped there.)

> **Retired:** the `UpdateNodeRequest` "forward at the mesh hub, relay the `UpdateNodeResponse`"
> pattern is gone (the forwarded request timed out in distributed deployments when the per-node
> hub didn't respond within ~30 s). `stream.Update` and the cache own the routing now.
> `MoveNodeRequest`/`CreateNodeRequest`/`DeleteNodeRequest` remain node-**lifecycle** requests.

**Why:** the mesh hub workspace doesn't carry a MeshNode collection — it has no `MeshNodeReference` reducer, no per-node validation context, no version tracking. Trying to read existing state via `workspace.GetMeshNodeStream()` on the mesh hub throws `Failed to create stream`. Forwarding lets routing activate the owning hub on demand; that hub's `MeshDataSource` init loads the node from persistence, and the handler runs locally with `GetMeshNodeStream()` (own).

---

## 🚨 Blazor / GUI rule — no `await` ever, stay in observables

> **Full treatment in [Blazor Async — `Subscribe`, not `await`](/Doc/Architecture/BlazorAsync).** That article is the practical playbook: lifecycle hooks, click handlers, parallel queries, multi-step flows, and the channel bridge for IAsyncEnumerable-shaped APIs. Read it before touching any `.razor` / `.razor.cs` file.

Never `await` a mesh operation in a Blazor component lifecycle method, click handler, autocomplete callback, or anywhere else. `Task.FromResult(snapshot)` is no better — it freezes the snapshot at call time and ignores live updates.

The pattern: **maintain a state list outside the observable; subscribe to the mesh observable; when the observable emits, fold the new items into your state list and call `StateHasChanged`**.

```csharp
public partial class MyView : ComponentBase, IDisposable
{
    private readonly List<Suggestion> _suggestions = new();
    private IDisposable? _sub;
    private string _query = "";

    private void RefreshSuggestions(string query)
    {
        if (query == _query) return;
        _query = query;
        _sub?.Dispose();
        _suggestions.Clear();

        _sub = MeshQuery.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Subscribe(change =>
            {
                ApplyChange(change);
                _suggestions.Sort(BestMatchComparer);
                InvokeAsync(StateHasChanged);
            });
    }

    public void Dispose() => _sub?.Dispose();
}
```

The view binds to `_suggestions` directly — no callback that returns `Task<T[]>`.

**Forbidden in GUI code and their substitutions:**

| ❌ Wrong | ✅ Right |
|---|---|
| `var x = await mesh.QueryAsync(...).ToListAsync()` | `mesh.Query<T>(req).Subscribe(c => ApplyChange(c))` |
| `await Hub.Observe<R>(req).FirstAsync().ToTask()` | `Hub.Observe(req).Subscribe(r => UpdateState(r.Message), ex => …)` |
| `var d = Hub.Post(req, o); Hub.Observe(d).Subscribe(…)` | `Hub.Observe(req, o).Subscribe(r => …, ex => …)` — pass the REQUEST, never a delivery you already posted (rule 3a) |
| `var n = await mesh.GetMeshNodeStream(p).Take(1).ToTask()` | live = `mesh.GetMeshNodeStream(p).Subscribe(n => UpdateState(n))`; one-shot = `Hub.GetMeshNode(p).Subscribe(n => …)` |
| `return Task.FromResult(_suggestions.ToArray())` | bind directly to `_suggestions`; let `Subscribe` push updates |
| `_ = LoadAsync(); await ...` | sync method that fires `Subscribe` |

**Lifecycle wiring:**

- `OnParametersSet` (sync) — kick off `Refresh*()`; never `OnParametersSetAsync` for mesh reads.
- Click handlers — `() => { svc.Op().Subscribe(r => UpdateState(r)); }`; never `async ctx => await svc.Op()`.
- `Dispose` — clean up all `IDisposable` subscriptions to stop emissions after the component unmounts.

---

## 🚨 Copy / recursive subtree operations — `Query` + `.Select(CreateNode)`

Recursive node operations (Copy, and Move which is Copy + Delete) must stay in the observable world end to end. **Never** read source content via `GetRemoteStream<MeshNode, MeshNodeReference>` for this — the remote stream subscribes to the owning per-node hub, which may not be activated yet for newly-created nodes, and the subscription waits indefinitely. **Never** `await meshService.QueryAsync(...)` either.

```csharp
meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(
        $"path:{sourcePath} scope:subtree"))
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(15))
    .SelectMany(change =>
    {
        var nodes = change.Items;
        var sourceNode = nodes.FirstOrDefault(n => n.Path == sourcePath);
        if (sourceNode is null)
        {
            hub.Post(CopyNodeResponse.Fail("Source not found", NodeCopyRejectionReason.SourceNotFound),
                o => o.ResponseFor(request));
            return Observable.Empty<MeshNode>();
        }

        var others = nodes.Where(/* IncludeDescendants / IncludeSatellites filter */);

        return meshService.CreateNode(RetargetNode(sourceNode, sourcePath, targetPath))
            .SelectMany(rootCreated => others.ToObservable()
                .Select(n => RetargetNode(n, sourcePath, targetPath))
                .SelectMany(retargeted => meshService.CreateNode(retargeted))
                .ToList()
                .Select(_ => rootCreated));
    })
    .Subscribe(
        rootCreated => hub.Post(CopyNodeResponse.Ok(rootCreated), o => o.ResponseFor(request)),
        ex          => hub.Post(CopyNodeResponse.Fail(ex.Message),  o => o.ResponseFor(request)));
```

Move uses Copy then Delete:

```csharp
meshService.CopyNode(source, target, includeDescendants: true, includeSatellites: true)
    .SelectMany(copied => meshService.DeleteNode(source).Select(_ => copied))
    .Subscribe(
        movedNode => hub.Post(MoveNodeResponse.Ok(movedNode), o => o.ResponseFor(request)),
        ex        => hub.Post(MoveNodeResponse.Fail(ex.Message), o => o.ResponseFor(request)));
```

> 🚨 Both blocks above are **sketches of the reactive SHAPE, not the move's contract** — copy either
> verbatim and you get issue #3272 back. `IncludeSatellites` is not a filter over a set the subtree
> query returned (it returns no metadata satellites on any backend), the delete leg enumerates
> storage rather than one path, and a move must refuse rather than delete what the copy could not
> carry. [Moving Nodes](/Doc/Architecture/MovingNodes) is the contract.

---

## 🚨 Reading the OWN node — `GetStream(new MeshNodeReference())`, never `GetStream<MeshNode>().FirstOrDefault`

To read the hub's own MeshNode, use the dedicated own-node reducer:

```csharp
// ✅ Right — direct subscription to the MeshNodeReference reducer.
workspace.GetStream(new MeshNodeReference())
    .Select(change => change.Value)
    .Where(node => node != null)
    .Subscribe(node => /* handle the own node */);
```

**Anti-pattern** — filtering `GetStream<MeshNode>()` by path:

```csharp
// ❌ Wrong — pulls the WHOLE InstanceCollection on every emission and filters in C#.
//    Allocates, scans, and emits one frame per collection mutation.
workspace.GetStream<MeshNode>()
    ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hub.Address.ToString()))
    .Where(n => n != null)
    .Subscribe(...);
```

The `MeshNodeReference` reducer is registered by `MeshDataSource.AddMeshDataSource()`. If the call throws `InvalidOperationException("Failed to create stream")`, the workspace was misconfigured (no MeshDataSource on this hub) — return a NodeNotFound error response, don't let the exception crash the delivery pipeline.

For reading any node by path (own or remote), use `workspace.GetMeshNodeStream(path)` which dispatches own → remote automatically.

---

## 🚨 Writing any MeshNode — ONE primitive: `GetMeshNodeStream(path).Update(...)`

Own node or someone else's, server-side application code always writes the same way:

```csharp
workspace.GetMeshNodeStream(path).Update(node =>
{
    // Bad-data tolerance: an existing node whose content can't be read is
    // left alone — never clobbered with a fresh instance.
    var content = node.ContentAs<MyContent>(hub.JsonSerializerOptions, logger);
    if (node.Content is not null && content is null) return node;
    content ??= new MyContent();
    return node with { Content = content with { Status = "updated" } };
})
.Subscribe(_ => { }, ex => logger.LogWarning(ex, "Update failed for {Path}", path));
```

The handle auto-dispatches:

- **`path == this hub's address`** — the write goes through the local data source (`UpdateOwn`).
- **`path != this hub's address`** — the write routes to the owning per-node hub via the process-wide `IMeshNodeStreamCache`: it diffs `current` vs `update(current)` and ships only the RFC 7396 JSON-merge patch. The owner serialises every mirror's write through its single-threaded action block, and merges the patch against its CURRENT state — concurrent writers touching different fields both land; there is no last-write-wins on the whole node.

Because the cache hands out one shared handle per path, repeated writes do **not** re-subscribe — there is no per-write `SubscribeRequest` churn, and readers (including the GUI) observe every write in order on the same handle.

**Don't** post `DataChangeRequest` / `PatchDataRequest` yourself for MeshNode writes — those are the internal plumbing `stream.Update` rides on, not an application surface. (Blazor views bind via `Hub.GetMeshNodeStream(path)` — the same shared cache handle — and push edits back through it; see [Data Binding](/Doc/GUI/DataBinding).)

---

## 🚨 The canonical layout-area / Blazor view pattern — hold the stream, never the snapshot

For any view that reads and writes the same MeshNode (collaborative editor, dashboard with edit, layout area with click actions), hold the **`ISynchronizationStream<MeshNode>`** as a field — not a snapshot, not a `Take(1)` re-subscription per click.

```csharp
public partial class MyEditor : BlazorView<MyControl, MyEditor>
{
    protected override void BindData()
    {
        base.BindData();

        // Read via the process-wide shared handle (IMeshNodeStreamCache) —
        // the same handle every other reader AND the writer use for this path.
        AddBinding(Hub.GetMeshNodeStream(BoundNodePath)
            .Where(node => node is not null)
            .Select(node => node!.Content as MarkdownContent)
            .DistinctUntilChanged()
            .Subscribe(content =>
            {
                if (content?.Content is { } text && text != RawContent)
                {
                    RawContent = text;
                    InvokeAsync(StateHasChanged);
                }
            }));
    }

    private Task SaveAsync(string newContent)
    {
        // Write through the SAME shared handle the read subscription is on —
        // every reader observes the patch in order.
        Hub.GetMeshNodeStream(BoundNodePath).Update(current =>
                current with { Content = new MarkdownContent { Content = newContent } })
            .Subscribe(_ => { }, ex => Logger.LogWarning(ex, "save failed"));
        return Task.CompletedTask;
    }
}
```

**Why this is the right shape:**

- `Hub.GetMeshNodeStream(path)` resolves the per-node reducer through the shared cache — direct, no FirstOrDefault on a collection, no per-emission filter, one upstream subscription process-wide no matter how many views are open.
- `AddBinding(...)` registers the subscription with the base class; it is disposed on component teardown while the upstream cache entry stays alive.
- `Update(...)` writes through the same handle the view is rendering from — the patch goes to the owning hub, which broadcasts the echo, updating the view without an extra read. `Update` is cold: the trailing `Subscribe` is what makes the write happen.

### Anti-patterns that show up in views

```csharp
// ❌ WRONG — Take(1) per save; subscribes, reads, disposes on every click.
private Task SaveAsync(string newContent)
{
    workspace.GetMeshNodeStream(BoundNodePath)
        .Take(1).Timeout(TimeSpan.FromSeconds(10))
        .Subscribe(node =>
        {
            var newNode = node with { Content = new MarkdownContent { Content = newContent } };
            Hub.Post(new DataChangeRequest { Updates = [newNode] },
                o => o.WithTarget(new Address(BoundHubAddress)));
        });
    return Task.FromResult(true);
}

// ❌ WRONG — caching a static MeshNode snapshot that goes stale.
private MeshNode? _currentNode;
private Task SaveAsync(string newContent)
{
    var newNode = _currentNode! with { Content = ... };
    Hub.Post(new DataChangeRequest { Updates = [newNode] }, o => o.WithTarget(...));
    return Task.FromResult(true);
}

// ❌ WRONG — GetRemoteStream<MeshNode>(addr) (collection variant) + FirstOrDefault.
//   Pulls the WHOLE InstanceCollection on every emission; emits a frame whenever
//   ANY other node in the collection mutates.
workspace.GetRemoteStream<MeshNode>(new Address(addr))
    ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == path))
    .Subscribe(...);
```

**The reduce-to-MeshNode (`MeshNodeReference`) form is always preferred over reduce-to-`InstanceCollection` (`CollectionReference`) when you only care about one node** — direct reducer, narrower change feed, supports `.Update(...)` write-back.

---

## 🚨 Decision rule — single op vs. long-standing stream

Both shapes use the **same primitive** for MeshNodes — `workspace.GetMeshNodeStream(path)` — the difference is only how long you stay subscribed:

| Caller shape | Use |
|---|---|
| **Single operation** — handler builds value once; HTTP / MCP / CLI endpoints; click actions; one-shot writes | `workspace.GetMeshNodeStream(path).Update(fn).Subscribe(...)` (write) · `.Where(n => n is not null).Take(1).Timeout(...)` (read) |
| **Long-standing stream** — anything that re-renders or re-computes when data changes; all layout areas; live editors; dashboards; collaborative views; streaming autocomplete | `workspace.GetMeshNodeStream(path)` + `.Subscribe(...)` — **stay subscribed**, push edits back via `.Update(...)` on the same handle |

> **Rule of thumb:** if any downstream code re-renders when data changes, stay subscribed. (`DataChangeRequest` is the typed-**entity** mutation message for EntityStore collections — see [CRUD](/Doc/DataMesh/CRUD) — not a MeshNode write surface.)

The rule applies symmetrically:

- **Layout areas always subscribe to a stream** and push edits back through `stream.Update(...)` on the same handle they render from.
- **Autocomplete** that streams suggestions incrementally uses a long-standing stream subscription.
- **MCP tools** and **`MeshPlugin` tool methods** are one-shot — internally they ride the same reactive surface (`MeshOperations`), bridging to `Task` only at the MCP boundary.
- **HTTP / CLI endpoints** that render once and close are one-shot.

### MeshNode write semantics: routing-supplied stream + sample-debounced save

| Operation | Path | Where it lives |
|---|---|---|
| **Read own MeshNode** (init + live updates) | Routing-supplied `IObservable<MeshNode>` attached via `config.WithOwnNodeStream(...)`. `DistinctUntilChanged().Replay(1).RefCount()` filters echoes; emissions seed the workspace and push subsequent updates without a duplicate persistence read | `MessageHubGrain.OnActivateAsync` / `MonolithRoutingService.CreateHub` plumb the stream into `MeshNodeTypeSource` |
| **Update own MeshNode** (editor-style writes) | Subscribe to `workspace.GetMeshNodeStream()`, `DistinctUntilChanged(n => n.Version)` to drop routing-stream echoes, `Sample(200ms)` to coalesce bursts, post `SaveMeshNodeRequest` per emission | `MeshDataSource.SubscribeToOwnDeletion` registers the persistence sampler at hub init |
| **Create / Delete own MeshNode** | Direct `IStorageService.SaveNode` / `DeleteNode` from inside `MeshNodeTypeSource.UpdateImpl` — instant write, no debounce | Adds and deletes are infrequent and ordering matters |

The classic "loop" risk — routing stream emits an external update → workspace emits → save subscriber posts → save handler writes → persistence emits → routing stream re-emits — is broken by `DistinctUntilChanged()` upstream of the workspace (drops same-Version repeats) and `DistinctUntilChanged(n => n.Version)` on the save subscription.

### Persistence belongs in MeshDataSource init — nowhere else

`IMeshStorage` is loaded **once**, during `MeshDataSource` initialization, to populate the workspace. After init, the workspace is the source of truth. **No handler ever calls `persistence.GetNodeAsync` or `persistence.SaveNode`.**

```csharp
// ❌ Reading existing state via persistence in a handler.
var existing = await persistence.GetNodeAsync(path, ct);

// ❌ "Fallback" to persistence when the workspace stream is empty.
var obs = workspace.GetStream<MeshNode>() != null
    ? workspace.GetMeshNodeStream(path)
    : Observable.FromAsync(ct => persistence.GetNodeAsync(path, ct));

// ❌ Writing to a remote node by reaching into persistence directly.
await persistence.SaveNode(node);
```

**The right primitives:**

| Operation | Primitive |
|---|---|
| Read own MeshNode | `workspace.GetMeshNodeStream()` |
| Read MeshNode at any path | `workspace.GetMeshNodeStream(path)` (auto-dispatches own / local collection / remote) |
| Update MeshNode at any path | `workspace.GetMeshNodeStream(path).Update(node => updated).Subscribe(...)` — same auto-dispatch |
| Create node | `meshService.CreateNode(node).Subscribe(...)` |
| Delete node | `meshService.DeleteNode(path).Subscribe(...)` |

---

## Workspace Updates (Non-Blocking)

`workspace.GetMeshNodeStream(path).Update(fn)` applies the update function to the current node state atomically on the owning hub's action block — no blocking. It returns a **cold** `RequireSubscribeObservable<MeshNode>`: the write only runs on `Subscribe`, and a handle that is garbage-collected without ever being subscribed logs a warning on the `MeshWeaver.Mesh.RequireSubscribe` channel.

```csharp
workspace.GetMeshNodeStream(path).Update(node =>
{
    var content = node.ContentAs<MyContent>(hub.JsonSerializerOptions, logger) ?? new MyContent();
    return node with
    {
        Content = content with { Status = "updated" }
    };
})
.Subscribe(_ => { }, ex => logger.LogWarning(ex, "Update failed for {Path}", path));
```

---

## AccessContext rides for free

Every framework write primitive (`IMeshService.CreateNode/UpdateNode/DeleteNode/CopyNode`, `MeshNodeStreamHandle.Update`, `IMeshNodeStreamCache.Update`) automatically captures the caller's `AccessContext` at invocation time and re-stamps it on every emission of the returned cold pipeline.

```csharp
// Handler runs with delivery.AccessContext = "alice" on AsyncLocal.
// The Subscribe callback runs on the workspace's emission thread —
// AsyncLocal would normally be wiped there, but the framework wrap
// captured "alice" before returning the observable and restores it
// before invoking the callback. The inner CreateNode therefore posts
// CreateNodeRequest with delivery.AccessContext = "alice".
streamCache.Update(path, fn).Subscribe(_ =>
    meshService.CreateNode(child).Subscribe(_ => { }));
```

The mechanism is `IObservable<T>.CarryAccessContext(IServiceProvider)` in `src/MeshWeaver.Messaging.Hub/AccessContextCaptureExtensions.cs`, applied inside each framework primitive (not at the callsite). Full reference: [AccessContextPropagation.md](/Doc/Architecture/AccessContextPropagation).

Legitimate hub-internal writes that must bypass user identity (cache hydration, SyncStream heartbeats) opt in explicitly via `accessService.ImpersonateAsSystem()` or `accessService.ImpersonateAsHub(hub)` / `PostOptions.ImpersonateAsHub`. PostPipeline fails closed otherwise.

---
## 🚨 `Observable.FromAsync` runs on the subscriber's thread and is unbounded — never write it

`Observable.FromAsync(() => someTask)` **looks** reactive, but two properties make it a defect
outside `IoPool`:

1. **The factory runs on the subscribing thread**, synchronously up to its first real `await`. When
   the subscribe happens mid-handler that prologue executes *on the hub action block / grain turn*.
2. **The `await` continuations resume on whatever scheduler the awaited task captured** — and
   `FromAsync` applies no concurrency bound. Chain a blocking subscriber on top (a hub `ActionBlock`,
   a test's synchronous `Should().Within(...).Match(...)`) and the continuation can be queued behind
   the very thread that is waiting for it.

Neither is fixed by `SubscribeOn` — that moves the *subscribe*, not the continuation. This is the
recurring "tests pass alone, hang when the full suite runs" shape.

**Don't do this:**
```csharp
// ❌ Prologue runs on the subscriber's thread; continuation resumes on a captured scheduler.
Observable.FromAsync(token => persistence.GetNodeAsync(path, token))

// ❌ Same problem — each MoveNextAsync of the await foreach captures the scheduler too.
Observable.FromAsync(async token =>
{
    var list = new List<MeshNode>();
    await foreach (var child in persistence.GetChildrenAsync(path).WithCancellation(token))
        list.Add(child);
    return list;
})
```

**Do this instead:**
- For a node's content — own or remote — use `workspace.GetMeshNodeStream(path)` (the shared
  `IMeshNodeStreamCache` handle; it dispatches own → remote for you). Do **not** reach for
  `GetRemoteStream<MeshNode, MeshNodeReference>` — that is framework plumbing, and going around the
  cache re-opens a subscription per read.
- For set discovery, use `meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(...))`
  (`.Take(1)` only if you genuinely want a one-shot, never for a live view).
- If a genuine I/O leaf must be bridged, put it on the pool — `pool.Invoke(...)` /
  `pool.InvokeBlocking(...)` / `pool.RunStream(...)` — which owns the gate and
  `ConfigureAwait(false)`. That is the *only* sanctioned bridge, and the only place
  `Observable.FromAsync` may appear is sealed inside `IoPool` itself.

## `Task` on the public surface is forbidden

Public methods on `IMeshService`, `HubNodePersistence`, `MeshOperations`, layout areas,
and similar mesh-facing services return **`IObservable<T>`**, not `Task<T>`. This is
not a style preference: a `Task` return type forces every caller into `.ContinueWith`
or `await`, and every one of those becomes a future deadlock candidate.

**Message HANDLERS are `SyncDelivery` too** — `IMessageDelivery SyncDelivery(IMessageDelivery request)`,
registered with `hub.Register<TMessage>(...)`. No `Task`, no `Task.FromResult`: a handler does its work
reactively and returns the delivery (`delivery.Processed()`), it never awaits its own reply.

## `DeliveryFailure` flows through `OnError`

When routing fails (no hub at target, target disposed, message undeliverable), the router posts a
`DeliveryFailure` response to the sender. `HandleCallbacks` recognises it and calls
`subject.OnError(new DeliveryFailureException(failure))` on the request's response subject — so it
surfaces at the `onError` arm of your `hub.Observe(...).Subscribe(onNext, onError)`:

```csharp
hub.Observe(new DeleteNodeRequest(path) { Recursive = true }, o => o.WithTarget(new Address(path)))
    .Subscribe(
        resp => { /* DeleteNodeResponse */ },
        ex   => logger.LogWarning(ex, "delete failed"));   // ← DeliveryFailureException lands here
```

**Always pass an `onError`.** A `Subscribe(onNext)` with no error arm rethrows on the emitting
thread instead of reaching your code — the failure becomes a wedge somewhere unrelated rather than
a logged, handled error at the call site.

## Every reactive chain must have a timeout — and know which ones already do

**A `hub.Observe(...)` is already bounded.** `MessageHub.Observe` applies the hub's
`RequestTimeout` (default 60 s, set with `WithRequestTimeout`) to its own response subject, so a
lost, misrouted or never-answered request surfaces as a named `TimeoutException`, and a routing
failure as a `DeliveryFailureException`. Do not stack a second ceiling on one of those.

What is **not** bounded is a chain **you** built. A hand-written
`Observable.Create(observer => ...)` can hang forever if nothing ever calls `observer.OnNext` /
`OnError`; so can a wait on a stream emission that never comes. Bound those explicitly, with a
budget the call site owns:

```csharp
return Observable.Create<Foo>(observer => { /* … */ })
    .Timeout(MyBudget);   // ← a chain with no hub round trip needs its own bound
```

### 🗑️ There is no `OpTimeout` on `IMeshService` — and adding one is not the fix

This section used to show `.Timeout(OpTimeout); // ← NEVER OMIT` on `IMeshService`'s surface.
`MeshService.OpTimeout` was **declared and never read**, so the instruction described a rule the
code did not follow, and #1270 removed both.

`MeshOperationOptions.Timeout` itself is real and stays — it is applied **server-side** by
`HandleDeleteNodeRequest` (and `HandleValidateDeleteRequest`), which own the fan-out they bound.
Tune it with `WithMeshOperationTimeout`.

🚨 **Do not "restore" the client-side ceiling.** A timeout on the CALLER's side of a mesh write
cancels nothing — the create is still running in the mesh — so the operation's continuation
outlives the caller's DI scope and resolves services from it after disposal. Measured on #1270's
first CI run: `[FATAL ERROR] System.ObjectDisposedException : Instances cannot be resolved … from
this LifetimeScope`, on a thread-pool thread, killing a test host that had just reported 90/90
passing. A bound whose failure mode is an unhandled exception in someone else's scope is worse
than the hang it replaces. To make a mesh operation fail sooner, set the hub's `RequestTimeout`.

## Route write-request handlers to the node's own hub

`IMeshService.DeleteNode` / `UpdateNode` target `new Address(path)` (the node's own
hub), not the mesh hub. This lets the handler read the node's state from its local
workspace stream — no persistence fallback, no `Observable.FromAsync` wrapping a
blocking async call. `CreateNode` still targets the mesh hub (the target node doesn't
exist yet).

## Rules Summary

| Pattern | Safe in Handlers? | Notes |
|---|---|---|
| `hub.Post(...)` | Yes | Fire-and-forget, safe from any thread |
| `hub.Observe(request).Subscribe(onNext, onError)` | Yes | Reactive request/response; DeliveryFailure → onError |
| `hub.Post(...)` then `hub.Observe(delivery).Subscribe(...)` | **NO** | Registers the response subject AFTER the post; a reply landing in that window is DROPPED and the callback hangs forever. Use `hub.Observe(request, options)` — see rule 3a |
| `meshService.CreateNode(...).Subscribe()` | Yes | Fire-and-forget, no callback logic |
| `workspace.GetMeshNodeStream(path).Update(...).Subscribe(...)` in handler body | Yes | Runs on grain scheduler |
| `hub.RegisterCallback(...)` / `hub.AwaitResponse(...)` | **GONE** | Deleted from the framework — these names no longer compile. `hub.Observe(...)` is the only request/response primitive |
| Direct workspace mutation in a Subscribe callback | **NO** | Wrong thread in Orleans, deadlocks — compose the next write into the observable chain instead |
| `workspace.GetStream(new MeshNodeReference())` | Yes | The hub's OWN node only — the dedicated own-node reducer |
| `workspace.GetMeshNodeStream(path)` | Yes | Any node, own or remote — the shared cache handle. Prefer this over `GetRemoteStream<MeshNode, …>`, which is framework plumbing |
| `meshService.ObserveQuery<T>(req)` | Yes | Reactive query for set discovery (`.Take(1)` only for a genuine one-shot) |
| `workspace.UpdateMeshNode(...)` | **`[Obsolete]`** | Superseded by `GetMeshNodeStream(path).Update(...).Subscribe(...)`, which forces the caller to subscribe so writes can't be silently dropped |
| `meshService.QueryAsync(...)` (IAsyncEnumerable) | **NO** | Blocks the caller's thread while enumerating — and reads the lagged index |
| `Observable.FromAsync(() => someTask)` | **NO** | Prologue runs on the subscribing thread, continuation resumes on a captured scheduler, no concurrency bound. Bridge through `IIoPool` |
| `Task<T>` on public mesh-facing API | **NO** | Forces callers into await; use `IObservable<T>` |
| Observable chain without `.Timeout(...)` | **NO** | A lost response = infinite hang |
| `await someTask` | **NO** | Blocks the hub scheduler |
| `stream.Subscribe(...)` | **Risky** | May deadlock if stream observes on hub scheduler |

---

## When async/await IS safe

`async/await` is safe only in contexts that do not run on the hub's scheduler:

- Blazor component event handlers (`OnClick`, `OnInitializedAsync`)
- HTTP middleware and API controllers
- Background services and hosted services
- Test code

The rule is simple: **if your code runs inside a hub message handler (registered via `.WithHandler<T>()`), never await.**

---

## Blocking Execution (AI Streaming)

Sometimes you genuinely need long-running I/O — streaming an AI response, for example. This uses a **hosted hub** (`_Exec`) that runs the blocking work on its own thread via `hub.InvokeAsync`. Even here:

- All **state updates** go through the **parent hub** or a long-lived workspace stream — never via per-chunk messages between hubs.
- All **messages** go through the parent hub — never post to the execution hub.
- The execution hub is purely for hosting the blocking I/O — it should never own state.

> **Streaming content into a thread message: push every delta through `GetMeshNodeStream(path).Update(...)` from the writer.** Posting `UpdateThreadMessageContent` (or any per-chunk message) between hubs creates the deadlock surface that `OrleansReentrancyTest.ToolCall_DuringStreaming_DoesNotDeadlock` exists to catch. See [Thread Execution Streaming](/Doc/Architecture/ThreadExecutionStreaming) for the full design and [Per-Hub TaskScheduler](/Doc/Architecture/OrleansTaskScheduler) for the threading-model rules.

```csharp
// In the submission watcher (runs on thread hub) — invoke directly and
// subscribe; completion is gated on the terminal Status write:
ThreadExecution.ExecuteMessageAsync(execHub, roundParams, accessContext)
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "round failed"));

// In ExecuteMessageAsync (runs on _Exec hub):
var parentHub = hub.Configuration.ParentHub!;   // _Exec has no AddData — route via parent

// Push every delta through the SHARED per-path handle — fire-and-forget per chunk.
parentHub.GetMeshNodeStream(responsePath)
    .Update(node => node with { Content = (ThreadMessage)node.Content with { Text = ... } })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "push failed"));

// Thread-state updates use the same primitive on the thread node.
parentHub.GetMeshNodeStream(threadPath)
    .Update(node => node with { /* IsExecuting, etc. */ })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "state update failed"));
```

The parent hub's scheduler is free — the streaming loop runs on `_Exec`, and the watcher's subscription completes only when the round's terminal status lands. Per-message content writes flow through the shared stream handle so the renderer sees them without the writer paying for a hub-to-hub round trip per chunk.
