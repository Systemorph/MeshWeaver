---
NodeType: Markdown
Name: "Controlled I/O Pooling — bounding the async edge"
Abstract: "Hub and grain code is single-threaded and turn-based; genuine I/O (file system, blob, HTTP, compile, process) must run off that scheduler, on the shared ThreadPool, and bounded so a fan-out can't exhaust handles/sockets or starve Orleans. IIoPool is the one hidden primitive that does this — the generalization of the Postgres Observable.FromAsync(work, Scheduler.Default) + connection-pool pattern to every resource that carries no pool of its own."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><rect x='4' y='5' width='16' height='3' rx='1.5' fill='white'/><rect x='4' y='10.5' width='11' height='3' rx='1.5' fill='white'/><rect x='4' y='16' width='6' height='3' rx='1.5' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Threading"
  - "IO"
  - "Orleans"
---

> **Read first:** [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) and [Orleans Task Scheduler](/Doc/Architecture/OrleansTaskScheduler). This page is the I/O-edge counterpart to those two — where the actor model meets real, blocking work.

## The problem

Every hub is an actor running on a **single-threaded, turn-based scheduler** — the Orleans grain scheduler for the root hub, `TaskScheduler.Default` for every other hub. That single-threading is a guarantee about *state*, not a claim that the process has one thread: the same process owns the multi-threaded .NET ThreadPool.

Genuine I/O at the leaves — a file read, a blob download, an HTTP call, a Roslyn compile, a `Process.Start` — must therefore satisfy two requirements:

1. **Run off the hub scheduler.** A bare `await` inside a handler captures `TaskScheduler.Current` and queues its continuation back onto the hub's single turn — blocking the action block, or (across hubs that share a scheduler) deadlocking. The work has to be handed explicitly to the ThreadPool.
2. **Be bounded.** Without a cap, a mesh of thousands of per-node hubs can each subscribe to the same kind of I/O at once, issuing thousands of concurrent file handles or sockets. This exhausts the resource and — for sync-blocking work — triggers ThreadPool thread-injection that starves the very pool Orleans' grain turns rely on.

Postgres already solved this: `Observable.FromAsync(work, Scheduler.Default)` pushes the DB round-trip onto the ThreadPool, and Npgsql's connection pool (`MaxPoolSize`, sized per role — 20/2/1) is the concurrency governor. File system, blob, HTTP, compile, and process carry **no pool of their own**. `IIoPool` is that missing governor, generalized and uniform.

---

