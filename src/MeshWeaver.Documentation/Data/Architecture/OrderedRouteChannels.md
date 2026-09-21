---
Name: Ordered Route Channels — the FIFO Key Is (Destination, Stream)
Category: Architecture
Description: A stream-routed address is a multiplexer, so keying the router's ordering FIFO on the destination serialised a whole process's data-sync traffic into one lane with one in-flight grain call — 62 of 64 dispatch slots queued behind one cache hub for 1.5 h on both production portals. The ordering invariant is per stream, the channel key now is too, and the identity comes from a stamp the envelope already carried.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7h10"/><path d="M4 12h16"/><path d="M4 17h10"/><path d="M17 4l3 3-3 3"/><path d="M17 14l3 3-3 3"/></svg>
---

# Ordered Route Channels — the FIFO Key Is (Destination, Stream)

The router has to preserve the order of some deliveries and is free to overlap the rest. Getting the
boundary between those two sets wrong is not a performance opinion: too narrow and a mirror is
corrupted, too wide and the busiest destination in the mesh gets one lane.

It was too wide, and that is the whole of this page.

## What must be ordered, and why nothing re-sends it

Data sync is a **delta protocol with a receive-side monotonicity guard**. `SynchronizationStream`'s
update path discards any frame whose owner version is below the mirror's current version, and nothing
ever re-sends it. Reorder two frames of one stream and the earlier one — possibly the one carrying a
layout area's actual content — is dropped as stale **forever**: the subscriber keeps its
"Building layout…" base frame, its wait dies on its own timeout, and nothing is logged above Debug on
either side. Measured before the FIFO existed: one Orleans suite run produced 854 stream-routed
deliveries, 46 destinations with out-of-order posts and 115 inverted pairs.

So the router claims the order on the **routing grain's turn** — `[StatelessWorker(1)]`,
non-reentrant, one turn per silo, and therefore the last point at which arrival order is still
authoritative — and drains it off the turn through `OrderedRouteDispatcher`, one in-flight leg per
channel, in arrival order. See [Controlled I/O Pooling](../ControlledIoPooling) for the pool the legs are
subscribed through.

**That guard operates per mirror**, i.e. per synchronization stream. Two frames of two different
streams have no ordering relationship at all.

## The defect: the channel was the destination

The FIFO's key was the destination **address**. A stream-routed address is not one consumer, it is a
**multiplexer**:

| Address | Fronts |
|---|---|
| `cache/{meshId}` | one `sync/{streamId}` sub-hub per node the process has a mirror of — every node stream in the whole process |
| `portal/{userId}` | one per layout area that viewer has open |

`DataExtensions.RouteStreamMessage` receives every frame at the hub's **own** address and forwards it
internally by `StreamId`, so the router never sees a per-stream address to key on. Keying on the
address alone therefore put a whole process's data-sync traffic on **one** channel with **one**
in-flight `IPodHubGrain.Deliver` grain call — a directed call carrying the full delivery, which
Orleans' `JsonCodec` copies in both directions.

Where that destination is on another silo, the channel's service rate is one network round trip per
frame. The arrival rate of a busy portal's change fan-out is not.

### What production measured

memex-cloud, 2026-09-20, 19:07–20:34Z, both portal pods, 17 crossings of the 64-slot reporting
threshold:

| Pod | In flight | Destinations queued | Deepest queue | Target that crossed |
|---|---|---|---|---|
| `…-s246c` | 64 | 1 | **62** | `cache/12xX8OXQIEmdwvf_ZZ8LjA` |
| `…-ndnxt` | 64 | 0 | 0 | a different address every time |

The same pair on 2026-09-19 (`cache/YORFhwiyqEyTP9ESF-NUNg`, then the same `12xX8…` eleven minutes
later) and again 2026-08-12. The episode stamps rose on both activations throughout, so every episode
**drained** — no slot ever leaked, and no leg was ever stuck.

**The asymmetry is the tell.** `cache/{meshId}` is keyed by the mesh hub's id, which is per PROCESS,
so each pod has its own cache address and its own `PodHubGrain` activation on its own silo. A pod's
channel to its OWN cache hub is a local call and drains as fast as it fills — deepest 0. Its channel
to the PEER pod's cache hub is a network round trip, and that is the one that stacked 62 legs. One
pod reporting head-of-line blocking while the other reports load was never two faults; it was one
fault seen from the two ends of one cross-silo channel.

### Why the reading "a leg is not completing" was wrong

The log line's own rule — *deepest ≥ 1 means legs are blocked behind a leg* — was applied correctly
and still produced the wrong conclusion, because it is silent about **why** the legs share a queue.
They were 62 independent streams with no ordering relationship, waiting on each other because the
channel key could not tell them apart. Nothing was slow; the lane was single.

That is also why the earlier work at this log site did not close it. The amplifiers were real and
were fixed — a storage query per routed message, O(node-size) JSON patch construction on hub action
blocks, a slot leak on a cancelled drain, and seven copies of every timed-out delivery
([A Timed-Out Delivery Is Still Held by the Callee](../ATimedOutDeliveryIsStillHeldByTheCallee)) — and
every one of them lowered the arrival rate or the service time of a lane that was still one lane
wide. A single-server queue whose arrival rate exceeds its service rate recurs at any offered load
above that rate; reducing the load moves the threshold, it does not remove it.

## The fix: the channel is (destination, payload identity)

The ordering domain is the stream, so the channel key is the stream. The router cannot read a stream
id out of the payload — on the turn the payload is `RawJson`, and parsing it there is the one thing
the turn must never do — but it does not have to: **the identity is already on the envelope.**

