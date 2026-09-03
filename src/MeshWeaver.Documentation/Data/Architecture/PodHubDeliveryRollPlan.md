---
Name: Pod-Hub Delivery — the Transport Swap and its Roll Plan
Category: Architecture
Description: "Delivery to a pod-process hub moves from an Orleans stream publish to a directed grain call on the owning silo. The transport swap is a two-release roll with a deliberate fallback, because the previous change in this area left the fleet split-brain for the duration of its own deploy."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h10a4 4 0 0 1 0 8H8a4 4 0 0 0 0 8h12"/><path d="m17 3 3 3-3 3"/></svg>
---

# Pod-Hub Delivery — the Transport Swap and its Roll Plan

Read [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability) first: it
is why this change exists. In one line — **a delivery to a hub that lives in a .NET process rather
than in a grain rides a stream publish, and a stream publish to nobody succeeds.** Every other
cross-process leg in the mesh is a grain call, which is retried, NACK'd, and heard about when it
fails. This one leg is not, and that asymmetry is the defect.

## 🚨 The roll plan comes first, and here is why

The previous fix in this area — a durable `PubSubStore` (#1770) — shipped without one and left the
fleet **split-brain on pub-sub for the duration of its own roll: 39 stranded addresses, unpredicted**.
A transport swap has exactly that shape, and worse, because during a surge-first roll the two
releases are *both live and both routing*:

| | release N (old pod) | release N+1 (new pod) |
|---|---|---|
| **listens on** | its Orleans stream subscription | the stream **and** its pod-hub grain |
| **sends via** | a stream publish | a directed grain call |

A new pod calling the pod-hub grain for a hub owned by an **old** pod finds no activation — the old
pod never attached one. Without a fallback that is a `NotFound` NACK for a hub that is alive and
well, on every cross-pod exchange, for the whole overlap window. **That is the outage this section
exists to prevent.**

## The two-release plan

**Release N+1 — dual: attach both, prefer the call, fall back to the stream.**

1. `OrleansRoutingService.RegisterStream` keeps doing everything it does today (local route, Orleans
   stream subscription) **and** attaches the address's pod-hub grain on the owning silo.
2. `RoutingGrain`'s stream branch tries the directed grain call first. A `PodHubNotHereException` —
   the grain answering *"no silo in this cluster is serving this address through me"* — is **not** a
   failure: it means the owner is either gone or still on release N. The router then takes the old
   path, publishing to the stream exactly as before.
3. The stream publish keeps the subscriber check landed for #1742, so the fallback is not a return
   to silence: an address that genuinely has no owner on *either* transport is NACK'd, not dropped.

   > This is the whole reason the subscriber check is worth landing separately and first. It is what
   > makes the fallback safe, and it stays useful for as long as the fallback exists.

4. Nothing needs to be sequenced with the migration, no schema, no new storage. A pod on release N
   is unaffected: it never calls a grain it does not know about, and its subscribers still receive
   stream publishes from release N+1 pods.

**Release N+2 — single: delete the fallback.** Only once *every* pod in *every* deployment runs
N+1 or later. The check for that is not a date — it is that no pod logs the fallback line for a
full roll (below).

**A revert is safe at any point.** Release N+1 is a strict superset of release N's behaviour: it
still subscribes the stream, still publishes to it when the call cannot land. Rolling back to N
loses the directed call and keeps working.

### The one-line signal that decides when N+2 may ship

The fallback logs, once per address per activation, at `Information`:

```
[ROUTE] Pod-hub grain for {Address} is not attached — falling back to the stream publish.
        This is expected only while a release-N pod still owns that hub.
```

**Ship N+2 when a full rolling deploy produces none of those lines.** A line during the overlap
window is the plan working. A line *after* the roll has completed is a hub whose owner never
attached — investigate that before removing the fallback, never after.

## The mechanism

The address→silo map is **Orleans' own grain directory**. There is no second directory to write, to
keep durable, or to lose:

- `IPodHubGrain` is keyed by the address path and placed with `[PreferLocalPlacement]`, so the
  activation lands on the silo whose `RegisterStream` created it. Orleans' single-activation
  guarantee then makes the grain directory the map, cluster-wide, with no custom placement director
  and no state of our own.
- Its `Deliver` looks up `OrleansRoutingService.TryGetLocalRoute(address)` — the table that has
  always been the authority for "this process hosts that hub", is written **synchronously and
  unconditionally** by `RegisterStream`, and does not depend on Orleans streaming being ready at
  all. That is the property the stream leg never had.
- **A silo that cannot serve the address fails LOUDLY and steps aside.** It calls
  `DeactivateOnIdle()` so the next attach can be placed on the true owner, and throws
  `PodHubNotHereException` — deliberately NOT a transient Orleans rejection, so
  `DeliverToGrainWithRetry` does not retry it into a loop.

Two lifecycle details that are the whole correctness argument:

- **Keep-alive.** `Attach` calls `DelayDeactivation` so grain collection can never re-place a live
  hub's activation onto whichever silo happens to call next (`PreferLocalPlacement` prefers the
  *caller's* silo, so a collected activation would migrate to a router). The activation's lifetime
  is deliberately tied to the owner's registration, not to traffic.
- **Detach on disposal.** `RegisterStream`'s disposal deactivates the grain, so a hub that MOVES
  silos — a `portal/{user}` circuit reconnecting to another pod is the everyday case — leaves no
  activation stranded on the pod it left. `Attach` reports `false` when it lands on a silo that is
  not the owner, and the owner retries, bounded, so the move converges instead of wedging.

`OrderedRouteDispatcher` is unchanged and stays: the per-destination FIFO is a correctness
requirement of the delta protocol, and a call into a `[Reentrant]` grain does not restore ordering.
`StreamMessageSizeGuard` (#1890) does not disappear either — it **retargets**. It still guards the
stream fallback, where the wall is Orleans' 1 MiB memory-stream block and crossing it is *silent*
(the publish succeeds and the pulling agent rejects the message forever, naming a queue id and
nothing else). On the directed call the wall is Orleans' own `MaxMessageBodySize`, and crossing it
**throws** — so the router NACKs instead of dropping, which is the outcome the guard exists to
produce. #1890 was itself "the NACK died at the wall it was describing"; reproducing that one layer
over would be the same bug at a new address, so when the fallback goes the guard moves to the call's
wall rather than being deleted with it.

## What this finally delivers

> An undeliverable delivery must surface as a `DeliveryFailure`, never as silence.

The subscriber check narrowed the silence. The directed call **removes the class**:

| residual | stream + subscriber check | directed grain call |
|---|---|---|
| no subscriber registered | NACK'd (checked) | NACK'd (the call has nowhere to land) |
| subscriber vanishes between check and publish | **silent** | NACK'd — the call fails |
| `MemoryStreamQueueGrain` dies with its silo | **silent** | not on the path at all |
| pub-sub registry lost (non-AdoNet multi-silo) | NACK'd, but **wrongly** — the hub is alive | delivered; the registry is not consulted |
| the reply's own requester | not reached — the NACK goes to the REPLIER | the reply is delivered, so there is nothing to report |

That last row is the one that matters most and the one a producer-side check can never fix: when a
*reply* cannot be delivered, the party that needs to know is the original requester — and it is
precisely the party that is unreachable. Making the reply land is the only answer.

## 🚨 What the swap traded — read this before calling #1742 closed

The table above is about the *stream's* residuals, and it is accurate. It is also only half the
ledger, because **the directed call is not free of a shared dependency — it moved onto a different
one.** Orleans' own grain directory is the address→silo map (that is the whole trick, and why this
design needed no directory of ours). The grain directory is *also* the component that is unstable
while cluster membership changes — i.e. during a rolling deploy, which is the exact window every
production symptom in #1729 / #1742 was measured in. Same window, new dependency:

```
Orleans cannot address the call
  → MessageCenter.OnAddressingFailure
  → RejectMessage(msg, RejectionTypes.Unrecoverable, ex)     ← the DIRECTORY's exception, attached
  → caller: CallbackData.HandleRejectionResponse
        exception = rejection?.Exception ?? new OrleansMessageRejectionException(…)
                    ^^^^^^^^^^^^^^^^^^^^ the carried one WINS
  → RoutingGrain sees a BARE Orleans.Runtime.OrleansException
        "…is not stable to perform the lookup … Retry later."
        "…cannot forward LookUpAsync to owner … because hop limit is reached"
```

`RoutingGrain.IsTransientFailure` matched `TimeoutException` and
`OrleansMessageRejectionException` — neither of which this is. So for a condition whose own message
ends in *"Retry later."*:

1. the delivery was **never retried**, although the retry-with-fresh-resolve primitive it needed was
   already sitting one line away and already applied to exactly this class of condition;
2. both exception arms then NACK'd the sender with a **hard-coded terminal `ErrorType.Failed`** — the
   same defect #2346 / #2451 removed from the neighbouring `result.State == Failed` arm and left
   standing on these two. That verdict is what costs a *subscription* rather than a message:
   `SynchronizationStream`'s resubscribe latch and `MeshNodeStreamCache.IsTransientOwnerFailure` ride
   out `ShuttingDown` and **tear down** on `Failed`;
3. and when `PostFailure`'s own directed NACK hit the same unstable directory it took
   `LogUndeliverableNack` — the stream fallback was reached only for `PodHubNotHereException` — so
   the sender was told **nothing at all**. That is #1742's headline symptom, reproduced on the
   transport that replaced it.

Production evidence: **#2357**, 16 occurrences across two rolling deploys, naming
`IPodHubGrain.Deliver` and `IMessageHubGrain.DeliverMessage` among the dropped targets.

**The cure is classification, not a new mechanism** (`OrleansRoutingService.IsDirectoryUnstable`,
`RoutingGrain.ClassifyDeliveryException`, and `PostFailure` falling back to the stream on ANY failed
directed NACK rather than only on `PodHubNotHere`). Nothing new spins: the retry existed and was
unreachable, which is the same inert-classifier shape as #2451.

> 🚨 **This is also why the "durable stream provider" decision does NOT close #1742.** A durable
> provider improves the *fallback* — the RAM-resident queue grain, item 1 of
> [Orleans Stream Pub-Sub Durability → What this does NOT fix](/Doc/Architecture/OrleansStreamPubSubDurability),
> plus hubs owned by an Orleans client process. It cannot touch the primary reply path, because
> the directed call never goes near the stream provider. Size that decision on #2320 / #2322
> (both stream-leg tickets), not on this issue — and that decision is now made:
> [Durable Streams Are Mesh Nodes](/Doc/Architecture/DurableStreamsViaMeshNodes) retires the
> fallback instead of hardening it.

## A released address answers terminally

`Detach` used to deactivate the activation on idle. That threw away the one fact the cluster cannot
otherwise learn — that the **owner said goodbye** — so the next delivery re-created an activation on
the caller's silo and the router could only answer "no silo serves this hub right now", the
transient verdict above. For a hub moving between pods that is right. For a closed Blazor circuit it
is a storm: the owner-side eviction (#2426/#2546) is gated against the transient verdict (#2756), so
the owner fanned out to the corpse until the stream's idle release — 46 minutes, 300–1,169 refusals
a minute, measured on memex 2026-09-03.

Now `Detach` marks the activation **released** and keeps it for `ReleasedTombstoneLifetime`;
`Deliver` answers `PodHubNotHereException { Released = true }`, and `AnswerPodHubNotHere` turns that
into `ErrorType.NotFound` + `TargetUnserved` — the shape the eviction acts on. `Attach` clears the
mark, so re-registration converges as before, and a peer predating the field degrades to the
transient verdict, never to a wrong eviction. The full case: [Dead-Circuit Fan-Out
Storm](/Doc/Architecture/DeadCircuitFanOutStorm).

## Related

- [Dead-Circuit Fan-Out Storm](/Doc/Architecture/DeadCircuitFanOutStorm) — the release tombstone,
  and the measured storm that showed the transient verdict alone could never end.
- [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability) — the defect,
  the production evidence, and the subscriber check this plan builds on.
- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — a silence is a wedge.
- [Deployment — AKS](/Doc/Architecture/DeploymentAKS) — the surge-first rolling deploy whose overlap
  window this plan is written against.
