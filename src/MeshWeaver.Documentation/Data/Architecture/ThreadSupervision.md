---
Name: Thread Supervision
Category: Architecture
Description: Wherever user code runs innermost in an agent thread it must gracefully error; what the thread hub guarantees for itself, the one thing it cannot (its own death), and the supervisor, dispatch pool and queue page that cover that — with the 2026-09-20 wedge that motivated all three.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="12" r="2"/><circle cx="12" cy="12" r="2"/><circle cx="18" cy="12" r="2"/><path d="M4 19h16"/><path d="M4 5h16"/></svg>
---

# Thread Supervision

> **Wherever user code is executed, innermost in a thread, it must gracefully error.** A failure the
> thread's own hub cannot stamp is observed, cleaned up and relaunched under a bound, and what a
> relaunch cannot fix is filed into bug triage. The relaunch/dispatch side is bounded by a
> configurable pool cap, and its queue is a page rather than a log.
> — policy [`thread-graceful-error`](../PolicyNotProse)

An agent thread is user code running innermost: a model that may refuse, a harness process that
may die, a tool that may throw, a mesh that may restart underneath it. The rule has three layers,
and this page says which layer owns which death. The implementation lives in `MeshWeaver.Plugins`
(`src/MeshWeaver.AI`, the AI engine); this page is the contract.

## Layer 1 — the round ends STAMPED, whatever failed

Every failure path of a round terminates with a state written ON THE NODE — never an endless
`Executing`, never a `Submitted` message nobody drains, never a claim that re-fires forever:

| What failed | Where it lands |
|---|---|
| The model faults mid-stream, or the provider refuses (429, 401, 5xx) | the response cell `Error` with the classified condition (and the text streamed so far kept), the thread `Idle`, `summary` = `Error: …` |
| A CLI harness subprocess dies, or is not logged in | the cell `Error` with the process's own verdict and stderr, or the `/login` affordance, in the viewer's language |
| No usable model, no credit, the history could not be loaded | the round never starts a provider call; the cell `Error` with the reason, thread `Idle` |
| Agent initialisation stalls | the init-stall guard flips the thread `Idle` and stamps the cell |
| A tool throws an error the model can act on (a missing argument) | returned to the model for self-correction — the round continues |
| A tool throws anything else | the round faults, as above, naming the tool |
| A whitespace-only pending message | dropped from the queue (an own write) — left pending it re-claimed forever, and hid a real message queued before it |
| The round settles, however it settles | `lastActivityAt` is stamped, so "quiet since" is measurable even for a round that never streamed a token |

Pinned in `MeshWeaver.AI.Test`: `RoundFaultTerminalStateTest`, `HarnessProcessFailureRoundTest`,
`ProviderRefusalRoundTest`, `ProviderStreamStallRoundTest`, `RoundCompletionHonestyTest`,
`RoundNeverParksTest`. The hub also guards ITSELF while it lives: a round whose node makes no
progress for 90 s is forced `Idle` by the activation's watchdog, and on every activation the hub
reads its own node and drives any non-terminal state to a valid one
([Thread Operations → Resurrection](/Doc/Architecture/ThreadOperations)).

## Layer 2 — the one death the hub cannot cover: its own

A per-node hub can only act while it exists. Two states are therefore invisible to layer 1 by
construction, and both were measured on the control instance on 2026-09-20:

- **Parked.** A thread node exists with its first message seeded in `pendingUserMessages`, and
  nothing ever routed to its address. A per-node hub is instantiated by the FIRST message that
  reaches it; the `CreateNodeRequest` that makes a thread is handled by the OWNER namespace's hub,
  so creation alone instantiates nothing. Every caller of `hub.StartThread` that subscribed to the
  new thread afterwards activated it by accident; the one that only recorded the path (the control
  instance's triage intake) left 100+ threads at version 1, their task `Submitted` and never
  ingested, one per red CI run since 2026-09-19 — no error, no status, nothing to grep. A single
  `get` on one of them ran its round within a minute. `StartThread` now owns the activation
  (`HubThreadExtensions.WakeThreadHub`: one subscription to the created thread's own stream, off the
  router, released on the first emission), so the residue is the creator that crashes between the
  create and the wake, and the hand-assembled node. 🚨 The caller's context rides along as the
  **subscriber context** only — the identity the cache's per-subscriber `Read` check evaluates, and
  the reason that check sees a real identity rather than a null one that would fail the read closed.
  The shared upstream keeps `MeshNodeCacheIdentity`; a wake that stamped a user onto it would be the
  cache-wide RLS failure, not a fix.
- **Stale.** A node that says `Executing` with no hub behind it: the pod restarted mid-round. The
  90 s watchdog was on the hub that died. Nothing re-reads the node until something reaches its
  address.

## Layer 3 — the supervisor

`ThreadSupervisor` (a mesh-owned singleton hosted service, registered by `AddAI`, one per mesh —
never a static) sweeps every `sweepIntervalSeconds` (default 60), as system, the newest
`lookbackLimit` threads of every partition it can declare (`partitions:all`) plus the `Admin`
partition that flag omits, and classifies each by its PERSISTED state alone
(`ThreadSupervisor.Classify`, pure, pinned by `ThreadSupervisorClassifyTest` with a case on each
side of every bound):

| The node says | Verdict | Bound |
|---|---|---|
| Idle/Cancelled, a message pending, `queuedAt` stamped | **Queued** — waiting its turn, by design | — |
| Idle/Cancelled, a message pending, quiet longer than the bound | **Parked** | `parkedAfterSeconds` (120) off the node's last modification, `lastActivityAt`, and the supervisor's own last touch |
| Executing/StartingExecution, the node unchanged longer than the bound | **Stale** | `staleAfterSeconds` (900), or the thread's own `heartbeatTimeout` when longer — never shorter |
| anything else, and every `Done` thread | Healthy | — |

A thread whose active cell carries an unfinished delegation call is left to the heartbeat ticker,
whatever the clock says: a parent waiting on a child is silent by design.

**Every STATE CHANGE is a write routed to the thread's OWNER, and that is the whole mechanism.** A
write reaching a cold address activates the hub; the hub's init installs the submission watcher (a
parked thread drains) and runs the activation-time recovery (a stale thread resumes its `Streaming`
cell or settles a finished one). The supervisor adds no second recovery — it makes the existing one
run. A recycle is **not** one of those writes: it is a separate dispose-only lifecycle operation
(`RecycleNode` posts a `DisposeRequest`, a no-op at the router on a cold address), and it precedes
the write wherever an activation may still be alive and wedged — so the write lands on a hub that
has to re-read its node rather than on one that is stuck:

