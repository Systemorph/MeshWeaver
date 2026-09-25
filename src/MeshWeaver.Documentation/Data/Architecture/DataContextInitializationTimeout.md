---
Name: What the DataContext Init Time-Box Bounds
Category: Architecture
Description: The 120 s DataContext initialization time-box is a wait nested three deep — data source, stream, type-source leg. For a per-node hub it is in practice one unbounded storage read. The timeout now names the leg it was waiting on instead of guessing, a failed init errors every stream the hub holds without creating new ones, and a timed-out on-demand hub is retired so the next access re-creates it — with no timer and no re-ask.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2"/><path d="M5 3 2 6"/><path d="m22 6-3-3"/></svg>
---

# What the DataContext Init Time-Box Bounds

Every hub that carries data runs a 120 s time-box around its `DataContext` initialization
(`DataContext.OpenInitializationGate`). When it expires, a hub that routing re-creates on demand
(every per-node hub) is retired and comes back on the next access; any other hub enters a FAILED
state and answers every later request with a terminal `DeliveryFailure`. The bound is a liveness guarantee and it
stays — see [Initialization Gates](../InitializationGates). This page is about what it is actually
waiting on, because the timeout used to guess and the guess was wrong.

## The wait is nested three deep

```text
DataContext.tasks                                   ← the 120 s time-box waits on this
  └─ IDataSource.Initialized                         = Task.WhenAll(each stream's sub-hub.Started)
       └─ sync/{clientId} sub-hub BuildupAction      = SynchronizationStream.Initialize
            └─ GetInitialValueAsync                  = SelectMany over EVERY type source
                 └─ ITypeSource.Initialize(…).Take(1)     + Aggregate + FirstAsync
```

Two consequences follow directly from that shape:

- **One silent leg hangs the whole data source.** `Aggregate` emits only when the fan-out
  *completes*, so a single type source that neither emits nor completes holds every sibling that
  finished long ago.
- **The sub-hub's bound and the DataContext's bound are cause and effect, not two views of one
  stall.** `InitializeDataSources` creates the sub-hubs a few microseconds before
  `OpenInitializationGate` arms the time-box, so the two bounds expire together — the sub-hub's
  *"BuildupAction … did not complete"* first, the `DataContext` timeout milliseconds later. In the
  event consolidated in Systemorph/MeshWeaver#1122 they were **5 ms apart**.

  🚨 **That ordering used to be an accident, and now it is a construction.** Both were
  independently written as `120 s`, and equal is not an ordering: the sub-hub's clock is armed on
  its OWN action block after a scheduling hop, so under load the order inverts — the enclosing
  time-box expires first, errors the streams the data source holds, and the sub-hub's init ends as
  a recognised shutdown that records nothing. The level that knew which action hung then says
  nothing at all. A hub born INSIDE another hub's initialization — which is exactly what a `sync/`
  sub-hub is — now takes a **strictly contracting** rung
  ([The Initialization Budget Ladder](../InitializationBudgetLadder)), so the sub-hub is the level
  that reports, every time and not most of the time. (A per-node hub is hosted by the mesh root but
  born long after it started, so nothing encloses it and its own rung 1 is unchanged; its time-box
  reads 115 s rather than 120 s, one rung inside it.)

## For a per-node hub it is one storage read

A default per-node hub has exactly one data source (`AddMeshDataSource`) with one type source,
`MeshNodeTypeSource`. Its `Initialize` concatenates a durable read *ahead of* the routing-supplied
node:

```text
DurableSeed()                  IStorageAdapter.Read(hubPath) — NO wall-clock bound, on purpose
  .Concat(_ownNodeStream)       subscribed only after the read settles
```

The missing bound is deliberate and documented on `DurableSeed`: a timeout there would let a
per-node hub seed from stale routing state, which is the acked-write-loss family. So for a
per-node hub the 120 s box is, in practice, a box around **one storage read**, which queues on the
process-wide `pg-read` gate (cap 16) — see [Controlled I/O Pooling](../ControlledIoPooling) for
that gate's measured wait profile.

