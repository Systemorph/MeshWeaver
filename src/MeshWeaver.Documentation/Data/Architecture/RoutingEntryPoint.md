---
Name: The Routing Entry Point
Category: Architecture
Description: "Why, on an Orleans mesh, every message that misses the local-stream fast path pays a grain PLACEMENT it mostly does not need, the two measured ways that placement fails and drops live traffic, and the case for a local routing service where a silo is co-hosted — with the three things such a change must carry deliberately."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h6"/><path d="M14 12h6"/><circle cx="12" cy="12" r="2"/><path d="M12 4v6M12 14v6"/></svg>
---

# The Routing Entry Point

> **The question this page answers:** on an Orleans mesh, every message that is not for a locally
> registered stream is routed through `IRoutingGrain`, a cluster grain. Placement of that grain is a dependency on the cluster being healthy — and it is
> the step that drops live traffic during a roll. Where the caller and the router are in the SAME
> PROCESS, that dependency buys nothing.

## What the entry point is today

**Scope.** This is the ORLEANS path only, and only its fallback. `OrleansRoutingService.DeliverMessage`
first checks the streams registered in this process (portals, in-process clients) and hands a hit to
that callback directly — no grain is involved. A Monolith mesh (`UseMonolithMesh`, which registers
`MonolithRoutingService`) never touches `IRoutingGrain` at all. So an incident from a Monolith, or a
delivery that hit a local stream, is never evidence about the placement described here.

On a miss, `OrleansRoutingService.DeliverMessage` ends in one call:

```csharp
var grain = GrainWhileRunning<IRoutingGrain>("default");
return Observable.Defer(() => grain.RouteMessage(delivery).ToObservable())
```

`RoutingGrain` is `[StatelessWorker(1)]` and non-reentrant: **one routing turn per silo**. Its turn
already does O(1) work and returns — path resolution, the stream post, the per-node grain hand-off
and the NACK all run OFF the turn through `IIoPool` (issue #1028, after a `RouteMessage` turn was
measured executing for **06:00:22** behind `NonReentrancyQueueSize=541`). So the turn is a
dispatcher, not a worker, and it is not the bottleneck.

**The bottleneck is reaching it at all.**

## 🚨 Placement fails in two measured ways, and both drop live traffic

A `[StatelessWorker]` is placed through `StatelessWorkerDirector` → `PlacementService.GetCompatibleSilos`,
which **intersects with the ACTIVE silo set**.

### 1. The silo is leaving — "no active nodes"

The instant a silo begins graceful shutdown it leaves `Active`, so every placement throws
`OrleansException: No active nodes are compatible with grain routing`. The silo is still running and
still processing; it simply may no longer take new activations.

Measured on memex, 2026-08-10: on all three pod shutdowns the first such exception landed **within
half a second** of the host logging *"Application is shutting down…"* — 11:44:31.341 → 11:44:31.750
— and then repeated **52, 838 and 944 times** until the process exited. Every one was an
`Orleans.Messaging[100071]` error AND a **terminal** `DeliveryFailure` to the sender. The traffic was
ordinary live routing — activity heartbeats, node streams. *Nothing was wrong with it except that the
silo underneath was going away.*

**This half is already mitigated**, and the shape of the mitigation is the point:
`IHostApplicationLifetime.ApplicationStopping` fires strictly BEFORE the silo hosted service stops,
so the router **never attempts the placement it knows cannot succeed** and answers the sender
immediately with the transient `ShuttingDown` verdict. The fix was to stop asking.

### 2. The cluster is busy — "placement operation timed out"

Measured 2026-09-20 22:25:14Z, one pod:

```text
fail: Orleans.Messaging[100071]
  Failed to address message Request
    [sys.client/hosted-10.244.9.229:11111@148947182] -> [routing/default]
    IRoutingGrain.RouteMessage(IMessageDelivery) #25BED5B67E2EBFF8
  System.TimeoutException: Grain placement operation timed out for grain routing/default.
   ---> Polly.Timeout.TimeoutRejectedException: … timeout of '00:00:30'
     at PlacementService.PlacementWorker.ExecutePlacementAsync(…)
```

**331 real deliveries to that one grain, inside a ~50 ms burst, all dropped.** They are genuine
messages — `RouteMessage(IMessageDelivery)`, with incrementing ids, so 331 distinct deliveries and
not one message retried. They never reached a turn: *"failed to address"* is upstream of the grain.

