---
NodeType: Markdown
Name: "A release refused at the source — the farewell that ends an owner-side hub was dropped by the hub posting it"
Abstract: "An UnsubscribeRequest is the only thing that ever ends an owner-side per-subscriber stream and its sync/{id} hub. It is posted from the SUBSCRIBING hub by a disposable registered on the client-side sync/{id} hub, so on the hub-teardown route — a circuit close, a DisposeRequest, a recycle — it runs while the subscribing hub is in DisposeHostedHubs BY CONSTRUCTION, and the teardown post guard refused it at the only door it has. The owner was never told: one RunLevel=Started hub per subscription, each holding its own Autofac scope and TypeRegistry, for the life of the process. Why the guard is right for events and wrong for a release, the surviving ancestor that carries it, and the teardown regression controls."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#2f6f5e'/><path d='M4 12h9' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round'/><path d='M10 9l3 3-3 3' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/><path d='M16.5 6v12' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round'/><circle cx='20' cy='12' r='1.6' fill='white'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Disposal"
  - "Lifecycle"
  - "Messaging"
---

# A release refused at the source

> **The rule.** A hub past `DisposeHostedHubs` refuses every post but its own `ShutdownRequest` /
> `DisposeRequest` and a correlated reply. That refusal is right for an **event** — nobody awaits
> one, and the receiver recovers from the loss through a fresh snapshot, a re-subscribe, a change
> feed or a heartbeat lapse. It is wrong for a **release**, because a release is the only thing that
> ever frees state the RECEIVER holds: there is no requester to NACK, no retry to trigger and no
> later probe that discovers the loss. So "nobody is waiting" is the reason a release must go out,
> not a reason it may be dropped.

This is the third asymmetry at the same gate. The first two are in
[Refused replies during teardown](../RefusedRepliesDuringTeardown): a refusal is advice to the
*sender*, which is the wrong party for a reply. This page is the one that leaks memory instead of
stranding a caller — and it leaks it **silently, in another hub, with nothing to grep.**

