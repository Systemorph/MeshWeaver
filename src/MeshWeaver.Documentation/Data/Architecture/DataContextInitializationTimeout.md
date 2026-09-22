---
Name: What the DataContext Init Time-Box Bounds
Category: Architecture
Description: The 120 s DataContext initialization time-box is a wait nested three deep — data source, stream, type-source leg. For a per-node hub it is in practice one unbounded storage read. The timeout now names the leg it was waiting on instead of guessing, and a failed init errors every stream the hub holds without creating new ones.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="13" r="8"/><path d="M12 9v4l2 2"/><path d="M5 3 2 6"/><path d="m22 6-3-3"/></svg>
---

# What the DataContext Init Time-Box Bounds

Every hub that carries data runs a 120 s time-box around its `DataContext` initialization
(`DataContext.OpenInitializationGate`). When it expires the hub enters a FAILED state and answers
every later request with a terminal `DeliveryFailure`. The bound is a liveness guarantee and it
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

## What this does not change

- **The storage read keeps no bound of its own**, for the reason given above. A saturated read gate
  still costs 120 s and still fails the hub. Whether the 34-minute event in #1122 was that, a remote
  hub that never answered, or something else is **not established**: its log lines named nothing.
  The next occurrence will say.
- **A timed-out hub stays FAILED for the life of the process** even when it would be re-created on
  demand, while a *transient infrastructure fault* retires it instead
  ([Retiring an Activation](../RetiringAnActivation)). That asymmetry is what turns one stall into
  a user-visible outage lasting until a restart. It is left as is here on purpose: retiring on a
  timeout risks trading a latch for a loop — a permanently stuck dependency would re-run a 120 s
  initialization on every delivery — and that needs its own decision, taken on the attributed
  evidence this change starts collecting.
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
