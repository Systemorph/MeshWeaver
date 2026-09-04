---
NodeType: Markdown
Name: "Refused replies during teardown — a NACK is advice to the sender, and a reply has none"
Abstract: "A hub past DisposeHostedHubs refuses everything it would hand to its parent, and every failure route in the framework answers the delivery's SENDER. For a request that is exact. For a REPLY the sender is the responder — it is waiting for nothing — while the party parked on the message is never told, so the answer is dropped and the caller burns its whole verdict budget. Why the owner-side claim-then-verify seam could not see it, where the hand-over belongs instead, and how a race that passed 15/15 locally with the defect present was made deterministic."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#8d3b72'/><path d='M6 8h12M6 12h12M6 16h7' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round'/><path d='M14.5 16.5l3 3 3-5' fill='none' stroke='white' stroke-width='1.8' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Disposal"
  - "Lifecycle"
  - "Messaging"
---

# Refused replies during teardown

> **The rule.** A delivery the transport refuses is answered by telling its **sender**. That is
> right for a REQUEST — the sender is the party waiting, and "retry against the fresh activation"
> is advice it can act on. It is meaningless for a **REPLY**: a reply's sender is the RESPONDER,
> which is waiting for nothing and will never re-send, while the party actually parked on the
> message — the request's originator — is told nothing at all. So before routing drops a delivery
> that carries a correlation id, it offers it to the in-process watch still armed for it.
>
> Teardown policy is in [Teardown Layers](/Doc/Architecture/TeardownLayers); the hub-level phase
> machine in [Hub Disposal Model](/Doc/Architecture/HubDisposalModel). This page is one asymmetry
> those two do not state, the incident it caused, and the seam that closes it.

---

## The asymmetry

`HierarchicalRouting` refuses to hand a delivery to a parent that has reached
`DisposeHostedHubs`, and words the refusal as advice:

```text
Hub TestData/teardown-nack-node cannot route PatchDataResponse to cache/mFUWHEUx… — its parent
hub mesh/mFUWHEUx… is shutting down (RunLevel=DisposeHostedHubs). The address may reactivate
(recycle / restart); retry to get the authoritative answer.
```

The refusal itself is correct and must stay: handing more traffic to a hub that is tearing its
children down is the #981 teardown hang. What was wrong is the sentence's addressee. Read it as a
**request** refusal and every word is exact. Read it as a **reply** refusal — which is what the
message type says it is — and it is advice to nobody:

| | refused REQUEST | refused REPLY |
|---|---|---|
| Who sent it | the caller | the responder |
| Who is waiting | the sender | **somebody else** |
| Can the sender act on "retry"? | yes | it has nothing to retry |
| Who learns the delivery failed | the sender, via `DeliveryFailure` | **nobody** |

Every failure route in the framework answers `delivery.Sender`: `MessageService.ReportFailure`
posts a `DeliveryFailure` to it, `NackThroughParent` NACKs it through the parent, the routing
services' NotFound path NACKs it directly. All of them are the wrong party for a reply. And two of
`ReportFailure`'s own exits then drop the message in silence — the `RunLevel >= DisposeHostedHubs`
gate returns during exactly the teardown this hole opens in, and `MayAnswer()` asks whether the
SENDER wants an answer, which is not the question a reply poses.

---

## What it cost — issue #3303

