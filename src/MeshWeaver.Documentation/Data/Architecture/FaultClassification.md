---
Name: Fault Classification
Category: Architecture
Description: One classifier answers why work ended in an exception — cooperative cancellation, timeout, scope teardown, hub teardown, a transient fault of the transport, store or network, or a real fault. Every predicate that used to answer a slice of that question on its own is a forwarder onto it, and where the copies disagreed this page records which answer was chosen.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M6 12h12"/><path d="M10 18h4"/></svg>
---

# Fault Classification

**One question, one answer.** "Why did this end in an exception?" was answered by about twelve
cancellation/teardown predicates and eight transient-fault predicates, spread across the messaging,
data, layout, hosting and Orleans layers and two test fixtures. They were kept "in sync" by comments,
and they were not: the same fault was benign in one layer and a ticket in the next, and some of them
classified by the order in which a reactive pipeline happened to deliver its errors.

`FaultClassifier.Classify(exception, context)` in `MeshWeaver.Messaging.Contract` is now the only
implementation. Every older predicate is a forwarder onto it.

## The verdict

| `FaultKind` | Meaning |
|---|---|
| `CooperativeCancel` | A token fired because someone cancelled it. Nothing failed. Never a cancellation raised to express a timeout. |
| `Timeout` | Something did not answer in time: a `TimeoutException`, a cancellation carrying one, or a routed transport timeout flattened to text. |
| `ScopeTeardown` | A DI scope was disposed under live work — the Autofac `LifetimeScope` fault, or, with a probe, the caller's own scope. |
| `HubTeardown` | A hub announced it is going away: `HubDisposingException`, `HubDisposedBeforeResponseException`, a `ShuttingDown` delivery failure or its "Hub X is shutting down" banner. |
| `Transient` | A dependency was momentarily unreachable; `Layer` says which: `Network`, `Store`, `StoreContention`, `StoreStall`, `Transport`. |
| `Fault` | Everything else. The conservative answer. |

`IsCancellation` is reported separately from the kind. It describes the SHAPE: every branch's outermost
frame is a cancellation without a timeout cause. A cancellation raised by a teardown is still a
cancellation, so it has kind `HubTeardown` and `IsCancellation = true`.