## 🚨 The observation this page exists for

Read the sender in that line. It is `sys.client/hosted-10.244.9.229:11111` — the Orleans **hosted
client inside the same process** as the silo `S10.244.9.229:11111`. And `StatelessWorkerDirector`
prefers the local silo.

**So that message went from a process, out through cluster placement, to a grain that was going to
be placed back in that same process — and was dropped by the round trip.** For the co-hosted case
the placement step is pure cost, and it is the step that fails.

## The proposal: a local routing service where a silo is co-hosted

Resolve the router from DI and call it in-process when this process hosts a silo; keep the grain for
callers that have no local silo. The dispatch BEHIND the entry point does not change — the same
`IIoPool`, the same `OrderedRouteDispatcher`, the same pod-hub leg.

What it removes: the placement dependency, its two failure modes, the 30-second Polly timeout that
holds a route slot while it expires, and the serialization of an envelope merely to cross into the
same process.

## 🚨 Three things such a change must carry deliberately

1. **Ordering is not free any more.** The non-reentrant turn is the LAST point at which the mesh's
   send order is authoritative, which is why the per-channel FIFO is claimed there
   ([Ordered Route Channels](/Doc/Architecture/OrderedRouteChannels)). The data-sync protocol is a
   DELTA protocol with a receive-side monotonicity guard: a frame below the mirror's current version
   is discarded as stale and never re-sent. Lose the ordering and two frames of one stream swap —
   the later lands, the earlier is dropped **forever**, and the subscriber sits on "Building layout…"
   until its own timeout with nothing logged above Debug. A service must provide an explicit
   single serialization point; it must not inherit one by accident.
2. **A pure client has no local silo.** An Orleans client process cannot host a grain at all — the
   router already reasons about exactly this when it decides whether a pod hub is reachable. Those
   callers still need a remote entry point, so the grain path stays and the choice is made once,
   by declaration, not per call.
3. **The size bound moves, it does not disappear.** `MessageSizeGuard` exists because the grain call
   SERIALIZES its argument. A local call serializes nothing — a saving — but the bound belongs on
   the legs that genuinely cross the wire, where `RefuseOversizedGrainDispatch` already applies it.

A fourth thing it happens to fix: `RouteMessage` is **not idempotent**, so a response timeout is
ambiguous — the grain may have accepted the delivery and not yet answered, and re-sending queues a
second copy (issue #1172, which is why the retry predicate is `IsResendableDeliveryFailure` and not
`IsTransientFailure`). A local call has no RPC and therefore no such ambiguity.

## What this does NOT fix, and what to do first

**It does not fix [#2299](https://github.com/Systemorph/MeshWeaver/issues/2299)** — `RoutingGrain`
turning a transient pod-hub condition into a terminal `DeliveryFailure` instead of re-resolving the
route. That one is `sev:H`, has 107 occurrences, and its own evidence is damning: the Orleans
exception carries `will retry after <n>ms`, i.e. **Orleans judged the condition transient and was
going to retry — and the router surfaced a hard failure anyway**.

Sequence accordingly: **#2299 first.** It is smaller, it stops deliveries being lost, and it removes
the 30-second holds that fill the dispatch budget. The entry-point change is the structural follow-up,
not the emergency.

## What is NOT established

- **The proportion of routing calls that come from a co-hosted client rather than a pure client.**
  One log line's sender was read; no survey was done. If most traffic already originates off-silo the
  saving is smaller than this page implies.
- **Whether placement is ever load-bearing for correctness here** — i.e. whether anything relies on
  Orleans choosing a *healthy* silo rather than the local one. `StatelessWorker` prefers local, but
  "prefers" is not "always", and the difference has not been characterised.
- **The cost of the explicit serialization point.** The turn is free today because Orleans provides
  it; a hand-built equivalent has a cost that has not been measured.
- Nothing here was prototyped. The two failure modes are measured; the remedy is reasoned.

## Cross-references

- [Ordered Route Channels](/Doc/Architecture/OrderedRouteChannels) — the ordering invariant the turn protects.
- [Reading a Routing Saturation Report](/Doc/Architecture/ReadingARoutingSaturationReport) — how the back-pressure gauge that these drops feed is read.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) · [Controlled IO Pooling](/Doc/Architecture/ControlledIoPooling) — the pool the turn hands off to.
