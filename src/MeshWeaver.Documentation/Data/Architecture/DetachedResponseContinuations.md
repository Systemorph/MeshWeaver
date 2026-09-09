---
Name: Detached Response Continuations
Category: Architecture
Description: Why a hub.Observe(...) continuation runs on the RESPONDING hub's action block, what that cost on the mesh's one node-CRUD hub, and why the hop off the block is declared per request type instead of applied to everything — the six invariants the block underwrites, and the crash that found them.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M13 2 3 14h9l-1 8 10-12h-9l1-8z"/></svg>
---

# Detached Response Continuations

**A response subject is signalled from INSIDE the turn that handled the response. Rx runs a
continuation on whichever thread signalled it. So the whole remainder of every
`hub.Observe(x).SelectMany(…)` chain in the framework executes ON THE RESPONDING HUB'S ACTION
BLOCK, inside that turn — and the turn cannot end until the chain does.**

That is not a defect on its own. A hub processes one turn at a time by design, and for most requests
the caller's follow-up is a `Select` that finishes in microseconds. It becomes a defect the moment a
single hub is the execution point for an operation the whole mesh performs.

## 1. What it cost, measured

`portal/nodeops-{meshId}` is the mesh's **one** node-CRUD execution hub. Every create, delete, move
and copy goes through it.

A create is well-behaved: it returns `Processed()` in about a millisecond and detaches its pipeline.
But that pipeline does a partition bootstrap, whose nested response comes back to **the same hub**,
and the rest of the create — validators, save, change feed — ran inline in that response's turn.

With one create parked inside its validator, a second and entirely unrelated create sat unprocessed:

```
Queue(buffer=1,deferred=0,openGates=0,drainsInFlight=1,draining=True)
Executing(CreateNodeResponse, 24913ms)
```

Every node write in the mesh serialised behind one create's continuation. Delete, move and copy
inherit it identically — they await nested node ops the same way. That is
[#2543](https://github.com/Systemorph/MeshWeaver/issues/2543), and
[#3674](https://github.com/Systemorph/MeshWeaver/issues/3674) is the same hub reached from a release
gate: a `CreateOrUpdateNodeRequest` recorded `RECEIVED` and `ENQUEUED` on `portal/nodeops` and never
a routing verdict.

🚨 **`Executing(T, N ms)` is a LIFETIME, not occupancy.** The field is cleared in the handler
observable's `.Finally`, so it measures how long the handler's observable has been alive — not how
long the block has been held. `buffer=N` is the occupancy signal. Three sessions misread the 24.9 s
capture as "the block was held for 24.9 s"; what it says is "the handler's chain has been alive that
long", and the queued second create is what proves the block was unavailable.

## 2. The fix, and why it is an OPT-IN

The hop itself is one line — `ObserveOn` onto a pooled scheduler, before the identity restore so
`.Do(SetContext)` runs on the thread that will run the continuation.

Applying it to **every** `hub.Observe(…)` was tried first. It crashed the test host **seven times**,
and each crash named an invariant the action block had been underwriting that nobody had written
down:

| # | what the block was also providing |
|---|---|
| 1 | **An error boundary.** A throw from a `Subscribe` callback was a turn fault the pump caught, logged and answered. Off the block it is an unhandled exception on a scheduler thread — process death. |
| 2 | **A disposal fence.** A continuation that runs inside a turn cannot outlive the scope it resolves services from. Off the block it can, and does. |
| 3 | **A concurrency bound.** One turn at a time is also a limit on how many continuations run at once. `ObserveOn` with a scheduler that advertises `ISchedulerLongRunning` gives Rx `ObserveOnObserverLongRunning` — **one drain worker per subscription**, i.e. a thread per in-flight request. |
| 4 | **An impersonation scope.** `AsyncLocal` identity set by `RunAs` is still in scope on the block; a continuation that leaves it resumes without one unless the identity is re-seeded on the new thread. |
| 5 | **Ordering between a response and later messages** to the same hub. |
| 6 | **Whatever `LogonActionRunner.RunFor` relies on** — its own comment says the identity is scoped with `RunAs`, *never* `Observable.Using(access.ImpersonateAsSystem, …)`, precisely because impersonation is an AsyncLocal store/restore. |

So the hop is declared **per request type**, by
`MeshWeaver.Messaging.IDetachedResponseContinuation`, and every other request keeps the semantics it
has always had. The node-CRUD requests declare it; nothing else does.

🚨 **Do not widen this marker to "requests that feel slow".** The criterion is not latency — it is
whether the responding hub is a SHARED EXECUTION POINT whose availability other work depends on.
`portal/nodeops` is; a per-node hub answering its own reads is not.

## 3. The two fences that make the hop survivable

**A pooled scheduler that withholds `ISchedulerLongRunning`.** `PooledContinuationScheduler` wraps
`TaskPoolScheduler.Default` and deliberately does not implement it, because `ObserveOn` uses the
long-running path when it is offered. That is invariant 3 above, and it is why Rx's own
`TaskPoolScheduler` and `DefaultScheduler` cannot be used directly — both advertise it.

**A fault fence that REPORTS AND TERMINATES.** `GuardContinuationFaults` catches what an observer
callback throws, reports it, and then `OnError`s the sequence.

🚨 The first version only reported, and that turned a crash into a **hang**: the caller's sequence
stayed open forever waiting for an emission that could never come. Rx's own contract is that a
throwing observer ends the subscription, and the pump did the same — it logged and failed the
delivery, so the caller got an answer. A fence that swallows is not a fence.

## 4. Where it lives

- `src/MeshWeaver.Messaging.Contract/IDetachedResponseContinuation.cs` — the marker, and why it is
  an opt-in.
- `src/MeshWeaver.Messaging.Hub/MessageHub.cs` — `ContinueOffBlockIfDeclared`,
  `GuardContinuationFaults`, `ReportContinuationFault`.
- `src/MeshWeaver.Messaging.Hub/PooledContinuationScheduler.cs` — the withheld capability.
- `src/MeshWeaver.Mesh.Contract/CreateNodeRequest.cs` — the six node-CRUD requests that declare it.
- `test/MeshWeaver.Graph.Test/NodeCrudDoesNotOccupyTheExecutionBlockTest.cs` — the behavioural
  proof, its positive control, and `TwoCreates_AreInFlightSimultaneously`.

## 5. What this does NOT fix

A hub whose pump does not turn at all is a different failure, and it looks identical from outside.
See [Reading a Disposal Stall Verdict](/Doc/Architecture/DisposalStallVerdicts) for the three
mechanisms and the counters that tell them apart — `drainsInFlight`, `drainsAwaitingScheduler` — and
[#3593](https://github.com/Systemorph/MeshWeaver/issues/3593) for the open half.
