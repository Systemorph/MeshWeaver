---
nodeType: Markdown
name: Router Traffic Detection
category: Architecture
description: The ROUTER_TRAFFIC detector has two sites — the receiving hub, which names the two addresses, and the origin, which names the call site. Why the receiver-side line alone could not close an issue in four re-filings and 41,087 lines, what each site can and cannot see, the seam a violating caller hops onto, and why the ratchet that holds that seam derives its denominator from the framework's own handler registrations instead of listing message types.
icon: /static/NodeTypeIcons/box.svg
---

# Router Traffic Detection

The root mesh hub is the mesh's **router**. Work executed on its action block competes with routing
itself: a burst of node CRUD there starves real `SubscribeRequest` traffic and the whole portal
wedges (prod 2026-06-11 — *"11× CreateOrUpdateNodeRequest + 3× CreateNodeRequest@mesh/&lt;self&gt;
stale &gt;60s while real user SubscribeRequests starved"*). `ROUTER_TRAFFIC` is the **detector** that
names deliveries with the router at one end. It never blocks anything: every violating path must
keep working while it is migrated, and must stay visible, because the failure is silent until it is
catastrophic.

The rule itself is a pure predicate — `RouterTrafficRule.RoleOf(targetType, senderType, message,
isResponse)` — pinned in `RouterTrafficRuleTest`. It is evaluated at **two** sites.

## The two sites

| | receiver — `ROUTER_TRAFFIC:` | origin — `ROUTER_TRAFFIC ORIGIN:` |
|---|---|---|
| where | `MessageHub.DeliverMessage`, on the hub the delivery is addressed to | `MessageHub.Post`, on the hub that created it |
| answers | *that* the rule was broken, and between which two addresses | *who* broke it — the posting stack |
| message type | the payload as it ARRIVED — `RawJson` once it has crossed a silo | the real CLR type, always |
| sees a violation posted by another process | yes | no |
| sees a target-less self-post | no (a self-post never reaches `DeliverMessage`) | no (deliberately — see below) |
| volume | one line per (role, type) per hub — scales with the number of hubs that SEE the traffic | one line per (role, type) per hub — one hub, so ~2 lines per process |

Both dedup on `(role, message type)` for the hub's lifetime, in separate dictionaries, so neither
can mute the other.

## Why the origin site exists

> 🚨 **A detector that says a rule was broken but not by whom cannot close the issue it opens.**

The receiver-side line carries two addresses and nothing else. A stack captured there is the routing
machinery, not the code that made the mistake — and the payload type does not help either, because a
delivery that crossed a silo arrives packed, so the honest report is `RawJson`.

That is not a theoretical cost. On `memex` the same finding was filed as
[#1113](https://github.com/Systemorph/MeshWeaver/issues/1113),
[#1121](https://github.com/Systemorph/MeshWeaver/issues/1121),
[#1136](https://github.com/Systemorph/MeshWeaver/issues/1136) and
[#1140](https://github.com/Systemorph/MeshWeaver/issues/1140), ran to **41,087 lines** between
2026-08-10 and 2026-09-12, and every pass over it ended at the same place: two addresses, several
candidate call sites, no way to choose between them. The production pair was

```
RawJson has the mesh hub as sender          (sender: mesh/6CuC…, target: Admin/_LogIncident/cb33ad…)
CreateNodeResponse has the mesh hub as target (sender: Admin/_LogIncident/cb33ad…, target: mesh/6CuC…)
```

— which every one of `MeshService.CreateNode`, the post-creation `DataChangeRequest`, and a direct
`hub.Observe(new CreateNodeRequest(…))` produces identically.

**The origin line prints the frames.** For #1140's shape that is two extra lines per process, against
tens of thousands from the receiver side (whose count scales with the number of per-node hubs that
see the traffic, not with the number of call sites).

## What neither site sees, and why

**A target-less self-post.** A delivery with no target is handled by the posting hub and never
leaves it, so *"the mesh hub is an end of this delivery"* is trivially true of it and says nothing.
The receiver side draws that boundary structurally — a self-post never reaches `DeliverMessage` —
and the origin side draws it explicitly, because reporting it fires on every mesh hub's own
`InitializeHubRequest`, on every boot. A line that appears on every boot is the kind that gets muted,
taking the real ones with it.

The class that goes with it — WORK self-posted on the router, e.g. a target-less `CreateNodeRequest`
executing on the router's block — is a different symptom with its own instruments:
`MeshExtensions.NodeOperationTarget`, which stops it being posted, and the turn-loop snapshot, which
measures the block. It was never covered by `ROUTER_TRAFFIC` at either site.

**Routing's own traffic.** A `HeartBeatEvent` is routing liveness. A response the router posts while
not also being the target is the undeliverable-mail NACK (`RoutingServiceBase.PostNotFound` /
`NackRouteFailure` post their `DeliveryFailure` from the mesh hub via `ResponseFor`, so its sender is
honestly `mesh/{id}`). Both are excluded by the shared predicate, at both sites.

**A routing HOP.** `HierarchicalRouting` sends every hosted hub's non-local delivery UP via
`parentHub.DeliverMessage(delivery)`, and for essentially every hub in the process that parent is the
root mesh hub. Keying the rule on the RECEIVING hub's address rather than the delivery's own ends
made the router's ordinary forwarding look like the router doing work — "validate a token + read a
node" alone emitted five lines. The ends are always the **delivery's**: `delivery.Target` and
`delivery.Sender`, never `Address`.

## The fix at a violating call site

Two seams, both in `MeshExtensions`, both the identity function for any hub that is not the router —
so adopting one is a no-op everywhere except where it matters:

| you are about to | hop onto |
|---|---|
| post a node-lifecycle request (`CreateNodeRequest`, `DeleteNodeRequest`, `MoveNodeRequest`, `CopyNodeRequest`, an upsert) | `hub.NodeOperationIssuingHub()` → `portal/nodeops-{meshId}` |
| issue a one-shot node READ | `hub.ReadIssuingHub()` → `portal/reads-{meshId}` |

The hop is needed on the **sender**, not only on the target. `hub.NodeOperationTarget()` puts the
request's destination off the router; it does nothing about where the request came FROM, and the
`"sender"` role is reported on the request and the `"target"` role on its reply. `MeshService` hops
both (`IssuingHub` + `NodeOperationTarget`) and is the reference shape.

The callers that need it are the ones holding the **DI-injected `IMessageHub`**, which in the mesh's
root container IS the router: mesh-singleton services and `IHostedService`s — the plugin-catalog boot
services, the log-incident ingest and its control plane, the content importers, one-shot
`GetMeshNode` reads. A GUI click action, a layout area or an MCP session hub already holds a
non-router hub and is unaffected either way.

### What enforces them

The seams are opt-in — nothing in the type system makes a caller reach for one — so each is held by
a **ratchet that may only shrink**, one per tree:

| tree | guard | allow file | seeded |
|---|---|---|---|
| `src/` | `RouterAsNodeOperationOriginRatchetGuard` | `test/RouterNodeOperationOriginSites.allow` | 1 |
| `test/` | `RouterAsTestRequestOriginRatchetGuard` | `test/RouterRequestOriginSites.allow` | 3 |

The `src/` guard matches an `.Observe(…)`/`.Post(…)` whose first argument is a **lifecycle message**,
built inline **or** hoisted into a local first, and reads the receiver as an expression rather than
as preceding text. Both tolerances were measured over the tree, not guessed: of the 33 sites it
matches, a construction-anchored scan would miss **7** (every verb of `MeshService`, the reference
implementation, plus the copy helper's hoisted request), and a receiver test that accepted only the
literal `NodeOperationIssuingHub()` text would misclassify the **11** that hoist the seam into a
local or a cached property.

#### The denominator is DERIVED, and that is the point (#4463)

> 🚨 **A list of message types is the same trap one level along.** The guard's first version carried
> a literal list of six node-CRUD names. `DisposeRequest` — a teardown, the most router-hostile
> lifecycle message there is — was not on it, so when `MeshOperations.RecycleCore` posted one
> straight off the DI-injected hub, **only the runtime ORIGIN detector saw it**
> ([#4463](https://github.com/Systemorph/MeshWeaver/issues/4463), measured on `memex`
> 2026-09-16 01:10:13Z). Adding one more name would have bought exactly one more message.

So the guard now **reads its denominator out of production** instead of restating it. A message is
lifecycle when the framework itself registers a lifecycle handler for it, and there are exactly two
such registrations:

| family | read from | today |
|---|---|---|
| hub lifecycle | every `Register<T>(…)` in **`MessageHub`'s constructor** — what a hub answers *because it is a hub* | `DisposeRequest`, `ShutdownRequest`, `PingRequest`, `InitializeHubRequest` |
| node lifecycle | every `.WithHandler<T>(…)` in **`MeshExtensions.WithNodeOperationHandlers`** — what the off-router node-CRUD execution hub answers | `CreateNodeRequest`, `CreateNodesRequest`, `CreateOrUpdateNodeRequest`, `DeleteNodeRequest`, `ValidateDeleteRequest`, `MoveNodeRequest`, `CopyNodeRequest` |

This is a *rule* rather than a *list* because **you cannot add a lifecycle message without passing
through one of those two registrations** — a handler is what makes a message lifecycle in the first
place. A marker attribute, or a name in a test, can be forgotten; a handler cannot, because without
one the message does nothing. On its first run the derivation picked up `ValidateDeleteRequest`,
which the hand-written list had already missed.

The exclusions are sourced the same way: the derived verbs are filtered through **`RouterTrafficRule`
itself**, the pure predicate both runtime sites evaluate, so `HeartBeatEvent` — which
`WithNodeOperationHandlers` also registers, and which the rule calls routing's own liveness — leaves
the denominator automatically, and a future exclusion added there is inherited rather than copied.
Every derived name must also resolve to a real type, so a rename or a move **reddens the guard
instead of silently shrinking what it measures**. That half is a separate test, and it fails on its
own: when the derivation was deliberately broken in rehearsal the ratchet went *green* over 25 sites
with `DisposeRequest` unguarded, and only the derivation test went red.

**One structural exclusion: a self-directed hub-lifecycle post** — no target, or
`o.WithTarget(thatHub.Address)` — is out of the denominator, and not because the detector is quiet
about it. It is because **the remedy does not apply**: the seam's whole effect is to issue the
delivery from a *different* hub, which for a self-directed teardown would send it to the wrong hub
and change what is torn down. `NodeTypeRebindWatcher`, the stale-build convergence, the overlay
self-heal and `LayoutAreaHost`'s own `InitializeHubRequest` are the four sites this covers. The NODE
family is deliberately *not* excluded this way: a target-less `CreateNodeRequest` on the router is
the literal prod 2026-06-11 wedge, and the seam does fix it by moving execution onto the
node-operation hub's block.

Over `src/` this yields **29 lifecycle post sites in the denominator**, 4 self-directed ones
excluded, and one allowed violation (the `#981` entry below).

**Adopting the seam is never a behaviour change**, which is what lets this be a rule rather than a
judgement call: `NodeOperationIssuingHub` returns the hub unchanged unless its address type is the
mesh type. A site already off the router is byte-for-byte unaffected; a site that is not is
corrected.

> 🚨 **What the ratchets still do not see, and why #1140 is NOT closed by this.** They key on
> messages the framework registers a LIFECYCLE handler for, so the read seam (`ReadIssuingHub`,
> which has no request type of its own) is recognised where it is used but its absence is not
> reported — and the other message families in
> [#1140](https://github.com/Systemorph/MeshWeaver/issues/1140)'s evidence have no single seam to
> hop onto at all. Measured 2026-09-16, one of them is still live in `src/`:
> `JsonSynchronizationStream` posts `new UnsubscribeRequest(reduced.StreamId)` on its own `hub`,
> and hopping *that* is not a no-op — it would change which hub the unsubscribe originates from,
> which is a correlation question, not a routing one.
>
> #1140 is therefore the same defect **family** as #4463 and not the same **defect**. Its evidence
> is receiver-side, where a routed payload arrives packed, so its type list is mostly the honest but
> useless `RawJson`; its stated hypothesis (portal hubs not assigning their own address) was wrong;
> and the bulk of its 1,386 lines was the HOP mis-keying, fixed by keying the rule on the delivery's
> own ends. What is left of it is this residue plus whatever the origin line names next. For those
> the origin line remains the instrument, and it is what makes a new one a five-minute question
> instead of a month-long one.

The one seeded `src/` entry is not debt: it is the #981 self-targeted inner create inside the
`CreateOrUpdateNodeRequest` **handler**, posted on and handled by the hub whose turn loop already
owns the upsert. A self-post is deliberately reported by neither detector site (above), so hopping
it would move the inner create off the hub that is mid-upsert and buy nothing. It stays an explicit
allowance rather than joining the self-directed exclusion, because that exclusion is for HUB
lifecycle only — a target-less node CRUD post on the router is the wedge, not a benign self-post.

### The two callers this rule was written from

`MeshOperations.RecycleCore` (the Recycle tool / Compile button) and
`HubRecycleExtensions.RecycleNode` (the framework's one recycle surface) both post their
`DisposeRequest` through `hub.NodeOperationIssuingHub()`. The second had no production sighting; it
is hopped because it is a **public extension on `IMessageHub`**, so its documented rule — "the caller
holds a hub that outlives the target" — constrains who *should* call it, not who *can*. The seam is
the identity function for every documented caller, so adopting it costs them nothing and removes the
undocumented one.

## Reading a report

`ROUTER_TRAFFIC ORIGIN:` prints up to twelve frames, with `MessageHub`'s own plumbing dropped off the
top so the first line is the caller. Read it as a normal stack: the first frame outside the framework
is the site to fix.

The receiver-side line stays, unchanged, and is still the only one that can see a violation posted by
another process.

## Pinned by

- `RouterTrafficRuleTest` — the predicate, over every combination of ends.
- `RouterTrafficDetectorTest` — the receiver site over a real hub hierarchy: a HOP must be silent,
  and both genuine end roles must still fire.
- `RouterTrafficOnNodeCreateFromTheRootHubTest` — the create seam (`IMeshService.CreateNode` issued
  from the root hub keeps the router off both ends) plus the origin site's call-site attribution,
  each with its own positive control in the same fixture.
- `RouterAsNodeOperationOriginRatchetGuard` — the `src/` ratchet, in two tests that fail
  independently: one asserts no new router-issued lifecycle site, the other asserts that the
  denominator was actually DERIVED (both families non-empty, every derived name resolving to a real
  type, `HeartBeatEvent` dropped by `RouterTrafficRule` rather than by a literal). The second exists
  because a broken derivation makes the first go green over an unguarded tree.

## See also

- [Asynchronous Calls](../AsynchronousCalls) — the no-`await` rulebook the seams live inside
- [Actor Model](../ActorModel) — why work on the router's block starves routing
- [Data Access Patterns](../DataAccessPatterns) — which API to reach for instead
- [Debugging Message Flow](../DebuggingMessageFlow) — when a message disappears rather than misaddresses
