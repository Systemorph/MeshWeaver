---
Name: Live Mirrors and the Change Feed — Why Every Write Ended Its Own Streams
Category: Architecture
Description: >-
  Until #1174 every cross-hub write evicted, and then disposed, the mirror of the node it wrote to —
  one fresh subscription, initial-state round trip and pair of sync/ hubs per write. The RCA of #2776,
  the measurement showing why the obvious liveness gate loses writes, and the fix that landed: a
  write base held to the version the change feed announced.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6h16"/><path d="M4 12h16"/><path d="M4 18h16"/><circle cx="8" cy="6" r="2" fill="currentColor"/><circle cx="16" cy="12" r="2" fill="currentColor"/><circle cx="10" cy="18" r="2" fill="currentColor"/></svg>
---

# Live Mirrors and the Change Feed

A **mirror** is the cross-hub half of `GetMeshNodeStream(path)`: when hub A reads or writes a node
owned by hub B, A opens a `SynchronizationStream` to B. That costs a `SubscribeRequest`, an
initial-state round trip, a `sync/{streamId}` sub-hub on **both** ends, and a registry entry on the
owner. Once open it is *live* — the owner fans every change out to it.

`IMeshChangeFeed` is the other invalidation signal. The persistence layer publishes one event per
`Created`/`Updated`/`Deleted`, and three components listen. This page is about what the third one
did, why it looked like a defect, why it was load-bearing anyway — and how
[#1174](https://github.com/Systemorph/MeshWeaver/issues/1174) replaced it.

🚨 **FIXED 2026-09-15 — the fix is the last section of this page.** A versioned `Updated` no longer evicts: the workspace records the version it
announced and keeps the mirror, and the next write's base must reach that version. The sections
between here and there describe the behaviour before the fix and are kept because the measurement
that rules out the liveness gate is still the reason the fix has the shape it has.

## Every cross-hub write ended its own streams (before #1174)

`Workspace.EvictForPath` dropped **every** cached remote stream whose owner matches the changed path —
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
| `Workspace.EvictForPath` (before #1174) | Evicts **unconditionally**. |

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

## The fix: a write base held to the announced version (#1174)

`MeshChangeEvent` already carries `Version`, so a write can wait for the mirror to reach the
announced version instead of throwing the mirror away to force a fresh snapshot. That is what
landed, in two halves.

**The workspace** (`Workspace.OnOwnerNodeChanged`) answers a commit on a mirrored owner in one of
two ways, decided by what the commit announced, never by how the mirror looks:

| Event | Answer |
|---|---|
| `Updated` with a version — one per write, the hot path | **Keep** the mirror and raise its floor to that version — a field of the mirror instance itself (`IAnnouncedVersionFloor`) |
| `Deleted` (announces 0) | **Evict**, as before — the node is gone and a recreate restarts its version counter |
| `Created` on a path already mirrored | **Evict** — a recreate is a new incarnation, whose versions say nothing about the old mirror |
| `Updated` with version 0 (the operator recycle broadcast) | **Evict** — nothing a mirror could be held to |
| any shape without a version or a kind | **Evict** — the pre-#1174 behaviour, the only safe one |

🚨 **The floor is a field of the mirror, not an entry beside the cache.** The handler raises it on
each instance it finds in the cache for the owner, and the write reads it off the very stream it
acquired (`Workspace.AnnouncedVersionFor(stream)`). So a floor cannot outlive its mirror and cannot
reach the next one: whatever removes a mirror — eviction, the mesh-node cache's idle release, a
fault, a recycle — removes its floor with it, and a new mirror starts with none, which is right
because it hydrates from the owner's current state, at or past every version announced before it.
The first cut kept a per-owner map instead, and review found the window that shape cannot close: a
removal landing between "the cache still mirrors this owner" and "record the floor" left a floor with
no mirror behind it, which the next mirror inherited (`AnnouncedFloorLifetimeTest` reproduces it
deterministically through a seam in exactly that window, and the same inheritance through the
faulted-stream path — both red against the map, both green now).

**The write path** (`MeshNodeStreamHandle.RebaseSource`, the same seam the #1910 conflict rebase
uses) reads the mark once per attempt and requires the base to satisfy
`Version > refused && Version >= announced`. A mirror that already carries the version passes at
once; one that is a moment behind is waited for (the owner's own fan-out delivers the version — no
round trip); the wait is bounded by `ConflictRebaseBound`, and it never parks — at the bound the
write proceeds on the state it has, exactly as before. With no mark (a cold path, or a feed that
never reaches the workspace) the read is byte-for-byte the old `mirror.Take(1)`.

🚨 **The fallback evicts only on PROOF.** When the bound elapses, the writer evicts the mirror — the
old freshness barrier, paid once — only if the mirror carried a node *below* the floor and nothing at
or above it. A mirror that carried nothing at all is still *hydrating*, not behind; evicting it would
throw away the one subscription about to deliver and make the next write hydrate yet another, which
is the churn this fix removes, re-entered at exactly the moment the owner is slowest.

🚨 **This is the opposite of the liveness gate above.** Liveness asks how the mirror *looks* and lets
a healthy-but-behind one through, which is what lost the 25-message batch. The version floor asks the
mirror to *prove* it carries the announced commit — and the announcement is the same event that used
to trigger the eviction, so every commit the old barrier covered is still covered.

### Measured

| | before | after |
|---|---|---|
| Five versioned commits on one owner, leased acquire per commit (`HotPathMirrorChurnTest`) | 6 distinct mirrors | 1 mirror, 0 `SubscribeRequest`, 0 `UnsubscribeRequest` |
| Six sequential cross-hub writes to one node on a real mesh (`HotPathWritesKeepTheirMirrorTest`) | 5 mirrors opened after the first write | 0 opened, 0 `MIRROR_BEHIND`, every write landed |
| `StaticRepoImportActivityWriteCountTest` at `DOTNET_PROCESSOR_COUNT=4` (the liveness-gate repro) | — | passes: 2000 messages in 80 appends → version 84 (80 + 4 sealed segments), no refused re-writes |
| A removal between locating a mirror and recording its floor, then a new mirror; and a faulted mirror rebuilt (`AnnouncedFloorLifetimeTest`) | per-owner map: the new mirror inherited floor 7, the rebuilt one 5 | floor on the instance: both start at 0 |

The real-mesh arm also asserts that the floor was *in force* (the workspace's "Mirror … kept" line
fired for the later commits) and that the owner's fanned-out version met the announced one every time
(no `MIRROR_BEHIND`) — the second is the property the whole design rests on, and only a real owner
can show it. Negative controls: with the source change set aside both measurements go red as shown
in the "before" column, and with either refinement removed (`Created` evicting; proof-only
eviction) its own arm goes red.

### What it means for #1174 and #2776

Each of those per-write hydrations had to complete inside the writer's 30 s
[base-state bound](../BaseStateTimeoutCensus) while the mirror it replaced was being torn down on the
same owner address, and a node like `{user}/_UserActivity/{user}` — written on every cold page load,
at version 6498 in production — paid that thousands of times. That is the only mechanism in the
code that predicts #1174's concentration on hot paths. The census from #4342 is what will confirm it
on a rolled replica: an occurrence after this fix would have to come from a mirror that was NOT
rebuilt per write. The `Dropping StreamEndedEvent … the target stream is gone` lines of #2776 were the
same churn seen from the owner, and stop with it for every write-driven eviction.

The #3432 retention ([The Evicted-Stream Retention](../EvictedStreamRetention)) is untouched as a
mechanism, but it is now reached only by the events in the table that still evict — rare ones —
rather than once per write.

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
