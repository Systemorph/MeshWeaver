---
nodeType: Markdown
name: Router Traffic Detection
category: Architecture
description: The ROUTER_TRAFFIC detector has two sites — the receiving hub, which names the two addresses, and the origin, which names the call site. Why the receiver-side line alone could not close an issue in four re-filings and 41,087 lines, what each site can and cannot see, and the seam a violating caller hops onto.
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

> 🚨 **The seams are opt-in and nothing enforces them.** There is no `src/`-side ratchet for this
> shape; `RouterRequestOriginSites.allow` covers the TEST tree only. A new mesh-singleton that posts
> node CRUD from its injected hub is a new #1140, and the origin line is what makes that a five-minute
> question instead of a month-long one.

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

## See also

- [Asynchronous Calls](../AsynchronousCalls) — the no-`await` rulebook the seams live inside
- [Actor Model](../ActorModel) — why work on the router's block starves routing
- [Data Access Patterns](../DataAccessPatterns) — which API to reach for instead
- [Debugging Message Flow](../DebuggingMessageFlow) — when a message disappears rather than misaddresses
