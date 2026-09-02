---
Name: The Pod-Hub Claim Must Be Re-Asserted
Category: Architecture
Description: A pod-hub claim publishes an address→silo mapping into Orleans' grain directory — the one component that is re-partitioned on every membership change. Asserting it once was the root cause of #2938/#2915, and the fix is the move Orleans' own ClientDirectory already makes.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/><circle cx="12" cy="12" r="2.5"/></svg>
---

# The Pod-Hub Claim Must Be Re-Asserted

Read [Pod-Hub Delivery — the Transport Swap and its Roll Plan](../PodHubDeliveryRollPlan) first. This
page is the bill that plan's own ledger predicted and did not price.

## The one-sentence cause

> **A pod-hub claim publishes an address→silo mapping into Orleans' grain directory, that directory
> is re-partitioned on every cluster membership change, and the claim was asserted exactly once.**

Everything else on this page follows from those three facts standing together.

## How a pod-process hub is reachable at all

A hub that lives in a .NET process rather than in a grain — `mesh/{meshId}`, `cache/{meshId}`,
`portal/nodeops-{meshId}`, `import/{meshId}`, every `portal/{circuitId}` — is reached by a directed
call to `IPodHubGrain`, whose *identity is the address*. The owning process creates that grain's
activation itself (`OrleansRoutingService.AttachPodHub` → `IPodHubGrain.Attach`, under
`[PreferLocalPlacement]`) and pins it, so **Orleans' single-activation guarantee turns its own grain
directory into the address→silo map** — with no directory of ours to write, keep durable, or lose.

That is an elegant trick and it is load-bearing. It also means the map has exactly the durability of
the grain directory, and no more.

## The two properties that turn a lost entry into a permanent one

**1 · `[PreferLocalPlacement]` places on the CALLER.** When the directory holds no entry for an
address, the next router to deliver to it does not fail — it *creates the activation on its own
silo*. That silo has no local route, so it answers `PodHubNotHereException` and deactivates. The next
delivery does the same. **Every refusal re-creates the condition that produced it**, from whichever
router happens to call next.

**2 · The refusal is reported to the SENDER, never to the owner.** `RoutingGrain` NACKs the message's
sender with a transient `DeliveryFailure`. The one process that could repair the mapping — the owner
— is by construction the process the router could not reach. Nothing tells it.

Put those together and a mapping that is lost once is lost for the life of the process.

## What was measured

`memex-cloud`, 2026-09-01, via Loki (31 d retention, so the windows below are fully covered) and
`kubectl get`:

| Observation | Reading |
|---|---|
| Every pod logs `Pod-hub claim for mesh/{id} … did not land within its initial budget` at startup | The claim runs before the local silo is `Active`, so prefer-local cannot place locally and falls back to a *random other* silo, which has no local route and answers `false` |
| **Zero** `landed after its initial budget was exhausted` lines in 8 days / 36 M log lines | A claim that missed **never** came back |
| One pod refused `portal/nodeops-{id}` + `cache/{id}` of a **live** peer at a flat ~40/h for 12 h, spanning a container restart | Per-pod and persistent, not per-request random |
| `cache/{id}` of a live pod refused by **three** other live pods simultaneously | The cluster genuinely held no entry — not one router's stale cache |
| The exception on the claim's warning was `PodHubNotHereException` with **no stack** | It was constructed by the claim itself, i.e. `Attach` answered `false` — not a directory fault thrown across the wire |

The startup population and the lost-after-landing population are two different triggers of one
cause. Neither recovers, for the same reason: **there is no second assertion.**

## The user-visible face of it: `/api/content` answers 503 (#2901)