## "A stuck NodeType compile" cannot reach this time-box

The timeout message used to end *"— likely a stuck NodeType compile, or a data source that never
initialised"*. The first candidate is unreachable on this path:

- NodeType enrichment (`NodeTypeEnrichmentHelpers.EnrichWithNodeType`) runs in the **routing layer**,
  from `MeshNodeHubFactory.ResolveHubConfiguration`, **before** `GetHostedHub` builds the hub. The
  time-box does not exist yet.
- It is bounded on its own — a 3 s registration probe, then a further 30 s for the build to settle.
- It fails to a **compilation-error overlay**, not to a `DataContext` timeout.

So the sentence sent every reader towards a mechanism the platform does not implement. Because a
`LogIncident` fingerprint is category + message template + exception type, every cause folded into
one issue behind it: 217 occurrences over five weeks, covering at least three populations the text
could not tell apart — a pod-wide wave of four hubs timing out inside one millisecond; a mixed
`_Access` / `_Activity` / `_Issue` wave; and one user partition whose per-node hubs all went dark
for 34 minutes.

## The timeout names what it was waiting on

At the instant the box expires every layer of the wait can be inspected, so the message now reports
it instead of guessing:

```text
Hub 'sglauser/_Answers/…/Quiz' DataContext initialization did not complete within 120s.
Still waiting on 1 of 1 data source(s): 'MeshNodes (MeshDataSource)' — 1 of 1 stream(s) never
produced a first frame (sync/… stream=… owner=… partition=(none)), type-source legs still
outstanding: …/MeshNode.
```

- **Data sources** that have not settled, and the ones that have.
- **Streams** that never produced a first frame, by the sub-hub address the stream actually holds.
  That address is read off the stream, never composed from its id — the sub-hub is addressed by the
  stream's *client* id, and a composed `sync/{StreamId}` names a hub no log line carries.
- **Type-source legs** still outstanding, recorded by `TypeSourceInitializationLedger`. This is the
  answer that separates *a storage read never came back* from *a remote hub never answered a
  subscribe*.

The ledger is diagnostic only: nothing waits on it and nothing branches on it. The log **template**
is unchanged, so the existing incident keeps collecting its history while every sample line now
carries the cause.

### 🚨 For a per-node hub the leg alone did NOT separate those two — it now says which half

