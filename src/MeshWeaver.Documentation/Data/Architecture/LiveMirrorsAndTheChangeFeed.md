---
Name: Live Mirrors and the Change Feed — Why Every Write Ends Its Own Streams
Category: Architecture
Description: Every cross-hub write evicts, and then disposes, the mirror of the node it wrote to — sending UnsubscribeRequest to the owner and making the owner announce StreamEndedEvent for a subscriber that is already gone. That is the RCA of #2776, the two arithmetic traps that made it read as a hub teardown, and the measurement showing why the obvious fix is wrong.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h16"/><path d="M4 18h16"/><circle cx="8" cy="6" r="2" fill="currentColor"/><circle cx="16" cy="12" r="2" fill="currentColor"/><circle cx="10" cy="18" r="2" fill="currentColor"/></svg>
---

# Live Mirrors and the Change Feed

A **mirror** is the cross-hub half of `GetMeshNodeStream(path)`: when hub A reads or writes a node
owned by hub B, A opens a `SynchronizationStream` to B. That costs a `SubscribeRequest`, an
initial-state round trip, a `sync/{streamId}` sub-hub on **both** ends, and a registry entry on the
owner. Once open it is *live* — the owner fans every change out to it.

`IMeshChangeFeed` is the other invalidation signal. The persistence layer publishes one event per
`Created`/`Updated`/`Deleted`, and three components listen. This page is about what the third one
does, why it looks like a defect, and why it is load-bearing anyway.

## Every cross-hub write ends its own streams

`Workspace.EvictForPath` drops **every** cached remote stream whose owner matches the changed path —
including the mirror the writer itself is using, on the writer's own write. A write leases that
mirror only for the duration of its `Observable.Create` subscription, so:

1. write *N* leases the mirror for `P`, posts its patch; the owner commits and persistence publishes
   `Updated` for `P`;
2. `EvictForPath("P")` removes that very mirror from `_remoteStreamCache` and parks it;
3. write *N* settles and **releases its lease**;
4. the parked stream now has no declared holder, so `ReclaimIfUnheld` **disposes** it — which posts
   `UnsubscribeRequest` to the owner, kills the client `sync/{id}` hub, and makes the owner dispose
   its own twin;
5. the owner's healthy-owner announcement then posts `StreamEndedEvent` back to a subscriber whose
   `sync/` hub step 4 has just destroyed.

The next read or write resolves a *fresh* mirror: another `SubscribeRequest`, another initial-state
round trip, another pair of `sync/` hubs. **Per write.**

Measured on the monolith harness — one activity node, one live reader, six progress writes:

| | count |
|---|---|
| client-side `sync/` hubs minted | 7 |
| owner-side `sync/` hubs minted | 11 |
| change-feed mirror evictions | 7 |
| mirrors disposed mid-run (⇒ `UnsubscribeRequest` + `StreamEndedEvent`) | 5 |

