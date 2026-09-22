---
Name: The Subsystem in the Title Is Not the Cause
Category: Architecture
Description: >-
  Six tickets named two Orleans subsystems and grouping them on that produced the wrong answer three
  times. What separates a ticket about a subsystem from a ticket about something that subsystem merely
  NOTICED; the two readings that flipped when the evidence was re-read — a pod-hub address suffix that
  is a process id rather than an operation id, and a "memory streams" pair whose live traffic has two
  different owners; and the rule about folding a ticket whose root has no record of its own.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M7 7h10v10"/><path d="M7 17 17 7"/><circle cx="5" cy="19" r="2"/><circle cx="19" cy="5" r="2"/></svg>
---

# The Subsystem in the Title Is Not the Cause

An auto-filed incident is titled after **the logger that emitted the line**, and the ticket body's
"probable cause" is written from that line alone. Both are honest and neither is a diagnosis: a log
site sees the condition it noticed, not the condition that caused it. So a cluster of tickets that
share a subsystem name may share nothing else.

[Reading a Routing Saturation Report](/Doc/Architecture/ReadingARoutingSaturationReport) is the
extreme case — 78 tickets on one gauge, one genuine routing defect. This page is the same discipline
applied to a small cluster, and it records what changed when each ticket's evidence was re-read
rather than its title.

## Ask, per ticket: which way does the arrow run?

Three questions, in order, before believing the subsystem in the title:

1. **Is this subsystem the SUBJECT or the INSTRUMENT?** A router that reports a delivery failure may
   be reporting a target that died, a process that is exiting, or a silo that has lost its CPU. Only
   the first is a routing fault.
2. **Is the failure REAL, or real-but-mis-reported?** A `DeliveryFailure` surfaced to a sender is as
   often a classification question as a delivery one: the condition happened, and what is wrong is the
   verdict attached to it. That is a fix, not a silencing — but it is a *different* fix from making the
   condition stop.
3. **Does the root have a record of its own?** If not, folding the ticket deletes the only record
   there is. A ticket whose cause is genuinely elsewhere may be closed *pointing at* that elsewhere;
   a ticket whose cause is nowhere must stay open, with its remaining work corrected.

## The cluster, per ticket