The claim just above — that the leg "separates a storage read never came back from a remote hub
never answered" — was not true for the case that matters most. A per-node hub has ONE leg, `…/MeshNode`,
and that leg is `MeshNodeTypeSource.Initialize`: the durable read **concatenated ahead of** the
routing-supplied own-node stream (above). The first attributed production sample after the ledger
shipped, `Collaboration` on memex-cloud (2026-09-21 17:06:24Z, Systemorph/MeshWeaver#1122), read
*"type-source legs still outstanding: 7j8ehN2m0UCo51iBcrGL2A/MeshNode"* — and could be either.

A type source that implements `IReportsInitialLoadProgress` now has its own sentence appended to its
leg, and `MeshNodeTypeSource` reports which half is outstanding:

| sentence after the leg | where to look |
|---|---|
| `[durable seed read of 'P' outstanding for 115.0s — a storage read that has not come back]` | storage: the `pg-read` gate's wait, a wedged adapter |
| `[durable seed read of 'P' found no row after 0.1s; the routing-supplied own-node stream has not emitted]` | the routing / stream-cache side — the read is done and the node never arrived |
| `[… ; the routing-supplied own-node stream emitted N time(s) and none was accepted]` | the own-node gate dropped every emission (a null, or a stale version) |
| `[durable seed read of 'P' FAULTED (…) after …]` | the read faulted and degraded to the routing leg, which then did not deliver |

Still diagnostic only: the progress is written from the load's own callbacks and read by the
failure path; nothing waits on it. Pinned by `DataContextInitTimeoutNamesTheWaitInsideTheLegTest`
(the rendering — red with the ledger printing keys only) and `MeshNodeTypeSourceInitialLoadProgressTest`
(the sentences, and the `TrackSeed`/`TrackRouting` wiring `Initialize` composes, driven with
controllable streams). A reporter that throws is printed as `[progress report FAULTED (…)]` rather
than escaping: the rendering runs inside `SettleInitializationGate`, and with that guard removed the
hub wedges instead of reaching FAILED — measured, it is the second assertion of the Data test.

## A failed init errors every stream it holds, and creates none

The failure used to be propagated with `ds.GetStreamForPartition(null).OnError(failure)`. That
accessor is get-or-**create**, and the call was wrong twice:

1. **It errored one stream of however many the source held.** Every other stream — each partition
   stream of a partitioned source — was left un-errored, so its subscribers were never told and each
   waited out an unrelated deadline instead. That is how one stall produced four log sites reporting
   four different causes: path-resolution timeouts, a broken quiz, and two 120 s bounds, none naming
   the stall.
2. **On a source with no null-partition stream it minted one.** `PartitionedHubDataSource.Initialize`
   opens only its declared partitions, so the null key misses, and a `SynchronizationStream`
   constructor always builds its sub-hub. The failure path therefore built a hub and a second
   container for a hub it had just declared FAILED — and the null-partition stream of that source
   opens one remote stream per declared partition, each starting its own 120 s initialization
   against the dependency that had just failed to answer.

The failure path now walks `IDataSource.OpenStreams` — presence only — and errors each one.

## A timed-out activation is retired, not latched

Policy [`init-timeout-retires-activation`](../PolicyNotProse). When the time-box expires on a hub
that demand routing re-creates — `WithReactivationOnDemand`, which every per-node hub declares in
`NodeTypeRebindWatcher` — the hub is **disposed**, and the **next access** re-creates it and
initializes again. It used to take the FAILED latch: `InitializationError` recorded, a rejection
handler answering every later request terminally, for the life of the process. That turned one
stall into an outage of the address until a restart — the 2026-09-15 event in #1122 took one
user's course navigation, quiz and activity tracking dark for 34 minutes, and a *transient fault*
on the same hubs was already retired rather than latched ([Retiring an Activation](../RetiringAnActivation)).

`DataContext.SettleInitializationGate` does it in the transient branch's order — `FailGate` first,
with the classification stated, then `Dispose()` — and logs at **Error**:

```text
fail: DataContext initialization TIMED OUT for {Address}. Retiring this activation instead of
      latching it FAILED — the next access re-creates it; nothing retries on its own.
      System.TimeoutException: Hub '…' DataContext initialization did not complete within 115s.
      Still waiting on …
```

🚨 That is a **new log template**. The old one (*"… Hub is now in FAILED state."*) would now be a
false sentence for these hubs, so a `LogIncident` watching this fault collects a new fingerprint
from the first roll that carries the change; the old fingerprint keeps collecting only hubs that
still latch (below).

### Why it cannot storm

The objection recorded when the latch was kept was that retiring on a timeout trades a latch for a
loop. It does not, for four reasons that each hold on their own:

| Property | What makes it true |
|---|---|
| **Self-pacing.** A permanently stuck address costs at most ONE activation per time-box, whatever the caller's rate. | The branch runs only after the whole box expired on *this* activation, and activation is single-flight per address (`HostedHubsCollection`'s creation `Lazy`; one grain activation on Orleans). A hot caller's deliveries park behind the one gate; none mints a hub. |
| **No re-ask.** Nothing re-creates the address without an access. | The parked backlog is answered **`ErrorType.Failed`**, in words no transient classifier matches — deliberately *not* the `ShuttingDown` banner the transient retirement uses. A `ShuttingDown` answer is ridden out by every `[ReaskedOnShutdown]` producer and resubscribe latch, which would re-create the address by itself: a background retry loop under another name. There is no timer. |
| **Re-access is already bounded on the read path.** | `MeshNodeStreamCache` records every non-missing-node fault in its **transient breaker**: the first `TransientGraceFailures` re-probe at once, after that re-probes back off exponentially up to `TransientMaxCooldown`, and the cached fault is replayed without opening an upstream subscribe while the window is open. A successful read or a change-feed invalidation clears it. No new breaker was needed. |
| **Only the timeout retires.** | A deterministic init *fault* fails in milliseconds, so retiring on it would re-create at the caller's rate — that is the loop the latch guards against, and a non-transient fault keeps the latch. A time-out is paced by its own box. |

