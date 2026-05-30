# Asynchronous Calls in MeshWeaver

> **For GUI rendering, see [Data Binding](xref:GUI/DataBinding) — that is the authoritative pattern.** Layout areas should not load data at all; they declare bindings, and the Blazor view subscribes via `GetRemoteStream<MeshNode, MeshNodeReference>`. The rules below cover hub-handler / service code, where you still need to compose async work safely.

## 🚨🚨🚨 `await hub.GetMeshNode(...)` (or any hub round-trip) IS A 100% DEADLOCK 🚨🚨🚨

**Every form of bridging a hub round-trip back to a `Task` and awaiting it deadlocks the mesh hub.** This is not a "depends on the scheduler" or "usually safe" rule — it is **100% reproducible** under load and the whole reason the framework switched to `IObservable<T>` end-to-end. The forbidden patterns:

```csharp
// ❌ DEADLOCK — direct .ToTask() bridge then await.
var node = await hub.GetMeshNode(path, TimeSpan.FromSeconds(10)).ToTask(ct);

// ❌ DEADLOCK — .FirstOrDefaultAsync() is just .ToTask() under a different name.
var node = await hub.GetMeshNode(path).FirstOrDefaultAsync();

// ❌ DEADLOCK — wrapping it in Observable.FromAsync does NOT help.
//    The Func<Task<T>> still re-invokes on each Subscribe and the inner
//    await still bridges back to the calling scheduler. The `Observable.FromAsync`
//    is theatre — the deadlock is unchanged.
return Observable.FromAsync(async ct =>
{
    var node = await hub.GetMeshNode(path).ToTask(ct);  // ← still deadlocks
    return Process(node);
});

// ❌ DEADLOCK — wrapping the inner method as `async Task` and awaiting later
//    is the same bug behind a new method boundary.
private async Task<X> Resolve(string path, CancellationToken ct)
{
    var node = await hub.GetMeshNode(path).ToTask(ct);   // ← still deadlocks
    return Process(node);
}
```

**The ONLY correct shape:** compose `hub.GetMeshNode(path)` into your observable chain with `.Select` / `.SelectMany`. The hub round-trip stays observable end-to-end; the continuation is never bridged to a Task that the caller awaits.

```csharp
// ✅ RIGHT — composable, no Task surface, no scheduler bridge.
return hub.GetMeshNode(path, TimeSpan.FromSeconds(10))
    .Select(node => Process(node));
// or, when the next step is itself an Observable:
return hub.GetMeshNode(path, TimeSpan.FromSeconds(10))
    .SelectMany(node => DoNextThing(node));    // returns IObservable<...>
```

### **Return type MUST be `IObservable<T>` — NEVER `Task<T>`.**

Every public method on a service / handler / helper / extension that participates in mesh work returns `IObservable<T>`. **Not `Task<T>`. Not `ValueTask<T>`. Not `async Task<T>`.** The instant a public method returns a Task, the next caller will `await` it — and that's the deadlock.

```csharp
// ❌ WRONG — Task on the public surface invites await everywhere it's called.
public Task<X> ResolveSomethingAsync(...) { ... }

// ✅ RIGHT — IObservable surface forces the caller to compose with .Subscribe / .Select.
public IObservable<X> ResolveSomething(...) { ... }
```

This is a **hard contract**, not a style preference:

- New service methods: `IObservable<T>` only.
- Refactoring an existing `async Task<T>` method that's reachable from hub code: change the signature; update every caller.
- Don't paper over it with default-interface Task shims. Don't keep the Task overload "for tests". Tests call `.FirstAsync().ToTask(ct)` themselves at *their* edge — the production interface stays Task-free.
- Don't introduce `Observable.FromAsync(ct => SomeAsyncMethod(...))` to "make it observable" while the inner method still uses `await` on a hub round-trip — that's the same deadlock with one more layer of indirection.

The Task boundary belongs at the test edge (`.FirstAsync().ToTask(ct)`) or at framework lifecycle hooks (`OnActivateAsync` etc.), and even there only when **no further mesh work** runs after the await.

**The same rule applies to every hub round-trip primitive:**

- `hub.GetMeshNode(path)` — never awaited, always composed.
- `hub.Observe(request, options)` — never awaited / `.ToTask()`'d in production code; subscribe.
- `meshService.QueryAsync(...)` / `.FirstOrDefaultAsync()` / `.ToListAsync()` — never inside hub-reachable code; use `meshService.ObserveQuery(...).Subscribe(...)`.
- `workspace.GetRemoteStream<T, TRef>(addr, ref)` — subscribe; never `.Take(1).ToTask()` to fake a fetch.

**No exceptions. No "it's a small helper". No "we wrap it in `Observable.FromAsync` so it's safe".** If you find yourself reaching for any of those, the design is wrong — the public method should return `IObservable<T>`, the call site should `.Subscribe(...)`, and the chain should never touch a Task.

## 🚨 Cold observables: Subscribe is mandatory, errors propagate to the subscriber

Every method that performs a write or any other side effect returns `IObservable<T>` and is **cold** — the side effect runs on `Subscribe`, never on call. Forgetting to subscribe means the work silently doesn't happen. The most common shape:

```csharp
// ❌ WRONG — fire-and-forget. UpdateMeshNode is a cold IObservable<MeshNode>;
//   the dsStream.Update side effect only runs on Subscribe, so this is a no-op.
//   This was the "chat doesn't work in prod" root cause: AppendUserInput called
//   workspace.UpdateMeshNode and discarded the IObservable, so the thread state
//   never changed and the watcher never dispatched.
workspace.GetMeshNodeStream().Update(node => node with { Content = … });

// ✅ RIGHT — subscribe with explicit success / error handlers. No fire-and-forget;
//   errors propagate to the caller (logged here; can also be re-thrown via OnError
//   into a wider chain).
var logger = workspace.Hub.ServiceProvider.GetService<ILoggerFactory>()
    ?.CreateLogger("MyComponent");
workspace.GetMeshNodeStream().Update(node => node with { Content = … })
    .Subscribe(
        _ => { /* optional success follow-up */ },
        ex => logger?.LogWarning(ex, "Update failed for {Path}", path));
```

**Where the next step depends on the commit completing**, chain via `SelectMany` so success/error flow through one observable:

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

Treat that warning as a hard failure — search the log channel `MeshWeaver.Mesh.RequireSubscribe` after every test/CI run.

### Compile-time signal

The legacy `workspace.UpdateMeshNode(update)` extension is `[Obsolete]` and points at the new API:

> *Use `workspace.GetMeshNodeStream(path?).Update(update).Subscribe(...)` — uniform read/write API; callers must subscribe so writes can't be silently dropped.*

Any obsolete-warning hit on a build is a missing-Subscribe bug; fix the callsite, don't suppress the warning.

### The same rule for every cold-write surface

The Subscribe-mandatory contract isn't just MeshNode updates. Any IObservable returned from a service that performs a side effect on Subscribe must be subscribed at every callsite:

- `meshService.CreateNode(node) / UpdateNode(node) / DeleteNode(path)` — cold; subscribe to commit.
- `meshService.MoveNode(...)` / `meshService.CreateTransient(node)` — cold; subscribe.
- `remoteStream.Update(current => updated, ex => …)` — `ex` callback fires on the stream's hub; the returned `void` IS the subscription. (But the patch routing is hub.Post, so this is hot — the exception in the lambda below is the only way to surface `OnError` here.)

## 🚨 `MeshNode.Content` is always typed at the `GetMeshNodeStream` boundary

`MeshNodeStreamHandle.Subscribe` and `MeshNodeStreamHandle.Update` round-trip `node.Content` through the workspace's `JsonSerializerOptions` at the boundary. The Subscribe path runs a `TypedContentObserver` that deserialises any `JsonElement` to its registered domain type before delivery; the Update path wraps the caller's lambda so the input is typed and the output is re-serialised back to `JsonElement` before the patch hits the wire.

This eliminates the silent default-fallback bug class:

```csharp
// ❌ Before — silently lossy whenever Content arrived as JsonElement (file-system /
//    Postgres / Cosmos all round-trip through JSON; only InMemory keeps typed).
//    `as MeshThread` returns null on JsonElement, the `?? new MeshThread()`
//    fallback overwrites every other field with defaults (Status=Idle, pending={}),
//    next stream.Update persists that default thread — silent data corruption.
workspace.GetMeshNodeStream().Update(node =>
{
    var t = node.Content as MeshThread ?? new MeshThread();
    return node with { Content = t with { PendingUserMessages = pending } };
});

// ✅ After — the framework hands the lambda a typed MeshThread regardless of
//    underlying storage. If Content is genuinely null/wrong-shaped the cast
//    fails and the lambda returns `node` unchanged — no silent overwrite.
workspace.GetMeshNodeStream().Update(node =>
{
    if (node.Content is not MeshThread t) return node;
    return node with { Content = t with { PendingUserMessages = pending } };
});
```

