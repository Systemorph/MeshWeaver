---
Name: Initialization Gates
Description: "Declare, open, and bypass hub initialization gates that defer inbound traffic until reactive data loads complete — without deadlocking via await."
---

# Initialization Gates

> **TL;DR** — A hub may need to hold back inbound traffic until its own data is
> loaded. Declare a gate at config time with `WithInitializationGate(name, letThroughPredicate)`,
> then open it on the **first emission of a reactive observable** — either inside
> `.Select(...)` or in a `Subscribe` callback. **Never** bridge to `await Task<T>` to
> open a gate; that captures the calling scheduler and deadlocks the hub action block.
> The condition must be expressible as a non-blocking observable emission.

---

## What is a gate?

A **hub initialization gate** is a delivery-pipeline filter installed at startup. While
the gate is closed, every inbound delivery is queued — except those that pass the gate's
bypass predicate. When `hub.OpenGate(name)` is called, the queue drains in order.
Closing again after opening is a no-op: gates are one-way.

Register a gate on `MessageHubConfiguration`:

```csharp
config.WithInitializationGate(
    gateName: MeshNodeExtensions.MeshNodeInitGateName,
    letThrough: d => d.Message is CreateNodeRequest);
```

| Parameter | Purpose |
|---|---|
| `gateName` | String identifier that lets multiple gates coexist and tells `OpenGate` which to flip. Always use a named constant — never inline the string. |
| `letThrough` | Predicate evaluated on every queued delivery. Return `true` for messages that must bypass the gate. For the canonical mesh-node init gate, `CreateNodeRequest` always passes so the hub can come into existence without timing out. |

`hub.OpenGate(name)` is **idempotent**: call it from multiple branches (success, failure,
no-persisted-node paths) without worry.

---

## When do you need a gate?

You need a gate when all three of these are true:

1. The hub's stream or reducer sources its initial value from persistence or another
   non-trivial load.
2. The hub also accepts request traffic that queries that stream.
3. Requests can arrive **before** the load finishes.

Without a gate, an early request reads an empty or null stream and gets a "not found"
response — not a wait. The gate turns that race into "wait until loaded, then respond."

**Canonical example:** `MeshNodeInitGateName`, opened by `MeshNodeTypeSource.Initialize`
after persistence reads the hub's own `MeshNode`. Without the gate, a `GetDataRequest`
for `MeshNodeReference` would race the load and silently return null.

> If your data is available synchronously — built-in nodes, `WithInitialData([...])`,
> a static provider — you don't need a gate at all. Open it eagerly at config time so
> no requests are unnecessarily queued. `MeshDataSource.WithMeshNodes()` does exactly
> this in its "built-in" and "static" branches.

---

## 🚨 The absolute rule: open gates from reactive observables, never from `await`

The gate opens when a *condition* is met — concretely, the **first emission of an
observable**, not the **completion of a Task**. Awaiting a Task to open the gate
captures the calling scheduler (typically the grain scheduler). While the await is
in-flight, the hub's action block is blocked. While the action block is blocked, every
other message — including the queued ones the gate is meant to release — waits behind
it. The gate never opens.

**Do not write this:**

```csharp
// ❌ DEADLOCK — await captures the grain scheduler; gate never opens.
protected override async Task<InstanceCollection> InitializeAsync(...)
{
    var node = await persistence.GetNode(path).FirstAsync().ToTask(ct);
    hub.OpenGate(MeshNodeInitGateName);   // never reached on the offending path
    return BuildCollection(node);
}
```

**Write this instead:**

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

The framework consumes `Initialize(...)` via `.Subscribe(...)` (or composes it into a
larger observable that bridges to `Task` only at the outer edge, e.g. inside
`WithInitialization`). The `Initialize` body itself must contain no `await`, no
`.ToTask`, and no `Observable.FromAsync` over a hub round-trip.

### When is `Observable.FromAsync` permitted?

`Observable.FromAsync(ct => DbCallAsync(ct))` is **sanctioned** for genuinely
Task-based primitives that do not post messages to the hub — a pure DB hit, file I/O,
an EF query, or an HTTP fetch.

