---
Name: Orleans Stream Pub-Sub Durability
Category: Architecture
Description: "Cross-silo delivery to a pod-process hub rides an Orleans memory stream, and whether that stream can find its subscriber is decided entirely by what backs PubSubStore. With the in-memory default the subscriber list dies with a silo, publishes still report success, and replies vanish with nothing logged."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 11a9 9 0 0 1 9 9"/><path d="M4 4a16 16 0 0 1 16 16"/><circle cx="5" cy="19" r="1"/><path d="m2 2 20 20"/></svg>
---

# Orleans Stream Pub-Sub Durability

Most of the mesh's cross-process traffic is an Orleans **grain call**: it is retried, it is NACK'd,
and when it cannot be delivered somebody hears about it. One leg is not. Delivery to a
**pod-process hub** — a hub that lives in a .NET process rather than in a grain — cannot be a grain
call, because Orleans places grains and nothing places a process. Those hubs are reached over an
Orleans **stream** instead, and a stream publish is fire-and-forget.

That set is `MeshConfiguration.StreamRoutedAddressTypes`: `mesh`, `portal`, `client`, `cache`,
`import`, plus whatever a module adds via `AddStreamRoutedAddressType`. Since a **reply** is just a
delivery addressed back at the requester, **every cross-silo reply to one of those hubs rides a
stream** — while the request that provoked it rode a grain call. The two legs of one exchange have
completely different failure semantics, and that asymmetry is the whole subject of this page.

## The mechanism, end to end

```
per-node hub (grain, silo A)              portal/nodeops-… (process hub, silo B)
        │                                                  ▲
        │ reply: GetDataResponse                            │ OrleansRoutingService
        ▼                                                   │  .RegisterStream → SubscribeAsync
  OrleansRoutingService.DeliverMessage                       │
        │ not in silo A's local route table                  │
        ▼                                                    │
  RoutingGrain.RouteMessage  (StatelessWorker, silo A)        │
        │ address.Type ∈ StreamRoutedAddressTypes             │
        ▼                                                     │
  streamProvider.GetStream("portal/nodeops-…").OnNextAsync(…)  │
        │                                                     │
        ▼                                                     │
  MemoryStreamQueueGrain  ──pulled by──▶ PersistentStreamPullingAgent
                                                │
                                                │ "who is subscribed to this stream?"
                                                ▼
                                    PubSubRendezvousGrain  ── state in ──▶  PubSubStore
```

The last box is the one that decides everything. `PubSubRendezvousGrain` holds the stream's
subscriber list; the pulling agent asks it who to hand the message to. **If the answer is "nobody",
the message is dropped and nothing is logged** — that is not a bug in Orleans, it is what a
publish-subscribe channel with no subscribers means.

## Why the loss is silent, permanent, and looks like a per-request flake

`AddMemoryGrainStorage(PubSubStore)` keeps that subscriber list in the RAM of whichever silo
happened to activate the rendezvous grain. So:

1. Silo B's hub subscribes. The registration lands in a rendezvous grain activated on **some** silo
   — possibly A, possibly B; Orleans chooses.
2. That silo departs. **Every rolling deploy guarantees this**: the new pod joins, both are Active
   for an overlap window, the old one leaves.
3. The rendezvous grain re-activates elsewhere with **empty** state. Its `PubSubStore` was RAM that
   no longer exists.
4. Silo B's `StreamSubscriptionHandle` is still valid and reports nothing. It has no way to know.
5. Every subsequent publish to that stream **succeeds** and is **discarded**.

Every observable signal stays clean, which is why this survived months disguised as an intermittent
flake:

| Signal | What it shows |
|---|---|
| `stream subscription could not be attached` (Critical) | never fires — the subscribe succeeded, long ago |
| `reported a delivery error` (the stream `onError` handler) | never fires — Orleans has no delivery failure to report |
| `SiloUnavailableException` / `QueueCacheMissException` | never fires — the queue grain is fine |
| `MEMORY_STREAM_OK` in the routing trace | fires, and means only *"the publish did not fault"* |
| the requester | waits out its **full 60 s** reply budget, then `TimeoutException` |

`RoutingGrain.PostFailure` already documents this exact property for the NACK leg — *"a publish to a
stream with NO live subscriber SUCCEEDS: nothing faults, the continuation never sees IsFaulted, and
the NACK is simply gone"* — and #1486 removed the stream from the **co-hosted** NACK path for that
reason. The forward and reply legs to a *remote* silo still depend on it.

### What it looked like in production (memex-cloud, issue #1729)