<svg viewBox="0 0 760 340" xmlns="http://www.w3.org/2000/svg" style="width:100%;max-width:760px;height:auto;display:block;margin:20px auto;">
  <defs>
    <marker id="arr" markerWidth="8" markerHeight="8" refX="6" refY="3" orient="auto">
      <path d="M0,0 L0,6 L8,3 z" fill="currentColor" fill-opacity=".6"/>
    </marker>
  </defs>
  <rect x="20" y="40" width="130" height="60" rx="10" fill="#1565c0"/>
  <text x="85" y="65" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">Hub</text>
  <text x="85" y="82" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">(single-threaded</text>
  <text x="85" y="96" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">turn scheduler)</text>
  <rect x="20" y="160" width="130" height="60" rx="10" fill="#1565c0"/>
  <text x="85" y="185" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">Hub</text>
  <text x="85" y="202" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">(single-threaded</text>
  <text x="85" y="216" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#bbdefb">turn scheduler)</text>
  <rect x="20" y="270" width="130" height="44" rx="10" fill="#37474f"/>
  <text x="85" y="288" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">more hubs</text>
  <text x="85" y="305" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#cfd8dc">(N per process)</text>
  <line x1="155" y1="70" x2="230" y2="128" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="155" y1="190" x2="230" y2="148" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="155" y1="292" x2="230" y2="165" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="230" y="110" width="140" height="70" rx="10" fill="#6a1b9a"/>
  <text x="300" y="133" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">IIoPool</text>
  <text x="300" y="150" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#e1bee7">SemaphoreSlim</text>
  <text x="300" y="165" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#e1bee7">concurrency gate</text>
  <line x1="370" y1="145" x2="420" y2="100" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="370" y1="145" x2="420" y2="145" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="370" y1="145" x2="420" y2="195" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="420" y="65" width="140" height="50" rx="10" fill="#00695c"/>
  <text x="490" y="85" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">ThreadPool worker</text>
  <text x="490" y="102" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b2dfdb">Invoke (async)</text>
  <rect x="420" y="120" width="140" height="50" rx="10" fill="#00695c"/>
  <text x="490" y="140" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">ThreadPool worker</text>
  <text x="490" y="157" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b2dfdb">InvokeBlocking (CPU)</text>
  <rect x="420" y="175" width="140" height="50" rx="10" fill="#00695c"/>
  <text x="490" y="195" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">ThreadPool worker</text>
  <text x="490" y="212" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#b2dfdb">InvokeStream</text>
  <line x1="560" y1="90" x2="605" y2="90" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="560" y1="145" x2="605" y2="145" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <line x1="560" y1="200" x2="605" y2="200" stroke="currentColor" stroke-opacity=".5" stroke-width="1.5" marker-end="url(#arr)"/>
  <rect x="605" y="60" width="130" height="50" rx="10" fill="#e65100"/>
  <text x="670" y="80" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">HTTP / Blob</text>
  <text x="670" y="97" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffe0b2">cap: 16 / 32</text>
  <rect x="605" y="118" width="130" height="50" rx="10" fill="#bf360c"/>
  <text x="670" y="137" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">Compile / Process</text>
  <text x="670" y="154" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#ffe0b2">cap: nCPU / 4</text>
  <rect x="605" y="175" width="130" height="50" rx="10" fill="#4e342e"/>
  <text x="670" y="194" text-anchor="middle" font-family="sans-serif" font-size="12" fill="#fff" font-weight="bold">FileSystem</text>
  <text x="670" y="211" text-anchor="middle" font-family="sans-serif" font-size="11" fill="#d7ccc8">cap: nCPU</text>
  <text x="85" y="16" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">Hubs (actor model)</text>
  <text x="300" y="16" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">Pool gate</text>
  <text x="490" y="16" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">ThreadPool</text>
  <text x="670" y="16" text-anchor="middle" font-family="sans-serif" font-size="11" fill="currentColor" fill-opacity=".55">I/O resources</text>
  <rect x="20" y="245" width="130" height="4" rx="2" fill="currentColor" fill-opacity=".15"/>
  <line x1="150" y1="145" x2="230" y2="145" stroke="currentColor" stroke-opacity=".15" stroke-dasharray="4,3" stroke-width="1"/>
</svg>

*IIoPool routes all I/O leaves off the hub scheduler onto bounded ThreadPool workers, with per-resource concurrency caps.*

---

## 🚨🚨🚨 ABSOLUTE: `Observable.FromAsync` is NEVER tolerated

**`Observable.FromAsync(...)` is FORBIDDEN everywhere in `src/` — no exceptions.** Not for storage, not for Postgres, not for "it already runs off the scheduler", not for a one-off. There is exactly **one** place the call may appear in the entire codebase: sealed *inside* `IoPool` (the primitive). Anywhere else it is a defect to be removed. Every genuine async/blocking I/O leaf goes through `IIoPool`.

A bare `Observable.FromAsync` only schedules *notification delivery*. It invokes the function's synchronous prologue on the **subscribing thread** — which is the hub/grain scheduler when the subscribe happens mid-handler — and applies no concurrency bound. That is the entire bug class this primitive exists to kill.

```csharp
// ❌ FORBIDDEN — runs the prologue on the subscriber (hub) thread, unbounded
=> Observable.FromAsync(ct => httpClient.SendAsync(req, ct));

// ✅ REQUIRED — routed through the resource-class pool: off the hub scheduler, bounded
=> _httpPool.Invoke(ct => httpClient.SendAsync(req, ct));
```

Pick the method by leaf kind:

| Method | Use for |
|---|---|
| `Invoke` | Genuinely-async leaves (HTTP, blob, DB, async file) |
| `InvokeBlocking` | Sync-blocking / CPU leaves (Roslyn compile, `File.ReadAllBytes`, `Process`) |
| `InvokeStream` | `IAsyncEnumerable` sources (partition objects, etc.) |

There is no "out of scope" residue. If you find yourself typing `Observable.FromAsync`, stop: the answer is an `IIoPool` call (or, for an idempotent one-shot, the promise-cache below). The only `FromAsync` that survives a review is the one inside `IoPool` itself.

### Promise-cache for idempotent one-shots