Full treatment with the read-side rule + helpers (`EnsureTypedContent`, `EnsureSerialisedContent`): [CqrsAndContentAccess.md → "Content is always typed at the GetMeshNodeStream boundary"](xref:Architecture/CqrsAndContentAccess).

## 🚨 `No handler found for message type X` is almost always a serialization/type-registry bug

When a routed `IRequest<T>` comes back as a `DeliveryFailure` saying *"No handler found for message type X"*, the handler usually IS registered via `WithHandler<X>(...)` on the target hub. The framework's `FinishDelivery` only emits this when an `IRequest<T>` reached the hub but no `Register<T>(...)` filter matched — and the most common reason is the message arriving on the wire **deserialized as a different type** (or as `JsonElement`) because the receiving hub's `ITypeRegistry` is missing the `WithType(typeof(X), nameof(X))` entry that the sender used as the `$type` discriminator.

**Triage in this order — don't skip steps:**

1. Verify `X` is registered on **both** the sender and the receiver via `config.TypeRegistry.WithType(typeof(X), nameof(X))` — typically through a module-level `AddXxxTypes(this ITypeRegistry)` extension.
2. For Orleans / cross-process: confirm the registration is wired into BOTH the silo's mesh / hub config AND any client / portal hub that posts the request. The shared mesh-level registry inherits to all hubs in the same DI scope, but client hubs created via `CreateMessageHub` get their own registry that needs an explicit `config.TypeRegistry.AddXxxTypes()` call.
3. Only after ruling out (1)–(2): suspect an actual missing handler / wrong target address.

It's *almost never* a "the `WithHandler<X>` line is missing" bug. Always verify the type-registry parity first — a message that deserializes into the wrong CLR type can never match the handler filter `d is IMessageDelivery<X>`.

## 🚨 Subscription callback → `hub.Post` → handler (don't do work directly in `Subscribe`)

