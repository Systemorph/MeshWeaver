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

## Related

- [Orleans Stream Pub-Sub Durability](/Doc/Architecture/OrleansStreamPubSubDurability) — the defect,
  the production evidence, and the subscriber check this plan builds on.
- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — a silence is a wedge.
- [Deployment — AKS](/Doc/Architecture/DeploymentAKS) — the surge-first rolling deploy whose overlap
  window this plan is written against.