For work that should run **at most once** and then be observed by many (schema provisioning, a cached resource handshake), cache the eager `pool.Run(...)` observable in an **instance** `ConcurrentDictionary<key, IObservable<T>>` (never static):

```csharp
// PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned — the canonical example.
// First caller kicks the CREATE SCHEMA off on the per-adapter pool; every later subscriber
// replays the cached completion. No Observable.FromAsync at the call site.
private readonly ConcurrentDictionary<string, IObservable<Unit>> _provisioned = new();

public IObservable<Unit> EnsurePartitionProvisioned(string @namespace) =>
    _provisioned.GetOrAdd(schema, _ =>
        _ioPool.Run(ct => EnsureSchemaAsync(def, ct)).Select(_ => Unit.Default));
```

`pool.Run` is `ReplaySubject`-backed (see [IoPoolExtensions](#hidden-inside-the-interfaces)) — eager, single-run, replays to all. That is the "promise pattern": the dictionary entry *is* the promise.

---

## The primitive

`IIoPool` (in `MeshWeaver.Mesh.Threading`) is the single sealed boundary between the hub schedulers and the I/O. It is **hidden inside the leaf adapters** — public signatures stay `IObservable<T>`; callers never see a pool.

```csharp
public interface IIoPool
{
    // Genuinely-async leaf (blob, HTTP, async file, DB round-trip).
    IObservable<T> Invoke<T>(Func<CancellationToken, Task<T>> io);

    // Sync-blocking / CPU leaf (File.ReadAllBytes, Roslyn compile, Process).
    IObservable<T> InvokeBlocking<T>(Func<CancellationToken, T> work);

    // IAsyncEnumerable leaf (partition objects), bridged to a bounded observable.
    IObservable<T> InvokeStream<T>(Func<CancellationToken, IAsyncEnumerable<T>> source);

    int CurrentInFlight { get; }   // diagnostics / tests only
}
```

All three return **cold** observables: the work runs on `Subscribe`, a pool slot is taken only on `Subscribe`, and released when the operation completes, errors, or is unsubscribed. This keeps the `MeshWeaver.Mesh.RequireSubscribe` semantics accurate — a never-subscribed leaf never takes a slot and never runs.

### The hybrid governor

The concurrency cap is enforced two ways, chosen per leaf kind:

| Leaf kind | Mechanism | Why |
|---|---|---|
| **Genuinely-async** (`Invoke`, `InvokeStream`) | `SemaphoreSlim` async gate, then `.SubscribeOn(TaskPoolScheduler.Default)` | The gate caps **in-flight ops**; the ThreadPool thread is *released during the await*, so a cap of 32 network ops uses ~0 threads while waiting. `SubscribeOn` moves the whole subscribe — gate wait and the function's synchronous prologue — onto the ThreadPool, so it never runs on the calling hub scheduler. (`FromAsync`'s own scheduler argument only schedules *notification delivery*, not where the function is invoked — hence `SubscribeOn`, exactly as `MeshQuery` does.) |
| **Sync-blocking / CPU** (`InvokeBlocking`) | Dedicated `LimitedConcurrencyLevelTaskScheduler` | Blocking work holds a real thread for its whole duration. The limited-concurrency scheduler borrows ThreadPool threads but dispatches at most *cap* at a time, so a burst can't trigger runaway thread-injection that starves Orleans' grain schedulers. |

> This design is "compatible with how Orleans wants us to pool": it **reuses the ThreadPool the framework already uses** and merely puts a governor in front of it — no custom OS threads that Orleans can't see or coordinate with.

---

## Named pools and caps

Pools are keyed by resource class and resolved lazily from `IoPoolRegistry` (a mesh-scoped singleton, disposed with the mesh — no static state). Caps come from `IoPoolOptions`, with sensible defaults that a host can override via `AddIoPools(o => o with { Blob = 64 })` without any call-site change.

| Pool (`IoPoolNames`) | Default cap | Wave |
|---|---|---|
| `FileSystem` | `Environment.ProcessorCount` | 1 |
| `Blob` | 32 | 1 |
| `Http` | 16 | 2 |
| `Compile` | `Environment.ProcessorCount` | 3 |
| `Process` | 4 | 3 |
| `pg:{adapter}` (per Postgres adapter) | **1** (mirrors the adapter's single Npgsql connection) | — |

---

## Hidden inside the interfaces

A leaf takes an optional `IIoPool? pool = null` and stores `_pool = pool ?? IoPool.Unbounded`. Each leaf reads uniformly:

```csharp
// HTTP leaf (McpRemoteMeshClient) — was Observable.FromAsync(async ct => …)
public IObservable<MeshNode?> Get(string path)
    => _httpPool.Invoke(async ct => { var client = await GetClientAsync(ct); … });

// CPU / process leaf — InvokeBlocking on the dedicated limited-concurrency scheduler
=> _compilePool.InvokeBlocking(ct => RunRoslynScript(…));   // KernelExecutor
=> _processPool.InvokeBlocking(ct => RunTestsCore(…));      // MeshPlugin.RunTests
```

`IoPool.Unbounded` is a stateless fallback used when a leaf is constructed with `new` outside DI. It still offloads onto the ThreadPool — never *worse* than the bare `Observable.FromAsync` it replaces — but applies no cap. It is an immutable constant, not a cache. Pools come from the mesh-scoped `IoPoolRegistry` (registered by `MeshBuilder.AddIoPools()`), resolved from the owning hub's `ServiceProvider` or injected via the leaf's constructor.

---

## Streaming an agent response into a cell — the precise process

> **Invariant: the thread hub must never block.** A blocked thread turn stops answering `GetData` / `GetPermission` / tool-call responses for its *own* output cell — so the response never renders and the round wedges (`GetDataRequest@{thread}/{cell}` pending for tens of seconds, `GetPermissionRequest` timing out). That **is** the entire "harness doesn't work after submit" symptom. **Therefore the streaming round runs in the I/O pool, never on the thread turn.** The pool is not an optimisation here — it is the mechanism that keeps the actor's single turn free while the multi-second LLM enumerable drains on a bounded ThreadPool worker.

An LLM round is the archetypal `InvokeStream` leaf: `IChatClient.GetStreamingResponseAsync(...)` returns an `IAsyncEnumerable<ChatResponseUpdate>` — a genuine async I/O source that must run **off the thread hub's scheduler** and be **bounded**, exactly like a blob download or an HTTP call. It is never consumed with a bare `Task.Run(async () => await foreach …)` on (or launched from) the hub turn: that runs the enumerator's continuations under the grain scheduler, and a tool call that needs the same scheduler to answer then deadlocks against the in-flight `await foreach`. That is the "harness hangs after submit" failure — the thread hub stops answering `GetData`/`GetPermission` for its own output cell.

The correct path is exactly **three steps**, and the **output cell is the rendezvous**: the pool writes it, the GUI reads it, and neither blocks on the other.

**1 — Resolve the output cell and mark it streaming.** The round's last entry in `MeshThread.Messages` is the assistant output cell; its path is `{threadPath}/{ActiveMessageId}`. Confirm the last cell *is* the output (assistant) cell, take that as the streaming target, and flip its `Status` to `Streaming` so the GUI renders a live cell:

```csharp
// thread.ActiveMessageId is the canonical handle; the full output path derives from it.
var output = $"{threadPath}/{thread.ActiveMessageId}";   // the last (assistant) cell in Messages
workspace.GetMeshNodeStream(output).Update(node =>
        node with { Content = ((ThreadMessage)node.Content) with { Status = ThreadMessageStatus.Streaming } })
    .Subscribe(_ => { }, ex => logger.LogWarning(ex, "mark-streaming failed for {Path}", output));
```

**2 — Stream *in the pool*, writing each chunk to the cell's sync stream.** Consume the LLM `IAsyncEnumerable` through `IIoPool.InvokeStream` (off the hub scheduler, bounded — never `Task.Run`), and fold every chunk into the output cell via `GetMeshNodeStream(output).Update(...)`. The owning cell hub serialises the writes on its single-threaded action block (no race, no clobber), and the grain scheduler stays free to answer the round's tool-call responses:

```csharp
var acc = new StringBuilder();
ioPool.InvokeStream(ct => chatClient.GetStreamingResponseAsync(messages, options: null, ct))
    .Sample(StreamingSampleInterval)        // one cell write per sampled tick, NOT per token
    .Subscribe(
        update =>
        {
            acc.Append(update.Text);
            workspace.GetMeshNodeStream(output).Update(node =>
                    node with { Content = ((ThreadMessage)node.Content) with { Text = acc.ToString() } })
                .Subscribe(_ => { }, ex => logger.LogWarning(ex, "stream write failed for {Path}", output));
        },
        ex => SetCellStatus(output, ThreadMessageStatus.Error),
        () => SetCellStatus(output, ThreadMessageStatus.Completed));   // terminal: flip Status once
```

**3 — The GUI subscribes to the same cell stream.** The Blazor view databinds the output cell with `GetMeshNodeStream(output)` (or `GetRemoteStream<MeshNode>`), rendering `Content.Text` as it grows and reacting to the terminal `Status`. It reads the **exact node the pool is writing** — the cell is the single source of truth, so there is no second channel to reconcile.

Why this is deadlock-free, point by point: the enumerator runs on a **pool** ThreadPool worker (step 2), never the grain turn, so an in-flight tool call still gets the scheduler. The cell writes go through the **owning hub's serialised action block** via the stream handle — a non-blocking cross-hub patch, not a synchronous wait. The GUI **only reads** (step 3). Three actors, one cell, no one blocks another.

> 🚫 **The anti-pattern this replaces.** `Task.Run(async () => { await foreach (var u in client.GetStreamingResponseAsync(…)) cell.Update(…); })` *looks* offloaded, but it (a) is **unbounded** — N concurrent rounds spawn N enumerators with no governor — and (b) bypasses `IIoPool`, so it is invisible to the pool's diagnostics and cancellation, and any synchronous wait on the output cell from the hub turn (e.g. a sync-handshake read of a cell that isn't reachable yet) still wedges the hub. Route the enumerable through `InvokeStream`; the offload, the bound, and the cancellation come for free.

---

## Scope — storage and Postgres are pooled too

Earlier guidance carved storage and Postgres out of the pool and left them on plain `Observable.FromAsync`. **That carve-out is rescinded — there is no exemption.** `FromAsync` is never tolerated (see the absolute rule above), so storage / file-system / Postgres leaves go through `IIoPool` like everything else.

**Per-adapter pools, cap = the connection count.** A Postgres storage adapter holds a single Npgsql connection (`MaxPoolSize=1`); its pool is named `pg:{adapter}` and capped at **1**, so the `IIoPool` gate *is* that one connection ("hook into the pg pool") rather than a redundant bound stacked on top of it. The naming + cap live in `IoPoolNames.PostgresAdapterPrefix` / `IoPoolOptions.MaxConcurrencyFor`. This sidesteps the old worry about a mis-sized gate deadlocking against the driver pool: the gate and the driver pool are the same size (1), by construction.

The cost concern that originally justified the carve-out (a `SubscribeOn` hop on every hot read under a constrained CI ThreadPool) is real — the answer is to size the per-adapter pools correctly, **not** to fall back to bare `FromAsync`. The migration is finished: every query/storage leaf is pooled (see "The sweep is complete" below), and new code (e.g. `PostgreSqlPartitionStorageProvider.EnsurePartitionProvisioned`) is pooled from day one.

---

## Edge cases — the review checklist

These four properties must hold for every leaf that uses `IIoPool`:

- **Pool the leaf, never the orchestration.** `IIoPool` is injected into leaf adapters only. Orchestration layers (`PersistenceService` fan-out, version-writing decorator, query providers, `MeshQuery`) compose adapter observables and hold **no** slot — a fan-out of N reads acquires N leaf slots and never nests behind a held one. Per-resource pools mean a FileSystem leaf never blocks on a Blob leaf while holding a FileSystem slot. **Never let an `IIoPool` call resolve an observable that itself acquires the same pool** — that is the one way to deadlock it.

- **Cancellation and release.** Every shape releases its slot in a `finally`. `WaitAsync(ct)` makes acquisition itself cancellable — a dispose before the slot is granted throws before the in-flight increment, so no slot leaks. The subscription's `ct` flows into the leaf, so file/blob calls cancel on unsubscribe.

- **Disposal.** `IoPoolRegistry` disposes each `IoPool`, which disposes its `SemaphoreSlim`. The limited-concurrency scheduler borrows ThreadPool threads — nothing to dispose.

- **No sync-context capture.** `.SubscribeOn(TaskPoolScheduler.Default)` + `.ConfigureAwait(false)` everywhere; `InvokeBlocking` dispatches via the scheduler-bound `TaskFactory`, never `TaskScheduler.Current`. Safe even when `Subscribe` is called inside a grain handler.

- **🚨 The pool carries NO `AccessContext` — it is an identity hole, by design.** `.SubscribeOn(TaskPoolScheduler.Default)` re-runs the whole subscribe (gate wait + the leaf's synchronous prologue + every `ConfigureAwait(false)` continuation) on a bare ThreadPool worker. That worker has **no `AsyncLocal` baton** — `AccessService.Context` reads as null/whatever the pool thread last held, NOT the originating user. The pool is the *threading* primitive (get IO off the action-block thread, bounded); it is deliberately **not** an *identity* primitive. Contrast the mesh write/read primitives (`MeshNodeStreamCache.Update` / `GetMeshNodeStream` / `GetQuery`), which wrap the cold pipeline in `CarryAccessContext(accessService)` — capturing the caller's identity synchronously on the caller's thread and re-stamping `AsyncLocal` on each emission. **So: never read or write a *mesh node* from inside an `IIoPool` leaf and expect RLS to see the user** — the leaf runs identity-less. Genuine external IO (subprocess, HTTP, blob) doesn't touch `AccessService.Context` and is fine. If a pooled leaf genuinely must do an authorized mesh operation, capture the identity *before* the pool boundary and pass it explicitly, or — far better — do the mesh read/write through `GetQuery` / `GetMeshNodeStream` (which carry the baton) and keep only the non-mesh IO in the pool. This is why the Copilot model catalog moved off `ioPool.Run(... ListModelsAsync ...)` and onto `workspace.GetQuery(...)`: the picker is per-user, and only the GetQuery path stamps the subscriber's identity.

---

## Disposal is reactive — `Dispose()` fires, the mesh drains

There is **no `DisposeAsync()` and no `IAsyncDisposable`** anywhere — not in the public API, not on
any hub or resource. The whole shape is deleted. An `await DisposeAsync()` inside hub-reachable code
(or a shared-scheduler fixture) captures `TaskScheduler.Current` and queues its continuation back onto
the very turn that is tearing down → the turn never drains → deadlock. Disposal is synchronous +
reactive instead:

- **`Dispose()` (`IDisposable`) fires and returns.** It does the synchronous unregister
  (e.g. `IRoutingService.RegisterStream` returns a synchronous unregister handle) or *kicks off*
  reactive teardown — it never blocks on it. `IDisposable` is the only disposal interface a hub
  or resource implements.
- **Asynchronous teardown is observed through `IObservable<Unit> DisposalCompleted`** (the shape
  `IMessageHub` uses), not awaited. `Dispose()` triggers it; completion is a reactive emission.
- **Someone has to subscribe.** `DisposalCompleted` is cold — with no subscriber the completion is
  never observed. That subscriber is the **mesh teardown**, never the individual hub or call site:
  the mesh's disposal merges its hubs'/resources' `DisposalCompleted` and waits for them all to emit
  before it completes. Hub-reachable code only *exposes* `DisposalCompleted`; it never subscribes to
  or drains its own.
- **Let them all finish.** When the teardown owns several reactive completions, drain them together so
  they all emit before it returns — never an awaited loop that serialises (and can deadlock on) each one.

### 🚨 The mesh teardown drains THREE things, not one

`DisposalCompleted` is **necessary but NOT sufficient**. It drains the hub's action blocks and
in-flight message round-trips — but **I/O offloaded through `IIoPool` runs on the ThreadPool,
independent of the action block, and `DisposalCompleted` knows nothing about it.** If the teardown
disposes the service scope after `DisposalCompleted` but while an `IIoPool` operation (or any other
async cleanup) is still in flight, that continuation resolves a service from the dead Autofac scope and
throws `ObjectDisposedException: …LifetimeScope… has already been disposed` — unobserved, it surfaces
as an xUnit **"catastrophic failure"** that aborts the whole run.

So the mesh teardown awaits **all three**, in order, before the scope is disposed:

1. `IMessageHub.DisposalCompleted` — action blocks + message round-trips. (Resources enqueue their
   async cleanup onto the `AsyncDisposeQueue` during this synchronous-`Dispose()` phase — `Dispose()`
   must never block, so async cleanup is *queued*, not run inline.)
2. `IoPoolRegistry.WhenDrained(timeout)` — offloaded ThreadPool I/O (`TotalInFlight == 0`).
3. `AsyncDisposeQueue.DrainAsync(timeout)` — the queued async cleanup. A TPL `ActionBlock` drains it;
   `DrainAsync` `Complete()`s the block and awaits the remainder (bounded), so it converges even under
   continuous influx — a *version-target* wait would not (the queue is a message stream / endless
   messages). `DrainedVersion` advances once per item, the test hook.

**The only sanctioned `await` is that single three-phase drain at the boundary** — the **mesh teardown**,
the same in tests and in prod (the silo's mesh disposal at shutdown). Capture the mesh-scoped teardown
services *before* `Dispose()` (never resolve DI once disposal has begun), then drain all three, bounded:

```csharp
// ✅ The one drain, at the mesh-teardown boundary (test mesh OR prod silo shutdown).
//    Either call the canonical helper:
await mesh.TeardownAsync(TimeSpan.FromSeconds(15));   // MeshWeaver.Mesh.MeshTeardownExtensions

//    …or, if you drive Dispose() yourself, do the phases by hand:
var ioPools = mesh.ServiceProvider.GetService<IoPoolRegistry>();        // capture BEFORE Dispose()
var disposeQueue = mesh.ServiceProvider.GetService<AsyncDisposeQueue>();
mesh.Dispose();
await mesh.DisposalCompleted
    .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
    .FirstOrDefaultAsync().ToTask().WaitAsync(TimeSpan.FromSeconds(15));   // phase 1
if (ioPools is not null)
    await ioPools.WhenDrained(TimeSpan.FromSeconds(15)).FirstAsync().ToTask();   // phase 2
if (disposeQueue is not null)
    await disposeQueue.DrainAsync(TimeSpan.FromSeconds(15));               // phase 3
// ONLY NOW dispose the service scope.
```

"Only drainage of async pipelines is allowed": the `await` lives at that one (two-phase) drain, the work
stays reactive. Same principle as `IIoPool` — the async boundary is pushed to the edge and bounded; it is
never an ambient `await` mid-flow. Full order + failure mode: [Mesh Lifecycle](/Doc/Architecture/MeshLifecycle). See also
[Asynchronous Calls](/Doc/Architecture/AsynchronousCalls).

---

## Applied to (current scope)

| Pool | Used by |
|---|---|
| **Http** | `McpRemoteMeshClient` (MCP mirror), Social publishers (`ScheduledPostPublisher` / `PostStatsRefresher` / `PastPostIngestJob`), `CopilotConnectStrategy` (SDK calls), `KernelExecutor` (`#r nuget` restore), `GoogleGeocodingService` (geocode fan-out) |
| **Process** | `MeshPlugin.RunTests` (`dotnet test` via `Process.Start`), `ClaudeConnectStrategy` / `CopilotConnectStrategy` (CLI spawn + scrape) |
| **Compile** | `KernelExecutor.RunOnePass` (the interactive Roslyn script compile+execute). The per-submission `executionLock` still serialises REPL order; the pool only bounds compiles across kernels and shares the gate with NodeType compilation, so a script compile and a NodeType compile never race on the same collectible-ALC assembly file (the deadlock a thread dump caught) |
| **FileSystem** | `TypeSource` initial-data load, `MeshExtensions` post-creation handler invoke |
| **`pg:{adapter}` / Cosmos** | `PostgreSqlStorageAdapter`, `PostgreSqlVersionQuery`, `PostgreSqlPartitionedMeshQuery`, `PostgreSqlPartitionStorageProvider`, `CosmosStorageAdapter`, `CosmosMeshQuery` — every DB round-trip goes through `_ioPool.Invoke`/`Run` |

**The sweep is complete.** `Observable.FromAsync` no longer appears anywhere in `src/`, `test/`, `samples/`, or `memex/` — the only occurrence is sealed inside `IoPool`. The former "migration debt" query/storage sites (`PostgreSqlMeshQuery` family, Cosmos, file-system adapters) are now pooled; orchestration that isn't an I/O leaf (layout view generators, message-delivery and routing bridges) was rewritten as pure reactive composition (`Observable.Create` / `Defer` + `Task.ToObservable()`), never `FromAsync`.

---

## Cross-references

- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) — rule 9, "the async boundary lives at the real I/O edge".
- [Aggregating Providers](/Doc/Architecture/AggregatingProviders) — "the async boundary lives at the I/O edge", pool-at-the-edge.
- [Orleans Task Scheduler](/Doc/Architecture/OrleansTaskScheduler) — per-hub schedulers and the `SubscribeOn` offload this complements.