Two portal replicas. Probing each pod directly, bypassing the ingress:

| pod | `GET /api/content/AgenticEngineering/content/og.png` |
|---|---|
| `10.244.3.130` | 200, four for four, 6–57 ms |
| `10.244.2.28` | hang, four for four → HTTP 500 at **60.015 s** |

Deterministic **per pod**, not per request — the load balancer's round-robin is what manufactured
the ~50/50 that made it look like a race. Both requests provably *arrived* at the owning hub (an
access denial on a neighbouring path logs its sender, and the cross-silo sender appeared in the
owning pod's log). Forward legs worked in both directions; reply legs worked into one pod and were
silently dropped into the other. Zero attach failures, zero delivery errors, zero
`SiloUnavailable`/`QueueCacheMiss` on either pod.

It survived pod restarts and image rolls **because** each roll re-creates the silo-departure window
rather than healing it.

## The fix: a durable PubSubStore

`ConfigureMeshWeaverServer` takes the store as an explicit decision:

```csharp
public static ISiloBuilder ConfigureMeshWeaverServer(
    this ISiloBuilder silo,
    Action<ISiloBuilder>? configurePubSubStore = null)
```

- **`null` ⇒ `AddMemoryGrainStorage(PubSubStore)`.** Correct *only* for a process that is a cluster
  of one by construction.
- **A delegate ⇒ it is invoked INSTEAD.** Not in addition: two providers registered under the same
  name would leave the working one decided by registration order.

`Memex.Portal.Distributed` **derives** the choice from its clustering provider rather than exposing
a second knob, so the two can never drift apart:

| `Features:Orleans:Clustering` | Silos | PubSubStore |
|---|---|---|
| `AdoNet` | many (AKS `memex-cloud`, HA self-host) | **`AddAdoNetGrainStorage` on the `orleans` Postgres database** |
| `Localhost` | one | memory |
| Bake mode (`Deployment:Mode=Bake`) | one, forced localhost | memory |
| `AzureTables` (ACA route) | many | memory — **still exposed**, see *Residual* below |
| Monolith (`MeshWeaver.Hosting.Monolith`) | one process, no Orleans streams | n/a |

The Postgres tables are the official Orleans 10
`Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql` script, created next to the membership
tables by `Memex.Database.Migration` → `OrleansClusteringSetup`. Each phase gates on **its own**
marker table (`orleansquery` for membership, `orleansstorage` for persistence) — a single combined
gate would have skipped the persistence phase forever on exactly the clusters that already have
membership tables, i.e. every existing deployment.

Cost is negligible: the pulling agent caches a stream's consumer set, so the store is read on first
use per stream and written only on subscribe/unsubscribe — a per-circuit, per-hub event, not a
per-message one.

### Two things an operator should know about

**The migration must reach the database before a new silo starts.** `AdoNetGrainStorage` loads its
four query texts from `OrleansQuery` during the silo lifecycle, so a portal that starts against a
database without the persistence tables **fails to start** — loudly, in the log's first lines, not
silently. The self-updater patches the portal and migration Deployments together
(`KubernetesDeploymentUpdater.PatchToVersionAsync`), and the roll is surge-first
(`strategy.maxUnavailable: 0`), so the worst case on the deploy that first picks this up is a new
pod in `CrashLoopBackOff` for a backoff interval while the old pods keep serving — never dropped
traffic. It self-heals the moment the migration lands; there is nothing to do but read the log if a
roll seems slow. This is the same ordering dependency AdoNet **clustering** already has.

**`orleansstorage` grows one row per stream, and shrinks again on a clean shutdown.** The row is a
`PubSubRendezvousGrain`'s state, keyed by stream — i.e. one per hub address that has ever
subscribed, and portal hubs are per circuit. A clean `UnsubscribeAsync` (which
`OrleansRoutingService.RegisterStream`'s disposal performs) leaves the grain with no consumers, and
it clears its own state. A pod that is SIGKILLed does not, so rows can linger. They are inert — a
stream nobody publishes to is never read — and prunable if the table ever warrants it. Watch it the
same way you would any other table; do not "fix" it with a shorter grain-collection age, which would
evict live subscriptions.

## 🚨 What this does NOT fix

**Durability removes the dominant loss mechanism. It does not make an undeliverable reply
observable.** A publish to a stream with zero live consumers still succeeds silently; a durable
registry only makes "zero consumers" a *rare and honest* answer instead of a routine one
manufactured by every deploy. Two residual silent-loss windows remain:

1. **The queue grain is still in RAM.** `MemoryStreamQueueGrain` holds enqueued-but-not-yet-pulled
   messages, and it dies with its silo. This is a narrow race at the moment of departure, not a
   permanent black hole — but it is silent.
2. **A genuinely absent subscriber.** A hub whose subscription attach failed, or which is torn down
   between publish and pull, still absorbs the message without a `DeliveryFailure`.

Plus one deployment-shaped residual: **the `AzureTables` / Azure Container Apps route still runs a
memory PubSubStore**, and it is a multi-silo shape. Anyone taking that route to production must pass
a durable `configurePubSubStore` (Azure Table or Blob grain storage under the name `PubSubStore`)
the same way the AdoNet path does.

### The standing invariant, and what would actually deliver it

> An undeliverable reply must surface as a `DeliveryFailure`, never as silence.

That is [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) applied to the
reply leg, and **a durable PubSubStore does not deliver it** — it removes the cause that was firing
in production, not the class. Delivering it means taking replies off streams entirely: routing a
delivery to a pod-process hub as a **directed, silo-targeted grain call**, so the reply leg gets the
same retry / NACK / failure signal the forward leg already has. Sketch of what that costs:

- a cluster-visible **directory** mapping a stream-routed address to the `SiloAddress` hosting its
  local route — written by `OrleansRoutingService.RegisterStream`, cleared on its disposal. It need
  not be durable: the owner re-registers on start, and a call to a departed silo *fails loudly*,
  which is the entire point.
- a grain whose **placement director** returns the `SiloAddress` encoded in its key
  (`IPlacementDirector` + `PlacementStrategy`, both public Orleans surface), so `RoutingGrain` can
  address one specific process.
- `RoutingGrain.BuildStreamRoute` replaced by that call, reusing the existing
  `DeliverToGrainWithRetry` → `PostFailureToSender` machinery verbatim.
- and then `OrleansStreamingReadiness` — 97 lines with exactly one consumer — becomes **deletable**.

> 🚨 **The cleanup dividend is smaller than this page used to claim.** It said the memory stream
> provider "is only kept alive by this one use". It is not: `PathCacheInvalidatorGrain`
> (`[ImplicitStreamSubscription("mesh-created"/"mesh-deleted")]`) and
> `OrleansMeshChangeFeed.BroadcastAsync` both publish on `StreamProviders.Memory`, so
> `AddMemoryStreams` and its `PubSubStore` stay whatever happens to the routing leg. The
> `subscriptionReady` attach gate (#1081) and `SubscribeWhenStreamingReadyAsync` likewise survive
> as long as any hub subscribes a stream at all. Budget the rewrite on its own merits, not on a
> deletion that is not there.

It is a real change (new placement strategy, a directory with its own lifecycle, and a two-PROCESS
test to prove it), which is why it is written down here rather than done alongside the durability
fix. Tracked in [#1742](https://github.com/Systemorph/MeshWeaver/issues/1742).

## The subscriber check — what it buys, and the measurement that put it there

Ahead of that rewrite the router now **asks whether anyone is listening** before it publishes
(`RoutingGrain.HasLiveSubscriber`). The stream provider exposes its own subscription registry —
`IStreamProvider.TryGetStreamSubscriptionManager(out …)`, since `PersistentStreamProvider` implements
`IStreamSubscriptionManagerRetriever` — so this is derived from the provider the routing turn already
captured: no DI lookup, no new grain, no new dependency. Zero subscribers ⇒ the delivery is refused
with a `DeliveryFailure{ErrorType=NotFound}` to its sender instead of being published into a
discard.

**This page previously rejected that check on hot-path cost. That rejection did not survive
measurement** (in-process cluster, 2026-08-21):

| | measured |
|---|---|
| subscriber lookup, warm | **0.010 ms** |
| the memory-stream publish it guards | **0.053 ms** |
| a hub registered on a SILO through `RegisterStream` | 1 subscription — **visible** |
| the same hub registered in a CLIENT process | 2 — **visible** |
| the root `mesh/{id}` hub (`RootMeshHubReplyStreamService`) | 1 — **visible** |
| an address nothing ever registered | 0 |
| after its registration is disposed | back to 0 within ~250 ms |

A fifth of the cost of the leg it protects is not a hot path argument, so the check is applied to
**every** stream-routed delivery rather than confined to replies — which it had to be anyway: a
forward request to an absent pod-process hub is dropped by exactly the same publish, and confining
the check to replies would have left that half silent.

Two properties are deliberate and must not be "tidied":

- **It fails OPEN.** An unavailable, faulting or slow registry returns *"assume someone is
  listening"* and the delivery is published exactly as before. A detector that cannot run must
  never become a refusal — that trade turns one silent drop into a cluster-wide outage.
- **It is check-then-act, and says so.** A subscriber can vanish between the answer and the publish,
  and `MemoryStreamQueueGrain` — still RAM — can die with its silo holding a message the check
  called deliverable. The check narrows the silent window; only the transport change below closes
  it.

The same question is asked before the **NACK** leg publishes (`RoutingGrain.PostFailure`). It cannot
be answered with a NACK — you cannot NACK a NACK — so an unreachable sender is now an **error log
naming the sender, the request and the original failure**, where before it was an env-var-gated
trace line whose own tag admitted the gap (`FAILURE_DELIVER_OK_UNCONFIRMED`) and which production
never emitted at all.

### The gate

`StreamRoutedSilenceGateTest` (test/MeshWeaver.Hosting.Orleans.Test) pins the invariant, and it does
so **in process** — see the correction below. It posts to a `client/…` address nothing has
registered (asserting first, through the same registry, that the stream really has no subscriber, so
a green cannot come from a hub that happened to exist) and requires a `DeliveryFailureException`
inside a bounded wait. Against the unfixed router it fails with the defect's own symptom: nothing
answers, and the bound converts the hang into a `TimeoutException` at 30 s. A sibling fact posts
between two REGISTERED pod-process hubs, so a check that refused everything cannot pass.

## 🚨 An in-process `TestCluster` cannot reproduce THE REGISTRY LOSS — but it does reproduce the silence

Every silo in an `Orleans.TestingHost.TestCluster` shares **one process, one heap, and therefore one
memory grain store**. "Silo A departs" never destroys the state silo B's subscription lives in, so
*that* defect is unrepresentable there.

🚨 **Read the scope of that sentence carefully — it was over-read for months, and it cost the gate
this page said was missing.** It is about the pub-sub REGISTRY dying with a silo. The *invariant* —
an undeliverable delivery surfaces as a `DeliveryFailure`, never as silence — needs no silo
departure and no second process: **a stream nobody has subscribed to is subscriber-less on any
cluster**, including one silo in one process. Same publish, same branch of
`RoutingGrain.RouteMessage`, same silence. `StreamRoutedSilenceGateTest` is that test, it is
deterministic, and it was writable before the fix rather than after. A two-silo `TestCluster` assertion for this bug was written and
**passed identically with and without the fix** — worse than no test, and it was deleted rather than
shipped as false assurance.

What *is* testable in-process is the wiring, and that is where the regression pin belongs: that
supplying a durable store yields **exactly one** provider named `PubSubStore` and that it is the
caller's, not a memory store shadowed by registration order
(`OrleansPubSubStoreConfigurationTest`). Anything about actual cross-process loss needs two
processes and a real silo departure — or measurement against a live cluster.

## How to check a live cluster

```bash
# 1. The tables exist (they are created by the migration, not by Orleans).
psql "$ORLEANS_CONNECTION_STRING" -c "\dt orleansstorage"

# 2. Subscriptions are actually being persisted — one row per PubSubRendezvousGrain.
psql "$ORLEANS_CONNECTION_STRING" \
  -c "select graintypestring, count(*) from orleansstorage group by 1"

# 3. The symptom itself: a stream-routed cross-silo read, repeated. Pre-fix this gave
#    4–6 hangs out of 10 once a roll had happened; it must be 10/10 and stay 10/10
#    ACROSS the next rolling deploy — a fresh pod always works until a silo departs.
for i in $(seq 10); do
  curl -o /dev/null -w "%{http_code} %{time_total}\n" --max-time 10 \
    "https://memex.meshweaver.cloud/api/content/AgenticEngineering/content/og.png"
done
```

A single restart "fixing" it proves nothing — that is the mitigation, not the fix. The verdict is
whether it still holds after the *next* roll.

## Related

- [Error Propagation & Wedges](/Doc/Architecture/ErrorPropagationAndWedges) — a silence is a wedge;
  every error must reach a graceful sink.
- [Message-Based Communication](/Doc/Architecture/MessageBasedCommunication) — the routing legs this
  page is about.
- [Orleans Task Scheduler](/Doc/Architecture/OrleansTaskScheduler) — why work leaves the routing
  turn at all.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — the trace tags
  (`MEMORY_STREAM_OK`, `STREAM_CALLBACK`, `FAILURE_DELIVER_OK_UNCONFIRMED`) quoted above.
- [Deployment — AKS](/Doc/Architecture/DeploymentAKS) — the rolling-deploy overlap window that
  triggers the departure.
