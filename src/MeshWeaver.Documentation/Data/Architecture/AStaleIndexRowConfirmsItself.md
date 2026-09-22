---
Name: A Stale Index Row Confirms Itself
Category: Architecture
Description: An index row and the listing that would disprove it come from one source, so absence is not merely unproven but unaskable. The storage providers are the only independent witness — and the answer is worth nothing unless it is asked before the work. And when the rows outlive the ROOT rather than the store, no witness can see it, because the platform re-roots the partition; what was missing there was a verb.
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

## 🚨 A probe in front of the work can VOID the work — `CombineLatest` and the empty completion

Putting a question in front of a pipeline means the pipeline is now composed **behind** it with
`SelectMany`. That is a liveness coupling, and it has a sharp edge that is easy to introduce and
invisible once shipped:

- `Observable.CombineLatest` **never emits** if *any* source completes without emitting.
- `Timeout` does **not** fire on an empty completion — it bounds silence, not absence.

So a single provider that completes empty — contract-breaking, but a provider is an extension point
and the contract is prose — makes the whole probe complete silent. The sweep behind it then produces
**no outcomes and completes normally**, which the hosted service marks Complete and the gate
certifies. A pod would report a clean bake it never performed: precisely the laundering
`WarmDynamicTypes` faults an enumeration error to avoid — *"finding nothing is not passing"* — reached
through a brand-new door.

The rule, and it is the same one `HostedHubsCollection.CloseScopeWhenDisposed` already states for a
hub's terminal signal: **an answer that can never arrive is settled from the known-terminal state
rather than parked.** Every stage gets a `DefaultIfEmpty` — the per-provider probe (indeterminate),
and each composite (the empty set) — so the question can be unanswerable but never absent.

The two failure modes are worth separating, because they are pinned by different assertions:

| Missing backstop | Symptom | What fails |
|---|---|---|
| the per-provider one | one empty probe **suppresses** a provider that did answer | a wrong VALUE — an empty set where a partition was denied |
| the composite ones | the probe completes silent | NON-EMISSION — and the sweep behind it does nothing |

A test that only asserts the *value* misses the second; a test that only asserts *emission* misses the
first. One case covering both is `OneContractBreakingProvider_CannotVoidTheAnswer`: it requires an
answer **and** requires it to be the answering provider's.

## The second defect: the rows outlive the ROOT, and the platform re-roots them

The store witness answers exactly one question — *was the schema dropped?* — and that is the state
a **completed** teardown leaves. It is not the state the incident's own partition is in.

Measured on the control instance with the first fix already live (image `c55301f3`, replica booted
2026-09-22 09:03Z):

- `get @Admin/Partition/UWDeepfield` → the record **still exists** (`version: 4`, last modified
  2026-07-09). `PartitionDropPostDeletionHandler` deletes that record as its second step, so the
  teardown never ran for this partition — and nothing dropped its schema.
- `/health` → `bake-report` on that replica still lists `frameworkstale in … UWDeepfield/…` among
  230 enumerated types; the sweep completed with 0 timed out. The rows are in a store the sweep, as
  System, can read, and the store witness — truthfully — confirms nothing absent.
- `get @UWDeepfield` and `path:UWDeepfield` answer nothing from a viewer's seat — but so do they
  for `BinaryClickerV2`, a partition with a live NodeType the same census names, so this separates
  nothing (see [Search Coverage and Refusal](../SearchCoverageAndRefusal)).

That is the **orphan** the delete pipeline names itself, at `Critical`, whenever a partition root
is deleted with no teardown handler matching it: *"the schema is now ORPHANED … drop partition
manually"*. Before the structural teardown existed (#3436) that was every partition root of a type
other than `Space`/`User`; the schema, every row in it and the `Admin/Partition` record were all
left behind.

### Why no witness can see an orphan — by the platform's own definition

