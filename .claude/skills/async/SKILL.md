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

### 🚨🚨🚨 ABSOLUTE: `.ToTask()` is FORBIDDEN — everywhere, tests included

**Maintainer, 2026-08-30: "no ToTask ever."** There is no test exemption, no "one-line adapter"
exemption, no helper that may hide one. Earlier revisions of this page, `AGENTS.md` and
[/testing](../testing/SKILL.md) said tests were the sanctioned edge; that is retracted.

**Why the test edge was never safe either** — and this repo measured it: a `Task` completed from
inside an Rx pipeline (exactly what `FirstAsync().ToTask()`, an `AsyncSubject` or a
`TaskCompletionSource` resolved on an `OnNext` does) resumes its awaiter **inline, on the
signalling thread, still inside Rx's trampoline** (`Producer.SubscribeRaw`). Everything the
continuation then does inherits that flag — a 558-frame stack in the reproduction shows it
escaping the pipeline entirely. So a bridge written "only in a test" changes how the code under
test runs, and a green test proves the wrong thing.

**What to write instead**

```csharp
// ✅ await the observable DIRECTLY (Rx's own awaiter), bounded so a hang is a failure
await hub.DisposalCompleted.FirstOrDefaultAsync().Timeout(TimeSpan.FromSeconds(30));

// ✅ or stay reactive and assert on the stream
await stream.Where(x => x is not null).FirstAsync().Timeout(30.Seconds());
```

**The one place it may work — and usually still should not:** inside an **activity**, where the
work is already off the hub turn and nothing mesh-side runs after the await. Even there, prefer the
reactive composition; reach for a bridge only when an external API forces a `Task` on you, and say
in a comment why the reactive shape was impossible.

### Rule 1a — A `Task`-returning override you must implement: subscribe, return `Task.CompletedTask`

Some signatures are not ours — `Grain.OnDeactivateAsync`/`OnActivateAsync`, `IHostedService`,
ASP.NET middleware. The rule is not "await is fine here": it is **do not await, hang the work off
the signal, and return a completed Task**. A grain turn is a single-threaded scheduler, so awaiting
inside it parks the very scheduler the thing you are waiting for may need in order to finish.

```csharp
// ❌ parks the grain turn, and races a timer to decide whether the work happened
var done = hub.DisposalCompleted.FirstOrDefaultAsync().ToTask(ct);
if (await Task.WhenAny(done, Task.Delay(TimeSpan.FromSeconds(5))) == done)
    loadContext.Unload();          // …and if the timer won? unload anyway? that is the bug

// ✅ subscribe; the turn returns immediately and the work belongs to the signal
hub.DisposalCompleted
    .Take(1)                                   // unsubscribes on first emission — no rooted subscription
    .Catch<Unit, Exception>(ex => { logger.LogError(ex, "…"); return Observable.Empty<Unit>(); })
    .Subscribe(_ => UnloadContextIfSafe());
return Task.CompletedTask;
```

**This deletes a whole class of bug, not just the deadlock.** With a timer race you must answer
"what if the wait expired?", and the tempting answer — do it anyway — is exactly how a collectible
`AssemblyLoadContext` gets unloaded out from under live code (`MessageHubGrain`; CI run
32713409169 caught it as a dedicated thread faulting on its first managed call, JIT-compiling a
dynamic method whose allocator was already gone). With a subscription there is no timer and no
branch: the callback runs when the signal says so, or it never runs — and "never runs" means the
resource is simply retained. Prefer retaining memory over acting on a guess.

`Take(1)` matters: a subscription that never completes roots whatever the callback closes over
(the discarded-timer-roots-the-hub defect).

### Rule 1b — Handle stream faults FLUENTLY, never with `try`/`catch` around `Subscribe`

A `try`/`catch` wrapped around a subscription only sees a throw from the *synchronous* subscribe
call. A fault travelling through the stream arrives later, usually on another thread, and sails
straight past it — so the `catch` you wrote to make the code safe never runs.

