---
NodeType: Markdown
Name: "Controlled I/O Pooling — bounding the async edge"
Abstract: "Hub and grain code is single-threaded and turn-based; genuine I/O (file system, blob, HTTP, compile, process) must run off that scheduler, on the shared ThreadPool, and bounded so a fan-out can't exhaust handles/sockets or starve Orleans. IIoPool is the one hidden primitive that does this — the generalization of the Postgres `Observable.FromAsync(work, Scheduler.Default)` + connection-pool pattern to every resource that carries no pool of its own."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#00695c'/><rect x='4' y='5' width='16' height='3' rx='1.5' fill='white'/><rect x='4' y='10.5' width='11' height='3' rx='1.5' fill='white'/><rect x='4' y='16' width='6' height='3' rx='1.5' fill='white'/></svg>"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Threading"
  - "IO"
  - "Orleans"
---

> Read first: [Asynchronous Calls](AsynchronousCalls) and [Orleans Task Scheduler](OrleansTaskScheduler). This page is the I/O-edge counterpart to those two — where the actor model meets real, blocking work.

## The problem

Every hub is an actor running on a **single-threaded, turn-based scheduler** — the Orleans grain scheduler for the root hub, `TaskScheduler.Default` for every other hub. That single-threading is a guarantee about **state**, not a claim that the process has one thread: the same process owns the multi-threaded .NET ThreadPool.

Genuine I/O at the leaves — a file read, a blob download, an HTTP call, a Roslyn compile, a `Process.Start` — must therefore do two things:

1. **Run off the hub scheduler.** A bare `await` inside a handler captures `TaskScheduler.Current` and queues its continuation back onto the hub's single turn — blocking the action block, or (across hubs that share a scheduler) deadlocking. The work has to be handed explicitly to the ThreadPool.
2. **Be bounded.** Without a cap, a mesh of thousands of per-node hubs can each subscribe to the same kind of I/O at once and issue thousands of concurrent file handles / sockets — exhausting the resource and, for sync-blocking work, triggering ThreadPool thread-injection that starves the very pool Orleans' grain turns run on.

Postgres already solved this: `Observable.FromAsync(work, Scheduler.Default)` pushes the DB round-trip onto the ThreadPool, and **Npgsql's connection pool** (`MaxPoolSize`, sized per role — 20/2/1) is the concurrency governor. File system, blob, HTTP, compile and process carry **no pool of their own**. `IIoPool` is that missing governor, generalized and uniform.

## 🚨 The rule: never raw `Observable.FromAsync` at an I/O leaf

**Every genuine async/blocking I/O leaf goes through `IIoPool` — `Observable.FromAsync(ct => SomeIoAsync(ct))` written directly at an I/O edge is forbidden.** A bare `Observable.FromAsync` only schedules *notification delivery*; it invokes the function's synchronous prologue on the **subscribing thread** — which is the hub/grain scheduler when the subscribe happens mid-handler — and applies no concurrency bound. That is the bug class this primitive exists to kill.

```csharp
// ❌ FORBIDDEN — runs the prologue on the subscriber (hub) thread, unbounded
=> Observable.FromAsync(ct => httpClient.SendAsync(req, ct));

// ✅ REQUIRED — routed through the resource-class pool: off the hub scheduler, bounded
=> _httpPool.Invoke(ct => httpClient.SendAsync(req, ct));
```

Pick the method by leaf kind: `Invoke` for async (HTTP, blob, DB, async file), `InvokeBlocking` for sync-blocking/CPU (Roslyn compile, `File.ReadAllBytes`, `Process`), `InvokeStream` for `IAsyncEnumerable`. The handful of **non-I/O** `Observable.FromAsync` bridges that already run off the hub scheduler are out of scope: message-routing callbacks, init-gates over a hub round-trip, opaque user-supplied `InitializationFunction`s. The litmus test is unchanged — if `FromAsync` wraps a *real* file/network/DB/compile wait, it must be a pool call instead.

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

All three return **cold** observables: the work runs on `Subscribe`, a pool slot is taken only on `Subscribe`, and released when the operation completes, errors, or is unsubscribed. That keeps the `MeshWeaver.Mesh.RequireSubscribe` semantics accurate — a never-subscribed leaf never takes a slot and never runs.

### The hybrid governor

The cap is enforced two ways, picked per leaf:

| Leaf kind | Mechanism | Why |
|---|---|---|
| **Genuinely-async** (`Invoke`, `InvokeStream`) | `SemaphoreSlim` async gate, then `.SubscribeOn(TaskPoolScheduler.Default)` | The gate caps **in-flight ops**; the ThreadPool thread is *released during the await*, so a cap of 32 network ops uses ~0 threads while waiting. `SubscribeOn` moves the whole subscribe — gate wait + the function's synchronous prologue — onto the ThreadPool, so it never runs on the calling hub scheduler. (`FromAsync`'s own scheduler argument only schedules *notification delivery*, not where the function is invoked — hence `SubscribeOn`, exactly as `MeshQuery` does.) |
| **Sync-blocking / CPU** (`InvokeBlocking`) | dedicated `LimitedConcurrencyLevelTaskScheduler` | Blocking work holds a real thread for its whole duration. The limited-concurrency scheduler borrows ThreadPool threads but dispatches at most *cap* at a time, so a burst can't trigger runaway thread-injection that starves Orleans' grain schedulers. |