When a long-lived `IObservable<T>` (workspace stream, synced query, change feed) drives action on a hub, the `Subscribe` callback fires on **whatever scheduler the upstream emits on** — often the workspace's emission thread, sometimes a thread-pool task from a downstream operator, occasionally the hub's own ActionBlock. Putting non-trivial work in that callback couples the work to an unpredictable thread and routinely deadlocks: the callback walks into `workspace.GetQuery(...)` (cold cache → upstream Subscribe), or `meshService.CreateNode(...)` (posts to mesh hub and waits on the same hub's ActionBlock), and the chain blocks because the thread that holds the lock is the one that needs to free it.

**Rule:** the `Subscribe` callback does ONE thing — `hub.Post(new TriggerMessage(...))` to a hub that owns the work. A registered handler on that hub picks the message off the ActionBlock and runs the logic there. The ActionBlock is the single-threaded actor for that hub; serialisation, re-entrancy, and ordering are all handled by the inbox.

```csharp
// ❌ WRONG — fires on workspace emission scheduler; does dispatch in-line.
// Symptom: the dispatch walks into GetQuery's cold cache; cache's first
// Subscribe needs the same hub to make progress; deadlock.
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

// ✅ RIGHT — subscription posts; handler runs on the hub's ActionBlock.
ownStream
    .Where(node => node.Content is NodeTypeDefinition def
                   && def.CompilationStatus == CompilationStatus.Pending)
    .Subscribe(pendingNode =>
        hub.Post(new DispatchCompileTrigger(pendingNode), o => o.WithTarget(hub.Address)));

// Handler is registered on the per-NodeType hub in MeshDataSource:
//   .WithHandler<DispatchCompileTrigger>(NodeTypeCompilationHelpers.HandleDispatchCompile)
// The handler body owns the Pending→Compiling transition, activity dispatch,
// and inline fallback. It runs on the ActionBlock — single-threaded — so the
// status check + Update is implicitly atomic and no in-memory `dispatchInFlight`
// flag is needed for the work itself (the Subscribe callback above keeps an
// in-memory coalesce flag to avoid flooding the inbox with redundant Posts on
// rapid Pending re-emissions; that flag is for back-pressure, not correctness).
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

**Why this matters specifically for `Subscribe` on the workspace stream:** the workspace's `MeshNodeReference` reducer emits on the thread that applied the change. When the change came from a hub message, that's the hub's ActionBlock. When it came from a remote stream, it's the workspace emission scheduler. The callback inherits whichever — and an `Update` chained off it inherits again. Anything downstream that needs a different scheduler (e.g. `GetQuery`'s `Task.Factory.StartNew(... TaskScheduler.Default)`) starts on thread-pool but its **continuation** captures the calling context. By the time you've chained three `.Subscribe`s deep, you've created a scheduler graph nobody can reason about.

The `hub.Post` indirection breaks the graph: the post returns immediately, the Subscribe callback completes, and the handler runs on the well-defined ActionBlock thread of the target hub. Reasoning becomes local again.

**Canonical reference:** `NodeTypeCompilationHelpers.InstallCompileWatcher` + `HandleDispatchCompile`. The watcher subscribes to the per-NodeType hub's own MeshNode stream filtered on `CompilationStatus == Pending`; on emission it posts `DispatchCompileTrigger` to its own hub address; the handler runs on that hub's ActionBlock and owns the Pending→Compiling transition + activity dispatch.

**When `Subscribe` may run the work directly:** read-only display work that emits to a `BehaviorSubject` (no upstream calls), or a closure that simply tears down a disposable on a terminal event. Anything that touches `workspace.GetQuery`, `workspace.GetMeshNodeStream(remotePath)`, `meshService.CreateNode/UpdateNode`, or any `hub.Post` whose response your continuation awaits — move it behind a `hub.Post` + handler.

## 🚨 Subscribe-all-upfront cell loading — `Observable.CombineLatest` of N synchronization streams, never `.Concat()`

Loading N node contents (chat history, thread thumbnail, sub-thread summary) needs **N hub activations in parallel**, not one at a time. The shape that gets this wrong is a serial fold:

```csharp
// ❌ WRONG — sequential; total = Σ(t_i).
// `.Concat()` subscribes to stream #1 first, waits for it to complete (Take(1)
// + Timeout), THEN subscribes to #2, etc. If each cell's hub takes 200 ms to
// activate cold, ten cells = 2 s of wall-clock — but only one activation runs
// at any moment, so most of the infra is idle. CI-load amplifies it: under
// contention each t_i grows and the serial sum hits the per-cell Timeout.
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
// ✅ RIGHT — Observable.CombineLatest subscribes to ALL N upstream streams
// the instant the consumer subscribes. The N per-node hub activations and
// initial-frame round-trips happen CONCURRENTLY, so total wall-clock is
// ≈ max(t_i) instead of Σ(t_i). On a 10-cell load with cold hubs this is
// ~10× faster and — crucially — the variance flattens because slow infra
// gets exercised in parallel rather than sequentially queued.
//
// Per-cell Catch returns a sentinel `null` so CombineLatest still fires
// when one cell times out. Without the sentinel, CombineLatest waits
// forever (it requires at least one emission from every input).
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

**Why `CombineLatest` and not `Merge`+`Distinct`:** both subscribe to all inputs upfront, so both achieve the parallel-activation goal. The difference is shape: `CombineLatest` produces a positional tuple/list (cell #i is at index i), which is what the aggregator wants. `Merge` produces a stream of values in arrival order — you'd then need `Distinct(node => node.Path)` + `Take(N)` + `ToList()` to recover the set. Both work; `CombineLatest` is simpler when each input emits exactly one value (`Take(1)`).

**Why this smears load over infra:** each `cache.GetStream(path)` / `workspace.GetMeshNodeStream(path)` triggers an `IMeshNodeStreamCache` lookup, possibly a `GetPermissionRequest` to the access hub, possibly cold-activation of the per-node hub, possibly a database read on the partition root. Running all N in parallel lets the cache, the access pipeline, the database, and the activation scheduler all be busy at once instead of idle for `(N-1)/N` of the wall-clock. The result is more uniform tail latency under load — slow cells no longer block fast ones.

**Lazy chain, eager subscribe:** building the `cellLookups.Select(...)` does NOT subscribe — these are cold observables. Subscription happens when `Observable.CombineLatest(cellLookups)` is itself subscribed (by `.ToTask()`, `.Subscribe(...)`, or by being chained into the caller's output). At that moment, all N inputs subscribe simultaneously. Don't materialize the inputs into a different shape (`.ToList()` of the source enumerable is fine; awaiting individual streams is not) — anything that touches each input sequentially before handing the collection to `CombineLatest` reintroduces the serial pattern.

**Canonical callsites:**

- `ThreadExecution.LoadFullConversationHistoryFromMesh` — N prior-cell loads for the agent's chat history per round.
- `ThreadExecution.LoadPriorUserMessagesFromMesh` — post-restart resume after `AgentChatClient` cache miss.

**When NOT to fan-out at all:** if the consumer only needs a *preview* (e.g. a thumbnail card in a catalog), don't load every cell to render a 60-char string. Return the synchronous data (title, count, last-modified) from the OWN node and delegate the preview to a `LayoutAreaControl` pointing at the relevant child cell's compact view — the child hub activates lazily on the Blazor side when the tile becomes visible. Canonical: `ThreadLayoutAreas.Thumbnail` returns title + count immediately and embeds a `LayoutAreaControl(lastCellPath, "Streaming")` for the preview.

## Streams are reactive — subscribe, don't snapshot

`ISynchronizationStream<T>` is consumed via `.Select(...)` / `.Where(...)` / `.Subscribe(...)`. The framework's snapshot accessor is `internal` — application code can't see it, so the temptation to `.Current?.Value` doesn't exist. If a sync handler needs a value it can't subscribe for, the handler is wrong: derive it from the request payload, or defer the work to a follow-up message posted from inside `Subscribe`.

## 🚨 `Stream.Take(1)` is the wrong primitive for a one-shot read

A stream is a **subscription** — `GetMeshNodeStream(path)` / `GetRemoteStream<MeshNode, MeshNodeReference>(addr, ref)` posts a `SubscribeRequest`, receives the initial frame, then stays subscribed for live updates. Calling `.Take(1)` on it snapshots the first frame and immediately unsubscribes — you paid for a subscription you don't use, and the next caller pays again.

For a **one-shot read** (handler, helper, click action that needs the current value once), post `GetDataRequest(new MeshNodeReference())` to the node's address — that's a true request/response with no subscription overhead.

Decision matrix for reading mesh state:

| What you need | Primitive |
|---|---|
| **Single node**, live (view re-renders on changes) | `workspace.GetMeshNodeStream(path)` / `GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())`, **stay subscribed** (no `.Take(1)`). |
| **Single node**, one-shot (handler, helper, click) | `hub.Post(new GetDataRequest(new MeshNodeReference()), o => o.WithTarget(new Address(path)))` + `RegisterCallback` (or `AwaitResponse` outside hub-reachable code). |
| **Set / listing**, live (dashboard, autocomplete) | `meshService.ObserveQuery<T>(MeshQueryRequest.FromQuery(...))` — emits initial set + deltas. |
| **Set / listing**, one-shot (MCP tool, CLI, HTTP endpoint that closes) | `meshService.QueryAsync<T>(...)` — but ONLY when the caller exits after the snapshot, never inside a reactive chain. |

Same rule, simpler form: **streams subscribe, requests fetch.** Don't `.Take(1)` a stream to fake a fetch.

### When `.Take(N)` *is* the right primitive: read-modify-write inside `SelectMany`

There's exactly one shape where `.Take(N)` on a workspace stream is correct: a **read-modify-write** chain inside a hub handler. The handler needs the current snapshot once to build a follow-up message (e.g. a `DataChangeRequest`); the stream keeps emitting after that, but the handler doesn't care about the rest. `.Take(N)` snapshots `N` values then completes — pure reactive composition, no `.ToTask()`, no `await`.

The canonical example, lifted from the unified-reference delete path in `DataExtensions.DeleteDataPath`:

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
        workspace.RequestChange(changeRequest, activity, null);

        return Observable.Create<DeleteUnifiedReferenceResponse>(observer =>
        {
            activity.Complete(log => { observer.OnNext(...); observer.OnCompleted(); });
            return Disposable.Empty;
        });
    });
```

Why this is fine — and why the rules above still apply:

- The stream comes from `workspace.GetStream(entityRef, x => x.ReturnNullWhenNotPresent())`, **not** from `GetRemoteStream` / `GetMeshNodeStream`. Same workspace, no `SubscribeRequest` round-trip — `.Take(1)` is just "next emission".
- `.Take(N)` is composed inside `SelectMany`. The chain is one observable — there is no `await`, no `Task`, no `ToTask`. The handler's `Subscribe(...)` consumes the whole pipeline.
- It's read-modify-**write**. The point of the `.Take(1)` is to snapshot input for the next message, not to display anything. The "no `.Take(1)` on display streams" rule applies to *display* — chains rendered to the UI must stay subscribed.

If you find yourself using `.Take(N)` outside a `SelectMany` that immediately produces a follow-up message, you're in the wrong shape — go back to the matrix above.

MeshWeaver uses a **truly asynchronous** message-passing model. This is fundamentally different from C#'s `async/await` pattern, which is better described as "fake async" — you still block the calling context waiting for a result.

## 🚨 The absolute rules (no exceptions outside tests)

1. **No `Task<T>` / `async` / `await` in mesh-reachable code.** Public methods on services, handlers, layout areas, and click actions return `IObservable<T>` (or `void`). Return types matter — an `async Task` method that `await`s a hub operation deadlocks the hub ActionBlock. No exceptions for "just a wrapper" or "small helper".
2. **No `*Async` extension shims on `IMeshService`.** Use `meshService.CreateNode(node)` / `UpdateNode(node)` / `DeleteNode(path)` / `CreateTransient(node)` — these return `IObservable<MeshNode>`. **Never** use `.CreateNodeAsync(...)` / `.UpdateNodeAsync(...)` / `.DeleteNodeAsync(...)` / `.CreateTransientAsync(...)` — those extensions are being removed. They bridge the Observable to Task via `.ToTask()` and make the caller `await`, which deadlocks every time they are reached from a hub handler.
3. **Use `hub.Observe(...)` instead of `RegisterCallback` / `AwaitResponse`.** The Task-returning `IMessageHub.RegisterCallback(...)` and `IMessageHub.AwaitResponse(...)` overloads are `[Obsolete]`. Production code MUST use `hub.Observe(delivery)` (already-posted) or `hub.Observe(request, options?)` (also posts) — both return `IObservable<IMessageDelivery[<TResponse>]>`. `DeliveryFailure` flows via `OnError`, no Task-await deadlock surface, no silently-skipped callback.
4. **Never `.QueryAsync<MeshNode>($"path:X").FirstOrDefaultAsync()` to read a known node.** Queries go through a lagged read-side index. For a known path: live = `GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference()).Subscribe(...)` (stay subscribed); one-shot = `hub.GetMeshNode(path, timeout?)` (or post `GetDataRequest(new MeshNodeReference())` directly). See the decision matrix below.
5. **Never wrap a Task-returning query in `Observable.FromAsync(() => query.QueryAsync(...).FirstOrDefaultAsync().AsTask())`.** This is fake-reactive — runs through the lagged index and returns stale content. Use `GetRemoteStream<MeshNode, MeshNodeReference>` for the authoritative live view.
6. **🚨 NEVER `Observable.FromAsync(() => hub.RegisterCallback(...))`.** This bridges the callback Task back into Rx and the continuation captures whichever scheduler completed the Task — including the calling sync-context if there is one — and deadlocks. Use `hub.Observe(...)` (which calls `task.ToObservable()` once on a TCS-backed Task — different operator, no func re-invocation, no scheduler capture).
7. **`ISynchronizationStream<T>.Update` callbacks must be synchronous.** Don't use the `Func<T?, CancellationToken, Task<ChangeItem<T>?>>` overload from hub-reachable code — it hides an `await` inside the stream update. Use the sync `Func<T?, ChangeItem<T>?>` form and compose any async I/O outside the callback.
8. **🚨 NO `.Take(1)` on display streams.** A `.Take(1)` snapshots and unsubscribes — the view freezes on the first emission and **stops updating**. For display, stay subscribed (`Subscribe(...)` directly, compose with `CombineLatest` / `SelectMany` / `Switch`). The only `.Take(1)` that's ever right is a one-shot read of a *non-live* stream, and even there `hub.GetMeshNode(path)` / `hub.Observe(request)` are the better primitives because they don't pay for a `SubscribeRequest` they immediately throw away.
9. **🚨 The async boundary lives at the real I/O edge — defer it as deep as possible.** `async` / `await` / `IAsyncEnumerable` are the bridge across a *genuine* I/O wait (a Postgres round-trip, a file read, a network call) — not a style choice. **In-memory work is never async:** anything that only touches in-process state (a registry, a dictionary, an already-loaded `ImmutableList`, a `DataContext`'s type sources) projects synchronously and lifts to `IObservable<T>` via `IEnumerable<T>.ToObservable()` — no `async`, no `Task`, no `IAsyncEnumerable`. An `async IAsyncEnumerable` method that never awaits I/O is a bug: it pays the state-machine cost and lies about doing I/O. **Only the leaf that actually performs the I/O** (the Postgres / file-system / network adapter) is allowed to be async, and it bridges back to the observable contract at one sealed point (`Observable.Create` + `await foreach`, or `FromAsyncEnumerable`), **pooling at that edge** (DB connection pool, bounded `Channel`) so the wait is amortized and back-pressured. Litmus test: before writing `async`, name the I/O it awaits — if you can't, delete it and return `IObservable<T>`. Full treatment: [Aggregating Providers → "The async boundary lives at the I/O edge"](AggregatingProviders).

## 🚨🚨🚨 NEVER USE `QueryAsync` TO OBTAIN A `MeshNode` 🚨🚨🚨

**Queries are not a node lookup. Queries are not a node lookup. Queries are not a node lookup.**

`IMeshService.QueryAsync` is for searching and listing — it runs through a **lagged, eventually-consistent read-side index** that can return stale content right after a write. It is **never** the right tool for reading the current committed state of a specific node.

### ❌ WRONG — every line below is a bug

```csharp
// ❌ Lagged index — returns stale content after a write.
var node = await mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync();