There is no store to deny, so the first witness is silent. And a root-row witness — *"a partition
is its top-level node; a listing that does not hold the root is a real negative"* — was tried on
this change and retracted, because it contradicts a pinned invariant: `EnsurePartitionBootstrap`
heals a missing root on the first child create into a partition whose store exists
(`AMissingRootOverALivePartition_IsStillHealed`, #638/#902), and a System-attributed create counts.
The compile watcher cutting a `Release` node for one of the surviving NodeType rows — measured on
the control instance for `UWDeepfield/Section` — is exactly such a create. So the platform
**re-roots** an orphan under System, with no access grant (there is no creator to grant), and the
partition comes back as a `Space` shell that nobody can see, own or delete. From then on every
instrument reports it live: the store is there, the root is there, the record is there, and the
bake sweep compiles its NodeTypes on every roll, on every replica. "Hasn't existed in ages" and
"exists to every instrument" are both true, and no probe the sweep could ask tells them apart.

Nor should it: which partitions a mesh holds is not the bake's decision. What was missing was a
**verb**.

### The verb: deleting the partition's RECORD finishes its teardown

The record — `Admin/Partition/{partition}` — is the one artefact that outlives the data *by
design*, it lives in the `Admin` partition, and a platform admin reaches it with the ordinary
delete. Deleting it already meant "this partition is gone"; it simply did nothing.
`StrandedPartitionRecordTeardownHandler` makes it do what the root delete would have done — the
same `PartitionDropPostDeletionHandler.DropStores` on every provider, the same cache eviction,
under the same tombstone the ordinary teardown holds so a concurrent child write cannot heal a root
back mid-drop (#3451):

```
delete @Admin/Partition/UWDeepfield
```

🚨 **Only for a partition that is STRANDED**, and that is what keeps a platform admin a platform
admin rather than a data superuser. The drop runs in exactly two shapes, both of which no owner can
reach through the partition's own root:

| Shape | What it is | Why nobody else can delete it |
|---|---|---|
| **no root at all** | no durable row and no static root at the partition path | the ordinary delete needs a root |
| **a healed shell** | a root carrying exactly the heal's fingerprint — `Space`, named after the partition, no content, created by System — with **no access grant** (durable or static) and no GitSync configuration | nobody was granted anything on it, and it can only have got that way by the bootstrap re-rooting an orphan |

Anything else — a real root, an owner, a synced partition, a static partition — is live: the store
is **not** touched, and the record delete is reported at Warning (a live partition's record
disappearing is already a defect worth a line — it leaves the partition out of every listing and
out of the routing prime; the remedy is to delete its root, which runs the structural teardown).
Every probe fails **closed**: an unreadable root or grant listing reads as *live*. And the handler
stands down while the partition's own deletion is in flight or on record, so the ordinary teardown
— which deletes this same record as its second step — never drops twice.

`StrandedPartitionRecordTeardownTest` pins all of it: the rootless shape drops, the ownerless shell
drops, a live plugin-like partition keeps its store, a shell with one grant keeps its store, the
ordinary teardown records exactly one drop, and the shell fingerprint is asserted member by member.

After the roll that carries it, the enumeration stops naming `UWDeepfield` the moment its record is
deleted: the schema goes, the rows go with it, and the next boot's population cannot see them —
while a replica that is still running keeps only what it already holds, which the store witness
above now files as `Removed` on its next sweep.

## What this does NOT fix

Stated plainly, because the issue reported two defects and this page now covers both — what the
sweep reads is validated against the store, and what no witness can see has a verb — and not the
rest:

- **The verb is an operator's.** Which partitions a mesh holds is a property of the mesh, so
  nothing here decides for them: the orphan stays enumerated, and compiled, until an admin deletes
  its record. `PartitionTeardown` → *Auditing a portal for orphans* is how to find them.
- **A running replica's standing queries are not told.** A completed teardown removes the rows from
  the store, so a fresh replica's population cannot see them; but `PartitionDropPostDeletionHandler`
  evicts only the security-anchored synced queries (`SecurityQueries.PartitionAnchoredQueryIds`),
  and a mesh-wide fold such as the live record census learns of a row only through a per-row change
  event, which `DROP SCHEMA` does not emit. Whether a real teardown leaves such a row behind in a
  fold — the recursive delete does emit a delete per row it can see — was **not measured**; it is
  named here so the next reader does not have to rediscover the question.
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
- [Partition Teardown](../PartitionTeardown) — the structural teardown, how to audit a portal for
  the orphans that predate it, and the record delete that finishes one