1. **Parked, first touch** — a write stamping `supervisorLastActionAt` and `supervisorNote`. This
   is the wake nobody gave it; no retry is counted. Row `Woken`.
2. **Parked again, or Stale, while `supervisorRetries` < `maxRetries` (2)** — the activation is
   RECYCLED first (`RecycleNode`, dispose-only, a no-op on a cold address: a wedged hub is torn down
   rather than written to), then the write counts the retry and records what was observed. Row
   `Relaunched`. A relaunch goes through the same dispatch pool as every other round.
3. **At the bound** — the thread is SETTLED: the active cell goes `Error` with the diagnosis
   FIRST (an `Error` cell makes the activation's recovery settle rather than resume the round the
   supervisor just gave up on), then the thread is reset `Idle` with `summary` = `Error: …`, and
   FILED into bug triage as a `Feedback/Feedback` submission, status `New`, naming the thread, the
   agent, the model, the last error, the retries, and what was NOT established (the supervisor sees
   only the node, never the process that owed it a round). Row `Failed`. On a portal without the
   Feedback plugin the filing fails and the row SAYS so — never silently.

The `supervisor*` fields on the thread are the `RequestedX`-shaped control plane
([Request via stream Update](/Doc/Architecture/RequestViaStreamUpdate)): written by the supervisor
through `GetMeshNodeStream(path).Update`, read by the next sweep and by the thread's page.

**What it is not.** Not a watchdog that resubscribes around a bug — a live hub force-idles its own
silent round in 90 s and drains its own queue the moment it activates, so what reaches a sweep is a
hub that does not exist or cannot act. Not a retry loop — two relaunches, then triage. Not a
second dispatcher — it never claims a round itself.

## The dispatch pool and the queue page

`ThreadDispatchPool` (a mesh-owned singleton, `IIoPool`-style state: an interlocked counter over
instance dictionaries, never a `SemaphoreSlim`, never a static) bounds rounds in flight per
process. The submission watcher asks it for a slot BEFORE it claims a round; a refused thread waits
`Idle` with its message pending and `queuedAt` stamped on the node, and re-asks on every release
(one re-read of its own node per release, throttled). A slot is released when the watcher observes
the thread leave its executing states, and when the hub is disposed. A round RESUMED on activation
takes a slot without asking (it never waits — it is already the thread's and half-written).

The cap is `maxConcurrentAgents` on the status node `Admin/Threads` (default 50), read LIVE off the
node stream by the supervisor and forwarded to the pool — raise it and the waiting watchers are
told at once; lower it and running rounds finish. The pool is per process: on a multi-silo mesh
every silo bounds its own activations.

**`Admin/Threads` is the queue page** (NodeType `ThreadSupervisor`, area `Queue`, a platform-admin
page — `hub.IsGlobalAdmin()` — because it names threads of every partition): a
`Controls.DataGrid` of one row per thread the pool is running or holding and one per thread the
supervisor acted on, newest first, capped at 200 — thread, state (Running · Queued · Woken ·
Relaunched · Failed), agent, model, started, last activity, retries, and the last failure verbatim
— plus the counts, the cap, when the last sweep ran and how many threads it examined, and the last
sweep's fault if it had one. The pool's census is written on every change (coalesced to one write
per two seconds); the supervisor's rows are written per sweep. Pinned end to end by
`ThreadSupervisorMeshTest` against a Monolith mesh: a parked thread woken by one sweep and run, a
fresh one left alone (the control), a stale thread relaunched and settled, an exhausted one settled
with the diagnosis and its filing said to have failed, and with a cap of one the second thread
queued on the node until the first settled.

## Reading it

- A thread at version 1 with a `Submitted` message and no `status`, minutes old: parked. Before
  the wake it needed a `get`; now it needs nothing, and the next sweep lists it `Woken` if the
  creator died first.
- `supervisorRetries: 2` and `summary` starting `Error:`: given up on; `supervisorFeedbackPath` is
  the triage item, or null with the row saying the filing failed.
- Many `Queued` rows and `Running` at the cap: the cap is the bound, not a fault — raise
  `maxConcurrentAgents` on `Admin/Threads` if the host has the headroom.
- `lastSweepError` set: the sweep faulted (usually a query against a partition store); the clock
  keeps ticking and the next sweep says whether it cleared.

## See also

- [Thread Operations](/Doc/Architecture/ThreadOperations) — the submission surface and the wake.
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — the `RequestedX` pattern.
- [Stale State Until a Recycle](/Doc/Architecture/StaleStateUntilRecycle) — what a `DisposeRequest` does and does not do.
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — why the supervisor writes as system.