// ❌ Same bug, wrapped in Observable.FromAsync to look reactive.
return Observable.FromAsync(ct =>
    mesh.QueryAsync<MeshNode>($"path:{path}").FirstOrDefaultAsync(ct).AsTask());

// ❌ Even with a path: filter, this is still a query. Still lagged. Still wrong.
await foreach (var n in mesh.QueryAsync<MeshNode>($"path:{path}")) { node = n; break; }

// ❌ Calling .Current on a stream — snapshot may be null before first emission.
var node = hub.GetWorkspace().GetStream(new MeshNodeReference())?.Current?.Value;
```

### ✅ RIGHT — the ONE way to obtain a known MeshNode

```csharp
// Direct subscription to the owning hub's workspace reference.
// Authoritative, live, no staleness, no query index involved.
var workspace = hub.GetWorkspace();
return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
        new Address(path), new MeshNodeReference())
    .Take(1)                                    // one emission then complete
    .Timeout(TimeSpan.FromSeconds(10))          // bound the wait
    .Select(change => change.Value);            // unwrap ChangeItem<MeshNode>
```

This is also how you **wait for work to finish** — subscribe until a field in the node's content flips to a completion state, then `.Take(1)`. No polling loop. No repeated queries.

### Sets / listings — **prefer `ObserveQuery`**, not `QueryAsync`

Even for the cases where a query is the right idea (listings, filters, existence across the mesh), **do not `await` the `IAsyncEnumerable<T>`** version — use the reactive `IMeshService.ObserveQuery<T>` overload. It returns `IObservable<QueryResultChange<T>>` with an initial full set and then incremental deltas, and it composes with `Select` / `Where` / `Subscribe` exactly like every other mesh observable.

> **Even `ObserveQuery` is wrong inside a layout area for displaying values.** Declare a binding (a path-bound control or a `JsonPointerReference`) and let the Blazor view subscribe. See [Data Binding](xref:GUI/DataBinding). Backend rendering code should be fully synchronous and side-effect-free.

**`QueryAsync` breaks the update flow.** It is a one-shot snapshot: you get the rows that existed at query time and nothing else. The view is frozen — if a row is added, removed, or mutated on the mesh, your list doesn't change. Any reactive chain downstream (a layout area, a dashboard, a dependent query) that re-renders when data changes is now silently broken because this particular upstream doesn't emit on updates. `ObserveQuery` emits the initial set plus a delta for every subsequent change, so the downstream chain stays live.

```csharp
// ❌ WRONG — IAsyncEnumerable + await — hub ActionBlock blocks on query pump.
var items = await meshService.QueryAsync<MeshNode>("nodeType:Post").ToListAsync();

// ✅ RIGHT — reactive, live, auto-updates on mesh changes.
meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery("nodeType:Post"))
    .Select(change => change.Result)           // full result set (or delta payload)
    .Subscribe(nodes => { /* render, react */ });
```

Valid uses of `ObserveQuery`:

- Listing children of a namespace (`path/*`)
- Filtering by predicate across the mesh (`nodeType:X`, `name:*sales*`)
- Checking "does any node match this predicate?" for existence tests
- Autocomplete / browsing / search UIs
- Any layout area that renders a list and wants live updates when the underlying set changes

Known-path single-node reads: **`GetRemoteStream<MeshNode, MeshNodeReference>`** (see above), never a query. No exceptions.

### The ONE case where `QueryAsync` is correct: one-shot lookups that exit the process

`QueryAsync` is a one-time snapshot — the result is frozen at query time and does not reflect subsequent mutations. That is exactly the shape needed for request/response call sites that return **once** and then the caller is gone:

- **MCP tool handlers** — an agent calls a tool, the tool returns a payload, the session ends. No reactive subscriber downstream, no update flow to break.
- **Export / import CLI services** — pull-and-leave jobs that dump to disk.
- **HTTP endpoints that render once and close** — e.g. a CSV download endpoint.

Anywhere else — layout areas, dashboards, chained reactive consumers, hub handlers, click actions, background orchestration that waits for state to flip — `QueryAsync` is wrong because the view won't update. Use `ObserveQuery` for those.

Rule of thumb: **if any downstream code re-renders or re-computes when data changes, you need `ObserveQuery`.** `QueryAsync` is only safe when the caller serialises the snapshot and walks away.


The handler method itself still returns `request.Processed()` immediately —
the Subscribe callback fires later, posts the response via
`hub.Post(response, o => o.ResponseFor(request))`. The caller blocks on
`RegisterCallback`, not on the handler method.

## 🚨 Related rule, read this first

**Queries are for sets and existence — never for reading a specific node's content.**
Queries go through a read-side index that lags behind writes; they are eventually
consistent. To read the current committed state of a known node, use
`workspace.GetRemoteStream<MeshNode, MeshNodeReference>(address, new MeshNodeReference())`.
That stream is also how you **wait for a job to finish** (subscribe until a completion
condition emits) — no polling, no `await` on a long-running task.

Full treatment: *[CQRS — Queries vs. Content Access](CqrsAndContentAccess)*.

The anti-patterns below (`Observable.FromAsync(() => query.FirstOrDefaultAsync(...).AsTask())`)
are fake-reactive wrappers over the lagged read path. They don't deadlock the hub —
they return stale content. Same bug class, different symptom.

## The T-Shirt Analogy

When you order a t-shirt online, you don't stand next to the mailbox until it arrives. Your life continues. The t-shirt shows up later, and you deal with it then.

**Truly async (MeshWeaver pattern):**
```csharp
// Post + observe in one go — emits exactly one IMessageDelivery<MyResponse>.
// DeliveryFailure / Timeout flow through onError naturally; no callback ever
// silently no-ops.
hub.Observe(new MyRequest(), o => o.WithTarget(address))
    .Subscribe(
        resp => { /* handle resp.Message — your "mailbox notification" */ },
        ex   => { /* DeliveryFailureException, TimeoutException, etc. */ });

// Your code continues immediately — no blocking
return delivery.Processed();
```

**Fake async (C# async/await — DO NOT do this in production):**
```csharp
// You ARE standing at the mailbox — deadlocks the hub action block.
var response = await hub.AwaitResponse<MyResponse>(request);
```

In tests `await MonolithMeshTestBase.AwaitResponseAsync(request, ...)` is the sanctioned bridge — it builds on `hub.Observe(...).FirstAsync().ToTask(ct)` with the test's cancellation token.

## Why `await` Deadlocks in Hub Handlers

The message hub processes messages sequentially through a single-threaded `ActionBlock`. When a handler calls `await`, it blocks the action block waiting for a response. But that response is itself a message that needs to be processed by the same action block — which is blocked. **Deadlock.**

```
Handler runs on ActionBlock
    → await AwaitResponse(request)
        → ActionBlock is blocked waiting
            → Response message arrives
                → Cannot be processed — ActionBlock is busy
                    → DEADLOCK
```

This applies to:
- `await hub.AwaitResponse(...)` — blocks the hub
- `await someTask` — blocks the hub scheduler
- `hub.InvokeAsync(...)` — schedules work on the blocked scheduler
- `workspace.GetStream().Subscribe(...)` — if the stream observes on the hub scheduler, the emission is queued behind the blocked handler

## The Observable Pattern

Use `IMeshService` to get into reactive/observable contexts. Observables are inherently truly async — you subscribe and get notified when data is available.

### Creating Nodes (Non-Blocking)

Fire-and-forget node creation. State updates go in the **handler body** (runs on the grain scheduler), not in the Subscribe callback:

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
hub.GetWorkspace().UpdateMeshNode(node => node with
{
    Content = content with { Messages = content.Messages.Add(id) }
});

// Handler returns immediately
return delivery.Processed();
```

### CRITICAL: Never Do State Updates in Subscribe Callbacks

Subscribe callbacks run on **arbitrary threads**. State updates (`workspace.UpdateMeshNode`) require the hub's scheduler. Mixing these causes deadlocks — this is not framework-specific, it's a fundamental consequence of truly async programming: you don't control which thread a callback runs on.

```csharp
// WRONG — callback runs on unknown thread, state update needs hub scheduler:
meshService.CreateNode(node).Subscribe(_ =>
{
    workspace.UpdateMeshNode(n => ...); // ← deadlock: wrong thread
});

// CORRECT — separate concerns: fire-and-forget for I/O, state update in handler body:
meshService.CreateNode(node).Subscribe();  // fire-and-forget
hub.GetWorkspace().UpdateMeshNode(n => ...);  // handler body = hub scheduler
```

The principle: **I/O is fire-and-forget, state changes happen where you control the thread.** This is true for any actor-based or message-passing system.

