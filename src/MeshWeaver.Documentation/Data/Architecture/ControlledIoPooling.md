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

> **Read first:** [Asynchronous Calls](AsynchronousCalls) and [Orleans Task Scheduler](OrleansTaskScheduler). This page is the I/O-edge counterpart to those two — where the actor model meets real, blocking work.

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

## 🚨 The rule: never raw `Observable.FromAsync` at an I/O leaf

**Every genuine async/blocking I/O leaf goes through `IIoPool`.** Writing `Observable.FromAsync(ct => SomeIoAsync(ct))` directly at an I/O edge is forbidden.

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

The handful of **non-I/O** `Observable.FromAsync` bridges that already run off the hub scheduler are out of scope: message-routing callbacks, init-gates over a hub round-trip, opaque user-supplied `InitializationFunction`s. The litmus test is simple — if `FromAsync` wraps a *real* file/network/DB/compile wait, it must be a pool call instead.

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

## Scope — and why storage is NOT pooled

This pool targets the **blocking I/O leaves that would otherwise block the single-threaded hub scheduler**: outbound HTTP (MCP mirror, social publishing), NuGet resolution, Roslyn script execution, and external processes (`dotnet test`). These are low-frequency and genuinely deadlock-prone.

**Storage and file-system adapters are deliberately left on plain `Observable.FromAsync`** (not pooled) for two reasons:

- Their I/O is already asynchronous — the `await` is on the real file/blob call, which already runs off the hub turn.
- They sit on the *hottest* path in the system — every node read/write, every hub init, every permission evaluation.

Routing them through the pool added a `SubscribeOn(TaskPoolScheduler.Default)` hop to **every** read. Under CI's constrained ThreadPool this starved init/permission reads past their timeouts (hub-init failures, spurious "access denied"); the bound also throttled a path that needs high concurrency. For storage the pool's cost outweighs its benefit — the existing async `FromAsync` is the right shape.

---

## Why Postgres is left as-is

`PostgreSqlMeshQuery` / `PostgreSqlVersionQuery` already do `Observable.FromAsync(work, Scheduler.Default)` bounded by Npgsql's `MaxPoolSize` (the correct, role-specific, driver-enforced bound). An extra `IIoPool` gate on top would be redundant and could deadlock against the connection pool if mis-sized. There is no gap there — leave it.

---

## Edge cases — the review checklist

These four properties must hold for every leaf that uses `IIoPool`:

- **Pool the leaf, never the orchestration.** `IIoPool` is injected into leaf adapters only. Orchestration layers (`PersistenceService` fan-out, version-writing decorator, query providers, `MeshQuery`) compose adapter observables and hold **no** slot — a fan-out of N reads acquires N leaf slots and never nests behind a held one. Per-resource pools mean a FileSystem leaf never blocks on a Blob leaf while holding a FileSystem slot. **Never let an `IIoPool` call resolve an observable that itself acquires the same pool** — that is the one way to deadlock it.

- **Cancellation and release.** Every shape releases its slot in a `finally`. `WaitAsync(ct)` makes acquisition itself cancellable — a dispose before the slot is granted throws before the in-flight increment, so no slot leaks. The subscription's `ct` flows into the leaf, so file/blob calls cancel on unsubscribe.

- **Disposal.** `IoPoolRegistry` disposes each `IoPool`, which disposes its `SemaphoreSlim`. The limited-concurrency scheduler borrows ThreadPool threads — nothing to dispose.

- **No sync-context capture.** `.SubscribeOn(TaskPoolScheduler.Default)` + `.ConfigureAwait(false)` everywhere; `InvokeBlocking` dispatches via the scheduler-bound `TaskFactory`, never `TaskScheduler.Current`. Safe even when `Subscribe` is called inside a grain handler.

---

## Applied to (current scope)

| Pool | Used by |
|---|---|
| **Http** | `McpRemoteMeshClient` (MCP mirror), Social publishers (`ScheduledPostPublisher` / `PostStatsRefresher` / `PastPostIngestJob`) |
| **Process** | `MeshPlugin.RunTests` (`dotnet test` via `Process.Start`, `InvokeBlocking`) |

The `Compile` pool exists but is currently unused: pooling `KernelExecutor`'s Roslyn run was reverted because it perturbed the carefully-ordered REPL execution path (submission-order / no-deadlock guarantees).

Also not pooled (see [Scope](#scope--and-why-storage-is-not-pooled) above): storage / file-system adapters (already-async, hottest path), `MeshNodeCompilationService` (recompile-timing path), `NuGetAssemblyResolver` (`MeshWeaver.NuGet` doesn't reference `Mesh.Contract`). Each can be revisited with care.

---

## Cross-references

- [Asynchronous Calls](AsynchronousCalls) — rule 9, "the async boundary lives at the real I/O edge".
- [Aggregating Providers](AggregatingProviders) — "the async boundary lives at the I/O edge", pool-at-the-edge.
- [Orleans Task Scheduler](OrleansTaskScheduler) — per-hub schedulers and the `SubscribeOn` offload this complements.
