---
Name: A Stale Index Row Confirms Itself
Category: Architecture
Description: An index row and the listing that would disprove it come from one source, so absence is not merely unproven but unaskable. The storage providers are the only independent witness — and the answer is worth nothing unless it is asked before the work.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/><path d="M8 11h6"/></svg>
---

# A Stale Index Row Confirms Itself

The platform's idiomatic existence test is a **listing**, and for a very good reason: a point read of
an absent node terminates with a routing NotFound, which opens the storm-breaker on that path — and
the breaker fast-fails writes too, so the read suppresses the write it was waiting for. [CQRS and
Content Access](../CqrsAndContentAccess) states the rule: a node that may not exist yet needs a
`scope:children` listing for EXISTENCE, then `GetMeshNodeStream` for CONTENT.

That rule is about a node that does not exist **yet**. It says nothing about the opposite case, and
the opposite case has a trap in it:

> **A listing cannot disprove a row that the listing itself produced.**

When a partition is torn down, its store goes and index rows can outlive it. Ask the index whether
one of those rows is real and it answers *yes* — truthfully, about itself. The row and its
disproof share a source, so absence is not merely unproven: it is **unaskable**.

## What that cost

Measured on `memex.systemorph.com` during helm revision 59. The new ReplicaSet's second pod sat
`2/3` for 48+ minutes. Because the rollout runs `maxUnavailable: 0` the old pod could not be retired,
so **two builds served one host** for the whole window — which users saw as dropped MCP sessions
(`[ROUTE] … no silo in this cluster is currently serving that hub`), read timeouts, and exceptions.

The pod's log was one path, repeated: subscribe timeouts and `MeshNodeStreamException: MeshNode
OwnerUnreachable` against nodes in the partition `UWDeepfield`, which in the maintainer's words
"hasn't existed in ages". `/health` named the same partition in its bake report.

The bake sweep's population is a live `nodeType:NodeType partitions:all` query, so it enumerated
NodeType rows for that dead partition. Nothing asked whether those rows still had a store behind
them — and the sweep's one absence test, `DynamicTypePreWarmer.TypeNodeExists`, is a listing.

## Two doors, both dead ends

The damage arrives differently depending on which driver the pod uses, and it is worth separating
them because only one of them matches the issue's title:

| Path | What happens | Cost |
|---|---|---|
| **activation-driven** | the `SubscribeRequest` routes to a partition hub with no store behind it and is never answered | a FULL per-type budget (5 min at the default) per row, ending as `TimedOut` |
| **batch-driven** (what a readiness-gated pod uses) | the compile's state write into that partition fails `OwnerUnreachable` | a `Faulted` **image verdict** |

Neither is rescued by what exists:

- A `TimedOut` is correctly **non-gating** — but `BakePhase.Running` withholds readiness for the
  whole sweep, so the pod is out of rotation for the budget times the number of dead rows. That is
  the 48 minutes. Note what this means: *the leniency already in the gate does not help*, because
  what holds readiness is the sweep not being FINISHED, not the verdict it reaches.
- A `Faulted` **is** an image verdict, and the rescue built for exactly this shape
  (`ReclassifyAbsent`, which turns a verdict against a node that no longer exists into `Removed`)
  asks `TypeNodeExists`. Same index, same row, answer "still there". The verdict stands, and the gate
  refuses readiness **forever**.

## The independent witness

There is exactly one party in the mesh that knows the **store** rather than the index:
`IPartitionStorageProvider.PartitionExists`. It was already used by the write guard that enforces
"no partition, no write", and `PathResolutionService` already held a private vote over it to decide
whether to synthesize a placeholder partition root. That vote is now
`PartitionExistenceProbe`, shared by both callers so the two cannot drift about what *gone* means.

**Absence is confirmed, never inferred.** A partition is confirmed absent only when at least one
writable provider says `false` — it knows its store and the partition is not in it — **and none says
`true`**. Read-only seeds own no per-partition store and are excluded. Every other answer is
indeterminate and **fails open**.

### 🚨 Fail-open is not caution, it is the only safe direction

A false *absent* would make a pod skip real NodeTypes and then serve a mesh it never baked — strictly
worse than the defect being fixed, and silent in a way the original is not. So each of these must
read NOT-absent, and each has its own case in the test:

| Shape | Why it is not absence |
|---|---|
| a provider with no per-partition store answers `null` | it cannot answer |
| one provider says `false`, another says `true` | contradicted |
| the probe throws | a probe that threw has not answered |
| the probe never emits | a bound expired; a bound is not an answer |
| there are no writable providers at all | nobody owns a store to miss it from |

## The ordering is the fix

This is the part that distinguishes a fix from a band-aid here, and it is easy to get wrong:
reclassifying a type **after** it has been warmed does not help. Afterwards, the per-type budget has
already been spent, or the verdict has already been formed and is self-confirming. **The question is
worth asking only in front of the work.**

So the probe runs once, before the sweep, over the distinct partitions of the **pending** set — the
types that would actually be warmed. The per-partition probes run together, so the whole answer is
bounded by one probe budget rather than by the number of partitions: seconds, in front of work that
would otherwise spend minutes per row discovering the same fact one row at a time.

A type in a confirmed-absent partition is reported `PreWarmStatus.Removed`, and deliberately not
given a new status. `Removed` already means *which nodes the mesh holds is a property of the mesh,
not of the framework being rolled out*, which is exactly the claim here — so it inherits the
non-gating treatment in `NodeTypeBakeGateState`, the `UpstreamContentBroken` cascade to dependents,
and its place in the report, instead of each of those needing to be taught a new member.

**The row is named, never deleted.** A boot sweep that pruned mesh data on the strength of a probe
would be a far worse failure than the one this closes. The log line names the partition so an
operator can clean it up; that is where the responsibility stops.

## What this does NOT fix

Stated plainly, because the issue reported two defects and this page closes one of them:

- **The stale rows are still there.** Nothing prunes a NodeType catalog row when its partition is
  dropped. `PartitionDropPostDeletionHandler` drops the partition's schema and its
  `Admin/Partition/{id}` definition and stops there; no hook invalidates a catalog listing, a bake
  report, or a pre-warm population. This change makes the rows **harmless to the readiness gate**,
  not absent.
- **A partition dropped mid-sweep** is not covered: the probe is one reading taken before the first
  type is warmed. The window is small and the consequence is the pre-fix behaviour for that one
  sweep.
- **Other consumers of the same population** — anything else that enumerates
  `nodeType:NodeType partitions:all` and then touches what it finds — are not fixed by this. The
  witness is now shared and cheap; the question has to be asked.

## The generalisation

Whenever code decides *"is this thing still there?"*, ask where the question's answer comes from:

> **If the candidate and its disproof come from the same source, you have no test at all.**

This shape has bitten more than once. A NodeType issue closed on a `Not found` and re-filed
unchanged weeks later is the same error from the reader's side rather than the code's — and
[Search Coverage and Refusal](../SearchCoverageAndRefusal) is the general case: a `Not found` from a
row you cannot see closes nothing, because the instrument and the population are the same thing.

## See also

- [CQRS and Content Access](../CqrsAndContentAccess) — the listing-then-read rule, and why a point
  read of an absent node is a framework defect
- [Postgres Schema Architecture](../PostgresSchemaArchitecture) — one schema per partition, which is
  what a drop actually removes
- [Search Coverage and Refusal](../SearchCoverageAndRefusal) — stating a denominator with a zero
- [NodeType Compilation](../NodeTypeCompilation) — what the bake sweep is for