Filed as [#3432](https://github.com/Systemorph/MeshWeaver/issues/3432), whose title stood for two
weeks as *population MEASURED, cause NOT established*.

---

## 1. What holds a `sync/` hub — settled before this page

A cross-hub subscription builds **two** `SynchronizationStream`s and therefore two `sync/{id}` hubs:
the client's, hosted by the subscribing hub, and the owner's twin, hosted by the owner
([Sync hub population](../SyncHubPopulation) shows the arithmetic — 6 925 `Started` + 1 495 `Dead`
sync hubs against 8 461 streams, 0.5 % apart, so there is no separate hub population to explain).
Each retains roughly 390 KB: its own Autofac `ILifetimeScope` and its own `TypeRegistry`.

Three earlier causes have landed, each removing a way the population GROWS:

| | what it was | where |
|---|---|---|
| #3427 | a disposed stream kept a strong reference to a dead hub; and the hub dying underneath an undisposed stream was never noticed | [Stream liveness and the hub reference](../StreamLivenessAndTheHubReference) |
| #3952 | the read path minted a stream, and a hub, per read | [The read path minted a hub per read](../ReadPathStreamMinting) |
| #4163 | a grain subscribed to its own cache entry, so the entry could never be released | [A hub that pins its own cache entry](../AHubThatPinsItsOwnCacheEntry) |
| #4505 | a `WorkspaceReference` whose record equality compared a collection by reference could never hit the stream cache | [A reference that cannot be a key](../AReferenceThatCannotBeAKey) |

This page is about the other direction: not how one is created, but **why the one thing that ends it
never arrives.**

## 2. The only thing that ends an owner-side stream

`Workspace` says it in as many words: *"only an `UnsubscribeRequest` disposes a server-side
stream"*. `JsonSynchronizationStream.CreateExternalClient` registers that release, and since #3986 it
registers it on the **stream's hub** rather than on the stream, so it runs from the hub's `ShutDown`
phase — strictly after **Quiescing**, whose whole job is draining the response callbacks an accepted
user action holds. That ordering is deliberate and is pinned by
[Refusing a lost user action](../RefusingALostUserAction).

```csharp
// src/MeshWeaver.Data/Serialization/JsonSynchronizationStream.cs
var release = new AnonymousDisposable(
    () => hub.Post(new UnsubscribeRequest(reduced.StreamId), o => o.WithTarget(owner)));
if (reducedHub is not null)
    reducedHub.RegisterForDisposal(release);
else
    reduced.RegisterForDisposal(release);
```

🚨 **Read the two hubs in that snippet.** The release is REGISTERED on `reducedHub` — the client-side
`sync/{id}` hub — and POSTED through `hub`, the subscribing hub that hosts it. Those are different
hubs at different run levels, and on one of the two teardown routes the difference is the defect.

## 3. Two teardown routes, and only one of them has an open door

| route | what starts it | subscribing hub's run level when the release runs |
|---|---|---|
| **stream dispose** | `stream.Dispose()` — an idle release, `DetachUpstreams`, an explicit release | `Started` — the door is open, the farewell leaves |
| **hub teardown** | a Blazor circuit ending, a `DisposeRequest`, a recycle | **`DisposeHostedHubs` — by construction** |

The second row is not a race. `DisposeHostedHubs` is *the phase that disposes the hosted hubs*, so
the child's `ShutDown` — which runs the release — can only ever execute while its parent is in it.
And at that run level `MessageService.PostImplGeneric`'s teardown guard refuses the post:

```csharp
if (hub.RunLevel >= MessageHubRunLevel.DisposeHostedHubs
    && message is not ShutdownRequest and not DisposeRequest)
{
    // … a correlated reply is handed to the parent; everything else:
    return ((IMessageDelivery)delivery).Failed("Hub is shutting down", ErrorType.ShuttingDown);
}
```

An `UnsubscribeRequest` is neither of the two exempt types and carries no `RequestId`, so it takes
the last line. Nothing downstream ever sees it — it is refused before the post pipeline and before
`ScheduleNotify`, so there is no intake trace, no NACK and no log line above `Debug`. The owner keeps
its per-subscriber stream and its `sync/{id}` hub at `RunLevel=Started`, and only an
unserved-subscriber eviction can ever reach it — which needs a later change on that node, so for a
node nobody writes again it is never.

🚨 **`CarriesAcceptedWorkOfAHostedHub` does not cover it, and correctly so.** That exemption (#3986)
carries a hosted hub's accepted work OUT through the disposing parent, but it is scoped to *a request
its originating hub holds a live response callback for* — the receipt the child's Quiescing drain is
waiting on. A release is fire-and-forget: nothing is waiting, which is exactly why the existing
clause cannot see it.

## 4. The category the guard was missing

The guard's own reasoning for refusing fire-and-forget is sound and must stay — forwarding every
event out of a disposing hub is the storm shape. What it did not distinguish is **who recovers from
the loss**:

| | a lost EVENT | a lost REPLY | a lost RELEASE |
|---|---|---|---|
| Who is waiting | nobody | the requester | nobody |
| Who notices | nobody | the requester, at its timeout | **nobody, ever** |
| How it is recovered | the next snapshot / re-subscribe / change feed | a retry against the fresh activation | **it is not** |
| What the loss costs | one stale frame | one burned budget | **the receiver's memory, permanently** |

So the third column gets the marker interface `IReleasesRemoteState`
(`src/MeshWeaver.Messaging.Contract/IReleasesRemoteState.cs`), and `UnsubscribeRequest` implements
it. Implementing it is a narrow claim — *no other mechanism in the system ever reclaims what this
message releases* — and deliberately NOT a way to make an ordinary event survive a teardown.

## 5. The carrier is the first ancestor whose post gate is still open

Each teardown post guard hands the release to its construction-captured `MessageService.ParentHub`.
A parent already at `DisposeHostedHubs` applies the same rule, until an ancestor whose post gate is
still open carries it. No service is resolved and no hub is activated. The original options are retained, with the same sender host qualifiers that normal upward routing
would add, and the actual delivery verdict propagates back to the caller. At the root, the captured
parent is null and the release reports the ordinary shutdown refusal. Do not walk
`Configuration.ParentHub` here: it can resolve through a disposed scope, and on a root it can
resolve the root itself, so a whole-tree teardown would loop forever.

A single parent hop is insufficient. Consider a live root hosting an owner and a separate subtree:

```text
root (Started)
  owner (Started) → owner-side sync stream
  subtree (DisposeHostedHubs)
    subscriber (DisposeHostedHubs)
      client-side sync stream (ShutDown → UnsubscribeRequest)
```

The subscriber's immediate parent is tearing down, but the owner is still serving. The previous
one-hop condition refused that release. Its premise that a disposing parent meant the receiver was
also being disposed was false: parentage describes the sender's lifetime, not the remote owner's.
Nested layout streams and a whole subscribing subtree must release their owner's streams just as a
single subscribing hub does.

**Sender correlation includes the routing hosts.** `UnsubscribeRequest` is `ICorrelatedBySender`:
the subscribe was posted from the same `workspace.Hub`, and normal upward routing adds each
non-mesh parent to its sender address. The teardown handoff must add those same qualifiers for the
hops it bypasses. Keeping only the bare sender drops correlation information; replacing it with the
carrier identifies someone else. The test compares the release sender to an actual live delivery
from the same subscriber, including all host qualifiers.

## 6. What this is NOT

- **Not a sweeper, a cap, a weak reference or a timer.** Nothing scans, nothing expires, nothing
  retries. The population drains because the message that drains it is delivered.
- **Not "dispose harder".** Teardown still lets accepted work finish; the subscribing hub's own
  teardown is not held open waiting for the owner to answer, and the second test arm below fails if
  it ever is.
- **Not a change to the release's ORDERING.** It still runs from the sync hub's `ShutDown`, strictly
  after Quiescing, so an accepted user action still orders ahead of it (#3986).
- **Not a widening of the guard.** An unmarked fire-and-forget event is still refused, with the same
  transient `ShuttingDown` classification, and the control arm measures that.

## 7. The negative control — both directions, no window

Two test classes pin both direct and nested teardown; neither can pass on no evidence.

**`SubscriberTeardownReleasesTheOwnerSyncHubTest`** (`test/MeshWeaver.Layout.Test`) builds a
population and drains it: three remote layout-area streams on one subscribing hub, each with its
owner-side `sync/{id}` live and holding the area's handlers, then disposes the **subscribing hub** —
never a stream.

| arm | unfixed | fixed |
|---|---|---|
| `DisposingTheSubscribingHubDrainsTheOwnerSidePopulation` | **fails** — the client's own `DisposalCompleted` fires, the three owner-side hubs emit nothing in 36 s | passes in **921 ms** |
| `TheSubscribingHubsOwnTeardownStillCompletes` | passes | passes — the control against "hold the teardown open until the owner answers" |

**`ReleaseLeavesATearingDownHubTest`** (`test/MeshWeaver.Messaging.Hub.Test`) pins the framework
contract on the production route — a disposable on a HOSTED hub, so it runs in the parent's
`DisposeHostedHubs` — and reads the POST's own verdict rather than inferring from a wait:

| assertion | unfixed | fixed |
|---|---|---|
| the marked release's post is not `Failed` | **fails** (`Failed`, `ShuttingDown`) | passes |
| …and it ARRIVES at the sink | — | passes |
| the unmarked event's post IS `Failed` / `ShuttingDown` | passes | passes — the growing-direction control |
| the unmarked event never arrived | passes | passes |

🚨 **The control's "never arrived" is decided by ORDER, not by a timer.** The unmarked event is posted
FIRST and dropped at the source, so once the sink has handled the release there is no queue left that
could still deliver it. A negative assertion with a window would have to choose one, and on CI
`TestTimeouts.Quick` exceeds the 30 s `methodTimeout`.

The nested cases dispose ancestors of the subscriber while leaving the owner live.
`AReleaseCrossesEveryTearingDownAncestor` fails on the old code with a `Failed` post verdict;
`DisposingANestedSubtreeDrainsTheSurvivingOwnersPopulation` requires all three owner-side streams to
reach `DisposalCompleted`. Both direct and nested framework cases assert that the original sender
reaches the owner unchanged, and ordinary unmarked events remain refused. The full-tree control
requires teardown to complete and the release to return `Failed` / `ShuttingDown` when no carrier
survives.

## Rules

1. **A message that is the only thing which frees state elsewhere implements
   `IReleasesRemoteState`** — and nothing else does. An event whose loss the receiver recovers from
   keeps the historical refusal.
2. **Never re-derive "is my hub past the gate?" at a call site.** The guard's predicate lives in one
   place; a second copy in another assembly is two lists that have to stay in step.
3. **When a farewell must leave a disposing hub, use the first ancestor whose post gate remains
   open.** A nested subtree can be dying while the receiver is still live elsewhere. Preserve the
   original sender and report a refusal when there is no surviving carrier.
4. **A leak fix asserts that the population DRAINS**, with a stated denominator, and shows that it
   does not before the change. A test that watches one object cannot tell "the release arrived" from
   "that one happened to go away".
