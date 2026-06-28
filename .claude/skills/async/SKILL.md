---
name: async
description: Forwarding AccessContext through async/reactive boundaries, and the no-async rule on the actor-model mesh. Use when writing or reviewing any hub-reachable / Blazor-view / agent-round / IIoPool code that creates, reads, or updates a mesh node, OR when a write "silently does nothing" / "Access denied" appears after a .Subscribe / IIoPool / Observable.Create hop. The async/await/ToTask/.Wait/.Result/.GetAwaiter/FirstAsync family and lost AccessContext are the two ways async boundaries cause wedges and storms. Grounded in AsynchronousCalls.md, SyncedMeshNodeQueries.md, AccessContextPropagation.md.
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /async — Cross async boundaries without wedging or losing identity

Everything on this mesh is `IObservable<T>` end-to-end. An "async boundary" is any place a
continuation runs on a *different thread* than the one that started it: a `.Subscribe(...)`
callback, an `IIoPool` leaf, an `Observable.Create`/`FromAsync`, an `await`. Two things break at
these boundaries, and both end in a wedge or a [storm](../storm/SKILL.md):

1. **You blocked the hub** — `await` / `Task<T>` / `.ToTask()` / `.Result` / `.Wait()` /
   `.GetAwaiter().GetResult()` / `.FirstAsync()` (blocking) on a hub action block, grain turn, or
   Blazor circuit parks the single-threaded scheduler. The message you're waiting for can never be
   processed → **deadlock**.
2. **You lost the identity** — `AccessService.Context` is an `AsyncLocal`. It is **wiped** when a
   continuation lands on a pool/scheduler thread that didn't carry it. The write then posts with
   *no* `AccessContext` → `PostPipeline` **fails closed** → partition RLS denies → the write
   silently does nothing (or "Access denied"). The caller usually swallows that → upstream retries
   → **storm**.

> Canonical references — read the relevant one BEFORE writing the call:
> - [AsynchronousCalls.md](../../../src/MeshWeaver.Documentation/Data/Architecture/AsynchronousCalls.md) — the reactive patterns + the mistake ledger. The first stop for any hub/UI call.
> - [AccessContextPropagation.md](../../../src/MeshWeaver.Documentation/Data/Architecture/AccessContextPropagation.md) — how identity flows (and is lost) across `.Subscribe`/IIoPool hops.
> - [SyncedMeshNodeQueries.md](../../../src/MeshWeaver.Documentation/Data/Architecture/SyncedMeshNodeQueries.md) — reading a node live via the synced stream instead of a one-shot blocking await.
> - [ControlledIoPooling.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md) — the ONE sanctioned async boundary: `IIoPool`.

## Rule 1 — Never block. Compose and Subscribe.

In `src/` (anything hub-reachable, every Blazor view/component, every agent round) the following
are **red flags to delete**, not to write:

```
async / await / Task<T> / Task.Run / TaskCompletionSource
.ToTask() / .Result / .Wait() / .GetAwaiter().GetResult()
.FirstAsync().Wait()        // blocking variant
Observable.FromAsync(...)   // FORBIDDEN outside IoPool itself — runs the prologue on the subscriber thread, no bound
SemaphoreSlim / lock-around-await / ManualResetEventSlim   // hand-woven async gates
```

Instead: return `IObservable<T>` and chain with `.Select` / `.SelectMany` / `.Where` /
`.Timeout`, then **`.Subscribe(onNext, onError)`**. Dependent work goes in `.SelectMany`, never
`await`. A genuinely-async leaf (DB, blob, HTTP, Roslyn, `Process`, sync file IO) goes through
`IIoPool` — `pool.Invoke(ct => …Async(ct))` / `pool.InvokeBlocking(ct => …)` / `pool.Run(...)`
(the promise-cached one-shot). `IIoPool` is the *only* place the turn-based world meets real async,
and it runs off-hub with `ConfigureAwait(false)`.