## UI Click Actions — Same Rules Apply

Blazor button clicks configured via `WithClickAction` flow through the layout area host, which is backed by a message hub. **The same rule applies: no `await` on mesh-backed operations inside the click handler.** The hub pump is shared with all the layout's other reactive updates — blocking it freezes the UI.

### The canonical reactive click handler

```csharp
.WithClickAction(ctx =>
{
    // 1. Immediate optimistic feedback so the user sees the click registered.
    ctx.Host.UpdateData(resultId, "<p>Working…</p>");

    // 2. Read form data via Subscribe (NOT await FirstAsync).
    //    The data stream emits its current value synchronously on subscribe.
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
            //    These compose hub.Post + RegisterCallback under the hood.
            myService.DoWork(label).Subscribe(
                result => ctx.Host.UpdateData(resultId, $"<p>Done: {result}</p>"),
                ex     => ctx.Host.UpdateData(resultId, $"<p>Error: {ex.Message}</p>"));
        });

    return Task.CompletedTask;  // click handler itself is synchronous
})
```

### Writing reactive services

Expose `IObservable<T>` (not `Task<T>`) from any service that will be called from a click handler or hub handler. Compose with `SelectMany`, `Select`, `FirstOrDefaultAsync` (the Rx operator, not the `IAsyncEnumerable` extension — do not `await` it).

```csharp
public IObservable<TokenResult> CreateToken(string label)
{
    var userNode = new MeshNode(...);
    return nodeFactory.CreateNode(userNode)                  // IObservable<MeshNode>
        .SelectMany(created =>
        {
            var indexNode = new MeshNode(...);
            return nodeFactory.CreateNode(indexNode)         // second reactive write
                .Select(_ => new TokenResult(rawToken, created));
        });
    // No await. Consumer calls .Subscribe(onNext, onError).
}

// ❌ WRONG — `Observable.FromAsync(() => query.FirstOrDefaultAsync().AsTask())`
//    is the fake-reactive pattern. It runs through the lagged read-side index,
//    can return stale content just after a write, and provides no live updates.
//
// ✅ CORRECT — read the current committed content directly from the owning hub:
public IObservable<bool> DeleteToken(string path)
{
    var workspace = hub.GetWorkspace();
    return workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
            new Address(path), new MeshNodeReference())
        .Take(1)
        .Timeout(TimeSpan.FromSeconds(10))
        .SelectMany(change => change.Value is null
            ? Observable.Return(false)
            : nodeFactory.DeleteNode(path));
}
```

See *[CQRS — Queries vs. Content Access](CqrsAndContentAccess)* for the full rule.

### Static handlers compose — don't wrap them in a service for "DI cleanliness"

Hub request handlers and other one-shot pipelines that **just compose I/O** (read inputs, fan out, render, post the response) belong in a **static class with private static helpers**. Do not extract them into an `IFooService` + instance class just because the body grew or because constructor-injecting `IMessageHub` and `IMeshService` "looks cleaner". The instance wrapper buys nothing here:

- The hub already carries the service provider (`hub.ServiceProvider.GetRequiredService<T>()`) — you can resolve dependencies inside the handler with one line.
- DI services exist to hold **state** (a singleton catalog, a per-circuit context, a cached resolver). A static handler holds none — every call is a pure observable chain.
- Adding an interface forces every existing call site to go through DI registration, fakes a "boundary" that has no observable difference, and increases the surface tests have to mock around.

The shape that scales:

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

        // Compose the pipeline — every step is IObservable, no await.
        hub.GetMeshNode(request.SourcePath, TimeSpan.FromSeconds(15))
            .SelectMany(root => brandingResolver.Resolve(request.Options.BrandNodePath)
                .Zip(CollectChapters(meshService, request, root),
                     (branding, chapters) => Render(request, root, chapters, branding)))
            .Subscribe(
                bytes => hub.Post(new ExportDocumentResponse(...), o => o.ResponseFor(delivery)),
                ex    => hub.Post(new ExportDocumentResponse(..., Error: ex.Message), o => o.ResponseFor(delivery)));

        return delivery.Processed();   // sync return — Subscribe is fire-and-forget
    }

    private static IObservable<List<(string, string)>> CollectChapters(...) { /* ... */ }
    private static byte[] Render(...) { /* ... */ }
}
```

When **scripts** want to reuse the same building blocks (e.g. an `ExportPdfTemplate.cs` Code node), they call the **public renderer types directly** (`new PdfDocumentRenderer().Render(doc)`) — they don't need a service interface either. The kernel script already has `Mesh.GetMeshNode`, `Log`, and `Ct`; pairing those with the public renderer/builder types is enough.

**Reach for an instance service only when there's actual state to hold.** Examples that justify it: a cache (`CompilationCacheService` — holds the `AssemblyLoadContext`), a per-circuit context (`AccessService` — `AsyncLocal<AccessContext>`), a registry that aggregates plug-ins (`PartitionRegistry`). A request handler that does load → render → respond is none of those.

If you find yourself thinking *"I'll extract this so it's testable"*: a static method that takes its dependencies as parameters is **already** testable — the test passes a stub `IMeshService` (or a real one from `MonolithMeshTestBase`) and calls the static directly. The interface adds an unused indirection.

### Anti-patterns in click handlers

```csharp
// ❌ async click handler with await — deadlocks under load.
.WithClickAction(async ctx =>
{
    var data = await ctx.Host.Stream.GetDataStream<T>(id).FirstAsync();
    var result = await myService.DoWorkAsync(data);  // hub-backed service
    ctx.Host.UpdateData(resultId, result);
})

// ❌ Task.Run as a "fix" — hides the problem: AccessContext doesn't flow,
// exceptions vanish into the thread pool, and you can't compose with other streams.
.WithClickAction(ctx =>
{
    _ = Task.Run(async () => { await myService.DoWorkAsync(); });
    return Task.CompletedTask;
})
```

## Post + Observe Pattern

For request-response flows where you need the result but can't block:

```csharp
// One call: posts the request and returns IObservable<IMessageDelivery<TResponse>>.
// onError fires for DeliveryFailureException / TimeoutException — never silent.
hub.Observe(new CreateNodeRequest(node), o => o.WithTarget(address))
    .Subscribe(
        resp =>
        {
            // Handle success — resp.Message is CreateNodeResponse
            DoSomething(resp.Message);
        },
        ex =>
        {
            // DeliveryFailureException (no route / no handler / unhandled),
            // TimeoutException (framework RequestTimeout fired), etc.
            logger.LogWarning(ex, "CreateNode failed");
        });

