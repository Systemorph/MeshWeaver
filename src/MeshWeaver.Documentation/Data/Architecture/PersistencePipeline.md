---
NodeType: "Doc/Article"
Title: "Persistence Pipeline — Per-Silo Write Coordinator"
Abstract: "Every write to durable storage flows through one coordinator hub per silo (StatelessWorker(1) in Orleans, singleton in monolith). The coordinator owns a serial queue, applies retries, batches to Postgres, and publishes acks back to the producer. One always-on entry point — easy to monitor, impossible to bypass, no hangs because it's bounded by the framework request timeout."
Icon: "Stack"
Published: "2026-04-25"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Persistence"
  - "Orleans"
  - "Reliability"
---

> Read first: [Asynchronous Calls](AsynchronousCalls), [CQRS — Queries vs. Content Access](CqrsAndContentAccess), [Per-Hub TaskScheduler](OrleansTaskScheduler), [Partitioned Persistence](PartitionedPersistence).

## The model

One **persistence coordinator hub per silo**. All writes to durable storage (Postgres in prod, FileSystem / InMemory elsewhere) flow through this single hub. It is:

- **Always alive.** Activated at silo start, never deactivates while the silo is up.
- **Always accepting writes.** Producers can `hub.Post(WriteRequest, target=PersistenceCoordinatorAddress)` from any context — no failure path on the producer side.
- **Serial per silo.** One actor, one action block — writes are processed in publish order. Cross-silo concurrency is the database's problem (Postgres handles it via row-level transactions + ETag/version checks).
- **Observable.** Every write event is published on a single `IObservable<PersistenceEvent>` that operators can monitor (latency, queue depth, failure rate, retry count).
- **Retry-aware.** Transient failures (deadlock, connection drop, transient I/O) retry with exponential backoff via a Polly resilience pipeline. Permanent failures (validation, permission) propagate as exceptions to the producer.

```
┌───────────────────┐                                 ┌─────────────────────────┐
│ Producer (any hub)│                                 │ Persistence Coordinator │
│                   │   Post(WriteRequest)            │ [StatelessWorker(1)]    │
│  meshService      │ ──────────────────────────►     │  per silo, always alive │
│   .UpdateNode()   │                                 │                         │
│                   │                                 │ ActionBlock<Write> queue│
│                   │ ◄──────────────────────────     │   ↓                     │
│  awaits ack       │   WriteAcknowledgement          │ apply via               │
│                   │                                 │ IStorageAdapter         │
└───────────────────┘                                 │   ↓                     │
                                                      │ Postgres / FileSystem   │
                                                      │   ↓                     │
                                                      │ publish PersistenceEvent│
                                                      └─────────────────────────┘
```

## Why this shape

Today, persistence happens inside many handlers: `HandleCreateNodeRequest`, `HandleUpdateNodeRequest`, `HandleDeleteNodeRequest`, `MeshDataSource.persister`, etc. Each path calls `IStorageAdapter` directly. Three problems:

1. **No single chokepoint to monitor.** When persistence is slow or failing, it manifests as random handler timeouts spread across hubs. Diagnosing a Postgres deadlock means tracing through 5+ files.
2. **No global ordering.** Two writes to the same node from different actors can race at the storage layer. Postgres versioning catches the conflict but we have to surface it consistently.
3. **No always-on guarantee.** Handlers can be in deferred init, the per-node hub can be deactivating, or the message can be dropped during a routing race. Producers see hangs or silent drops.

A per-silo coordinator collapses these:
- One file owns the persistence side of the world.
- One queue serializes operations per silo (Postgres handles cross-silo).
- One always-on grain that never declines a write.

## The Orleans concept — `[StatelessWorker(1)]`

Orleans grain types decorated with `[StatelessWorker(maxLocalWorkers)]` get one activation per silo (per local worker count). The runtime auto-activates them on first use, never deactivates them while the silo is up, and routes calls to the local-silo activation by default.

We already use this pattern: `RoutingGrain` is `[StatelessWorker(1)]`. The persistence coordinator follows the same shape.

In monolith there's no Orleans, but the same contract applies — the coordinator is registered as a singleton hub on the mesh and gets the same address. Production code doesn't notice the difference.

## Wire-level shape — fire-and-forget enqueue

