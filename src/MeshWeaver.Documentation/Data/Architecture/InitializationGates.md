# Initialization Gates

> **TL;DR:** A hub may need to defer non-`CreateNodeRequest` traffic until its own data
> is loaded. The pattern: declare a gate at config time (`WithInitializationGate(name,
> letThroughPredicate)`), open it on the **first emission of a reactive observable**
> (`stream.Subscribe(_ => hub.OpenGate(name))` or inside `.Select(...)`). **Never**
> bridge an `await Task<T>` to "open the gate when the load completes" — that
> captures the calling scheduler and deadlocks the hub action block. The condition
> for opening the gate must be expressible as a non-blocking observable emission.

This page collects the moving parts: what a gate is, where the framework already
uses one, how to declare a new one, and the rules that keep gating non-deadlocking.

---

## What a gate is

A **hub initialization gate** is a delivery-pipeline filter the hub installs at
startup. While the gate is closed, every inbound delivery is queued except those
explicitly let through by the gate's predicate. When the gate is opened, the queue
drains in order. Closing again is a no-op once opened.

The shape, registered on `MessageHubConfiguration`:

```csharp
config.WithInitializationGate(
    gateName: MeshNodeExtensions.MeshNodeInitGateName,
    letThrough: d => d.Message is CreateNodeRequest);
```

- `gateName` — string identifier so multiple gates can stack and so the open call
  knows which to flip. Use a constant; never inline the string.
- `letThrough` — predicate evaluated on every queued delivery. Returns `true` for
  messages that must NOT be deferred (they bypass the gate). For the canonical
  mesh-node init gate, `CreateNodeRequest` always passes — the hub is being
  asked to create itself, deferring would cause the round-trip to time out.
- Anything for which the predicate returns `false` is queued until
  `hub.OpenGate(gateName)` runs.

`hub.OpenGate(name)` is **idempotent** — call it as many times as you want; the
second and subsequent calls are no-ops.

---

## When you need a gate

You need a gate when:

1. The hub serves a stream/reducer that sources its initial value from persistence
   (or any non-trivial async load).
2. **And** the hub also accepts request traffic that would query that stream.
3. **And** request traffic can arrive before the load finishes.

Without the gate, the early request reads an empty / null stream and gets a
"not found" response — not a wait. The gate gives you "wait until loaded, then
respond."

The canonical example is `MeshNodeInitGateName`, opened by
`MeshNodeTypeSource.Initialize` after persistence reads the own MeshNode.
`GetDataRequest(MeshNodeReference)` would race the load and return null without
this gate; the gate forces the request to wait until the workspace's
`InstanceCollection<MeshNode>` is populated.

If your data load is purely in-memory (built-in nodes, `WithInitialData([...])`,
static node provider) you don't need to gate — the data is available
synchronously at config time. Open the gate eagerly in that path so you don't
queue requests for nothing. `MeshDataSource.WithMeshNodes()` does exactly that
(see the "built-in" / "static" branches that call `OpenGate` immediately).

---

## The 🚨 absolute rule: open the gate from a reactive observable, never from `await`

The whole point of gating is to defer traffic until a *condition* is met. The
condition is the **first emission of an observable**, not the **completion of a
Task**. Bridging the load to a Task and `await`-ing it captures the calling
scheduler — typically the grain scheduler — and the gate-open code lives
*on the same scheduler*. While the await runs, the action block is blocked.
While the action block is blocked, every other message (including queued ones
the gate is meant to release) waits behind it. The gate never opens.

**Do not write this.**

```csharp
// ❌ DEADLOCK — await captures the grain scheduler; gate never opens.
protected override async Task<InstanceCollection> InitializeAsync(...)
{
    var node = await persistence.GetNode(path).FirstAsync().ToTask(ct);
    hub.OpenGate(MeshNodeInitGateName);   // never reached on the offending path
    return BuildCollection(node);
}
```

**Do write this.**

```csharp
// ✅ Pure reactive composition. The framework subscribes; the gate opens on emission.
protected override IObservable<InstanceCollection> Initialize(...)
    => persistence.GetNode(path)              // already IObservable
        .FirstAsync()
        .Select(node =>
        {
            hub.OpenGate(MeshNodeInitGateName);
            return BuildCollection(node);
        });
```

The framework consumes `Initialize(...)` with `.Subscribe(...)` (or composes it
into a larger observable that ends in `.ToTask` *only at the framework edge* —
e.g. `WithInitialization` whose Func signature is `Task<TStream>`). The
`Initialize` body itself contains no `await`, no `.ToTask`, and no
`Observable.FromAsync` over a hub round-trip.

When the underlying primitive is genuinely Task-based (a pure DB hit, file
I/O, an EF query, an HTTP fetch — anything that does not post messages to the
hub), `Observable.FromAsync(ct => DbCallAsync(ct))` is the sanctioned bridge.
**It is forbidden over hub round-trips:** `hub.RegisterCallback`,
`hub.AwaitResponse`, `meshService.QueryAsync`, `workspace.GetRemoteStream(...).Take(1).ToTask()`,
or any `Task` whose completion depends on the hub's own action block making
forward progress. See [Asynchronous Calls](AsynchronousCalls).