// Handler returns immediately — Subscribe callback fires off the action block
return delivery.Processed();
```

**Why `hub.Observe(...)` and not `RegisterCallback`:** the legacy `RegisterCallback` returns `Task<IMessageDelivery>` and the framework short-circuits the user callback for `DeliveryFailure` (the Task gets the exception, the callback never fires). Callers that drop the Task get silent infinite hangs on routing failures. `hub.Observe(...)` builds on the same TCS-backed Task internally but exposes it via `task.ToObservable()` so onError fires naturally — and there's no Task to be tempted to `await`.

### When you already have a delivery

If the request was already posted (e.g. you got the delivery from elsewhere), use the delivery overload:

```csharp
var delivery = hub.Post(request, o => ...);
hub.Observe(delivery).Subscribe(onNext, onError);
```

### Inside an `IObservable<T>` chain

When the surrounding code is an Observable returning function:

```csharp
public IObservable<TResult> DoOperation(...)
{
    return hub.Observe(new MyRequest(...))
        .SelectMany(resp =>
        {
            // Compose with downstream observables — no Subscribe yet.
            return hub.Observe(new SecondRequest(resp.Message.X));
        })
        .Select(secondResp => Project(secondResp.Message));
    // Caller subscribes — neither Post fires until then.
}
```

## 🚨 Node-mutating requests must run on the owning hub — forward, don't process locally

`UpdateNodeRequest`, `MoveNodeRequest`, and any future per-node mutation request **must** be processed on the owning per-node hub. The owning hub holds the authoritative MeshNode in its workspace (loaded by `MeshDataSource` at init via `MeshNodeReference`); the mesh hub does not.

When a request arrives at a hub that is not the owning hub (typically the mesh hub, where `IMeshService.UpdateNode` posts by default), **forward** it to the owning hub and relay the response — same shape as `UpdateThreadMessageContent` (which posts to the message's per-node address and is handled there by `HandleUpdateContent`).

```csharp
// Mesh hub's UpdateNodeRequest handler — forward to owning hub.
private static IMessageDelivery HandleUpdateNodeRequest(
    IMessageHub hub, IMessageDelivery<UpdateNodeRequest> request)
{
    var updatedNode = request.Message.Node;

    if (!hub.Address.ToString().Equals(updatedNode.Path, StringComparison.Ordinal))
    {
        var fwd = hub.Post(request.Message, o => o.WithTarget(new Address(updatedNode.Path)));
        hub.RegisterCallback((IMessageDelivery)fwd, response =>
        {
            if (response is IMessageDelivery<UpdateNodeResponse> ur)
                hub.Post(ur.Message, o => o.ResponseFor(request));
            else if (response.Message is DeliveryFailure df)
                hub.Post(UpdateNodeResponse.Fail(df.Message ?? "Forwarding failed"),
                    o => o.ResponseFor(request));
            return response.Processed();
        });
        return request.Processed();
    }

    // Own hub: read existing via the local MeshNodeReference stream and validate locally.
    var existingNodeObs = hub.GetWorkspace().GetMeshNodeStream()
        .Take(1).Timeout(TimeSpan.FromSeconds(15));
    // ... validate, persist, respond inside the Subscribe callback ...
}
```

**Why:** the mesh hub workspace doesn't carry a MeshNode collection — it has no `MeshNodeReference` reducer, no per-node validation context, no version tracking for the target. Trying to read existing state via `workspace.GetMeshNodeStream()` on the mesh hub throws `Failed to create stream`. Trying via `GetMeshNodeStream(path)` (remote subscription) hangs because no per-node hub has been activated yet for nodes the caller hasn't touched. Forwarding the request lets routing activate the owning hub on demand; that hub's `MeshDataSource` init loads the node from persistence, the gate opens, and the handler runs locally with `GetMeshNodeStream()` (own).

The 2026-04-24 PlanStorage / MeshNodeAuditing test failures all traced to the same bug: the mesh hub's UpdateNodeRequest handler was trying to read existing state locally. The fix is to forward to the owning hub. Same pattern applies to `MoveNodeRequest` (forward to source hub).

## 🚨 Blazor / GUI rule — *no `await` ever, no `Task.FromResult`, stay in observables*

> **Full treatment in [Blazor Async — `Subscribe`, not `await`](BlazorAsync).** That
> article is the practical playbook: lifecycle hooks, click handlers, parallel
> queries, multi-step flows, and the channel bridge for IAsyncEnumerable-shaped APIs.
> Read it before touching any `.razor` / `.razor.cs` file.

**Never** `await` a mesh operation in a Blazor component lifecycle method, click handler, autocomplete callback, or anywhere else. This is non-negotiable: every `await meshService.QueryAsync(...)`, `await meshService.UpdateNode(...)`, `await Hub.AwaitResponse(...)` in GUI code is a deadlock waiting to happen, and `Task.FromResult(snapshot)` is no better — it freezes the snapshot at the call moment and ignores live updates.

The pattern is: **maintain a state list (or scalar) outside the observable; subscribe to the mesh observable; when the observable emits, fold the new items into your state list (sorted/dedup as required) and call `StateHasChanged`**.

```csharp
public partial class MyView : ComponentBase, IDisposable
{
    // Maintained list — kept sorted by score / Path / whatever the view requires.
    private readonly List<Suggestion> _suggestions = new();
    private IDisposable? _sub;
    private string _query = "";

    private void RefreshSuggestions(string query)
    {
        if (query == _query) return;
        _query = query;
        _sub?.Dispose();
        _suggestions.Clear();

        // ObserveQuery — the live stream of result-set changes for this query.
        // Initial / Reset → replace; Added / Updated → upsert; Removed → drop.
        _sub = MeshQuery.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Subscribe(change =>
            {
                ApplyChange(change);                  // updates _suggestions IN PLACE
                _suggestions.Sort(BestMatchComparer); // keep the list sorted (e.g. by score)
                InvokeAsync(StateHasChanged);
            });
    }

    public void Dispose() => _sub?.Dispose();
}
```

The view binds to `_suggestions` directly — no callback that returns `Task<T[]>`. Where a third-party control (FluentUI Autocomplete, etc.) requires a `Task<T[]>` callback, bind to a property/list instead of using the callback; the observable subscription pushes updates into the bound list and `StateHasChanged` triggers re-render.

Specifically forbidden in GUI code (and the substitution to use):

| ❌ Wrong | ✅ Right |
|---|---|
| `var x = await mesh.QueryAsync(...).ToListAsync()` | `mesh.ObserveQuery<T>(req).Subscribe(c => ApplyChange(c))` |
| `await Hub.AwaitResponse<R>(req, ...)` | `Hub.Observe(req).Subscribe(r => UpdateState(r.Message), ex => …)` |
| `Hub.RegisterCallback(delivery, r => { … })` | `Hub.Observe(delivery).Subscribe(r => …, ex => …)` |
| `var n = await mesh.GetMeshNodeStream(p).Take(1).ToTask()` | live = `mesh.GetMeshNodeStream(p).Subscribe(n => UpdateState(n))`; one-shot = `Hub.GetMeshNode(p).Subscribe(n => …)` |
| `return Task.FromResult(_suggestions.ToArray())` | bind directly to `_suggestions`; let the `Subscribe` push updates |
| `_ = LoadAsync(); await ...` | sync method that fires `Subscribe` |

Lifecycle wiring:
- `OnParametersSet` (sync) — kick off `Refresh*()`; never `OnParametersSetAsync` for mesh reads.
- Click handlers — `() => { svc.Op().Subscribe(r => UpdateState(r)); }`; never `async ctx => await svc.Op()`.
- `Dispose` — clean up all `IDisposable` subscriptions to stop emissions after the component unmounts.

## 🚨 Copy / recursive subtree operations — `ObserveQuery` + `.Select(CreateNode)`

Recursive node operations (Copy, and Move which is Copy + Delete) MUST stay in the observable world end to end. **Never** read source content via `GetRemoteStream<MeshNode, MeshNodeReference>` for this — the remote stream subscribes to the owning per-node hub, which may not be activated yet for newly-created nodes, and the subscription waits indefinitely. **Never** use `await meshService.QueryAsync(...)` either — drop into the observable world via `ObserveQuery`.

The canonical shape for Copy:

```csharp
meshService.ObserveQuery<MeshNode>(MeshQueryRequest.FromQuery(
        $"path:{sourcePath} scope:subtree"))
    .Take(1)                                       // initial result set: source + descendants + satellites
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
                .SelectMany(retargeted => meshService.CreateNode(retargeted))   // ← Select(create)
                .ToList()
                .Select(_ => rootCreated));
    })
    .Subscribe(
        rootCreated => hub.Post(CopyNodeResponse.Ok(rootCreated), o => o.ResponseFor(request)),
        ex          => hub.Post(CopyNodeResponse.Fail(ex.Message),  o => o.ResponseFor(request)));
```

Move uses Copy then Delete — the Delete only fires after Copy completes (SelectMany short-circuits on Copy error):

```csharp
meshService.CopyNode(source, target, includeDescendants: true, includeSatellites: true)
    .SelectMany(copied => meshService.DeleteNode(source).Select(_ => copied))
    .Subscribe(
        movedNode => hub.Post(MoveNodeResponse.Ok(movedNode), o => o.ResponseFor(request)),
        ex        => hub.Post(MoveNodeResponse.Fail(ex.Message), o => o.ResponseFor(request)));
```

**Why:**
- `ObserveQuery` is observable from the start — no `await`, no `Observable.FromAsync` bridge over `QueryAsync`.
- The initial emission contains the full subtree snapshot we need to copy.
- `meshService.CreateNode(...).SelectMany(...)` chain ensures each child create completes before we move on. `.ToList()` aggregates all child completions before propagating success.
- `Move = Copy + Delete` keeps Move's handler trivial (no per-node logic, no subtree iteration); the Copy handler owns the recursion.
- `IncludeSatellites`/`IncludeDescendants` flags on `CopyNodeRequest` let callers opt in. By default Copy includes descendants but not satellites — Move sets both `true` to hard-move activity logs, comments, etc.

## 🚨 Reading the OWN node — `GetStream(new MeshNodeReference())`, never `GetStream<MeshNode>().FirstOrDefault`

To read the hub's **own** MeshNode (the node whose path equals the hub's address), use the dedicated own-node reducer:

```csharp
// ✅ Right — direct subscription to the MeshNodeReference reducer.
//    Always populated when MeshDataSource is registered.
workspace.GetStream(new MeshNodeReference())
    .Select(change => change.Value)
    .Where(node => node != null)
    .Subscribe(node => /* handle the own node */);
```

**Anti-pattern** — filtering `GetStream<MeshNode>()` by path:

```csharp
// ❌ Wrong — pulls the WHOLE InstanceCollection on every emission and filters
//    in C#. Allocates, scans, and emits one frame per collection mutation
//    (every other-node update too). Drops on the floor when the collection
//    hasn't loaded yet (FirstOrDefault returns null and the consumer waits).
workspace.GetStream<MeshNode>()
    ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == hub.Address.ToString()))
    .Where(n => n != null)
    .Subscribe(...);