| ticket | subsystem in the title | where the arrow actually starts |
|---|---|---|
| pod-hub `DeliveryFailure` (#2299) | routing | **routing's own** — one of the few. A misrouted delivery gets the right refusal; the verdict attached to it was terminal because the classifier's discriminator was never passed on that leg. See [A Departed Silo Is Not A Delivery Defect](/Doc/Architecture/ADepartedSiloIsNotADeliveryDefect) |
| cancelled pod-hub deliveries (#5034) | message delivery | the **router's own process going away**, not the sender's operation — see the correction below |
| stream-routed enqueue timeout (#2322) | memory streams | a **silo-local stall**: the queue grain's turn could not run, so the publish timed out. A co-located caller timed out on the same activation, which rules a network partition out. The stream leg was the instrument |
| pulling agents lose queue grains (#4802) | memory streams | **deployment churn** meeting a RAM-resident queue grain. Structural, recorded, and its remaining work is not what the ticket says — see below |
| placement retries (#4890) | silo churn | **deployment churn**, absorbed by Orleans' own placement retry. Every occurrence was retried, none lost, and no application frame appears in any sample |
| router-addressed traffic (#1140) | messaging | the **detector's own keying** plus node CRUD whose target fell back to the router — never the stated "portal hubs not assigning their own address". See [Router Traffic Detection](/Doc/Architecture/RouterTrafficDetection) |

Two of the six have a cause outside the subsystem they name; one has a cause inside it whose remedy
lives in another repository; one is genuinely its subsystem's own.

## Correction 1: a pod-hub address suffix is a PROCESS id, not an operation id

A ticket reported two directed deliveries failing 2 ms apart, to `import/SpkmR6TjbUq2wZ0QDz3d5w` and
`cache/SpkmR6TjbUq2wZ0QDz3d5w`, and reasoned from the shared suffix that both legs belonged to one
**operation** — "a per-operation cancellation token shared by the import activity" — because the
sender was an import activity.

The suffix is the **mesh id**: `cache/{meshId}`, `import/{meshId}`, `portal/nodeops-{meshId}`,
`portal/reads-{meshId}` are the pod hubs of one process. The positive control is in a neighbouring
incident, where `cache/TmrXhZkCpE6r94nulQBzpQ` and `portal/nodeops-TmrXhZkCpE6r94nulQBzpQ` appear
minutes apart on the same pod — the same suffix under two different address types, which an operation
id cannot be.

So the two failures are **two pod hubs of one target process**, cancelled at the same instant. That
moves the question from the sender to the router: the routing pool's teardown cancels every accepted
leaf, and an activation being deactivated has its outstanding requests cancelled too. **Neither is
established** — the pod was not observed stopping in that window, and this is recorded as the corrected
question rather than an answer. What is established is that the ticket's stated mechanism cannot be
read off its own evidence.

> 🚨 **A shared identifier is evidence of a shared SOMETHING, and which something is a fact about the
> naming scheme, not about the incident.** Reading it as the sender's operation put the whole
> investigation on the wrong side of the call.

## Correction 2: two "memory streams" tickets, two different live owners

Two tickets carry the `memory-streams` label and look like one root — a RAM-resident
`MemoryStreamQueueGrain` that dies with its silo. Measured against the code they are not:

- The **enqueue** ticket is the ROUTER's stream leg. That leg is now taken only for an address type
  *declared* client-hosted, and production declares none, so the router cannot publish on it at all.
  Its two siblings were closed as made-unreachable by the same change; it is the one that was left
  open.
- The **dequeue** ticket is the provider's own pulling agents, which serve whatever streams exist. In
  production that is the mesh change-feed broadcast and its invalidator grain — not the routing
  fallback. So the provider is still registered *because of the change feed*, and its retirement is
  blocked behind two slices ahead of it in
  [Durable Streams Are Mesh Nodes](/Doc/Architecture/DurableStreamsViaMeshNodes), one of which is a
  notification-payload change in another repository.

The ticket's own stated remedy — "delete the provider registration, it is the last row" — is therefore
not available yet, and a session that acted on it would have deleted a live transport. Same label,
same grain type, different live traffic, different remaining work.

## The rule about folding

Consolidating a symptom ticket into the ticket for its root is how a cluster shrinks honestly. It has
one precondition: **the root must have a record.** A closed issue is a record; a doc page and a pinned
test are records. Nothing is not.

Applied here: the churn-absorbed placement ticket can close pointing at the departed-silo root, which
has an open issue, a closed sibling, a predicate, a test that pins the production strings verbatim and
a doc page. The dequeue ticket cannot close, because the slice it is evidence for has no other record
of the fact that the provider is *still live in the fleet* — so it stays open with its remaining work
corrected instead.

## Related

- [Reading a Routing Saturation Report](/Doc/Architecture/ReadingARoutingSaturationReport) — the same
  discipline at the single most-ticketed log site, and the numbers that can and cannot answer
- [A Departed Silo Is Not A Delivery Defect](/Doc/Architecture/ADepartedSiloIsNotADeliveryDefect) —
  the one ticket here that is genuinely its subsystem's own, and the safe default that made its fix
  inert
- [Durable Streams Are Mesh Nodes](/Doc/Architecture/DurableStreamsViaMeshNodes) — the slice table the
  dequeue ticket is waiting on
- [Router Traffic Detection](/Doc/Architecture/RouterTrafficDetection) — the router-addressing
  ticket's three mechanisms, and why its own hypothesis was never the one
- [Red-Log Watching & Ticketing](/Doc/Architecture/LogWatchTriage) — how a red line becomes exactly one
  issue, and the consolidation rule these dispositions follow