`NackReachesTheWaiterDuringTeardownTest.OwnerDisposingUnderMeshTeardown_StillAnswersTheWaitingCaller`
timed out intermittently, **dequeued a merge-queue group build** (#3293, `queue-rejected`) and
reddened #3296, #3297, #3299, #3300 and #3305 in one afternoon — core `main` could not merge
reliably while it stood, and core CD delivered nothing for four hours. The fix landed as #3302
(`684cc6ac1`), whose branch is named for a quiescing gate that was dropped before merge; the change
that shipped is the routing hand-over below. The whole sequence fits in four milliseconds of the
test's own trace:

```text
[write]   patch posted with marker teardown-nack-4de6af5289; owner merge is parked
[fence]   caller is armed on the late watch (ArmedCount=1)
[dispose] mesh disposal invoked
[Warning] Message delivery failed for DataChangedEvent  … cannot route … is shutting down
[Warning] Message delivery failed for DataChangedEvent  … cannot route … is shutting down
[Warning] Message delivery failed for PatchDataResponse … cannot route … is shutting down
[Warning] Message delivery failed for GetDataResponse   … cannot route … is shutting down
```

The `PatchDataResponse` is the owner's verdict for a write a caller was parked on. It was minted,
posted, accepted, and refused one turn later — and the caller then sat out its whole 31 s
`WriteVerdictBound` and reported `OwnerUnreachable` for a write the owner had already judged.

### Why the race is a race, and why it lands under load

`Mesh.Dispose()` is not one step. It **synchronously** cascades `CloseCreation` through the entire
subtree — so every descendant's `IsShuttingDown` flips at once, while the mesh is still at
`Started` — and then posts `Quiescing` to its own loop, from where it advances to
`DisposeHostedHubs`. The parked merge turn wakes on that first, synchronous signal. Whether its ack
is ROUTED before the mesh reaches `DisposeHostedHubs` is a race between two independent executors,
and the ack is not a single step either: `hub.Post` is evaluated on the owner's turn (accepted —
the owner's own run level is still open) and routed on a **later** turn. On an unloaded machine the
merge wins and the reply is delivered normally; on a loaded CI shard the mesh advanced in 3 ms and
the reply was dropped. Locally the test passed **15/15 with the defect fully present**.

### Why the owner-side fix could not see it

#3196 already made the owner's ack gate **claim-then-verify**: `AckOnce` latches its once-only flag
before posting — correct, two racing legs must not both answer — and `PostPatchVerdict` therefore
falls back to `ILatePatchVerdictSink` when the post is REFUSED, because latching is also what
disables `RegisterOwnerDisposingNack`'s dispatch ("already answered").

That seam reads the delivery `hub.Post` returned. It catches the **synchronous** refusal, where
`PostImplGeneric` stamps `POST_REFUSED_SHUTTING_DOWN` and hands back a `Failed` delivery. It cannot
catch an ACCEPTED post that dies in routing a turn later — and `RoutePatchVerdict`'s own remarks
say so, and place the fix here:

> The teardown hole is real and belongs one layer down: a correlated reply that
> `HierarchicalRouting` cannot forward is DROPPED with nobody told, and the process still holds the
> registry that could take it. Answering it here cannot be right — this seam is called before
> routing has run.

Serving the armed sink at the post site was tried twice and reverted both times: a late watch is
armed for EVERY patch, so dispatching there answers the caller **ahead of** the state change the
ack is about, and the write completes before what it wrote is readable (`ComboGateRollTest`,
`ImportTypeBeforeInstanceTest`).

---

## The seam

`IUndeliverableReplySink` (`MeshWeaver.Messaging.Hub`) is the in-process last resort for a
correlated reply routing is about to drop:

```csharp
public interface IUndeliverableReplySink
{
    bool TryDeliver(IMessageDelivery delivery);
}
```

`HierarchicalRouting` offers a delivery to it on the two arms where it gives up — the parent past
`DisposeHostedHubs`, and the disposing no-route arm — and returns `delivery.Processed()` when a
waiter takes it, so the drop never happens. It is offered **only** where the delivery is being
dropped: serving an armed caller alongside a HEALTHY post reorders the answer ahead of the state it
acknowledges, which is why that was tried twice at the patch-verdict seam and reverted both times.
Here there is no post left to race.

The implementation is `LatePatchResponseRegistry` — **the same instance** `ILatePatchVerdictSink`
exposes to the owner side, registered as a factory over the concrete singleton so a salvaged reply
reaches the identical watch an owner-minted late verdict would. Two message shapes are recognised,
and they are the two a patch verdict can arrive as:

| Message | Dispatched to | Why |
|---|---|---|
| `PatchDataResponse` | `Dispatch(requestId, …)` | the owner's verdict — the #2778 seam |
| `DeliveryFailure` | `DispatchFailure(requestId, …)` | the pipeline's RLS refusal — the #2661 seam |
| anything else | *nothing; `false`* | not a guess — see below |

**The correlation id is the whole test.** A request carries none — it IS the correlation — so
requests never reach the sink and keep the ordinary NACK path unchanged. Only a message posted with
`ResponseFor` / `WithRequestIdFrom` carries `PostOptions.RequestId`, and that is exactly the set of
messages whose waiter is somebody other than the sender. A miss costs one dictionary lookup, which
is what makes it affordable to ask on every dropped delivery.

The other abandonment path a teardown can take — `NackThroughParent`, used by the intake gate and
the disposal drain — needs no hook, and that is checkable rather than assumed: it declines a typed
reply on its first line (it admits only `IRequest` and `RawJson`) **and** declines everything when
the parent is already past `DisposeHostedHubs`, which is exactly the state this defect lives in. So
the hand-over needs the routing arms, and the storm-sensitive NACK path is left alone.

### What it deliberately does not cover

- **A reply that crossed a process boundary.** It arrives packaged (`RawJson`) and its waiter is in
  the other process, where this registry cannot see it. The sink answers `false` rather than
  guessing — the same answer `Dispatch` already gives for a caller it never armed.
- **Replies with no armed watch — `GetDataResponse` above.** Reads have their own recovery (the
  paced re-probe in `GetMeshNodeOutcome`, `SynchronizationStream`'s resubscribe latch). Inventing a
  delivery for them here would be answering a question the registry was never armed for. The
  `GetDataResponse` in the trace is a real second instance of the same asymmetry; it does not have
  the same consequence, because its caller is not parked on a single terminal.
- **Fire-and-forget events — the two `DataChangedEvent`s.** Nobody awaits them; the historical
  silent drop is correct.

---

## Making the race deterministic

The fix shipped with the field detector that found it — `NackReachesTheWaiterDuringTeardownTest`,
which only fails when the race lands. `RefusedReplyReachesTheWaiterTest`
(`test/MeshWeaver.Graph.Test`) reaches the same state **by construction**, and the lever is the one
`DisposalRaceNackTest` already uses: an accepted turn that parks the action block.

1. A parent hub `P` hosts an owner hub `C`. `C`'s action block is parked on an accepted turn.
2. `P.Dispose()`. `P` advances to `DisposeHostedHubs` and disposes `C` — whose own
   `ShutdownRequest` **queues behind the parked turn**, so `C` cannot leave `Started`.
3. `P` cannot advance past `DisposeHostedHubs` until `C` reports it is done. Both halves of
   *"owner still open, parent already past"* are therefore facts, and the test fences on reading
   them back.
4. The parked turn is released and posts the reply — accepted by `C`, refused by routing.

**Two controls decide what the green means, and neither depends on which layer performs the
hand-over.** The waiter is a LIVE hub. Before anything is torn down, the owner posts a probe to it
along the same route and the test waits for it to arrive — so "the reply never arrived" afterwards
is a measured fact about the refusal, not a property of an address that was never reachable. After
the release, the waiter's hub must have received **nothing but that probe** while the registry watch
was consumed: the answer reached the caller, and the transport did not carry it.

With the hand-over disabled at both `HierarchicalRouting` call sites, the two tests were run
TOGETHER — and the tally is the argument for having both:

```text
[xUnit.net 00:00:38.14]  RefusedReplyReachesTheWaiterTest…StillReachesTheArmedWatch [FAIL]
  Expected the observable to emit a value within 36s … but it did not. The observable emitted
  nothing at all.

  [probe] owner → waiter route is live while nothing is shutting down
  [fence] parent=DisposeHostedHubs owner=Started — a reply posted now is accepted by the owner and
          refused by routing
  [Warning] Message delivery failed for PatchDataResponse (ID: _LUemby4UkWe5wYCX4OZdg) in
          refused-reply-owner/1: Hub refused-reply-owner/1 cannot route PatchDataResponse to
          refused-reply-waiter/1 — its parent hub refused-reply-parent/1 is shutting down
          (RunLevel=DisposeHostedHubs). …

Failed!  - Failed: 1, Passed: 1, Skipped: 0, Total: 2
```

`Passed: 1` is `NackReachesTheWaiterDuringTeardownTest` — **the field detector passed with the
production fix removed.** It printed the same refusal it complains about in CI; this machine simply
did not lose the race. Restore the hand-over and both are green, the deterministic one in under a
second.

`UndeliverableReplyShapesTest` pins the other half — which shapes the registry takes and which it
declines — including the `DeliveryFailure` arm, which is the RLS refusal of #2661 and the one a
salvage that knew only about `PatchDataResponse` would silently drop.

---

## Rules

- **A reply is not work.** When you add a refusal, an intake gate or a drop during teardown, ask
  who is waiting on the message — not who sent it. If those differ, the sender-addressed answer is
  not an answer.
- **A refusal's own advice is a claim about its addressee.** "Retry to get the authoritative
  answer" asserts that the recipient of the NACK is able to retry. On a reply that assertion is
  false, and a message that reads correctly is the reason this stood for two days.
- **Verify the DELIVERY, not the post.** A `Post` that returns a non-failed delivery has been
  ACCEPTED, not delivered. Any seam that treats acceptance as success is blind to every failure
  routing can produce, which is all of them during teardown.
- **A detector that only fires under load is still a detector.** #3303's field test passed 15/15
  locally with the defect fully present, and reddened five PRs plus a merge-queue group build in one
  afternoon. It was right; cataloguing it as a flake would have taught the merge-queue steward to
  re-queue a real failure fleet-wide. A load-dependent detector earns a deterministic pin beside it,
  not an entry in `known-flakes.json`.
- **The last resort is offered only where the drop happens.** Handing a reply to an armed waiter
  ALONGSIDE a healthy post reorders the answer ahead of the state it acknowledges — measured twice,
  reverted twice. "There is no post left to race" is the precondition, and it is what makes the
  hand-over safe rather than clever.