Both `FaultKind` and `FaultLayer` are open string constants (policy
`open-vocabulary-string-constants`). An exception type that lives above the contract assembly states
its own verdict through `IClassifiedFault`, whose members have default implementations. Two types do
this: `QueryProviderStalledException` is `Transient/StoreStall`, and `SyncStreamOwnerRefusedException`
is `Transient/Transport`. A third, `MeshNodeStreamException`, is `Transient/OwnerStore` when the
OWNING hub classified the store fault before answering (`MeshNodeError.Transient`, #7066), and a
`Fault` otherwise. `OwnerStore` is a layer of its own so that only a consumer that chose to trust an
owner's verdict (a standing watch) acts on it.

## How it walks

- **The graph, never the `InnerException` line.** `AggregateException.InnerException` is branch 0,
  so a walker built on it classifies by arrival order (see `ExceptionChain`). Each branch is classified
  on its own, from the outer frame inward, and the first frame that decides wins. A driver exception
  wrapping a socket fault is therefore a store fault, not a network one. An aggregate's branches
  inherit its place: under a wrapping fault they are not outermost, so a fault around a bridged
  task's cancellation is not a cancellation. A node SHARED by two branches is judged in BOTH, from
  each branch's own position, and the result does not depend on branch order. The walk is bounded at
  256 expansions, memoized per node, and safe against cycles.
- **A teardown anywhere wins.** A hub that is going away cannot serve whatever else went wrong at the
  same time, and the next activation meets any real defect again.
- **Otherwise the worst branch wins.** The order is Fault > Timeout > Transient > CooperativeCancel. A
  real defect is never hidden by a benign sibling. A caller that needs EVERY branch to be transient
  (the store and infrastructure rules) reads `EveryBranchTypedTransientIn(layers…)`.
- **Text is weaker evidence than a type.** A verdict reached by matching a message, which is what
  survives a routed failure being flattened to a string, carries `ByMessage`. The typed store and
  infrastructure rules refuse such a verdict. A genuine fault whose text merely mentions "Failed to
  connect" is not an outage. A text match only STANDS IN: when the frame it matched wraps a typed
  cause that decides, the typed verdict replaces it.
- **A driver exception over a network cause needs it in EVERY branch.** A non-transient
  `DbException` is a store outage only when every branch below it reaches a timeout, socket or IO
  fault; a permanent defect beside a socket fault is a fault.

## What the caller knows: `TeardownContext`

- `ScopeDisposed` is a probe for "my own DI scope is gone". When it is set, any `ObjectDisposedException`
  counts as a scope teardown exactly when the probe confirms it. When it is absent, only the Autofac
  text counts. `TeardownContext.DisposedIsOwnTeardown` is for callers whose only possible source of a
  disposed-object fault is their own teardown: a sync hub's startup gate and a stream's own error path.
- `OwnAddress` limits a hub teardown to THIS hub. Another hub's disposal is then a transient fault of
  the transport.

## The forwarders

| Public name | Reads |
|---|---|
| `CancellationClassifier.IsCooperativeCancellation` | `IsCancellation` |
| `AreaErrorClassifier.IsTransientHubFailure` | every kind except `Fault`, and never when ANY branch is a stalled provider |
| `AreaErrorClassifier.IsStorageUnavailable` | every branch typed `Store` or `StoreStall` |
| `StorageFaults.IsTransientConnectFault` | every branch typed `Store` |
| `InfrastructureFault.IsTransient` | every branch typed `Store`, `StoreContention` or `Network` |
| `StandingWatchRecovery.IsTransient` (so `ReopenOnTransientFault`) | every branch typed `Store`, `StoreContention`, `StoreStall`, `OwnerStore` or `Network` |
| `MeshNodeStreamCache.IsTransientOwnerFailure` | every kind except `Fault` and `CooperativeCancel` |
| `ScopeTeardown.IsScopeTeardown` | `ScopeTeardown` with the caller's probe |
| `HubDisposingException.IsDisposedContainer` | the Autofac fact, `FaultClassifier.IsDisposedContainer` |
| `ActivityControlPlaneExtensions.IsOwnHubDisposing` | `FaultClassifier.IsHubDisposalOf` |
| `MessageHub.FailStartup`, `SynchronizationStream.OnError` | `ScopeTeardown` under `DisposedIsOwnTeardown` |
| `DataExtensions.IsOwnerTeardown`, `MessageHubGrain.IsActivationTeardownRace`, a node operation's teardown check | `IsTeardown` |
| the storage walk's teardown cancellation | `IsCancellation` |
| `RoutingGrain.IsTransientFailure` | `OrleansRoutingService.IsTransientFailure`, the one Orleans definition |
| the three test-fixture teardown filters | `FaultClassifier.IsDisposedContainer` |

## Where the copies disagreed, and what was decided

Each decision is pinned by a test that fails on the copy that was wrong (`FaultClassifierTest`,
`FaultClassifierForwardersTest`, `FaultClassifierHostingForwardersTest`,
`FaultClassifierOrleansForwardersTest`, `FailStartupTeardownClassificationTest`).

1. **A cancellation that carries a timeout is not teardown.** The storage walk treated every
   `OperationCanceledException` as mesh teardown and ended the whole walk. A timeout is one slow read,
   so it is now dropped and recorded per path like any other read fault.
2. **A timeout cause is found anywhere in the graph.** `CancellationClassifier` searched only the
   `InnerException` line, so a timeout at aggregate index 1 was called a cooperative cancellation.
3. **A teardown is found at every aggregate index.** `FailStartup` and the sync stream's error path
   looked only at branch 0.
4. **Several cancellations together are still a cancellation.** An aggregate whose branches are all
   cooperative cancellations is now one. Before, any multi-branch aggregate counted as a fault.
5. **One Orleans predicate.** The router grain lacked `SocketException` and `HttpRequestException`,
   which the routing service retried. It now forwards to the routing service.
6. **A view and the stream cache share the transport list.** The view did not know the routed Orleans
   reject ("Forwarding failed … invalid activation. Rejecting now."), the store banners or the
   network faults. The cache did not know a typed `SocketException` or a scope teardown.
7. **A view still retries a cooperative cancellation.** This is not a contradiction of the
   cancellation classifier, which asks "did anything fail". The view asks "is asking again worth it",
   and its subscription is gone.
8. **The store rules require EVERY branch.** `StorageFaults` used to read branch 0 alone, so an
   aggregate of a connect fault and a real defect was "storage unavailable".
9. **A connection-class SQLSTATE is transient infrastructure even if the provider did not flag it.**
   A driver exception wrapping a timeout, socket or IO fault is also transient infrastructure.
10. **A wrapped stalled provider is "storage unavailable".** Before, only the outermost exception was
    checked. A stall is still never retried by a view or by the connect retry.
11. **The Autofac text counts only on an `ObjectDisposedException`.** Two test fixtures accepted it on
    any exception type.
12. **An activation abandoned on a hub that is shutting down is a teardown race.** The activation
    classifier knew only the typed disposal and the Autofac text.
13. **The database's "is shutting down" is a store fault, never a hub teardown.**
14. **The sync stream logs routed timeouts and transient store faults at Information,** like a
    timeout, rather than at Warning with a stack trace.
15. **A stall is refused in any branch.** Beside a socket fault the stall combined to a layer-less
    transient, and beside a timeout it lost on severity, so the combined verdict alone retried it.