Those step-5 announcements are the `Dropping StreamEndedEvent … the target stream is gone` lines
that [#2776](https://github.com/Systemorph/MeshWeaver/issues/2776) was filed on.

## The two arithmetic traps

#2776 was filed as *"the activity node's owner ends its streams ~5 s in"*, and two hypotheses — a
hub **recycle** and the **hosted-hub drain cap**, which is also 5 s — were built on that reading.
Both are wrong, and each is wrong for a reason worth keeping.

**1. The drop diagnostic is written five seconds after the event it describes.** A `StreamMessage`
whose `sync/{id}` sub-hub is not registered is not dropped on arrival: `RouteStreamMessage` holds it
for `SyncStreamOptions.SyncHubRegistrationGrace` — **5 s** — waiting for the sub-hub to appear, and
only then logs the drop. So a line at *T* describes an event at *T − 5 s*. In #2776 the streams
ended at **+0.12 s**; the `ADVANCE_WITHOUT_HANDOFF` beside it is a *different* 5 s bound
(`QueueAdvanceBound`) measured from the write's own start, which was also +0.12 s. Two unrelated
five-second bounds counting from one instant produced a coincidence that looked like a mechanism.
The drop line now states its own age.

**2. The sender rules out a recycle.** There are two emitters of `StreamEndedEvent`. The recycle
announcement (`Workspace.AnnounceRecycleToClientSubscriptions`) is deliberately posted by a
**carrier** — the parent hub or its spokesman — because a dying hub must never speak for itself. The
healthy-owner announcement (`JsonSynchronizationStream`'s disposal registration) is posted by the
owner and **refuses to fire once `RunLevel > Started` or `IsDisposing`**. #2776's two events name the
activity node itself as sender, which is provably the healthy-owner path: at that instant the owner
was `Started` and **not** disposing. No recycle, no teardown, no drain cap.

## Why the obvious fix is wrong

Three components listen to this one broadcast, and two of them already refuse to act on a healthy
subscriber:

| Listener | Its rule |
|---|---|
| `MeshNodeStreamCache.ResetFailureState` | *"A healthy live entry is left untouched: the owner's sync stream already delivers routine updates, and tearing the shared handle down on every post-commit broadcast would sever live GUI subscribers."* Evicts only a **faulted** entry. |
| `JsonSynchronizationStream`'s `Resubscribe` | Coalesced and version-gated: *"a HEALTHY subscriber receives that same write through its own subscription, so resubscribing on it is pure churn — at scale it is the storm that starved prod's hubs."* |
| `Workspace.EvictForPath` | Evicts **unconditionally**. |

So the obvious change is to give `EvictForPath` the same rule — skip a mirror that is still
`StreamLiveness.IsUsable`. It was implemented and measured, and it is **wrong**.

**The eviction is, incidentally, what keeps a cross-hub writer's BASE current with respect to writes
the OWNER makes for itself.** The per-path update queue hands a predecessor's locally-computed node
to its successor (`_pendingSelfWrites`), but that only carries *this cache's* writes forward. An
owner-side write — an activity's `messageCount`, a sealed log segment — reaches the mirror only
through the asynchronous fan-out. Evicting on the change event forces the next write to resolve a
fresh stream and therefore to diff against a freshly-fetched authoritative snapshot.

Controlled measurement, `StaticRepoImportActivityWriteCountTest.AppendCost_DoesNotGrowWithTheLengthOfTheActivity`,
same machine, `DOTNET_PROCESSOR_COUNT=4` (the CI-race repro):

| arm | outcome |
|---|---|
| liveness gate ON | **FAILED** — 2000 messages appended, **1975** recorded: one whole 25-message batch lost |
| liveness gate OFF (as shipped) | **PASSED** |

And on CI with the gate on, the same test turned 80 append calls into **99 writes** with **44**
`OWNER_NACK_REENQUEUE` and `MergeGuard: refused stale/reordered cross-hub write to 'messageCount'
(changed since the writer's base)` — refusing every other version. Both shapes are one mechanism: a
stale base.

Note what this means for the two rules quoted above. They are right *for their layer*: the shared
handle and the subscription itself must not be rebuilt on every write. `EvictForPath` sits below
them and is doing a different job than its own comment claims — it is not (only) cache hygiene, it is
the writer's freshness barrier. That is why removing it broke writes rather than merely changing
their cost.

## The real fix

Make the writer's base **version-aware** instead of buying its freshness with a full re-subscribe:
`MeshChangeEvent` already carries `Version`, so a write can wait for the mirror to reach the
announced version rather than throwing the mirror away to force a fresh snapshot. That is a change to
the write path (`MeshNodeStreamHandle` / `MeshNodeStreamCache`), not to `EvictForPath`, and it is what
would let the eviction become conditional without losing writes.

Until then the churn is the price of correctness, and `EvictForPath` carries a comment saying so.

## What #2776's other half was

Its *visible* failure was a write that reached no verdict in 31 s. That silence has a separate,
already-fixed cause — [#2882](https://github.com/Systemorph/MeshWeaver/issues/2882): the write
registered its response subject *after* posting, so a warm owner's sub-millisecond ack could land
before anything was listening for it, and the trail could only ever say `REGISTERED_AFTER_POST`
(exactly what that trace says). Fixed in `4d231170a`.

## See also

- [The MeshNode Stream Cache](/Doc/Architecture/MeshNodeStreamCache) — the layer above, and the
  write path whose base freshness this page is really about.
- [Debugging Message Flow](/Doc/Architecture/DebuggingMessageFlow) — how to read a stalled hub trace
  without mistaking a bound for a mechanism.