It is **forbidden** over hub round-trips: `hub.RegisterCallback`, `hub.AwaitResponse`,
`meshService.QueryAsync`, `workspace.GetRemoteStream(...).Take(1).ToTask()`, or anything
whose `Task` completion depends on the hub's own action block making forward progress.
See [Asynchronous Calls](AsynchronousCalls) for the full rule.

---

## The `ITypeSource.Initialize` contract

`ITypeSource` exposes a single reactive method:

```csharp
internal IObservable<InstanceCollection> Initialize(
    WorkspaceReference<InstanceCollection> reference,
    CancellationToken cancellationToken);
```

Implementations emit exactly one `InstanceCollection`. The framework's data-source
initializer (`GenericUnpartitionedDataSource.GetInitialValueAsync` and its partitioned
twin) composes per-type-source `Initialize` calls via `SelectMany` + `Aggregate`, then
bridges to `Task<EntityStore>` exactly **once** at the `WithInitialization(...)` edge.

| Type source | Init source | Bridge inside `Initialize` |
|---|---|---|
| `TypeSource` (base) | User callback `InitializationFunction` (Task) | `Observable.FromAsync` — sanctioned, opaque user callback. |
| `MeshNodeTypeSource` | `IStorageService.GetNode` (already `IObservable`) | None — pure `.FirstAsync().Select(...)`. |
| `TypeSourceWithTypeWithDataStorage<T>` | `IDataStorage.Query<T>().ToDictionaryAsync` (DB) | `Observable.FromAsync` — sanctioned, pure DB. |
| `VirtualDataSource.VirtualTypeSource` | `StreamUpdates()` (already `IObservable`) | None — `.Take(1).Timeout(...).Select(...)`. |
| `PartitionTypeSource<T>` | `IStorageService.GetPartitionObjectsAsync` (DB) | `Observable.FromAsync` — sanctioned, pure DB. |

The legacy `Task<InstanceCollection> InitializeAsync(...)` surface has been removed from
the interface, eliminating the temptation to await inside implementations.

---

## Opening a gate from inside `Initialize`

The cleanest approach is to open the gate inside the `.Select` that produces the
`InstanceCollection`:

```csharp
protected override IObservable<InstanceCollection> Initialize(
    WorkspaceReference<InstanceCollection> reference,
    CancellationToken cancellationToken)
    => storage.Get(...)                   // IObservable<...>
        .FirstAsync()
        .Select(loaded =>
        {
            // 1. Side effect: gate open. Idempotent — safe on re-emission.
            workspace.Hub.OpenGate(MyGateName);

            // 2. Pure result.
            return new InstanceCollection(loaded, TypeDefinition.GetKey);
        });
```

**Why `.Select` rather than `.Do`?** Placing the side effect inside `.Select` ties it to
the same emission as the result. If `.Do` fires before a downstream `.Where` filter
drops the emission, the gate opens while the `InstanceCollection` is never delivered —
a subtle divergence. `.Select` keeps the side effect and the result atomic.

### Handling load failures

If the load can fail, open the gate in the catch branch too — using an empty
`InstanceCollection` so the hub serves "not found" rather than infinite silence:

```csharp
.Catch<InstanceCollection, Exception>(ex =>
{
    logger.LogError(ex, "Init failed for {HubPath} — opening gate with empty collection so traffic doesn't stall", hubPath);
    workspace.Hub.OpenGate(MyGateName);
    return Observable.Return(new InstanceCollection(System.Array.Empty<object>(), TypeDefinition.GetKey));
});
```

Without this, a failed load leaves the hub permanently gated and every queued message
eventually times out with no useful error surfaced.

---

## Opening a gate from a hub-init hook

When the gate condition isn't "type source loaded its collection" — for example, "a
remote readiness signal arrived" or "the permission service has refreshed" — register
the gate in hub config and open it from a `WithInitialization(...)` hook:

```csharp
config
    .WithInitializationGate(MyGateName, d => d.Message is BootstrapRequest)
    .WithInitialization(hub =>
    {
        // Subscribe to the readiness observable. The gate opens on first emission;
        // subsequent emissions are no-ops (OpenGate is idempotent).
        // NEVER `await`-bridge inside this method.
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
        return Task.CompletedTask;   // hook signature is Task; body stays sync
    });
```