---

## The reactive `ITypeSource.Initialize` contract

`ITypeSource` exposes:

```csharp
internal IObservable<InstanceCollection> Initialize(
    WorkspaceReference<InstanceCollection> reference,
    CancellationToken cancellationToken);
```

Implementations emit exactly one `InstanceCollection`. The framework's
data-source initializer (`GenericUnpartitionedDataSource.GetInitialValueAsync` /
its partitioned twin) composes per-type-source `Initialize` calls via
`SelectMany` + `Aggregate`, then bridges to `Task<EntityStore>` ONCE at the
`WithInitialization(...)` edge.

Existing implementations:

| Type source | Init source | Bridge inside `Initialize` |
|---|---|---|
| `TypeSource` (base) | User callback `InitializationFunction` (Task) | `Observable.FromAsync` — sanctioned, opaque user callback. |
| `MeshNodeTypeSource` | `IStorageService.GetNode` (already `IObservable`) | None — pure `.FirstAsync().Select(...)`. |
| `TypeSourceWithTypeWithDataStorage<T>` | `IDataStorage.Query<T>().ToDictionaryAsync` (DB) | `Observable.FromAsync` — sanctioned, pure DB. |
| `VirtualDataSource.VirtualTypeSource` | `StreamUpdates()` (already `IObservable`) | None — `.Take(1).Timeout(...).Select(...)`. |
| `PartitionTypeSource<T>` | `IStorageService.GetPartitionObjectsAsync` (DB) | `Observable.FromAsync` — sanctioned, pure DB. |

Subclasses opt into reactive by overriding `Initialize(...)` with pure
composition. The legacy `Task<InstanceCollection> InitializeAsync(...)` path is
gone from the interface — there is no Task surface to tempt callers into
awaiting.

---

## Declaring + opening a gate from inside `Initialize`

The simplest form — open inside the `.Select` that produces the
`InstanceCollection`:

```csharp
protected override IObservable<InstanceCollection> Initialize(
    WorkspaceReference<InstanceCollection> reference,
    CancellationToken cancellationToken)
    => storage.Get(...)                   // IObservable<...>
        .FirstAsync()
        .Select(loaded =>
        {
            // 1. side effect: gate-open. Idempotent; safe even on re-emissions.
            workspace.Hub.OpenGate(MyGateName);

            // 2. pure result.
            return new InstanceCollection(loaded, TypeDefinition.GetKey);
        });
```

Why open from inside `.Select` instead of `.Do(_ => OpenGate(...))`?

- `.Select` carries the side effect on the same emission as the result, so
  there is no observable subscriber whose state can diverge. With `.Do`
  someone composing `.Where` between the `.Do` and the consumer can drop the
  emission while the gate-open already fired.
- The whole chain is consumed exactly once by the framework's per-stream
  init machinery; we want a single, deterministic side effect on the same
  emission as the InstanceCollection.

If the load can fail (`.Catch(...)`), you generally still want to open the
gate — otherwise the hub stays gated forever and every queued message
eventually times out. Open in the catch branch with an empty
`InstanceCollection` so the hub serves "not found" instead of "no response":

```csharp
.Catch<InstanceCollection, Exception>(ex =>
{
    logger.LogError(ex, "Init failed for {HubPath} — opening gate with empty collection so traffic doesn't stall", hubPath);
    workspace.Hub.OpenGate(MyGateName);
    return Observable.Return(new InstanceCollection(System.Array.Empty<object>(), TypeDefinition.GetKey));
});
```

---

## Opening a gate from a hub-init hook

When the gate condition is something other than "type source loaded its
collection" — e.g. "remote data has arrived for this hub" or "permission
service has refreshed" — register the gate in the hub config and open it from
a `WithInitialization(...)` hook that subscribes to the relevant observable:

```csharp
config
    .WithInitializationGate(MyGateName, d => d.Message is BootstrapRequest)
    .WithInitialization(hub =>
    {
        // Subscribe to the readiness observable. The gate opens when the
        // observable emits; subsequent emissions are no-ops (OpenGate is
        // idempotent). NEVER `await`-bridge inside this method.
        var sub = readinessObservable
            .Take(1)
            .Subscribe(
                _ => hub.OpenGate(MyGateName),
                ex =>
                {
                    logger.LogError(ex, "Readiness signal failed; opening gate to release queued traffic");
                    hub.OpenGate(MyGateName);
                });

        hub.RegisterForDisposal(sub);
        return Task.CompletedTask;   // hook signature is Task; the body is sync
    });
```

`WithInitialization` accepts a `Func<IMessageHub, CancellationToken, Task>`.
The hook body must remain synchronous — return `Task.CompletedTask` and let
the `Subscribe(...)` carry the deferred work. Awaiting inside this hook is the
same deadlock pattern as awaiting inside `Initialize`.