The producer's only responsibility is to **put the request on the queue**. No waiting for completion, no per-callsite CancellationToken. The coordinator drains the queue asynchronously; producers that care about "did it commit?" subscribe to the change feed.

```csharp
// Address — same in monolith and Orleans cluster, resolves to the local-silo activation
public static readonly Address PersistenceCoordinatorAddress = new("_persistence");

// Producer side — fire-and-forget enqueue. No CancellationToken because there's nothing
// to cancel: the call is synchronous (puts the request on the queue and returns).
public void UpdateNode(MeshNode node) =>
    hub.Post(
        new WriteRequest { Op = WriteOp.Update, Node = node, RequestedBy = CaptureContext() },
        o => o.WithTarget(PersistenceCoordinatorAddress));

// Coordinator side — one always-on hub, one serial queue, one place to watch.
[StatelessWorker(1)]
internal class PersistenceCoordinatorGrain(IStorageAdapter storage, IMeshChangeFeed feed, ILogger<PersistenceCoordinatorGrain> logger)
    : Grain, IPersistenceCoordinatorGrain
{
    public Task ProcessWrite(WriteRequest req) => /* async per-op handling */;
}
```

The producer hands off and walks away. **No timeouts at the producer side** — the queue accepts everything synchronously. If a producer needs to know "did this actually commit?", it subscribes:

```csharp
// Caller wants to react to commit (e.g., test waiting for read-after-write):
meshChangeFeed.Updated
    .Where(e => e.Node.Path == nodePath)
    .Take(1)
    .Subscribe(e => /* persisted */);

meshService.UpdateNode(updatedNode);   // fire-and-forget; commit notification arrives via feed
```

This is the actor-model "tell, don't ask" pattern. The producer tells the coordinator. The coordinator does the work. Anyone interested subscribes to the result stream.

## The retry policy

Built on Polly's resilience pipeline. **All knobs come from `PersistenceCoordinatorOptions`** (bound from `appsettings.json` / `IOptions<PersistenceCoordinatorOptions>`) — no hardcoded constants in the coordinator.

```csharp
public record PersistenceCoordinatorOptions
{
    public int MaxRetryAttempts { get; init; }      // default in defaults class, not here
    public TimeSpan InitialBackoff { get; init; }
    public TimeSpan PerAttemptTimeout { get; init; }
    public DelayBackoffType BackoffType { get; init; } = DelayBackoffType.Exponential;
    public bool UseJitter { get; init; } = true;
}

// Coordinator builds the pipeline from options
private ResiliencePipeline<WriteAcknowledgement> BuildPipeline(PersistenceCoordinatorOptions opts) =>
    new ResiliencePipelineBuilder<WriteAcknowledgement>()
        .AddRetry(new RetryStrategyOptions<WriteAcknowledgement>
        {
            ShouldHandle = new PredicateBuilder<WriteAcknowledgement>()
                .Handle<NpgsqlException>(IsTransient)
                .Handle<TimeoutException>()
                .Handle<DbUpdateConcurrencyException>(),   // ETag mismatch — re-read + retry
            MaxRetryAttempts = opts.MaxRetryAttempts,
            BackoffType = opts.BackoffType,
            Delay = opts.InitialBackoff,
            UseJitter = opts.UseJitter
        })
        .AddTimeout(opts.PerAttemptTimeout)
        .Build();
```

- **Transient Postgres errors** retry per options config.
- **ETag concurrency mismatch** triggers a re-read + reapply (the version check inside `WriteRequest` is part of the retry loop).
- **Permanent failures** (constraint violation, missing FK, validation rejection) propagate after the first attempt — no point retrying.
- **Non-handled exceptions** bubble to the change feed as a `PersistenceFailed` event so dashboards see them.

Defaults live in one well-known place (`PersistenceCoordinatorDefaults`) — every other layer reads from `IOptions<PersistenceCoordinatorOptions>` and never knows the actual numbers.

## Monitoring — one stream, one dashboard

```csharp
public interface IPersistenceMonitor
{
    IObservable<PersistenceEvent> Events { get; }
}

public record PersistenceEvent
{
    public DateTime Timestamp { get; init; }
    public string NodePath { get; init; }
    public WriteOp Op { get; init; }
    public TimeSpan Duration { get; init; }
    public PersistenceStatus Status { get; init; }   // Committed | Retrying | Failed
    public int RetryCount { get; init; }
    public string? Error { get; init; }
}
```