```csharp
// ❌ catches almost nothing that actually goes wrong
try { stream.Subscribe(x => Handle(x)); } catch (Exception ex) { logger.LogError(ex, "…"); }

// ✅ the fault is part of the stream, so handle it in the stream
stream
    .Catch<T, Exception>(ex => { logger.LogError(ex, "…"); return Observable.Empty<T>(); })
    .Subscribe(x => Handle(x));
```

Choose the recovery deliberately: `Observable.Empty<T>()` means "this did not happen" — downstream
`OnNext` never runs, which is what you want when the callback performs an irreversible action —
while `Observable.Return(fallback)` means "carry on with a default". Keep a `try`/`catch` only for
genuinely synchronous work sitting beside the chain, and say so in a comment, so the next reader
does not believe it covers the stream.

> Reading a single node's content? Use the synced stream (`GetMeshNodeStream(path)`), not
> `QueryAsync` (eventually consistent → stale after writes) and not a blocking await. See
> SyncedMeshNodeQueries.md and [/mesh-data](../mesh-data/SKILL.md).

### 🚨🚨🚨 ABSOLUTE: `Observable.FromAsync` is NEVER tolerated

**Writing `Observable.FromAsync(...)` anywhere in `src/` is FORBIDDEN — no exceptions, no "Postgres
is special", no "storage is the hot path".** A bare `FromAsync` runs the function's synchronous
prologue on the **subscribing thread** (the hub/grain scheduler when the subscribe happens
mid-handler) and applies no concurrency bound — the exact deadlock-and-exhaustion bug class the I/O
pool exists to kill. There is exactly **one** place `FromAsync` may appear: sealed *inside* `IoPool`
itself. Everywhere else it is a defect.

**Every async / blocking / IO edge goes through `IIoPool`** (`MeshWeaver.Mesh.Threading`), resolved
from `IoPoolRegistry` (mesh-scoped singleton — never static):

| You have | Use |
|---|---|
| A `Task<T>`-returning leaf (DB round-trip, blob, HTTP, async file) | `pool.Invoke(ct => SomethingAsync(ct))` — or `pool.Run(...)` for the eager **promise-cache** (ReplaySubject-backed: runs once, replays to all) |
| A sync-blocking / CPU leaf (`File.ReadAllBytes`, Roslyn compile, `Process`) | `pool.InvokeBlocking(ct => Work(ct))` — or `pool.RunBlocking(...)` for the promise-cache |
| An `IAsyncEnumerable<T>` leaf | `pool.InvokeStream(...)` / `pool.RunStream(...)` |

**The promise-cache pattern (idempotent one-shots like schema provisioning):** hold the
`pool.Run(...)` observable in an *instance* **`PromiseCache<TKey,TValue>`** (or `PromiseSlot<TValue>`
when there is no key) — never static, and 🚨 **never a bare
`ConcurrentDictionary<key, IObservable<T>>`**: a `ReplaySubject` latches `OnError` too, so a bare
dictionary replays ONE transient fault to every later caller for the life of the process (#1369 — a
single connect blip left a partition permanently un-provisionable, every write `42P01`).
`PromiseCache` caches success and **evicts a fault, pair-exact** — the next caller re-attempts; it
never retries on its own. Canonical:
`PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned`
(`_provisioned.GetOrAdd(schema, _ => _ioPool.Run(ct => EnsureSchemaAsync(def, ct)))`). PG pools are
named `pg:{adapter}` and capped at **1** so the gate *is* the single Npgsql connection.

- **Public surface returns `IObservable<T>`, never `Task<T>`.** A `Task`-returning method that does
  IO is the smell; rewrite it to return `IObservable<T>` and bridge the leaf through `IIoPool`
  internally.
- **MCP/SDK surface adapters** must not bridge either: an external signature that demands a
  `Task` is the ONLY reason to have one, and the body still stays reactive — see the ABSOLUTE rule
  above, which admits no `.ToTask()` anywhere.

Full reference:
[ControlledIoPooling.md](../../../src/MeshWeaver.Documentation/Data/Architecture/ControlledIoPooling.md).

### 🚨🚨🚨 ABSOLUTE: no hand-woven async/concurrency primitives — not even a `SemaphoreSlim`