### What still latches

- a hub **without** `WithReactivationOnDemand` — the root mesh hub, and a `sync/*` sub-hub owned by
  a live stream. Nothing would re-create them if they were retired, so the latch stays the honest
  answer and the old template still reports it;
- a non-transient init **fault** (above);
- a `MessageHub` **BuildupAction** that hangs (`HandleInitialize`'s own bound). That is a different
  seam from this time-box — the `DataContext` wait is armed by a BuildupAction that returns at once,
  so it never runs inside it — and it was not part of this decision.

### Pinned by

`DataContextInitTimeoutRetiresTheActivationTest` (Data.Test, `HubTestBase`, no mocks):

- `ATimedOutInit_RetiresTheActivation_AndTheNextAccessIsServedByAFreshOne` — the parked requester
  is answered `Failed` naming the box and the retirement (never the shutdown banner), the activation
  reaches `Dead`, and the next access is served by a fresh activation whose initialization ran again.
- `WithoutReactivationOnDemand_TheSameTimeOutKeepsTheFailedLatch` — the control: the marker, not
  the time-out, selects the retirement.
- `AHotCallerOnAPermanentlyStuckAddress_CostsOneActivationPerTimeBox_AndNothingRetriesOnItsOwn` —
  25 concurrent requests cost one activation, three time-boxes with no access create none, and the
  next burst costs exactly one more.
- `ALiveStreamSubscriberOnAStuckAddress_DoesNotReCreateItOnItsOwn` covers a held synchronization
  stream, the caller with its own re-ask machinery. Its `SubscribeRequest` waits behind the stuck
  gate, so the owner never registers a client subscription and its recycle announcement has nobody
  to notify. With the terminal answer, the stream re-creates nothing for three time-boxes.
  **Sensitivity control:** if the retirement answers with the transient `ShuttingDown` banner
  instead, this test fails. The stream re-asks by itself and re-creates the address, which is the
  loop the terminal answer exists to prevent.

## What this does not change

- **The storage read keeps no bound of its own**, for the reason given above. A saturated read gate
  still costs 120 s and still fails the hub. Whether the 34-minute event in #1122 was that, a remote
  hub that never answered, or something else is **not established**: its log lines named nothing.
  The next occurrence will say.
- **The sub-hub's own message is attributed too, and it is now the FIRST of the two to fire.** It
  used to read *"a BuildupAction did not complete within 120s (a hung dependency or stuck
  compile)"* — two candidates, neither measured — and it names the pending action by position and
  by the method behind the delegate (*"BuildupAction 3 of 3 (DataExtensions.StartDataSourcesAndOpenGate)
  …"*, issue #2886). Read BOTH for a stall: the sub-hub line says which action inside the sub-hub
  never signalled, the `DataContext` line says which stream and which type-source leg the enclosing
  wait was still on.

## Tests

- `DataContextInitTimeoutAttributionTest` — one type source settles and one never does; the
  recorded message must name the data source and the outstanding leg, must not name the settled
  leg, and must not offer a candidate cause.
- `DataContextInitTimeoutAttributionTest.TimedOutInit_ErrorsEveryStreamTheDataSourceHolds_NotOnlyThePrimary`
  — a second stream on the same source must receive the failure.
- `DataContextInitTimeoutPartitionedSourceTest` — a `PartitionedHubDataSource` whose remote owner
  never answers: its partition stream must receive the host's failure, and afterwards the source must
  hold exactly one stream and the host exactly one `sync` sub-hub. Calling
  `GetStreamForPartition(null)` on the failure path measured `streams=2 hostSyncHubs=2` there.
- `DataSourceOpenStreamsIsPresenceOnlyTest` — `GetStreamForPartition` creates a stream and its
  `sync` sub-hub while `OpenStreams` only reports; the property the failure path depends on.