```csharp
// ❌ deadlocks the hub
var node = await workspace.GetMeshNodeStream(path).FirstAsync();

// ✅ compose + subscribe (server AND Blazor); never .Take(1) on a stream feeding a live view
workspace.GetMeshNodeStream(path)
    .Where(n => n is not null).Take(1).Timeout(TimeSpan.FromSeconds(10))
    .Subscribe(n => { /* use n */ }, ex => logger.LogWarning(ex, "read failed for {Path}", path));
```

Tests are the *only* place `await …FirstAsync().ToTask()` is acceptable.

> Reading a single node's content? Use the synced stream (`GetMeshNodeStream(path)`), not
> `QueryAsync` (eventually consistent → stale after writes) and not a blocking await. See
> SyncedMeshNodeQueries.md.

## Rule 2 — Carry the identity across every boundary

Every framework write primitive (`meshService.CreateNode/UpdateNode/DeleteNode`,
`GetMeshNodeStream(path).Update(...)`, `IMeshNodeStreamCache.Update`) **snapshots
`AccessService.Context` at the moment you CALL it** and carries that snapshot through the eventual
`.Subscribe()`. So the question is always: *was the right context set when I called the write?*

- **Inside a normal hub handler** — the `MessageHub` already stamped the caller's context from
  `delivery.AccessContext`. Just call the write. ✅
- **Inside a `.Subscribe(...)` callback / an `IIoPool` body / an `Observable.Create` / a reactive
  hop far from the handler** — the AsyncLocal is gone. You **must** re-establish it at the write
  call site:

```csharp
// External/Rx-hop write under the circuit/round user — re-stamp at the .Update() call site
using (accessService.SwitchAccessContext(user))            // user = the originating identity
    workspace.GetMeshNodeStream(path).Update(cur => cur with { Content = … })
        .Subscribe(_ => { }, ex => logger.LogWarning(ex, "update failed for {Path}", path));

// Infrastructure write (cache hydration, activity progress, heartbeat emit) → System
using (accessService.ImpersonateAsSystem())                // "system-security", All granted
    NotificationService.CreateNotification(meshService, …)
        .Subscribe(_ => { }, ex => logger.LogWarning(ex, "notify failed"));
```

The owner re-stamp pattern (re-establish `SwitchAccessContext(owner)` at *each* cross-hub
`Append`/`Finish` write) is shown in `ContentIndexingActivity.cs` and
`MeshWeaver.GitSync.ActivityRunner`. The "silently stamp hub-self as principal" fallback was
**deleted** (2026-05-21) — application code that writes MUST carry a real identity.

### The smell test

> If your write runs on (or is awaited from) a `.Subscribe` callback, an `IIoPool` leaf, an
> `Observable.Create`, or any thread that isn't the original handler turn — **assume the
> AccessContext is gone** and wrap the write in `SwitchAccessContext`/`ImpersonateAsSystem`. If you
> don't, it fails closed, you swallow it, and something upstream retries it into a storm.

## Cold observables: Subscribe is mandatory

Writes are **cold** — the side effect runs on `Subscribe`, not on call. A composed write you never
subscribe to **silently does nothing** (the chat-doesn't-work root cause). Always `.Subscribe(_ =>
{ }, onError)`. `Update(...)` returns a `RequireSubscribe` observable that logs a warning at GC if
never subscribed — grep the `MeshWeaver.Mesh.RequireSubscribe` channel after a run.

## Checklist before committing any hub/UI/agent write

- [ ] No `async`/`await`/`Task<T>`/`.ToTask()`/`.Result`/`.Wait()`/`.GetAwaiter()`/`Observable.FromAsync` outside a test or `IIoPool` internals.
- [ ] Genuinely-async leaf goes through `IIoPool`, returns `IObservable<T>`.
- [ ] Every write is `.Subscribe(onNext, **onError**)` — error arm present and either surfaces or logs at a graceful boundary (never an empty swallow that lets a retry loop — see [/storm](../storm/SKILL.md)).
- [ ] If the write is in a `.Subscribe`/IIoPool/`Observable.Create`/reactive hop, the AccessContext is re-established (`SwitchAccessContext(user)` or `ImpersonateAsSystem()`) at the call site.
- [ ] No `.Take(1)` on a stream feeding a live data-bound view (freezes the binding).