**A `SemaphoreSlim` (or any hand-rolled async gate / lock-for-async / signal) anywhere in `src/` is
FORBIDDEN — outside the one place sealed inside `IoPool`.** `SemaphoreSlim.WaitAsync()`
blocks/parks a thread and its continuation captures the awaiting scheduler. On a hub it parks the
single-threaded action block (or a grain turn) → the message you're waiting on can never be
processed → **deadlock**. This is the same defect class as `async`/`await`/`Task<T>` in hub code; a
`SemaphoreSlim` is just a lock-shaped version of it.

- **Serialization channels through the hub, never a semaphore.** "Only one at a time" / "wait your
  turn" is what the hub's single-threaded action block already gives you for free. When you need
  ordered, one-at-a-time processing, push items into a `Subject<T>` and run them with
  `.Select(Run).Concat().Subscribe(...)` — `Concat` subscribes the next only after the previous
  completes, so you get order without a lock (the canonical fix is `KernelExecutor`'s REPL queue,
  which **replaced** a hand-woven `SemaphoreSlim`) — or route state changes through
  `GetMeshNodeStream(path).Update(...)`, where the owning hub serialises every writer.
- **Concurrency bounding / one-shot init / "run once" channels through `IIoPool`** — a bounded I/O
  gate, a promise-cached one-shot (schema provisioning, blob-cache init, connect handshake), a
  "first caller does it, the rest wait". That is `pool.Run(...)` held in an **instance**
  `PromiseCache`/`PromiseSlot`. NOT a `SemaphoreSlim(1,1)` `_initLock` / `_connectGate`.
- **`Task`-as-a-gate is the same sin.** `TaskCompletionSource` used to make callers "await a
  signal", a `Task.Delay` timeout race, `ManualResetEventSlim`, `lock`-around-`await` — all
  hand-woven async. Make the **source observable** (`AsyncSubject`/`Subject` + `Concat`) and
  `Subscribe`, or push it onto `IIoPool`.

**The ONLY sanctioned `SemaphoreSlim` is the one sealed inside `IoPool` itself** — it IS the single
boundary between the turn-based hub schedulers and genuinely-async I/O leaves, running work OFF the
hub with `ConfigureAwait(false)`. Everywhere else, a `SemaphoreSlim` is a bug to delete. The litmus
test: if your gate runs on (or is awaited from) a hub action block / grain turn / Blazor circuit, it
deadlocks — channel it through a hub or `IIoPool` instead.

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

A write that must run as the hub rather than a user (legitimate infrastructure — cache hydration,
SyncStream heartbeats) says so explicitly: `using (accessService.ImpersonateAsHub(hub)) { … }`, or
`o.ImpersonateAsHub(hub.Address)` on the post, which stamps the hub's address as principal.

The owner re-stamp pattern (re-establish `SwitchAccessContext(owner)` at *each* cross-hub
`Append`/`Finish` write) is shown in `ContentIndexingActivity.cs` and
`MeshWeaver.GitSync.ActivityRunner`. `PostPipeline` **fails closed** when no context is set, and the
"silently stamp hub-self as principal" fallback was **deleted** (2026-05-21) — it masked the prod
EventCalendar bug. Application code that writes MUST carry a real user identity on
`AccessService.Context` (the MessageHub sets it on every handler invocation from
`delivery.AccessContext`).

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

- [ ] No `async`/`await`/`Task<T>`/`.Result`/`.Wait()`/`.GetAwaiter()`/`Observable.FromAsync` outside `IIoPool` internals — and **no `.ToTask()` at all, tests included** (see the ABSOLUTE rule above).
- [ ] Genuinely-async leaf goes through `IIoPool`, returns `IObservable<T>`.
- [ ] Every write is `.Subscribe(onNext, **onError**)` — error arm present and either surfaces or logs at a graceful boundary (never an empty swallow that lets a retry loop — see [/storm](../storm/SKILL.md)).
- [ ] If the write is in a `.Subscribe`/IIoPool/`Observable.Create`/reactive hop, the AccessContext is re-established (`SwitchAccessContext(user)` or `ImpersonateAsSystem()`) at the call site.
- [ ] No `.Take(1)` on a stream feeding a live data-bound view (freezes the binding).