This is the "compatible with how Orleans wants us to pool" property: we **reuse the ThreadPool the framework already uses** and merely put a governor in front of it — we never spawn our own OS threads that Orleans can't see or coordinate with.

## Named pools and caps

Pools are keyed by resource class and resolved lazily from `IoPoolRegistry` (a mesh-scoped singleton, disposed with the mesh — no static state). Caps come from `IoPoolOptions`, sensible defaults that a host can override via `AddIoPools(o => o with { Blob = 64 })` without any call-site change.

| Pool (`IoPoolNames`) | Default cap | Wave |
|---|---|---|
| `FileSystem` | `Environment.ProcessorCount` | 1 |
| `Blob` | 32 | 1 |
| `Http` | 16 | 2 |
| `Compile` | `Environment.ProcessorCount` | 3 |
| `Process` | 4 | 3 |

## Hidden inside the interfaces

A leaf adapter takes an optional `IIoPool? pool = null` and stores `_pool = pool ?? IoPool.Unbounded`. Every leaf then reads uniformly:

```csharp
public IObservable<MeshNode?> Read(string path, JsonSerializerOptions options)
    => _pool.Invoke(ct => ReadAsyncCore(path, options, ct));           // was Observable.FromAsync(...)

public IObservable<object> GetPartitionObjects(string nodePath, string? subPath, JsonSerializerOptions options)
    => _pool.InvokeStream(ct => GetPartitionObjectsAsyncCore(nodePath, subPath, options, ct));

public IObservable<bool> Exists(string path)
    => _pool.InvokeBlocking(_ => { var (f, _) = FindFileWithExtension(path); return f != null && File.Exists(f); });
```

`IoPool.Unbounded` is a stateless fallback (used when an adapter is constructed with `new` outside DI — i.e. tests). It still offloads onto the ThreadPool, so it is never *worse* than the bare `Observable.FromAsync` it replaces; it just applies no cap. It holds no mutable state, so it is a true immutable constant, not a cache.

DI wires the real pool: the storage-adapter factories and `Add…Persistence` registrations resolve `sp.GetService<IoPoolRegistry>()?.Get(IoPoolNames.FileSystem | Blob)` and pass it to the adapter constructor. `MeshBuilder` registers the registry via `.AddIoPools()` in its core `ConfigureServices` chain.

## Why Postgres is left as-is

`PostgreSqlMeshQuery` / `PostgreSqlVersionQuery` already do `Observable.FromAsync(work, Scheduler.Default)` bounded by Npgsql's `MaxPoolSize` (the correct, role-specific, driver-enforced bound). An extra `IIoPool` gate on top would be redundant and could deadlock against the connection pool if mis-sized. There is no gap there — leave it.

## Edge cases (the review checklist)

- **Pool the leaf, never the orchestration.** `IIoPool` is injected into leaf adapters only. Orchestration layers (`PersistenceService` fan-out, version-writing decorator, query providers, `MeshQuery`) compose adapter observables and hold **no** slot — a fan-out of N reads acquires N leaf slots and never nests behind a held one. Per-resource pools mean a FileSystem leaf never blocks on a Blob leaf while holding a FileSystem slot. **Never let an `IIoPool` call resolve an observable that itself acquires the same pool** — that is the one way to deadlock it.
- **Cancellation + release.** Every shape releases its slot in a `finally`. `WaitAsync(ct)` makes acquisition itself cancellable — a dispose before the slot is granted throws before the in-flight increment, so no slot leaks. The subscription's `ct` flows into the leaf, so file/blob calls cancel on unsubscribe.
- **Disposal.** `IoPoolRegistry` disposes each `IoPool` → each disposes its `SemaphoreSlim`. The limited-concurrency scheduler borrows ThreadPool threads — nothing to dispose.
- **No sync-context capture.** `.SubscribeOn(TaskPoolScheduler.Default)` + `.ConfigureAwait(false)` everywhere; `InvokeBlocking` dispatches via the scheduler-bound `TaskFactory`, never `TaskScheduler.Current`. Safe even when `Subscribe` is called inside a grain handler.

## Rollout waves

- **Wave 1 — storage** (done): `FileSystemStorageAdapter`, `AzureBlobStorageAdapter`, `CachingStorageAdapter`.
- **Wave 2 — HTTP/network** (`Http` pool): social publishers (LinkedIn/X), AI providers, web search, embeddings, `McpRemoteMeshClient`.
- **Wave 3 — compile/process/nuget** (`Compile` / `Process` pools): `MeshNodeCompilationService` and `KernelExecutor` (CPU-bound → `InvokeBlocking`), `MeshPlugin.RunTests` (`Process.Start`), `NuGetAssemblyResolver`.

## Cross-references

- [Asynchronous Calls](AsynchronousCalls) — rule 9, "the async boundary lives at the real I/O edge".
- [Aggregating Providers](AggregatingProviders) — "the async boundary lives at the I/O edge", pool-at-the-edge.
- [Orleans Task Scheduler](OrleansTaskScheduler) — per-hub schedulers and the `SubscribeOn` offload this complements.
