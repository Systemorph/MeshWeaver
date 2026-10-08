---
Name: Change-Feed Gaps — When the Cross-Process Feed Loses Notifications
Category: Architecture
Description: >-
  PostgreSQL LISTEN/NOTIFY never replays, so a listener that reconnects has lost every commit made in
  between — and every cache retracted only by the change feed stayed stale with no signal. The
  ChangeFeedGap contract: the backend DECLARES the hole, and each dependent re-reads its
  authoritative state (Plugins#3000).
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12h6"/><path d="M15 12h6"/><path d="M9 8v8"/><path d="M15 8v8"/></svg>
---

# Change-Feed Gaps

**A cross-process change feed is not a log.** PostgreSQL's `LISTEN/NOTIFY` delivers a notification
only to sessions listening at the instant it is sent and never replays one. When the listener's
connection drops, `PostgreSqlChangeListener` (MeshWeaver.Plugins) logs *"LISTEN connection error,
reconnecting in 5s"* and re-issues `LISTEN` on a fresh connection. Every commit another replica made
in that window is gone for good.

Before the gap contract, nothing said so. Every piece of process-local state that is retracted
**only** by the feed kept its pre-gap answer for the life of the process, with no error and no log
line beyond the reconnect:

| State | What a missed notification left behind |
|---|---|
| `PathResolutionService` cache | a missed `Deleted`/`Created` kept routing to a node that was gone (or the old shape of a moved one) |
| `MeshNodeStreamCache` storm-breaker windows | a negative window opened before the gap stayed open for its full back-off after the node existed |
| live / synced queries (`ObserveQuery`, `GetQuery`) | a node created or deleted in the window never entered or left the result set |
| a per-node hub's own-node mirror (`MeshDataSource`) | the owner kept serving — and building its next write on — a snapshot another process had replaced |
| `NodeTypeRebindWatcher` | a retype committed in the window never recycled the hub onto its new type |

Core #6045, #6046 and #6047 were symptoms of exactly this; #6109 removed their dependence on the
notification, but not the hole itself.

## The contract: the producer declares the hole

`ChangeFeedGap(Source, LostAt, ResumedAt, Reason)` (`MeshWeaver.Mesh.Contract`) says: *between these
instants this feed could not deliver, and nothing committed in that window will ever arrive.* It
travels on two default-implemented members, so no existing implementer has to change:

- `IStorageAdapter.ChangeFeedGaps` — the backend's declarations. The default never emits (an
  in-memory or single-process store has no cross-process feed to lose). `PersistenceService` merges
  every provider's; every decorator that forwards `Changes` must forward this too
  (`StorageAdapterDecoratorsForwardBatchReadGuard.EveryDecoratorThatForwardsTheFeed_ForwardsItsGaps`).
- `IMeshInvalidationFeed.Gaps` — the same declarations, relayed by `InProcessMeshChangeFeed` to the
  process-local caches, with one Warning per gap so a replica that may have served stale state says so.

The producer is the only party that KNOWS a gap happened. The listener emits one after each
successful re-`LISTEN` that follows a lost connection — never for the first registration, which has
its own readiness signal (`Listening`) — and only once the new subscription exists, so every commit
after the declaration is notified normally.

## What a dependent does: re-read, never poll

A gap names no path, so a dependent cannot know WHAT changed — only that anything may have. Each one
gives the answer it already gives to a notification it cannot classify, applied to everything it holds:

- **Path resolution** clears its cache and in-flight fill claims; the next resolution re-asks the store.
- **The stream cache** runs its per-path failure reset for every path holding failure state (negative
  entry, transient streak, faulted entry). Healthy live entries are untouched: they are fed by the
  owner's sync stream, not by the feed.
- **Live queries** (`StorageAdapterMeshQueryProvider`, and in MeshWeaver.Plugins `PostgreSqlMeshQuery`
  and `PostgreSqlPartitionedMeshQuery`) take `ChangeFeedGap.ToRequeryTrigger()` past their relevance
  filters. The re-query is the only source of rows (#1250) and is diffed against the current set, so
  a gap that changed nothing here emits nothing.
- **A per-node hub** fires its coalesced own-node re-read and adopts forward-only (`AdoptPersisted`).
- **The rebind watcher** reads the node once and runs the answer through its usual predicate.

🚨 **Never a timer.** A poller that re-reads "in case a gap happened" is the band-aid this contract
exists to avoid: the cost is paid on every tick by every replica, and it still answers late. The gap
is an event, and the work happens once per gap.

The cost of a gap is one coalesced re-read per live dependent — every live query, every live per-node
hub — through the same bounded read pools as any other read. Gaps are rare (a dropped database
connection); this is the price of being correct after one.

## The other half: a dead connection must ERROR

A gap can only be declared for a loss the listener notices. An idle `LISTEN` session behind a NAT or
firewall (AKS → Azure Postgres) is dropped silently, and without keepalives `WaitAsync` waits for ever
on a dead socket — a replica that is deaf with no log line at all. The partitioned listener's data
source therefore sets `KeepAlive` and `TcpKeepAlive`
(`PostgreSqlPartitionStorageProvider.CreateChangeListenerDataSource`), which turns a dead socket into
an exception, then a reconnect, then a declared gap.

## How it is tested

`test/MeshWeaver.FaultInjection.Test/ChangeFeedGapTests.cs`: two silos over one store, joined by the
`CrossProcessChangeRelay` (the LISTEN/NOTIFY model). Silo 1 holds a live children query; the relay is
switched to `Drop`; silo 0 commits a child straight to the store; the drop's arrival proves the
notification was lost; the relay comes back.

- With the gap declared, silo 1's query converges on the child.
- **Negative control** — `Drop(announceGap: false)`, the pre-fix listener: the query never shows it.

Mutating the fix away (the `ChangeFeedGaps` merge in the live-query pipeline) turns the first case
red, so the case measures the gap signal and nothing else.

`test/MeshWeaver.FaultInjection.Test/ChangeFeedGapConsumerTests.cs` pins the other four consumers on
the same harness and the same two arms (gap declared, and `announceGap: false`):

| Consumer | Stale state before the gap | Commit lost during the gap | Fixed arm converges on |
|---|---|---|---|
| Path resolution | silo 1 resolves `ns/Item` (cached) | silo 0 deletes it | a resolution that no longer routes to it |
| Stream cache | an open storm-breaker window on the path (a grown 256 s history, so it cannot expire mid-case) | silo 0 creates the node | window closed, and a read answers the node |
| Own-node mirror | the node's hub is live on one silo | the OTHER silo writes the node | the hub's mirror carries the write |
| Rebind watcher | the node's hub bound type `Markdown` | the OTHER silo retypes it | the hub disposes (recycled) |

For the two per-node cases the commit goes through the store of the silo that does **not** host the
hub. A commit through the host's own store reaches the hub as a local notification that the relay
never carries, and the control arm would converge without any gap.

Each consumer was mutated in turn (its gap subscription removed: `PathResolutionService`,
`MeshNodeStreamCache`, `MeshDataSource`, `NodeTypeRebindWatcher`). Each mutation turned exactly its
own case red and left the other three green.

## See also

- [Live Mirrors and the Change Feed](../LiveMirrorsAndTheChangeFeed)
- [Change-Feed Isolation](../ChangeFeedIsolation)
- [Fault Injection Harness](../FaultInjectionHarness)