```

The `MeshNodeReference` reducer is registered by `MeshDataSource.AddMeshDataSource()` for every hub that owns a node. If the call **throws** `InvalidOperationException("Failed to create stream")`, the workspace was misconfigured (no MeshDataSource on this hub) — return a NodeNotFound error response, don't let the exception escape and crash the delivery pipeline.

For reading any node by path (own or remote), use `workspace.GetMeshNodeStream(path)` which dispatches own → remote.

## 🚨 Writing a remote MeshNode — pick the right primitive

Two correct patterns, depending on what the caller is doing:

### One-shot fire-and-forget mutation — `DataChangeRequest`

When the caller just wants to push one update and walk away (handler that builds the new node and posts it, HTTP endpoint that performs an action, MCP tool, click action), post a `DataChangeRequest` directly:

```csharp
// ✅ Right for one-shot writes — owning hub's data layer (registered by
//    AddData()) handles DataChangeRequest natively: applies the patch,
//    persists, and broadcasts to subscribers via the synchronization
//    protocol. No subscription required, no SubscribeRequest round trip,
//    works even if no per-node hub is separately activated for the path.
hub.Post(new DataChangeRequest { Updates = [updatedNode] },
    o => o.WithTarget(new Address(updatedNode.Path)));
```

### Long-standing subscription that also writes — `GetRemoteStream + Update`

When the caller is *already subscribed* to the remote stream (live editor, dashboard view, collaborative session) and wants to push edits back through the same channel they're watching, use the remote stream's `.Update(...)`:

```csharp
// ✅ Right for long-standing streams — caller is already paying the
//    subscription cost; pushing the patch through the same stream means
//    the same subscriber sees the update echo back without an extra read.
var remote = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(targetPath), new MeshNodeReference());
remote.Update(current => current with { Name = "Renamed", LastModifiedBy = me });
// keep the subscription alive — the editor renders subsequent updates from it.
```

`stream.Update` ships the patch through the synchronization protocol; the owning hub validates it, runs its node validators, writes to persistence, and republishes to all subscribers (including the caller).

### Don't use `GetRemoteStream + Update` for one-shot writes

Subscribing just to push one update is wasteful: it incurs a `SubscribeRequest` round trip, allocates the subscription, and then disposes it. For nodes whose per-node hub isn't separately activated, the `SubscribeRequest` gets `DeliveryFailure` and the write is silently dropped — `DataChangeRequest` doesn't have that failure mode. **Rule of thumb:** if you're not also reading from the stream, use `DataChangeRequest`.

### `workspace.UpdateMeshNode(...)` is own-hub only

The local `UpdateMeshNode` extension writes through the data source's MeshNode partition stream — there's no remote variant. For remote, choose between `DataChangeRequest` (one-shot) or `GetRemoteStream + Update` (subscribed) per the rules above.

## 🚨 The canonical layout-area / Blazor view pattern — hold the stream, never the snapshot

For any view that **reads and writes** the same MeshNode (collaborative editor, dashboard with edit, layout area with click actions), hold the **`ISynchronizationStream<MeshNode>`** as a field — not a snapshot, not a `Take(1)` re-subscription per click. Subscribe once at init, write through `stream.Update(...)` on save:

```csharp
public partial class MyEditor : BlazorView<MyControl, MyEditor>
{
    // Long-standing per-node stream: subscribed at BindData, disposed via AddBinding.
    // Save handlers call _nodeStream.Update(...) to push edits through the same
    // stream the view is rendering from — the echo updates the view, no extra read.
    private ISynchronizationStream<MeshNode>? _nodeStream;

    protected override void BindData()
    {
        base.BindData();

        var workspace = Hub.GetWorkspace();
        try
        {
            _nodeStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
                new Address(BoundNodePath), new MeshNodeReference());
            AddBinding(_nodeStream
                .Where(change => change.Value != null)
                .Select(change => change.Value!.Content as MarkdownContent)
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
        catch
        {
            // Workspace has no MeshNodeReference reducer for this address —
            // fall back to one-time bind from the ViewModel.
            DataBind(ViewModel.Value, x => x.RawContent, defaultValue: "");
        }
    }

    private Task SaveAsync(string newContent)
    {
        if (_nodeStream == null) return Task.FromResult(false);
        _nodeStream.Update(current =>
            current with { Content = new MarkdownContent { Content = newContent } });
        return Task.FromResult(true);
    }
}
```

**Why this is the right shape:**

- `GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference())` is the **per-node reducer** — direct, no FirstOrDefault on a collection, no per-emission filter that allocates and scans.
- The stream is held **as a field**, not refetched on every save. One `SubscribeRequest` at view init; the subscription stays alive until the view is disposed.
- `_nodeStream.Update(...)` writes through the same stream the view is rendering from — the patch goes to the owning hub, the owning hub broadcasts the echo, the view's existing subscription updates `_processedHtml` / `RawContent` and re-renders. No extra read, no DataChangeRequest, no second subscription.
- Save-handler reuse is **free** — every click handler that needs the current node uses `_nodeStream.Update(current => ...)`. The lambda receives the live snapshot at apply time.

### Anti-patterns that show up in views

```csharp
// ❌ WRONG — Take(1) per save; subscribes, reads, disposes, every click.
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
// ... view subscribes, sets _currentNode on each emission ...
private Task SaveAsync(string newContent)
{
    var newNode = _currentNode! with { Content = ... };
    Hub.Post(new DataChangeRequest { Updates = [newNode] }, o => o.WithTarget(...));
    return Task.FromResult(true);
}

// ❌ WRONG — GetRemoteStream<MeshNode>(addr) (collection variant) + FirstOrDefault.
//   Pulls the WHOLE InstanceCollection on every emission; emits a frame whenever
//   ANY other node in the collection mutates; loses the typed write-back path.
workspace.GetRemoteStream<MeshNode>(new Address(addr))
    ?.Select(nodes => nodes?.FirstOrDefault(n => n.Path == path))
    .Subscribe(...);
```

**The reduce-to-MeshNode (`MeshNodeReference`) form is always preferred over reduce-to-`InstanceCollection` (`CollectionReference`) when you only care about one node** — direct reducer, narrower change feed, supports `.Update(...)` write-back.

## 🚨 Decision rule — single op vs. long-standing stream

The choice between `DataChangeRequest` and `GetRemoteStream + Update` is the same shape that comes up everywhere there's a hub-to-hub interaction (writes, reads, autocomplete, layout areas, …):

| Caller shape | Use |
|---|---|
| **Single operation** — handler builds the value once and is done; HTTP / MCP / CLI endpoints; click actions; one-shot writes | `hub.Post(new DataChangeRequest { … }, o => o.WithTarget(addr))` (or the equivalent one-shot request/response message) |
| **Long-standing stream** — anything that re-renders or re-computes when data changes; **all layout areas**; live editors; dashboards; collaborative views; **streaming autocomplete** | `workspace.GetRemoteStream<T, TRef>(addr, ref)` + `.Subscribe(...)` (and `.Update(...)` for write-back through the same stream) |

> **Rule of thumb:** if any downstream code keeps re-rendering when data changes, you need a long-standing stream. One-shot writes use `DataChangeRequest`.

The rule applies symmetrically:
- **Layout areas always subscribe to a stream** (live updates) and push edits back through `stream.Update(...)` — never `DataChangeRequest` for a write the area is also rendering.
- **Autocomplete** that streams suggestions in (the suggest widget repaints as items arrive) uses a long-standing stream subscription. A one-shot autocomplete (no incremental UI updates) uses request/response.
- **MCP tools** (agent calls a tool, gets a response, session ends) are always one-shot — write via `DataChangeRequest`, read via `QueryAsync` / `Get`. There is no live observer downstream of an MCP tool result, so a long-standing stream would only allocate and tear down on every call.
- **`MeshPlugin` tool methods** (Get / Search / Create / Update / Delete / NavigateTo) follow the same shape: each tool call maps to a single hub round-trip (request/response or `DataChangeRequest`) — never to a `GetRemoteStream` subscription.
- **HTTP / CLI endpoints** that render once and close are one-shot — `DataChangeRequest` on writes, `QueryAsync` on reads.
- **Handlers that persist a side-effect** (activity log, version write, audit) use `DataChangeRequest` — no live observer to keep alive.

### MeshNode write semantics: routing-supplied stream + sample-debounced save

Per-node hubs now follow this split:

| Operation | Path | Where it lives |
|---|---|---|
| **Read own MeshNode** (init + live updates) | Routing-supplied `IObservable<MeshNode>` (catalog stream / `Observable.Return(node)` on Monolith) attached via `config.WithOwnNodeStream(...)`. `DistinctUntilChanged().Replay(1).RefCount()` filters echoes; emissions seed the workspace and push subsequent updates without a duplicate persistence read | `MessageHubGrain.OnActivateAsync` / `MonolithRoutingService.CreateHub` plumb the stream into `MeshNodeTypeSource` |
| **Update own MeshNode** (editor-style writes) | Subscribe to `workspace.GetMeshNodeStream()`, `DistinctUntilChanged(n => n.Version)` to drop routing-stream echoes, `Sample(200ms)` to coalesce bursts, post `SaveMeshNodeRequest` per emission. Handler subscribes to `IStorageService.SaveNode` (already async at the storage adapter) | `MeshDataSource.SubscribeToOwnDeletion` registers the persistence sampler at hub init |
| **Create / Delete own MeshNode** | Direct `IStorageService.SaveNode` / `DeleteNode` from inside `MeshNodeTypeSource.UpdateImpl` — insta write, no debounce | Adds and deletes are infrequent and ordering matters |

The per-node hub does NOT keep a debounce buffer, a flush-on-dispose, or a Task-bridged save loop. Updates ride a single subscription on the workspace's MeshNode stream; the actor inbox serialises `SaveMeshNodeRequest` per node.

The classic "loop" risk — routing stream emits an external update → workspace emits → save subscriber posts → save handler writes → persistence emits → routing stream re-emits — is broken twice: `DistinctUntilChanged()` upstream of the workspace drops same-Version repeats, and `DistinctUntilChanged(n => n.Version)` on the save subscription suppresses the same Version landing on the persistence side again.

### Persistence belongs in MeshDataSource init — nowhere else

`IMeshStorage` is loaded **once**, during `MeshDataSource` initialization, to populate the workspace. After init, the workspace is the source of truth. Every read/write goes through reactive streams. **No handler ever calls `persistence.GetNodeAsync` or `persistence.SaveNode`** — not even as a fallback, not even as a one-liner. (The two sanctioned exceptions are the `SaveMeshNodeRequest` handler and the create/delete branches in `MeshNodeTypeSource.UpdateImpl`, described in the table above — both run inside the per-node hub at write time, not in application handlers.)

The wrong patterns (every line is a deadlock or a stale-content bug):

```csharp
// ❌ Reading existing state via persistence in a handler.
var existing = await persistence.GetNodeAsync(path, ct);

