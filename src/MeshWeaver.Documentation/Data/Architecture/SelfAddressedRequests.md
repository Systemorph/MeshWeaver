---
Name: A Request a Hub Sends to Itself
Category: Architecture
Description: >-
  The mesh's node CRUD is issued on the same hub that executes it, so sender and target are ONE
  hub. Which failure causes that rules out, the three that remain, and why the timeout message
  used to name none of them.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="8"/><path d="M12 8a4 4 0 1 1-4 4"/><path d="M12 4v4"/></svg>
---

# A Request a Hub Sends to Itself

`MeshService.CreateNode` posts a `CreateNodeRequest` from
[`NodeOperationIssuingHub()`](../DataAccessPatterns) with the target
[`NodeOperationTarget()`](../DataAccessPatterns). For every caller that holds the root mesh hub —
the plugin-catalog boot services, the log-incident ingest, activity tracking, every mesh-singleton
that takes the DI-injected `IMessageHub` — **both resolve to the same address**,
`portal/nodeops-{meshId}`: the issuing hop exists to keep the request off the router, and the
target hop exists to keep the execution off it. So the mesh's single busiest request/response
exchange is one a hub makes **with itself**.

That is not an anomaly. It is the normal shape of node CRUD in this codebase, and it changes which
failures are possible.

## What a self-addressed request rules out

`HierarchicalRouting.RouteMessageAsync` strips `Host` and compares the target to `hub.Address`; on
a match the delivery is handled locally and never enters `RouteAlongHostingHierarchy`. The reply is
posted `ResponseFor(request)`, whose target is the request's sender — the same hub again. So:

- **there is no routing leg** that could lose the request;
- **there is no reply leg** that could lose the answer;
- **the target's `RunLevel` and queue ARE the caller's**, because they are one object.

## What it does NOT rule out

Everything that can happen inside ONE `MessageService`:

| where | what it does | is the sender told? |
|---|---|---|
| the teardown intake gate (`RefusesIntake`) | refuses the delivery | NACK, or a classified `Failed` |
| the per-key storm breaker | `Ignored()`, nothing enqueued | no — and the key is `(sender, target, type, payload identity)`, which for a self-post is one bucket per node path |
| the aggregate shedder | `Ignored()`, nothing enqueued | no — but only `[CanBeIgnored]` traffic is sheddable |
| the deferred-backlog cap (`MaxDeferredMessages`) | `Ignored()` past 512 parked behind a closed gate | **now yes** — see below |
| the handler | returns `Processed()` and owes its reply from a DETACHED observable | only when that observable produces a terminal |

🚨 **The last row is the one that makes an empty queue meaningless.** Every canonical mesh handler
— create, delete, move, copy, upsert — returns `request.Processed()` within a millisecond and
answers later from a composed observable it subscribed and let run. While that observable is
pending, the hub's queue is *empty* and the hub is *idle*, and it looks exactly like a hub that
never received the request at all.

## The gate that made this reachable on nodeops

`NodeOperationExecutionHub` builds the hub with `.AddData()`, which installs the `DataContextInit`
initialization gate whose only bypass predicate is `PingRequest`. The framework additionally
bypasses `ShutdownRequest`, `DisposeRequest`, `DeliveryFailure`, `InitializeHubRequest`,
`HeartBeatEvent` and any **awaited response**. A `CreateNodeRequest` is none of those, so node CRUD
**is deferrable on the mesh's node-CRUD hub** — and past the 512 cap the overflow was dropped with
no answer at all, justified by a comment written about a different kind of traffic (*"a client
re-syncs a fresh Full on reconnect"*). Nothing re-syncs a `CreateNodeRequest`. That drop now goes
through `AnswerUnreleasableDelivery`, classified `Unavailable` ("no verdict was reached, retry"),
with the same answer-once and `MayAnswer()` guards the `GATE_FAILED` branch beside it already uses:
fire-and-forget traffic keeps the historical silent drop, an awaited request is told.

## Reading the timeout

When nothing answers, the caller ends at the hub's `RequestTimeout` with `No response received in
hub … → target …`. Three things in that message are what make it usable on a self-addressed
request:

1. **`handledWhileWaiting=N`** — how many messages this hub handled between registering the
   callback and giving up (`MessageHub.Version` differenced against the value captured at
   registration). The queue snapshot beside it is an *instantaneous sample* at the give-up moment;
   this is the *interval*. A hub reporting `buffer=0` and `handledWhileWaiting=4312` was not idle
   while waiting, whatever its queue says now.
2. **The self-addressed verdict** — `🚨 THIS HUB IS ALSO THE TARGET`, followed by what that
   excludes and what it leaves. The generic three-way unknown ("routing / wedged target / lost
   reply") is not printed, because two of its three branches cannot occur and its stated
   discriminator — *"the target's own RunLevel and queue"* — is the number already on the line.
3. **`Trail: …`** — the request's own
   [`RequestFateLedger`](../DebuggingMessageFlow) stages, ending in the ledger's one-sentence
   verdict. The ledger is per hub TREE, so for a self-addressed request it is complete by
   construction: intake, gate, routing, `HANDLER_ENTER`, the handler's own detached stages, the
   reply's journey. `HANDLER_ENTER` with nothing after it is the detached-chain case above.

## And the post's own verdict

`MessageService.Post` returns what `ScheduleNotify` decided. A drop at intake (`Ignored`) or a
refusal (`Failed`) now reaches the caller in the returned envelope instead of being replaced by the
pre-pipeline `Submitted` one. That is what makes `MeshExtensions.WasCarried` — the claim-then-verify
on both create-verdict posts, which documents `Ignored` as *"the one that reads like a success"* —
able to fire at all; before, every post went through the same `ScheduleNotify` whose result nobody
read, so a guard written for exactly this case could not.

The success path is untouched: `ScheduleNotify` returns `Forwarded()` there, and `Post` still hands
back the same `Submitted` delivery it always did.

## Related

- [Debugging Message Flow](../DebuggingMessageFlow) — the fate ledger and how to read a trail.
- [Reading a Base-State Timeout](../BaseStateTimeoutCensus) — the *other* exception shape that
  shares MeshWeaver#1174's fingerprint.
- [Write Verdict Totality](../WriteVerdictTotality) — why every terminal path must answer.
