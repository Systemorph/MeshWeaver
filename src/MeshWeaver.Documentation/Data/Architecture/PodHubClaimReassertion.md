---
Name: The Pod-Hub Claim Must Be Re-Asserted
Category: Architecture
Description: >-
  A pod-hub claim publishes an address→silo mapping into Orleans' grain directory — the one component
  that is re-partitioned on every membership change. Asserting it once was the root cause of
  #2938/#2915, and the fix is the move Orleans' own ClientDirectory already makes.
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

The public instance, 2026-09-01, via Loki (31 d retention, so the windows below are fully covered) and
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

### The chain, measured end to end on the public instance, 2026-09-02

| # | Observation | Reading |
|---|---|---|
| 1 | `Content read timed out for DoublePendulum/content/og-card.png` — **33 occurrences in 24 h, every one on a single pod**, zero on the other six replicas and zero in the control instance's namespace | Per-pod and persistent. Neither a settle window nor a cluster-wide transport fault can produce that distribution |
| 2 | Same millisecond: `HubUnreachableException: Reading content collection config from 'DoublePendulum' gave up after 10s … Reader: Hub portal/nodeops-Gpdh… RunLevel=Started Queue(buffer=0,deferred=0,exec=0) … Target: NO LOCAL HUB` | The reader is healthy and idle. Nothing arrived — this is the reply leg, not the request leg. 🚨 **The `Target: NO LOCAL HUB` clause carried NO information when this was captured** and must not be read as evidence here: the probe behind it asked the READER's own hosted-hub collection, and a per-node hub is hosted by the MESH hub, so on this seam it printed that verdict for every read alike. Fixed in [#3931](https://github.com/Systemorph/MeshWeaver/issues/3931); rows 3 and 5 are what carry this verdict, and they are independent of it |
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
sum by (namespace,pod) (count_over_time({namespace=~"<control-ns>|<public-ns>"} |= "Content read timed out" [24h]))
sum by (pod)           (count_over_time({namespace="<public-ns>"} |= "portal/nodeops-" |= "was refused" [1h]))
```

🚨 The second filter is `portal/nodeops-` — the **prefix**, deliberately, with no mesh id. Every
pod-hub address shares it, so the query runs as written against any deployment. An earlier draft
wrote `portal/nodeops-<meshId>`, which returns zero rows unless the reader first substitutes a value
they do not have yet — and a zero-row answer here reads exactly like "the refusals have stopped",
which is the one conclusion this page exists to prevent someone drawing by accident. If you want a
single mesh, add ` |= "<meshId>"` as a third filter once you have one from the first query's output.

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
ClaimTriggers()            // after Active, then once per membership change
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

## Two windows in which the trigger was dropped anyway (#3931)

Re-asserting on the right event is necessary and was not sufficient. The trigger could be lost in two
places *after* the design above was in, and in both the outcome is indistinguishable from never having
had re-assertion at all: no claim, no retry, no log line, and nothing left to re-make the mapping
until the next membership change — the #2938 state this page exists to remove.

> 🚨 **Both are DROPS, not delays.** The grain-side attach count never moves, so "the claim was made
> and its result went unobserved" is excluded by the instrument itself: the count is published from
> inside `IPodHubGrain.Attach`.

### Window one — `StartWith` subscribed the feed only after the first claim round had run

`ClaimTriggers` composed the initial assertion as `Changes.StartWith(0L)`. **`StartWith` is a
`Concat`**: the source is subscribed only once the prefix has been fully *processed*, and processing
that prefix runs the entire first claim round — `Attach` included — on the subscribing thread.
`IClusterMembershipFeed.Changes` is hot and deliberately does not replay, so a change arriving in
that window was dropped **where it was published**: `Subject.OnNext` with no observer is a no-op.

The window is the first claim of a hub's life — i.e. the pod's boot, which is exactly when membership
moves. #3983 measured 34 placement failures in 65 s across two pods, all inside it.

Measured on System.Reactive 6.1.0 (the pinned version) with the real shape
(`…ObserveOn(Scheduler.Default).SelectMany(_ => triggers).Select(round).Switch()`) and a round that
parks inside its subscribe, so the window is observable rather than inferred:

```
StartWith: ROUND-0-enter -> PUSH-enter -> PUSH-leave -> RELEASE -> ROUND-0-leave -> SUBSCRIBE-feed
Merge    : SUBSCRIBE-feed -> ROUND-0-enter -> PUSH-enter -> RELEASE -> ROUND-0-leave -> ROUND-42-enter
```

Under `StartWith` the push completes with the feed still unsubscribed and no round for it ever runs.
The fix is `Changes.Merge(Observable.Return(InitialClaim))`: `Merge` subscribes its sources
left-to-right, so the **durable** source is listening before the one-shot initial trigger can start
any work, and the merge gate then serialises the two — a change arriving mid-round queues behind it
instead of racing it.

### Window two — the claim/dispose handshake answered a transient condition terminally

The handshake protecting `Attach`/`Detach` ordering was a single token. A round that found the token
at "someone is already starting an attempt" answered `Observable.Empty` — a **terminal** answer to a
**transient** condition. A retry round re-subscribes on its backoff timer's thread while a membership
round arrives on the feed's, so the overlap is ordinary, and it is most likely exactly during churn,
because churn is what makes a claim bounce in the first place.

It is worse than a dropped trigger: `Switch` has already cancelled the round it overlapped, so the
membership change **destroyed an in-flight claim and made none of its own**.

The handshake is now a **ledger, not a mutex**. One interlocked word carries "disposal has been
requested" in bit 0 and the number of rounds currently inside their synchronous attach window in the
rest. The only reason a round may refuse to claim is disposal; whoever observes "disposed **and** the
window is empty" owns the release — `Dispose` when it finds the window empty, otherwise the last
round out — so `Detach` can never overtake an `Attach` that is still being invoked, and concurrent
rounds are ordinary rather than an error.

### What pins it

`PodHubClaimReassertionTest` carries both directions, and both are deterministic rather than
probabilistic: the test grain PARKS inside a nominated `Attach` call, so the contention is created
rather than waited for.

| Test | Direction |
|---|---|
| `AMembershipChangeDuringTheInitialClaim_IsStillAsserted` | window one |
| `AMembershipChangeDuringARetryingClaim_IsStillAsserted` | window two |
| `AContendedMembershipChange_IsAssertedExactlyOnce` | the opposite failure — one change is one claim |

The third is not decoration. A "fix" that merely re-triggered the round would satisfy both positive
pins and reintroduce the claim storm `Switch` exists to bound: every attempt makes the owning silo
log a line, which is the log-storm shape #2426/#2546 exist to remove.

## #7842: replies refused while ANOTHER silo leaves

During a memex roll a cache hub's replies were refused by two routers ("no silo in this cluster is
currently serving that hub", both naming the same third silo) 200–400 ms after the OLD silo began its
stop, while the cache hub's own pod was alive; its requests timed out after 60 s. #7907 repeated the
shape on another instance.

### The mechanism, reproduced on production's directory

**Every in-process Orleans test cluster runs a different grain directory from production.**
Orleans' `TestClusterBuilder` adds its own `ConfigureDistributedGrainDirectory` configurator to every
test cluster, so test silos run `DistributedGrainDirectory`, which hands a leaving silo's directory
partitions over. A production silo (`ConfigureMeshWeaverServer`) registers no directory and runs Orleans'
default `LocalGrainDirectory`, which hands nothing over when the partition owner leaves. That is why the
first in-process attempt (a three-silo `FaultInjectionCluster`; the silo holding the pod hub's
directory partition held in `ShuttingDown` by a lifecycle participant one stage below `BecomeActive`; the
owner's re-assertion held at its outgoing `Attach`) delivered the post every time: it was measuring
the test default.

`FaultInjectionCluster.UsesDistributedGrainDirectory` now declares which directory a fixture runs, and
`DefaultDirectoryIsDistributed(i)` reads it off each silo; every fixture asserts the two agree at deploy.
With the harness's configurator removed, so that the silos run `LocalGrainDirectory`, the same case
measured (Orleans 10.4.2):

| Directory | While the leave and the owner's re-assertion are both held | After the re-assertion is released |
|---|---|---|
| `DistributedGrainDirectory` (test default) | The post from the third silo is delivered. | — |
| `LocalGrainDirectory` (production) | The request from the third silo is **refused**: the directory has no registration, prefer-local places a throw-away activation on the asking silo, and it answers `PodHubNotHere`. The hub receives nothing. | Posts reach the hub again. |

So on production's directory the gap is exactly the time between the partition owner leaving and the
owner's membership-change re-assertion landing. That fits the 200–400 ms of #7842. The cases:

- `ADeliveryToAPodHubSurvivesItsDirectoryPartitionLeavingTest`: the distributed directory survives the leave.
- `ALocalDirectoryLosesAPodHubEntryUntilItsClaimIsReassertedTest`: the local directory loses the entry, and the re-assertion heals it. It is the counterpart that shows the directory is the variable.
- `ADeliveryToAPodHubWithNoSiloLeavingArrivesTest`: the route with no leave.

Two fixes were weighed. Part 2 is shipped. Part 1, a move to `DistributedGrainDirectory`, is decided
against and parked (see below).

### Part 2 (shipped): a refused reply ends in a named failure at its asker

A transient pod-hub refusal used to be answered only to the delivery's SENDER
(`RoutingGrain.AnswerPodHubNotHere`). For a reply, the sender is the REPLIER, which has nothing to retry,
while the hub waiting for the reply is the delivery's TARGET, the very address the router could not
reach. Now, when the refused delivery is a correlated reply (`RoutingGrain.IsAwaitedReply`: it carries
`PostOptions.RequestId`, and `MayAnswer()` holds, so a NACK never begets a NACK), the router also sends
that target a `DeliveryFailure` (`RoutingGrain.RefusedReplyFailure`):

- correlated by the ORIGINAL request's id, so the asker's `hub.Observe(...)` callback errors at once;
- `ErrorType.ShuttingDown`, because the outcome is unconfirmed rather than failed (the same verdict
  `MessageService` gives a reply a disposing hub could not send), and it is the verdict
  `SynchronizationStream` and `MeshNodeStreamCache` ride out and re-ask on;
- whose message is a sentence a person may read (`PortalErrorReporting.Describe` shows it verbatim):
  the catalog key `error.replyRefusedOnTheWay`, resolved in the asker's language. The reply carries the
  asker's `AccessContext`, because `ResponseFor` copies it from the request, so the router reads
  `reply.AccessContext.Locale`. This follows the pattern core's other user-facing refusals use
  (`SynchronizationStream`: `LocalizationCatalog.Get(key, delivery.AccessContext?.Locale)` on the
  failure, with the details in the log).

The router's detailed refusal is diagnostic text and goes to the log, never to the viewer
(`RoutingGrain.DescribeRefusedReply`). It names the reply, the replier, the asker, the request and the
refusal. A routed reply is packaged as `RawJson`, whose CLR type names only the wrapper, so the payload
is identified by `DeliveryPayloadBounds.Preview` and JSON-quoted as that reader's contract requires.

**The carrier does not use the asker's directory entry**, because that entry is the thing that is
missing. `ISiloLocalDeliveryService` is an Orleans grain SERVICE: a per-silo system target, addressed by
the silo itself through `GrainServiceClient.GetGrainService(SiloAddress)`. It hands a delivery to that
silo's local route table, and it is registered by `ConfigureMeshWeaverServer`.

The carrier is a grain service rather than a grain keyed by the silo because of a draining asker.
Orleans places no new activation on a `ShuttingDown` silo and is deactivating the ones it has, so a cold
per-silo grain could not reach an asker there. A grain service exists from silo start to termination
and needs no placement. Orleans stops grain services first on a graceful stop, but `Stop()` only flips
their status: the system target keeps answering calls until the silo's message centre goes down.

The router does not know which silo hosts the asker, and the envelope does not say, so it asks each
member silo that is `Active` or `ShuttingDown`. The one whose local route table holds the asker's host
delivers, and every other silo answers `false`. That costs a few system-target calls per refused reply,
never per message. If no silo takes it (the asker's process is gone, or it runs a build without the
carrier), the router logs ONE Warning with the diagnostic description, never silence. The work runs on
the routing pool and is tracked by the silo-stop quiescence gauge, like every NACK.

Tests (`MeshWeaver.FaultInjection.Test`, real multi-silo Orleans test clusters):

- `ARefusedReplyTellsItsAskerByNameTest`. The asker's claim is held by `ClaimHold`, an outgoing Orleans
  grain-call filter, so its address has no registration. The responder's correlated reply is packaged as
  `RawJson`, the shape production routes, and is refused on the other silo. The test awaits the positive
  refusal signal: the responder receives the router's `TargetUnserved` NACK, and the answer's dispatch
  did not fault. The asker then receives the correlated `ShuttingDown` failure whose message is the
  German catalog sentence, because the asker's request carried `Locale = "de"`.
- `ARefusedReplyReachesAnAskerOnAShuttingDownSiloTest`. The asker's silo is held in `ShuttingDown` by
  `ShutdownHold`, its local route is still live, and its carrier has never been called. The reply is
  refused on the Active silo, and the asker is told through its silo's grain service.
- **Negative control 1:** `ARefusedReplyTellsItsAskerByNameTest` with the router's report call removed is
  red: nothing reaches the asker within 10 s. This was measured locally and is not a committed test.
- **Negative control 2:** `AnUncorrelatedMessageRefusedOnItsWayIsNotReportedTest`. The same refusal,
  with the same positive signal at the responder, of a message that is not a reply produces no failure
  at its target.
- `RefusedReplyFailureTest` (`MeshWeaver.Hosting.Orleans.Test`). For a packaged reply, the diagnostic line
  names the payload's `$type` and never `RawJson`. The asker's sentence is the catalog's in the asker's
  language, is not English for a German asker, and carries no router diagnostics.

### Part 1 (moving to `DistributedGrainDirectory`): DECIDED AGAINST, parked

**Decision: stay on `LocalGrainDirectory`, ship part 2 only.** There is no directory switch, no
coordinated restart and no migration that accepts duplicates. The reasons:

- On Orleans, a rolling migration between the directories means **duplicate activations** while old
  and new silos coexist. For this mesh that is two activations serving one node, i.e. two per-node hubs
  for one address, during every such roll.
- The gap the directory leaves is the 200–400 ms between the partition owner leaving and the owner's
  re-assertion landing. With part 2, a reply lost in that gap fails fast with a named,
  `ShuttingDown`-classified error, and callers re-ask on that error.

What the analysis found (Orleans 10.4.2, from the decompiled runtime and the upstream repository):

1. **Each silo picks its directory itself**, so old and new silos run side by side during any roll that
   changes it. A switch on the deployment record does not avoid that, because flipping it is itself a
   roll, and which builds the silos run does not change it.
2. **Per grain type (a named directory) is a split by construction.** `AddDistributedGrainDirectory(name)`
   plus a per-type resolver (or `[GrainDirectory]`) puts `IPodHubGrain` in a second directory on the new
   silos only. An old silo looks the hub up in its `LocalGrainDirectory`, finds nothing, places a
   throw-away activation on itself and refuses. That is #7842's failure on every roll, until the last old
   silo is gone. Separately, a named distributed directory caused duplicate activations of
   default-directory grains from 10.2.0 to 10.4.0 (dotnet/orleans#11512, fixed in 10.4.2 by
   dotnet/orleans#11553).
3. **The default directory (all grains) has a rolling-upgrade path, and Orleans' own path accepts
   transient DUPLICATE ACTIVATIONS while it is mixed.** 10.x ships compatibility system targets for a
   mixed cluster: `DistributedRemoteGrainDirectory` on new silos, and
   `LocalGrainDirectoryPartitionCompatibility` and `LocalGrainDirectoryClientCompatibility` on old ones
   (dotnet/orleans#9881). Orleans describes the mixed phase as one that "can expose one older local
   activation alongside one newer distributed activation", converging once the legacy hosts are gone
   (dotnet/orleans#10705).
4. **The upstream proposal that would have tightened the mixed phase was not merged.** dotnet/orleans#11564
   ("preserve mixed-cluster registration safety") was closed without merging. Its closing comment
   accepts that duplicate activations can occur while legacy and distributed directory hosts coexist,
   and says the proposal is not being pursued. The issue it addressed, **"Mixed local/distributed
   directory rolling upgrade loses registrations when local silos leave"** (dotnet/orleans#11557), is
   still open, and its reproduction is our failure: after a legacy silo leaves, a live activation has no
   registration.

**If this is ever reopened**, the options are, stated neutrally:

- a two-rollout migration that accepts duplicate activations, and therefore duplicate per-node hubs,
  during the mixed roll;
- a coordinated full-cluster restart onto the distributed directory, which means downtime;
- staying on `LocalGrainDirectory` with part 2, the current decision.

A rolling migration should be reconsidered only against a separately verified guarantee that
registrations are preserved and no duplicates occur in a mixed cluster.

### Not established

- That production's #7842 and #7907 refusals came from this window. The mechanism is reproduced
  in-process on production's directory, and it fits the production timing. The asker pod's membership
  notification time and its `[POD-HUB]` lines for 18:16:21Z were not in the shipped logs.
- Whether the re-assertion closes the gap as fast on a loaded production pod as in-process, where it
  lands within milliseconds of being released.
- The part 2 carrier in a mixed roll. An old-image silo has no `ISiloLocalDeliveryService`, so an asker
  hosted there is not told, and the router logs the Warning. This was reasoned, not tested.
- How late in a graceful stop the carrier stays reachable. The test holds the silo in `ShuttingDown`.
  After the message centre stops, the asker's process is going away and is not told.
- Whether a reply's DeliveryFailure always lands in a callback. If the asker's wait has already ended
  (its own budget elapsed), the failure arrives uncorrelated and is handled as an ordinary
  `DeliveryFailure` message.

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

The refusal also names the answering **pod** (`PodHubNotHereException.RespondingPod`, the host name,
which is the pod name on Kubernetes), not only its silo's IP: `… is on silo '10.244.6.213:11111@…'
(pod 'memex-portal-deployment-…')`. An IP cannot be matched against the pod names every other fleet
line is keyed by. Measured on a memex roll (#7842): when the old silo began its stop, three routers on
three pods refused the same live `cache/{meshId}` hub, all naming silo `10.244.6.213`. That cache hub
was waiting for replies its owners had already produced. Whether that silo was the cache hub's own pod
(its claim no longer finding its local route) or another (the directory pointing away from the owner)
could not be decided from the log. A peer predating the field prints `(not reported — a peer
predating the field)` for the pod.

> 🚨 **The owner-side half of this failure is not visible in production by default.** `PodHubGrain`'s
> step-aside and refusal lines are `Information` and now name their pod, and the successful claim is
> `Debug`. The code default ships none of them; an investigation reaches for the *router-side*
> `Warning`, and that line therefore carries the identifying facts (silo and pod). For #7842 the
> portal configuration ships `PodHubGrain` and Orleans' grain-directory namespace at `Information`,
> as a permanent, costed entry in that file (MeshWeaver.Plugins `Memex.Portal.Distributed`
> appsettings). The directory lines fire on membership changes. The `PodHubGrain` refusal line is
> per refused delivery, so a refusal burst costs one line per refusal. Never raise a level in code to
> investigate; the configuration entry is the place, with its cost stated.

## What this does not fix

Re-assertion repairs a lost mapping at the next membership change. On a cluster that is completely
static it would not fire — the mapping would also not be at risk there, since the directory is only
re-partitioned when membership moves, but that is an argument from the mechanism rather than a
measurement, and it is stated here rather than glossed. A stronger form (the owner detecting
unreachability directly) has no seam today: the refusal is raised on a silo that does not know who
the owner is.

The claim's placement during **silo startup** is now ordered on the same
`OrleansStreamingReadiness` signal as the stream subscription (lifecycle stage `Active`). Before
#3983/#3984, `RegisterStream` issued the pod-hub claim immediately, unordered. The eagerly-registered
`mesh/{meshId}` and `cache/{meshId}` hubs therefore called `IPodHubGrain.Attach` before this silo —
and sometimes before any silo — advertised the grain type. Orleans rejected the invalid placement
with `Known nodes with grain type: none`; its per-attempt logger produced #3983 while the exhausted
Polly call produced #3984. The claim now waits for `Active` and then moves off the lifecycle thread
before touching the grain factory. The membership-change re-assertion remains the repair for a
mapping lost after startup.

## Related

- [Pod-Hub Delivery — the Transport Swap and its Roll Plan](../PodHubDeliveryRollPlan) — the transport,
  and the "what the swap traded" ledger this page settles.
- [Orleans Stream Pub-Sub Durability](../OrleansStreamPubSubDurability) — why the transport exists.
- [Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly) — the method every number above
  was produced with.
- [Error Propagation & Wedges](../ErrorPropagationAndWedges) — a silence is a wedge.