---

## Gate-bypass predicates

The predicate must return `true` for messages whose handling cannot wait for
the gate. Anything user-driven (`GetDataRequest`, queries, mutations) should
NOT bypass — that's the whole reason the gate exists.

The most common bypass case is **`CreateNodeRequest`** — the hub is being
asked to come into existence; deferring the request would prevent the gate
from ever being relevant.

```csharp
.WithInitializationGate(MeshNodeInitGateName, d => d.Message is CreateNodeRequest)
```

### Framework-bypassed messages — do NOT add to your predicate

The framework unconditionally bypasses **every** gate for these system
messages — see [`MessageService.cs`](../../../MeshWeaver.Messaging.Hub/MessageService.cs)
(the `delivery.Message is ShutdownRequest or …` short-circuit before gate
evaluation):

| Message | Why bypassed |
|---|---|
| `ShutdownRequest`, `DisposeRequest` | Deferring breaks disposal. |
| `DeliveryFailure` | Routing layer's reply for an undeliverable request; deferring strands the sender's `hub.Observe(...)` waiting on a response that's already in the deferred buffer. |
| `InitializeHubRequest` | Posted during construction to mark `BuildupActions` complete and open the framework `InitializeGateName`. If a user-defined gate queues this, BuildupActions never finish → the user gate (which opens on initialization emission) never opens → hub deadlocks. |
| `HeartBeatEvent` | Orleans grain keep-alive; deferring causes premature deactivation. |

Predicate authors should NOT add these to their `WithInitializationGate(...)`
predicate — the framework handles them. Adding them is harmless but redundant
and clutters the gate definition.

Repro for the `InitializeHubRequest` case: prod thread hubs whose
`SubscribeRequest` timed out at 30 s while `InitializeHubRequest` sat queued
behind `MeshNodeInitGateName`. Fixed by adding `InitializeHubRequest` to the
framework-level bypass; no per-gate change needed.

---

## Anti-patterns

```csharp
// ❌ Awaiting inside the init function. Captures the calling scheduler.
//   The gate never opens because nothing else can run on this scheduler
//   while the await is pending.
protected override async Task<InstanceCollection> InitializeAsync(...)
{
    var x = await SomeHubRoundTrip();       // deadlock
    hub.OpenGate(name);
    return ...;
}

// ❌ Opening the gate "eagerly" before the data is actually loaded.
//   Defeats the purpose: queued reads now run against an empty collection.
config.WithInitializationGate(name, d => d.Message is CreateNodeRequest)
      .WithInitialization(hub => { hub.OpenGate(name); return Task.CompletedTask; });

// ❌ Opening the gate from a Subscribe(...) callback that wraps an awaited
//   Task. Same deadlock as the first anti-pattern, hidden inside .FromAsync.
.WithInitialization(hub =>
{
    Observable.FromAsync(ct => SomeHubAwait(ct))
        .Subscribe(_ => hub.OpenGate(name));  // SomeHubAwait awaits hub round-trip
    return Task.CompletedTask;
});

// ❌ Forgetting to open the gate on the error path. Hub stays gated forever;
//   every queued message eventually times out with no useful failure.
ownStream.Select(...).Subscribe(v => hub.OpenGate(name) /* no error handler */);
```

---

## Naming + organisation

- Define gate names as `public const string` on the module that owns the
  gate. Cross-file string typos are a permanent foot-gun otherwise.
- One gate per readiness condition. Stacking multiple gates with separate
  predicates is fine if you have multiple independent loads; opening one
  doesn't release messages waiting on the other.
- Document on the gate constant exactly what condition opens it and which
  bypass predicates make sense — future readers will want both.

```csharp
public static class MeshNodeExtensions
{
    /// <summary>
    /// Gate name for "the per-hub MeshNode collection has been loaded from
    /// persistence". Bypasses CreateNodeRequest. Opened by
    /// <see cref="MeshNodeTypeSource.Initialize"/> on first emission of
    /// <c>IStorageService.GetNode</c>. Idempotent — safe to open from multiple
    /// branches (load success, load failure, hubs without a persisted node).
    /// </summary>
    public const string MeshNodeInitGateName = "mesh-node-init";
}
```

---

## Related

- [Asynchronous Calls in MeshWeaver](AsynchronousCalls) — the broader rule:
  no `await` of hub round-trips anywhere in mesh-reachable code.
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — queries are
  lagged and not the right primitive for known-path content reads, regardless
  of gating.
- `src/MeshWeaver.Graph/MeshNodeTypeSource.cs` — canonical reactive
  `Initialize` implementation that opens `MeshNodeInitGateName`.
- `src/MeshWeaver.Data/GenericUnpartitionedDataSource.cs` —
  `GetInitialValueAsync` shows the framework-edge bridge: per-type-source
  `Initialize` composed via `SelectMany` + `Aggregate`, single `.ToTask` at
  the `WithInitialization` boundary.
