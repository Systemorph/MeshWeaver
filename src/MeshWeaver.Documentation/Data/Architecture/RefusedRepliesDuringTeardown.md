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
`ReportFailure`'s own exits then dropped the message in silence — the `RunLevel >= DisposeHostedHubs`
gate returned during exactly the teardown this hole opens in (removed in #4072, see below), and
`MayAnswer()` asks whether the SENDER wants an answer, which is not the question a reply poses.

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

The other abandonment path a teardown can take — `NackThroughParent`, used by the intake gate,
the disposal drain and the handler-fault arms — declines a typed reply on its first line (it admits
only `IRequest` and `RawJson`) **and** declines everything when the parent is already past
`DisposeHostedHubs`. That second decline was read, until #4072, as "every sender is going away
too"; it is not, and what happens to the NACK it drops is the next section.

### The NACK's own carrier of last resort — #4072

A **NACK** is the one reply whose loss is never the responder's fault to notice: it is minted
precisely because the responder could not do its work. Three carriers used to be tried, in this
order, and the third was not a carrier at all:

| carrier | declines when |
|---|---|
| `NackThroughParent` — post through the parent | the parent is past `DisposeHostedHubs` |
| the post seam's forward — `PostImplGeneric` hands a correlated reply to a live parent | the parent is past `DisposeHostedHubs` |
| `ReportFailure` — this hub's own `Post` | **its own run level** was `>= DisposeHostedHubs` — it returned before reaching the post seam at all |

The third row is the defect #4072 was filed on. The gate predates the post seam's forward, so
every `ReportFailure` caller — the genuine-fault arm of the handler `Catch`, the routing tail, the
unpack failure, the post-pipeline reject — computed its verdict past `DisposeHostedHubs` and then
dropped it, while the requester burned its whole budget. `ReportFailure` now hands the answer to
the post seam unconditionally; the seam forwards through a live parent and otherwise **stamps the
request's trail** with what it could not do (`REPLY_REFUSED_SHUTTING_DOWN runLevel=… parent=…`),
which is what that line used to do silently.

The first two rows are the state a **whole-tree teardown** answers every sibling in: a parent
reaches `DisposeHostedHubs` *before* it disposes its children, so no child's answer to another
child can ever pass through it. The requester is not "going away too" — it is `Quiescing` on
exactly that callback, re-arming its budget because the responder is a shutting-down sibling that
"answers before it goes" (`[QUIESCE-WAIT]`), and gives up only on `[QUIESCE-CUT]`. The whole
teardown therefore paced itself on the sum of those budgets for an answer that existed the whole
time. Measured on `LeavingHubAdoptionSweepTest` (core `main`, 14 s, **passing**): the responder's
trail read

```text
HANDLER_FAULT TargetInvocationException→TargetInvocationException→HubDisposingException
  → NACK_DECLINED reason=parent-DisposeHostedHubs
  → FAILURE_REPORTED errorType=ShuttingDown runLevel=Dead
  → RESPONSE_POSTED type=DeliveryFailure target=cache/…
  → REPLY_REFUSED_SHUTTING_DOWN runLevel=Dead parent=mesh/…@DisposeHostedHubs
```

while the `cache/…` requester sat `Quiescing` with that callback pending for 4 s; the mesh's
teardown took 4.0 s and printed a "still waiting" snapshot at 3 s. With the carrier below the same
teardown takes 2.0 s and prints nothing.

**`MessageHub.TryDeliverNackInProcess`** is the fourth carrier: a `DeliveryFailure` that carries
`PostOptions.RequestId` and that no transport can take is handed **straight to the in-process hub
its requester lives under** — the target's host chain is climbed to the tree root, the top-level
hub looked up (never created), the chain descended, and the delivery put on that hub's own
intake — when that hub is still below `DisposeHostedHubs` (its gate admits replies while
`Quiescing`, and its callbacks are cancelled from `DisposeHostedHubs` on, so a later hand-over
would be wasted). It is offered on the post seam's refusal arm, on the two routing drop arms
after the `IUndeliverableReplySink` has declined, and — through `ReportFailure` — at the intake
gate's tier 2, which used to return without answering because "there is genuinely no route".

Why this is not the second transport the rule above forbids:

- **It carries NACKs only.** A `DeliveryFailure` acknowledges no state, so the reorder hazard that
  keeps typed replies off any bypass — an ack overtaking the change it acknowledges — cannot
  arise. Typed replies keep the documented drop (the `CreateOrUpdateNodeResponse` a sibling's
  `nodeops` mints during the same teardown still costs its requester one quiesce budget).
- **It is offered only where the drop happens.** Every other carrier has declined by the time it
  runs; there is no post left for it to race.
- **It lands on the requester's intake, not in its handler.** The requester's own gate decides and
  its own pump serialises — exactly what a routed delivery would get. Nothing runs on the
  responder's turn.

Two classification changes ride along. A handler fault whose chain carries the DI container's
"this scope is gone" (`HubDisposingException.IsDisposedContainer`) is a **teardown fact** and is
answered `ShuttingDown` in the owner's refusal vocabulary, like a `HubDisposingException` — it
used to fall to the genuine-fault arm and be answered `Unknown`, a bug read into a recycle. And
the `HANDLER_FAULT` stage now names the inner exception types (`Outer→Inner→…`), because the
dispose snapshot is the one artefact a green run keeps and #4072 was filed on a trail that could
not tell the two apart.

Pinned by `MeshWeaver.Messaging.Hub.Test.HandlerFaultPastDisposeHostedHubsTest`: a delivery
pipeline stage detaches the handler from its turn (the `AccessControlPipeline` shape, which is how
a handler comes to run on a hub that has moved past `DisposeHostedHubs` at all) and releases it
once the fence — parent past `DisposeHostedHubs`, victim at `DisposeHostedHubs`, requester
`Quiescing` — is read back. Falsified by reverting the production change: all four cases then
time out with the requester unanswered, and the whole-tree cases hold the mesh teardown open on
the pending callback for the requester's entire quiesce budget.

**Reading a trail after this change.** The verdict is decided by the LAST post-seam stage, not by
`RESPONSE_POSTED` alone: `NACK_DELIVERED_IN_PROCESS` / `REPLY_FORWARDED_THROUGH_PARENT` mean a
carrier accepted it (chase the requester's side); `REPLY_REFUSED_SHUTTING_DOWN` means the seam
refused it and names both run levels; `NACK_DECLINED reason=…` with nothing after it means every
carrier declined and says why.

**And the reply's own journey is on the same line.** A reply is posted under a fresh delivery id
that nobody awaits, so until now every stage after `RESPONSE_POSTED` — its intake at the parent,
its routing, its intake at the requester, the callback rule — was recorded nowhere, and the
verdict could only say *"chase the response delivery"* (which is exactly what the 2026-09-13
merge-queue failure on `DanglingNodeTypeUpdateTest` left a reader with: `PATCH_ACK ok →
RESPONSE_POSTED … → PATCH_FLUSH_SUBSCRIBED ⇒ the reply was lost between the responder and the
requester`, and nothing to chase it by). The ledger now **aliases the reply's id to the
request** at `RESPONSE_POSTED`, so the same `Find(id)?.Add(…)` every hub already performs writes
the reply's stages into a sub-trail rendered as `↩ reply#1: RECEIVED@… → ROUTED … → HANDLER_EXIT
state=Processed@<requester>`, and the verdict reads its last stage: no stage at all ⇒ the post
seam refused it or the target is outside the tree; `ROUTED … state=Failed` ⇒ routing dropped it
and the stage names the hub; `RESPONSE_ARRIVED_NO_SUBJECT` ⇒ it arrived where no callback was
held; a last `RECEIVED` ⇒ it is sitting in that hub's queue. Three replies per request are kept
(a subscription is answered many times, and only the first few can say anything about a lost
verdict). Pinned by `ReplyTrailFollowsTheRequestTest`.

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