// ❌ "Fallback" to persistence when the workspace stream is empty.
//    The workspace being empty means the data isn't loaded — the fix is to
//    fix MeshDataSource init, not to bypass the workspace.
var obs = workspace.GetStream<MeshNode>() != null
    ? workspace.GetMeshNodeStream(path)
    : Observable.FromAsync(ct => persistence.GetNodeAsync(path, ct));

// ❌ Writing to a remote node by reaching into persistence directly.
//    Bypasses the owning hub's validators, version tracking, change feed,
//    and post-write hooks.
await persistence.SaveNode(node);
```

The right patterns:

| Operation | Primitive |
|---|---|
| Read own MeshNode | `workspace.GetMeshNodeStream()` |
| Read MeshNode at any path | `workspace.GetMeshNodeStream(path)` (auto-dispatches own / local collection / remote) |
| Update remote MeshNode | `workspace.GetRemoteStream<MeshNode, MeshNodeReference>(addr, new MeshNodeReference()).Update(current => updated)` |
| Update own MeshNode (in handler) | `workspace.UpdateMeshNode(node => updated)` |
| Create node | `meshService.CreateNode(node).Subscribe(...)` |
| Delete node | `meshService.DeleteNode(path).Subscribe(...)` |

Symptom of getting this wrong: persistence reads from a non-owning hub return stale content (workspace had a fresh value the persistence layer hadn't flushed yet); persistence writes from a non-owning hub silently skip validators, fail to update version history, and let other subscribers read stale workspace state.

## Workspace Updates (Non-Blocking)

`workspace.UpdateMeshNode` applies an update function to the current node state. It posts the update to the data stream — no blocking, no subscription:

```csharp
// Read current state and update atomically — no stream subscription needed
workspace.UpdateMeshNode(node =>
{
    var content = node.Content as MyContent ?? new MyContent();
    return node with
    {
        Content = content with { Status = "updated" }
    };
});
```

## AccessContext rides for free

Every framework write primitive (`IMeshService.CreateNode/UpdateNode/DeleteNode/CopyNode`, `MeshNodeStreamHandle.Update`, `IMeshNodeStreamCache.Update`) automatically captures the caller's `AccessContext` at invocation time and re-stamps it on every emission of the returned cold pipeline. Callers don't need any per-Subscribe wrapper:

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

The mechanism is `IObservable<T>.CarryAccessContext(IServiceProvider)` in `src/MeshWeaver.Messaging.Hub/AccessContextCaptureExtensions.cs`, applied INSIDE each framework primitive (not at the callsite). Full reference: [AccessContextPropagation.md](AccessContextPropagation.md).

Legitimate hub-internal writes that must bypass user identity (cache hydration, SyncStream heartbeats) opt in explicitly via `accessService.ImpersonateAsSystem()` or `accessService.ImpersonateAsHub(hub)` / `PostOptions.ImpersonateAsHub`. PostPipeline fails closed otherwise — the silent hub-self-impersonation fallback was deleted 2026-05-21.

## Rules Summary

| Pattern | Safe in Handlers? | Notes |
|---------|-------------------|-------|
| `hub.Post(...)` | Yes | Fire-and-forget, safe from any thread |
| `hub.Observe(request).Subscribe(onNext, onError)` | Yes | Reactive request/response; DeliveryFailure → onError |
| `hub.Observe(delivery).Subscribe(...)` | Yes | Same, when the delivery was already posted |
| `meshService.CreateNode(...).Subscribe()` | Yes | Fire-and-forget, no callback logic |
| `workspace.UpdateMeshNode(...)` in handler body | Yes | Runs on grain scheduler |
| `hub.RegisterCallback(...)` | **OBSOLETE** | Use `hub.Observe(...)` — RegisterCallback's Task short-circuits DeliveryFailure → callback silently never fires → caller hangs |
| `await hub.AwaitResponse(...)` | **OBSOLETE / NO** | Use `hub.Observe(request).Subscribe(...)` (production) or `MonolithMeshTestBase.AwaitResponseAsync(...)` (test) |
| `Observable.FromAsync(() => hub.RegisterCallback(...))` | **NEVER** | Bridges Task back into Rx; continuation captures sync-context → deadlock. Use `hub.Observe(...)` instead. |
| `workspace.UpdateMeshNode(...)` in Subscribe callback | **NO** | Wrong thread in Orleans, deadlocks |
| `meshService.QueryAsync(...)` | **NO** | Blocks waiting for response |
| `await someTask` | **NO** | Blocks the hub scheduler |
| `hub.InvokeAsync(...)` | **NO** | Schedules on potentially blocked scheduler |
| `stream.Subscribe(...)` | **Risky** | May deadlock if stream observes on hub scheduler |

## When async/await IS Safe

`async/await` is safe in contexts that don't run on the hub's scheduler:
- Blazor component event handlers (`OnClick`, `OnInitializedAsync`)
- HTTP middleware and API controllers
- Background services and hosted services
- Test code

The rule is simple: **if your code runs inside a hub message handler (registered via `.WithHandler<T>()`), never await.**

## Blocking Execution (AI Streaming)

Sometimes you genuinely need long-running I/O — for example, streaming an AI response. This uses a **hosted hub** (`_Exec`) that runs the blocking work on its own thread via `hub.InvokeAsync`. But even here:

- All **state updates** (workspace, thread content) go through the **parent hub** or through a long-lived workspace stream — never via per-chunk messages between hubs
- All **messages** go through the parent hub — never post to the execution hub
- The execution hub is purely for hosting the blocking I/O — it should never own state

> **Streaming content into a thread message: use a long-lived `GetRemoteStream(...).Update(...)` from the writer.** Posting `UpdateThreadMessageContent` (or any per-chunk message) between hubs causes the deadlock surface that `OrleansReentrancyTest.ToolCall_DuringStreaming_DoesNotDeadlock` exists to catch. See [Thread Execution Streaming](xref:Architecture/ThreadExecutionStreaming) for the full design and [Per-Hub TaskScheduler](xref:Architecture/OrleansTaskScheduler) for the threading-model rules that complete the picture (each non-grain hub gets its own `TaskScheduler`; the grain hub keeps the grain's scheduler for activity attribution).

```csharp
// In HandleSubmitMessage (runs on thread hub):
var executionHub = hub.GetHostedHub(new Address($"{hub.Address}/_Exec"), ...);
executionHub.Post(request);  // Only message to execution hub: start the work

// In ExecuteMessageAsync (runs on _Exec hub):
var parentHub = hub.Configuration.ParentHub!;
var workspace = parentHub.GetWorkspace();

// Open the per-message remote stream once at start, hold for the streaming run.
var responseStream = workspace.GetRemoteStream<MeshNode, MeshNodeReference>(
    new Address(responsePath), new MeshNodeReference());

// Push every delta through the stream — fire-and-forget, no shared scheduler in the write path.
responseStream.Update(node => node with { Content = (ThreadMessage)node.Content with { Text = ... } });

// Thread-state updates that aren't on the message itself stay on parentHub.UpdateMeshNode.
parentHub.GetWorkspace().UpdateMeshNode(node => node with { /* IsExecuting, etc. */ });
```

The parent hub's scheduler is free (the handler returned `delivery.Processed()` immediately). State updates and callbacks process normally on it. Per-message content writes flow through the workspace stream so the renderer sees them without the writer paying for a hub-to-hub round trip per chunk.