`MessageDelivery.Package` stamps `IDiagnosticKeyed.DiagnosticKey` onto the delivery's properties
immediately before erasing the payload type, and for every `StreamMessage` that key **is** the stream
id. Every mesh delivery reaches the router through `Package`, so `RoutingGrain` reads it with one
dictionary lookup of a string — no cast, nothing parsed, nothing deserialized:

```csharp
var orderingKey = DeliveryIdentity.Read(delivery);
orderedDispatcher.Enqueue(addressPath, orderingKey, leg, onLegCompleted);
```

- **Same stream ⇒ same channel.** Arrival order preserved exactly, one in-flight leg, as before.
- **Different streams ⇒ different channels.** Their round trips overlap.
- **No identity ⇒ the destination-wide channel.** There is nothing saying such a delivery is
  independent of anything, so it keeps the old behaviour rather than a guess.

The key is a `(string, string?)` **pair**, never the two concatenated: mesh addresses contain both
`/` and `~`, so any single delimiter makes `("cache/a", "b/c")` and `("cache/a/b", "c")` the same
channel — which would silently re-serialise unrelated streams, or let two frames of one stream
overtake each other.

### This raises no bound

Nothing was widened. The legs a silo may have in flight are still capped by the routing `IIoPool`
(256, unchanged); the receiving `PodHubGrain` is still non-reentrant and still serialises its own
hand-offs; `SaturationThreshold` is still 64 and still gates a log line and nothing else. What changed
is only that legs with **no ordering relationship** no longer wait on each other — the pipelining a
correct channel key allows and an over-coarse one forbade. `PodHubGrain.Deliver` is a synchronous
hand-off into the target hub's own queue, so what overlaps is the network, not any work.

### One identity, two readers

`DeliveryIdentity.Read` is shared with `MessageStormBreaker`, which keys its per-second rate counters
on `(sender, target, type, identity)`. Both layers are asking the same question — *which messages are
about one thing?* — and both fail the same way when they cannot answer it: the breaker folds a
legitimate fan-out into one bucket and **drops** it, the router folds independent streams into one
channel and **queues** them. Resolving it in one place is what keeps "one rate bucket" and "one
ordered channel" from drifting apart.

## What the log line now says

```
ordered channels queued 64 over 1 stream destination(s), deepest per-channel queue 0
```

A **channel** is (destination, stream), so a non-zero `Deepest` is a much stronger statement than it
was: frames of the *same* stream are stacking up, which really is one destination not keeping up with
one producer. The former reading — many unrelated streams sharing a lane — now reports as **many
channels over few destinations**, which is load. Both counts are printed because their ratio is the
remaining discriminator.

The rest of the line is unchanged, including the parts that matter most: a slot is held across the
unbounded wait for a ThreadPool thread before the leg's own timeouts start, so a CPU-starved silo
still raises this with nothing stuck; and the **episode stamp**, not the depth, is what says whether
an episode drained.

🚨 **The load shape at this site is a separate defect and is not closed by this.** `deepest 0` with
many channels is dispatch volume or ThreadPool starvation on the silo — a slot charged for a wait that
has no bound, where load and a leak are not distinguishable from the count. Nothing here narrows it.

## Controls

| Test | Side it holds |
|---|---|
| `OrderedRouteDispatcherTest.IndependentStreamsOnOneDestination_NeverWaitOnEachOther` | the blocking stops: two streams, one destination, first leg never terminates, both still subscribed. Times out on the destination-only key |
| `RoutingBackpressureShapeTest.OneMultiplexerDestination_ManyStreams_IsBreadthNotHeadOfLine` | the production shape: 64 legs, 1 destination, 64 streams ⇒ 64 channels / 1 destination / deepest 0. Reports deepest 63 on the destination-only key |
| `OrderedRouteDispatcherTest.FramesOfOneStream_OnAMultiplexer_StillSubscribeInOrderOneAtATime` | the invariant the narrowing could have broken: one stream still serialised, still in arrival order, on a destination now serving many channels |
| `OrderedRouteDispatcherTest.ChannelKeyIsAPair_NotAConcatenation` | the key is a pair; no delimiter collision |
| `DeliveryIdentitySurvivesPackagingTest` | the seam the whole fix rests on — the identity is stamped by `Package`, readable off the envelope after the type is gone and after a JSON hop, and `null` for a payload that exposes none |
| `RoutingBackpressureShapeTest.SameInFlightCount_MeansBothBusyAndBlocked_…` | the in-flight count alone still cannot tell breadth from blocking |
| `OrderedRouteDispatcherDrainRecursionTest` | a 32-deep backlog behind one head leg still drains without recursing its stack — now built on one shared stream id, because a backlog that deep only exists within a channel |

## Where it lives

| | |
|---|---|
| The FIFO and its channel key | `src/MeshWeaver.Hosting.Orleans/OrderedRouteDispatcher.cs` |
| The read on the turn, and the report | `src/MeshWeaver.Hosting.Orleans/RoutingGrain.cs` |
| The shared identity reader | `src/MeshWeaver.Messaging.Contract/DeliveryIdentity.cs` |
| Where the identity is stamped | `MessageDelivery.Package` |
| The other reader | `src/MeshWeaver.Messaging.Hub/MessageStormBreaker.cs` |
| Why the FIFO cannot simply be removed | [Pod-Hub Delivery — the Transport Swap and its Roll Plan](../PodHubDeliveryRollPlan) |
| The guard the order protects | [Data Sync and CRDT](../DataSyncAndCrdt) |