`WithInitialization` accepts a `Func<IMessageHub, CancellationToken, Task>`. The hook
body must remain synchronous — return `Task.CompletedTask` and let `Subscribe` carry
the deferred work. Awaiting inside this hook is the same deadlock as awaiting inside
`Initialize`.

---

## Gate-bypass predicates

A bypass predicate returns `true` for messages whose handling cannot wait. Anything
user-driven — `GetDataRequest`, queries, mutations — should **not** bypass: that is the
entire reason the gate exists.

The most common bypass case is **`CreateNodeRequest`**: the hub is being asked to come
into existence, so deferring would prevent the gate from ever mattering.

```csharp
.WithInitializationGate(MeshNodeInitGateName, d => d.Message is CreateNodeRequest)
```

### Messages the framework always bypasses

The framework unconditionally bypasses **all** gates for the following system messages —
see `MessageService.cs` (the `delivery.Message is ShutdownRequest or …` short-circuit
evaluated before any gate predicate). There is no need to repeat these in your own
predicate.

| Message | Why bypassed |
|---|---|
| `ShutdownRequest`, `DisposeRequest` | Deferring breaks disposal. |
| `DeliveryFailure` | The routing layer's reply for an undeliverable request; deferring it strands the sender's `hub.Observe(...)` waiting on a response already in the deferred buffer. |
| `InitializeHubRequest` | Posted during construction to mark `BuildupActions` complete and open the framework `InitializeGateName`. If a user-defined gate queues this, `BuildupActions` never finish → the user gate (which opens on initialization emission) never opens → hub deadlocks. |
| `HeartBeatEvent` | Orleans grain keep-alive; deferring it causes premature deactivation. |

> **Background:** the `InitializeHubRequest` bypass was added after a prod incident where
> thread-hub `SubscribeRequest`s timed out at 30 s because `InitializeHubRequest` was
> sitting queued behind `MeshNodeInitGateName`.

---

## Anti-patterns

```csharp
// ❌ Awaiting inside the init function — captures the calling scheduler.
//    The gate never opens because nothing else can run on this scheduler
//    while the await is pending.
protected override async Task<InstanceCollection> InitializeAsync(...)
{
    var x = await SomeHubRoundTrip();       // deadlock
    hub.OpenGate(name);
    return ...;
}

// ❌ Opening the gate eagerly before the data is loaded.
//    Defeats the purpose: queued reads now run against an empty collection.
config.WithInitializationGate(name, d => d.Message is CreateNodeRequest)
      .WithInitialization(hub => { hub.OpenGate(name); return Task.CompletedTask; });

// ❌ Wrapping a hub round-trip in Observable.FromAsync, then opening from Subscribe.
//    Same deadlock as pattern one, hidden one level deeper.
.WithInitialization(hub =>
{
    Observable.FromAsync(ct => SomeHubAwait(ct))
        .Subscribe(_ => hub.OpenGate(name));  // SomeHubAwait awaits a hub round-trip
    return Task.CompletedTask;
});

// ❌ No error handler — gate stays closed forever on load failure.
//    Every queued message eventually times out with no useful error surfaced.
ownStream.Select(...).Subscribe(v => hub.OpenGate(name) /* no OnError */);
```

---

## Naming and organisation

- **Define gate names as `public const string`** on the module that owns the gate.
  Cross-file string typos are a silent foot-gun.
- **One gate per readiness condition.** Multiple independent loads can use multiple
  stacked gates; opening one does not release messages waiting on another.
- **Document the constant:** state what condition opens the gate, which bypass
  predicates apply, and which sources are idempotent-safe.

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

- [Asynchronous Calls in MeshWeaver](AsynchronousCalls) — the broader rule: no `await`
  of hub round-trips anywhere in mesh-reachable code.
- [CQRS — Queries vs. Content Access](CqrsAndContentAccess) — queries are lagged and
  not the right primitive for known-path content reads, regardless of gating.
- `src/MeshWeaver.Graph/MeshNodeTypeSource.cs` — canonical reactive `Initialize`
  implementation that opens `MeshNodeInitGateName`.
- `src/MeshWeaver.Data/GenericUnpartitionedDataSource.cs` — `GetInitialValueAsync`
  shows the framework-edge bridge: per-type-source `Initialize` calls composed via
  `SelectMany` + `Aggregate`, with a single `.ToTask` at the `WithInitialization`
  boundary.