The measurement above is stated in routing terms. This is what the same fault looks like from a
browser, and it is worth writing down because the shape was mis-attributed twice before the routing
cause was found — once to a post-upload *settle window*, once to the memory `PubSubStore` and the
streams-retirement program (#1729/#1742).

`/api/content/{node}/{collection}/{file}` resolves the owning node's collection config with **one
`GetDataRequest` per request** — deliberately uncacheable, because that round trip IS the permission
check (`ContentFileResolver.Resolve`). It is issued from `portal/nodeops-{meshId}` and bounded by
`ReadBudget.Default` (10 s). When the reply cannot get back, the budget is the only terminal, and the
route answers **503 after ~10.3 s, per request, for as long as the process lives**.

### The chain, measured end to end on `memex-cloud`, 2026-09-02

| # | Observation | Reading |
|---|---|---|
| 1 | `Content read timed out for DoublePendulum/content/og-card.png` — **33 occurrences in 24 h, every one on a single pod**, zero on the other six replicas and zero in namespace `memex` | Per-pod and persistent. Neither a settle window nor a cluster-wide transport fault can produce that distribution |
| 2 | Same millisecond: `HubUnreachableException: Reading content collection config from 'DoublePendulum' gave up after 10s … Reader: Hub portal/nodeops-Gpdh… RunLevel=Started Queue(buffer=0,deferred=0,exec=0) … Target: NO LOCAL HUB` | The reader is healthy and idle. Nothing arrived — this is the reply leg, not the request leg |
| 3 | `[ROUTE] Directed delivery to pod hub 'portal/nodeops-Gpdh…' was refused: no silo in this cluster is currently serving that hub. Message RawJson (…) **from `AgenticEngineering`** was NOT posted` — logged by **four** different peer pods | The owning per-node hub DID produce the `GetDataResponse`. It had nowhere to go. Exactly the "reply produced, no route home" pair, now on the directed transport |
| 4 | Onset: the refusals and the content timeouts start in the **same hour**, ~8 h after that pod had started clean and served fine | Lost-after-landing, not never-landed |
| 5 | `03:12:52Z` — a peer silo logs `I have been told I am dead, so this silo will stop` and restarts | The membership change that re-partitioned the directory, immediately before the onset |

Rows 3 and 5 together are the whole issue: a membership change moved the grain directory, one
process's entry did not survive it, and every answer addressed to that process was refused from then
on.

### The two corrections this measurement makes

**It is not a settle window.** Nothing about a freshly written file is involved. The discriminator is
*which replica served the request*, which is why the same file alternated 200/503 across round-robin
attempts and why a single-replica probe cannot see the fault at all.

**It is not the memory `PubSubStore`.** The refusal line says in its own words that the message
**was NOT posted to the Orleans stream** — the directed pod-hub call replaced that publish
(`PodHubDeliveryRollPlan`). A discarded stream publish is a different failure with a different fix,
and attributing this to the streams-retirement program would have parked a closed bug behind an open
programme.

### How to re-run it

Two Loki queries, no mutation, and the first one alone settles the family:

```
sum by (namespace,pod) (count_over_time({namespace=~"memex|memex-cloud"} |= "Content read timed out" [24h]))
sum by (pod)           (count_over_time({namespace="memex-cloud"} |= "portal/nodeops-<meshId>" |= "was refused" [1h]))
```

🚨 **`sum by (pod)` is the whole method.** An aggregate count reads as a low-grade cluster-wide
flake; per pod it is one replica at 33 and six at zero, which names the cause. See
[Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly).

### What closes it

Re-assertion repairs the mapping at the next membership change, so the same measurement after the
roll must show the failures **spread across replicas or absent**, never concentrated on one pod
again. A fresh single-pod concentration would mean the re-assertion did not land for that address,
and the next thing to look at is its ORDERING against the directory re-partition — the claim fires on
the membership *notification*, which is not the same instant as the re-partition completing.

> 🚨 A green probe through a load balancer in front of **one** replica has no power to detect a
> per-replica fault. The replica count is part of the verification criterion, not context for it.

## Why the fix is an event and not a retry

The claim already retried indefinitely on failure. That was never the gap. The gap is that **landing
was treated as a terminal**: the moment `Attach` first answered `true`, the claim stopped for the
life of the registration, even though the structure it had written into can be re-partitioned
underneath it minutes later.

So the claim is now re-asserted on **every cluster membership change** —
`IClusterMembershipFeed`, fed on the silo by Orleans' own `ISiloStatusListener`:

```
ClaimTriggers()            // immediately, then once per membership change
    .Select(_ => ClaimOnce())
    .Switch()              // exactly one claim in flight per address, ever
    .Subscribe(…)
```

This is not a watchdog and not a poll. It fires on the *specific event that can invalidate the
assertion*, which makes the claim's lifetime DERIVED (the rule from #2426) rather than bounded by a
counter or by a single success. **Orleans itself already makes exactly this move**: `ClientDirectory`
re-publishes its whole client routing table to every silo on every membership change, for precisely
this reason.

`Switch` rather than `Concat` or `Merge` matters twice. A membership change makes every placement
decision the in-flight round has already taken stale, so the new round must *replace* it rather than
queue behind it; and it bounds the work absolutely — one claim in flight per address no matter how
fast membership churns, so a scale event cannot become a claim storm.

Where no feed is registered — an Orleans client, the Monolith, a bare mesh in a test — membership
cannot change under the process, so the claim is asserted once, exactly as before.

## The diagnosability that was missing

For twelve hours the production line read:

> `Directed delivery to pod hub 'X' was refused: no silo in this cluster is currently serving that hub.`

That sentence covers two different faults with two different fixes, and nothing in the log separated
them:

- the owner's claim genuinely is not held, or
- the grain directory has **no entry at all**, and prefer-local put a throw-away activation on *the
  router's own silo*.

The refusal now names the silo whose activation answered (`PodHubNotHereException.RespondingSilo`).
When that is the silo printing the line, it is the second case. And the claim's own refusal — `Attach`
answering `false` — now says so in its own words instead of borrowing the wire-level sentence, which
is what made eight days of logs ambiguous.

> 🚨 **The owner-side half of this failure is invisible in production by design.** `PodHubGrain`'s
> step-aside and refusal lines are `Information`, and the successful claim is `Debug`; the deployed
> log configuration ships neither. That is the correct cost model — those lines are per-delivery —
> but it means an investigation must reach for the *router-side* `Warning`, and that line therefore
> has to carry the identifying facts. Do not "temporarily" raise a level to investigate; add the fact
> to the line that already ships.

## What this does not fix

Re-assertion repairs a lost mapping at the next membership change. On a cluster that is completely
static it would not fire — the mapping would also not be at risk there, since the directory is only
re-partitioned when membership moves, but that is an argument from the mechanism rather than a
measurement, and it is stated here rather than glossed. A stronger form (the owner detecting
unreachability directly) has no seam today: the refusal is raised on a silo that does not know who
the owner is.

The claim's placement during **silo startup** is also not ordered on readiness. `RegisterStream`
orders its Orleans *stream* subscription on `OrleansStreamingReadiness` (lifecycle stage `Active`)
and issues the pod-hub claim immediately, unordered — which is why the eagerly-registered
`mesh/{meshId}` and `cache/{meshId}` hubs burn their whole initial budget in a window where
prefer-local provably cannot place locally. The membership change that fires when the silo reaches
`Active` now repairs that, so the fault is closed; the wasted burst and its per-pod startup `Warning`
remain, and closing *those* is a separate, smaller change.

## Related

- [Pod-Hub Delivery — the Transport Swap and its Roll Plan](../PodHubDeliveryRollPlan) — the transport,
  and the "what the swap traded" ledger this page settles.
- [Orleans Stream Pub-Sub Durability](../OrleansStreamPubSubDurability) — why the transport exists.
- [Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly) — the method every number above
  was produced with.
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — a silence is a wedge.
