---
Name: Repairing a Stale MainNode — When the Broken Field Guards Itself
Category: Architecture
Description: Why a node whose MainNode points into another partition is invisible to every listing and refuses to be corrected, and how the detection-driven repair fixes both shapes without deleting anything.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></svg>
---

# Repairing a Stale MainNode

`MainNode` is the field that says **which node a row belongs to**. When it equals the row's own
`Path`, the row *is* a main node — that is the framework's literal definition, and `is:main` is SQL
`n.main_node = n.path`. When it differs, the row is a satellite of whatever it names.

A third state exists that is neither, and it is a defect: `MainNode` naming **this node's own id in a
partition the node no longer occupies**. The row is Active, fully formed, returned by `get` — and
absent from every listing, because it is not a main node by the only test anything applies.

This page is about repairing rows already in that state. The mint sites that produced it are fixed
(see [How the shape is minted](#how-the-shape-is-minted) below); this is the other half.

## What it looks like

```
Hosting/Skill/deployment    mainNode → Skill/deployment
Skill/deployment            mainNode → Hosting/Skill/deployment
```

Both Active, byte-identical instructions, created a week apart. `get @Hosting/Skill/deployment`
returns the node in full. `search nodeType:Skill limit:200` does not list it. Neither does
`path:Hosting/Skill scope:descendants`, which returns only the one healthy sibling.

Nothing errors. Nothing logs. No status flips. The only symptom is absence.

## Two properties make this defect unusual

### 1. The broken field is the field that decides who may fix it

Authorization resolves against `MainNode`, not `Path`. A node pointing into another partition has
its permission check answered by **that partition's** scope, so the obvious correction is refused:

```
patch @Hosting/Skill/deployment {"mainNode":"Hosting/Skill/deployment"}
→ Access denied: user 'rbuergi' lacks Update permission on 'Hosting/Skill/deployment'
```

The corruption protects itself. Here it fails closed, which is the safe direction — but nothing in
the mechanism guarantees it always will, and *"a node whose `MainNode` is wrong delegates its own
authorization to a partition it does not belong to"* is worth reading on its own terms next to
[Access Control](/Doc/Architecture/AccessControl).

The repair therefore runs under `RunAsSystem` — the sanctioned *legitimate infrastructure* case from
[Access Context Propagation](/Doc/Architecture/AccessContextPropagation). 🚨 `RunAsSystem`, never
`Observable.Using(access.ImpersonateAsSystem, …)`: Rx runs a `Using` factory on the subscribing
thread and disposes it on the terminating one, latching System onto whatever the subscriber does
next.

### 2. Detection cannot use a query

The condition's own definition is *"invisible to the index"*. `is:main` keeps exactly
`main_node = path`, and Postgres' `search_across_schemas` hard-filters **every union branch** on that
predicate — unconditionally, not only when the caller asks for `is:main`.

So a query-driven detector would pass its tests against a permissive in-memory provider and find
**zero rows on the deployment that actually has the corruption**. That is the
green-signal-measuring-the-wrong-thing trap in its purest form.

Enumeration runs on `IStorageAdapter.ListChildPaths` / `ListDescendantPaths` instead — the
authoritative path-routed tree walk the recursive-delete planner is built on. It routes by **path**,
which is intact, so a wrong `MainNode` cannot hide from it.

## Both shapes, and why the detector does not care which

The original report described a mutual **cycle**: A names B, B names A. That is real, but it is not
the whole population — a measurement on 2026-09-01 found that two of the seven were **dangling**
(the named node does not point back), and an eighth node shared the signature and was missing from
the report's list entirely.

A repair driven by the reported list would have left that eighth node corrupt; one written for
cycles would have skipped the dangling pair or faulted looking for a partner that was not there.

**So the repair detects a condition rather than consuming a list.** The predicate —
`MeshExtensions.IsStaleSelfDefaultMainNode` — is a **per-node shape test**:

- `MainNode`'s **last segment is this node's own id** (which is what makes it a frozen self-default
  rather than a deliberate pointer at some other node), **and**
- it names a **different partition** (first segment).

Both halves are required. Either alone over-reaches: a node may legitimately point `MainNode` at a
parent inside its own partition, and it may legitimately point at another partition under a
different id. Both are left untouched.

Because the test is per-node, every affected row is found by construction, a partner is never
required to exist, and repairing one end of a cycle never depends on the other — the other is found
and repaired on its own merits in the same pass. What the pointer points at is read **only to
classify the finding for the report**, never to decide whether to repair:

| Shape | Meaning |
|---|---|
| `Cycle` | The named node exists and points back at this one. |
| `DanglingMissingTarget` | Nothing exists at the named path. |
| `DanglingUnrelatedTarget` | The named node exists but names something else. |
| `Unclassified` | The named node could not be read. **Repaired anyway.** |

🚨 That classification read goes through `IStorageAdapter.Read` (null-on-absent), **never**
`GetMeshNodeStream(target)`. A point stream read of a node that does not exist terminates the stream
with a routing NotFound *and* opens the storm breaker on that path — and the breaker fast-fails
writes too. On a dangling finding, where the target is absent by definition, classifying through the
stream would suppress the very repair the pass is there to perform.

## One predicate, shared with the forward fix

`IsStaleSelfDefaultMainNode` is the **same method** the create and upsert paths apply to every node
they write. Two predicates would drift, and the drift would be invisible in exactly the way this
defect already is: a repair that skipped a shape the guard refuses would leave rows corrupt with
nothing reporting it. Widening the shape widens both halves at once.

## The write

```csharp
access.RunAsSystem(() => hub.GetMeshNodeStream(path)
    .Update(current => current with { MainNode = current.Path }))
```

`GetMeshNodeStream(path).Update(…)` is not merely the preferred route, it is the **only** one that
can express this correction. A full-instance upsert can move a `MainNode` anywhere *except* back onto
the node's own path, because that intent is indistinguishable from the untouched default —
`MainNode` is non-nullable and self-defaults, so "the writer never touched it" and "the writer set it
to this node" are the same value on the wire. See `MeshNode.HasExplicitMainNode`, and
[the mutation API](/Doc/Architecture/DataAccessPatterns).

## The idempotence contract

The repair writes the one value the predicate **cannot match**: after `MainNode = Path`,
`HasExplicitMainNode` reads `false` and `IsStaleSelfDefaultMainNode` returns `false` at the first
test.

That is the whole mechanism, and it matters that it is a *property of the written value* rather than
a ledger:

- A second run enumerates the same paths, reads the same rows, and finds nothing. It does not
  remember having run — there is nothing left to find.
- A mesh with zero affected nodes performs zero writes.
- A run that fails partway is safe to repeat: an unrepaired node still matches, a repaired one does
  not.
- Concurrent runs converge — both compute the same value.

## Reporting: evidence, not a count

Every finding carries the path, the pointer as found, the classified shape, and the outcome
(including the error message when a write failed). A failed node does not stop the rest, and it comes
back **named** rather than as a swallowed exception, so a run against a live portal produces
something a maintainer can act on.

Every pass ends with one summary line whether or not it found anything — *"the repair never
considered the row"* and *"the repair found nothing"* must not look the same.

## Using it

```csharp
// Measurement. Writes nothing. Safe on production.
StaleMainNodeRepair.Detect(hub).Subscribe(r => …);

// The repair. Scope it to partitions, or omit for the whole mesh.
StaleMainNodeRepair.Repair(hub, ["Hosting", "Store"]).Subscribe(r => …);
```

Both return **cold** observables — the sweep runs on `Subscribe`, not on the call.

🚨 **Nothing self-arms.** This is a static one-shot pipeline, not an `IHostedService`, and nothing in
the composition root calls it: deploying an image containing it runs no repair. Running it against a
live portal is a separate, deliberate decision, and `Detect` exists so that decision can be taken on
measured evidence first.

This is the same shape `AssemblyCacheRetentionHostedService` uses for its own destructive sweep —
report by default, act only when explicitly armed — and it is the reason the raw `psql UPDATE`
alternative is not on the table: a direct SQL write bypasses the workspace cache, so live
subscriptions keep serving the old value until the portal restarts. See
[Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture).

## The unresolved duplicate-copy question

**The repair deliberately does not answer it, and deletes nothing.**

A cycle is two Active copies of one node — `Skill/deployment` beside `Hosting/Skill/deployment` —
with identical content, created a week apart. Re-stamping both makes both visible, which is correct
as far as pointers go and leaves the real question open:

> Why does a package skill have a second Active copy in the platform `Skill/` partition at all?

That is likely the mechanism that produced the cycle in the first place — two writers each rebasing
the other's half — so repairing pointers without answering it invites a recurrence. But deciding
which copy is canonical, and whether the other should be deleted, is a judgement about **content**,
not a mechanical one. The repair re-stamps `MainNode = Path` on both ends and leaves both copies in
place.

A `Detect` pass is the input to that decision: its `Cycle` findings are exactly the duplicate pairs.

## How the shape is minted

`MainNode` is a **stored** property; `Path` and `Segments` are **computed**. So a plain
`with { Namespace = … }` moves the path and leaves `MainNode` naming the partition the node was born
in — and because the field is non-nullable, that stale value is indistinguishable on the wire from a
deliberate satellite pointer, so every upsert faithfully persists it.

The fix at the mint sites is `MeshNode.WithPath(id, ns)`, which rebases the pointer when it was the
self-default and carries a deliberate one across untouched. The create and upsert paths additionally
re-stamp anything that arrives with the shape, so no new row can acquire it.

Neither of those touches a row already stored: the defect moved out of the code and into the data,
and only a pass over the data closes it. `SelfTypedDeclarationDurableRepair` exists for the same
reason and draws the same distinction — a write guard can only ever refuse the *next* bad row.

🚨 **Restoring `MainNode == Path` is necessary but not sufficient** for a decentral node to be
searchable again. A second, independent defect produces the identical symptom: a query union whose
legacy single `Query` field carries only `list[0]`, so a static node matched by the *second* query is
silently absent. Both halves are required; neither alone restores visibility.

## Related

- [CQRS and Content Access](/Doc/Architecture/CqrsAndContentAccess) — why a query is the wrong tool
  for reading, deciding on, or gating a specific node.
- [The MeshNode Stream Cache](/Doc/Architecture/MeshNodeStreamCache) — the handle the write goes
  through, and the storm breaker that makes an absent-node point read a defect.
- [Access Context Propagation](/Doc/Architecture/AccessContextPropagation) — when running as System
  is legitimate, and how the scope is sealed.
- [Postgres Schema Architecture](/Doc/Architecture/PostgresSchemaArchitecture) — one schema per
  partition, and why raw SQL is not the repair route.