Every coordinator emission flows through here. Dashboards subscribe to:

- **Queue depth** — `Events.Buffer(TimeSpan.FromSeconds(1)).Select(b => b.Count)` → ops/sec.
- **Tail latency** — percentiles over `Events.Where(e => e.Status == Committed).Select(e => e.Duration)`.
- **Failure rate** — `Events.Where(e => e.Status == Failed).Window(TimeSpan.FromMinutes(1))`.
- **Retry storms** — `Events.Where(e => e.RetryCount > 0).GroupBy(e => e.NodePath)` to spot hot rows.

Operators get one place to watch. Health checks subscribe to the same stream — alarm if `Failed` count crosses a threshold or if `Committed` rate drops below the historical floor.

## Ordering and consistency

- **Per-silo FIFO.** Two writes from the same silo to the same node land on Postgres in publish order. The coordinator's action block guarantees this.
- **Cross-silo concurrency.** Two silos may write the same node concurrently. Postgres' row-level lock + the node's `Version` column resolve it: second writer detects ETag mismatch, retries with the latest version, validates that the merge still makes sense (or fails the request).
- **Read-after-write within the same hub.** The producer awaits the `WriteAcknowledgement` before the next read — but the in-memory workspace is *also* updated on the producer side at the same moment we publish the WriteRequest (optimistic local apply). The coordinator's ack confirms the persisted side; reads from the local workspace see the new state immediately.
- **Read-after-write across hubs.** The producer's hub publishes the change to the `IMeshChangeFeed` only after the ack arrives. Subscribers (other hubs, GUI views) see the change after Postgres commits.

## What this rules out

- **Direct `IStorageAdapter` calls from handlers.** They become an architectural error — only the coordinator may call into storage. Static analysis / build-time check enforces it.
- **`MeshDataSource.persister` as a separate concept.** The data source still owns in-memory state; persistence is no longer its responsibility.
- **`HubNodePersistence` and similar wrappers.** Their bodies collapse to `hub.Request<...>(...)` against the coordinator.
- **Per-callsite retries.** The coordinator owns retries. Producers just await the ack.

## Always-on guarantee

The producer's contract: **`hub.Post(WriteRequest, PersistenceCoordinatorAddress)` always succeeds.** No conditional, no fallback, no "if hub disposing". The coordinator is the last hub to deactivate at shutdown — until then, it accepts writes.

If the silo is shutting down while a producer publishes, the coordinator queues the write and processes it before deactivation completes. Writes in flight at silo crash are durable on Postgres (they were applied before ack). The producer's `hub.Request<T>` either gets the ack or a `TimeoutException` — never a hang.

## Phased migration

1. **Define `WriteRequest` / `WriteAcknowledgement` + `PersistenceCoordinatorAddress`** in `MeshWeaver.Mesh.Contract`.
2. **Build `PersistenceCoordinatorGrain` (`[StatelessWorker(1)]`) + monolith singleton equivalent.** Wire to `IStorageAdapter` + Polly pipeline + `IPersistenceMonitor`.
3. **Migrate `MeshService.UpdateNode`** to publish through the coordinator. Verify Patch/Update tests pass.
4. **Migrate `MeshService.CreateNode / DeleteNode / CopyNode`** — same shape.
5. **Migrate `HandleCreateNodeRequest / HandleUpdateNodeRequest / HandleDeleteNodeRequest`** — they become thin forwards to the coordinator.
6. **Strip `MeshDataSource.persister` flush logic** — workspace is in-memory only; coordinator owns the storage write.
7. **Strip `IStorageAdapter` direct callers** outside the coordinator. Add a build check that fails the build if anyone reintroduces them.
8. **Wire `IPersistenceMonitor` into the existing dashboard.**

## Cross-references

- [Asynchronous Calls](AsynchronousCalls) — the actor-model rules this lives within.
- [Per-Hub TaskScheduler](OrleansTaskScheduler) — the coordinator gets its own scheduler (not the grain's, by default; see that doc for why).
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — reads stay direct; only writes go through the coordinator.
- [Partitioned Persistence](PartitionedPersistence) — partition layout (per-tenant tables in Postgres) is orthogonal; the coordinator dispatches into the right partition.
- [Debugging Message Flow](DebuggingMessageFlow) — coordinator events show up in the same trace, plus the dedicated `IPersistenceMonitor` stream for slow-burn issues.
