---
Name: Copy Completeness
Category: Architecture
Description: A copy asserts a set equality it never established — that what it enumerated is the source, and that what it wrote is what it enumerated. Both shortfalls read as a count, so a subtree could half-land and report success. The two readings that turn a silence into a number, why the failure stops instead of rolling back, and the measurement that produced the orphan.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15V5a2 2 0 0 1 2-2h8"/><path d="M13 15h4" opacity="0.35"/></svg>
---

# Copy Completeness

A copy is a **set** operation: after it, every node the source subtree holds must have a node at its
target path. `NodeCopyHelper.CopyNodeTree` reported an `int` — the number of writes that succeeded —
and nothing anywhere established either half of the equality that number was standing in for.

> **A count cannot say which member of a set is missing.** It cannot say that the set enumerated was
> not the source, and it cannot say that the set written was not the set enumerated. Both shortfalls
> therefore arrive as a number, and a number reads as success.

The consequence is the orphan recorded in [Missing Declared Sources](../MissingDeclaredSources):
`rbuergi/OperationRequest` on memex.meshweaver.cloud, a NodeType whose entire `Source/` subtree is
absent — `search 'namespace:rbuergi/OperationRequest scope:subtree'` → **0**, against **21** for the
package it was copied from — which then took the identical doomed Roslyn compile on every pod boot
for four days and reported three missing symbols that live in nodes nobody has.

## What was measured

Both halves reproduce deterministically on an in-memory mesh with row-level security on
(`ACopyThatCannotCompleteSaysSoTest`). The subtree is a package root, a readable branch and a branch
the copier holds a role DENY on; the copier can read the root and not that branch.

```
A. a descendant the caller may not read
     subject-scoped enumeration = 5 of the 7 nodes the subtree holds
     copy RETURNED count = 5                          <- reported success
     …/Pkg/Restricted        ABSENT                   <- silently left behind
     …/Pkg/Restricted/Beta   ABSENT

B. one write refused in the middle
     copy THREW "Copy of 'TestData/Pkg/Source' … failed"    <- names ONE of five paths
     …/Pkg                   PRESENT
     …/Pkg/Source            ABSENT
     …/Pkg/Source/Alpha      PRESENT                  <- ORPHAN, under a parent that never landed
     …/Pkg/Restricted        PRESENT
     …/Pkg/Restricted/Beta   PRESENT
```

**Only A was silent.** B *did* raise — so "the copy reports success either way" is not what was
wrong with it. What was wrong with B is that the exception named one of five paths, the merge
cancelled the observation of siblings whose writes had already been POSTED and landed anyway, and
the result was a target tree containing a node whose parent does not exist. Nobody could say which
nodes were where.

## The unit that replaces the count

`NodeCopyOutcome` carries a `NodeCopyStatus` plus one `NodeCopyEntry` per enumerated node.
`CopyNodeTree` — the count surface — is **derived** from it and errors on anything that is not
`Copied`, so the two cannot drift. That two-surface shape is instance 13 of
[Controls That Cannot Fail](../ControlsThatCannotFail): one return type serving a consumer who wants
a number and a consumer who renders a verdict is a compromise that is wrong for one of them.

| Status | Meaning |
|---|---|
| `Copied` | Every node the subtree holds has a node at its target path. The only success. |
| `SourceNotFound` | Nothing resolved at the source path. Nothing written. |
| `SourceNotFullyReadable` | The enumeration is short of what the subtree holds. **Refused before any write.** |
| `CompletenessNotDetermined` | Whether it was short could not be established. **Refused before any write.** |
| `Incomplete` | The writes ran and the target set is short. Every missing path is named. |

`CompletenessNotDetermined` exists for the reason that page gives, and collapsing it into either
neighbour re-creates the defect: *"I checked and it is short"* and *"I could not check"* must never
share a shape.

## 1. The enumeration's own completeness — the subtle half

The subtree listing runs **as the caller**, and that is not negotiable: it is the read row-level
security filters, and copying out from under it would let a caller duplicate rows they may not read
into a place they control. (`HandleCopyNodeRequest` states the same rule for its satellite sweep:
*"the cure is NOT to enumerate the copy from storage as well"*.)

But then the listing's shortness is a fact about the **reader**, not about the subtree — and the two
were the same value. An enumeration that returns `[root]` because the caller may not read the
children is indistinguishable from a subtree that has none.

**The size of the subtree is therefore read a second time, from the same query, as System.** That is
the shape MeshWeaver.Plugins' installer already uses and for the identical reason — a gated package
hides its children from a subject-scoped query. Sharing the query shape is load-bearing: the only
thing that can differ between the two answers is what the identity is permitted to see, so the
difference means only what it is being read to mean. Nothing from the System reading is ever copied;
only its size is taken.

A difference is a **refusal**, not a smaller copy. A subtree copied without part of itself is not a
smaller copy — it is a broken one, and `rbuergi/OperationRequest` is what that looks like four days
later.

🚨 **The refusal states COUNTS and never the paths.** The shortfall is exactly the set the caller may
not read, so naming it would turn a refusal into a disclosure surface — the same reason
[#3890](https://github.com/Systemorph/MeshWeaver/issues/3890) closed the autocomplete drill-down, which
worked by naming other people's node titles. An `Incomplete` report DOES name its paths: every one of
them came out of the caller's own enumeration, so it discloses nothing the caller cannot already see.

🚨 **And with no `AccessService` there is no second identity, so there is no answer.** Running the
"System-scoped" read unimpersonated would compare a set with itself and pass by construction — a
control whose green is guaranteed. That case is `CompletenessNotDetermined`, and it names what is
missing rather than shrugging.

## 2. Parents before children — a failure-atomicity fix, stated as one

Nodes are written **level by level**, deepest last, with the batch size in flight *within* a level. A
level that does not fully land **ends the descent**; everything below it is recorded as
`NotAttempted`.

**This is not a correctness fix for the happy path, and saying otherwise would be wrong.** Nothing in
the platform requires a parent to exist: no handler, no validator, no router and no storage adapter
probes one — Postgres has no foreign key, the in-memory adapter synthesises intermediate directory
levels on purpose, and the `NextLevel` query planner deliberately *skips* empty intermediate
segments. `HandleCopyNodeRequest` itself writes `A/B/C` before `A/B` routinely.

What ordering buys is the shape of the FAILURE:

- **The residue is a well-formed tree.** Every node that landed has every ancestor that landed with
  it. On the unfixed helper, measurement B left a leaf under a folder that never arrived.
- It honours what `IStorageAdapter.WriteMany` already states about ordering — *"callers order
  parents before children on purpose … activating a child's per-node hub while its parent's is still
  cold is the race that used to wedge installs"*.

**The alternative that was considered and rejected: pruning only the failed node's own subtree.** It
would carry a sibling branch further — in measurement B, `…/Restricted/Beta` is reported
`NotAttempted` although its own parent landed. But the operation is already known to be incomplete at
that point, and writing *more* nodes into a copy known to be broken buys nothing a caller can use
while costing a residue that is harder to reason about. Stopping is simpler, is deterministic, and
minimises what a failed copy leaves behind. The report names every stranded path either way, so
nothing about the choice is silent.

## 3. A post-condition over the ledger, not a re-read

Every enumerated node ends with an entry saying what the owning hub **acknowledged**: `Created`,
`Updated`, `SkippedExisting`, `Failed` or `NotAttempted`. The outcome is then a set comparison —
covered against enumerated.

Two details are deliberate:

- **`SkippedExisting` counts as covered, and is not counted as copied.** The property a copy has to
  guarantee is that the target path holds a node; a `force=false` skip satisfies it. The returned
  count has always meant "writes that happened", and a skip is not one — so the number this operation
  emits is unchanged for every copy that was already correct.
- **`Failed` means NOT ACKNOWLEDGED, never "not written".** A delivery that faults after the request
  left may well have landed. The report says what the copy was told, which is the only thing it
  knows — the same distinction [`NodeReadOutcome`](../DenialIsAnAnswer) keeps between Absent and
  Unavailable.

The ledger is assembled from the write acknowledgements rather than by querying the target back,
because a query is eventually consistent ([CQRS and Content Access](../CqrsAndContentAccess)) and
would answer about a moment that is not this one.

Collecting a per-node fault instead of letting it abort the merge is **not** a swallow: the fault is
recorded, it ends the descent, and it makes the whole operation fail carrying the full inventory.
What it removes is the merge cancelling the observation of siblings that had already been posted —
which is precisely why one path in an exception used to be all anybody could name.

## Why there is no rollback

An all-or-nothing outcome was the obvious fourth candidate and it is **not implementable here**:

- **There is no cross-partition transaction.** Each node is its own hub and its own row, possibly in
  another schema. "Undo the copy" is N more writes, on the path where the code is least trustworthy.
- **Under `force` a copy overwrites nodes it holds no before-image of.** A compensating delete could
  not restore them; it would destroy them. That is strictly worse than a half-copy.
- **The helper deleting a target is a known incident shape** — the race against the per-node hub's
  disposal that produced *"GetNode returns null after force-overwrite"*.

So the guarantee offered instead is: **all-or-nothing at the point of refusal** (§1 — nothing is
written at all), and **a well-formed prefix plus a complete inventory** when a write fails (§2 + §3).

## What is NOT fixed here

- **`MeshExtensions.HandleCopyNodeRequest` still has the same enumeration shape.** Its completeness
  guard (`RequireComplete`, a comparison against `IStorageAdapter.ListDescendantPaths`) is set only
  by the Move leg, for the reason its own doc gives — a plain copy deletes nothing, so carrying less
  loses nothing *at the source*. That reasoning covers data loss and not a half-landed target, so the
  gap is real; closing it is a separate change, because a plain copy's denominator has to decide what
  it does about satellites and `RequireComplete`'s does not (Move always carries them).
- **A copy still writes no install record**, so [Install Completeness](../InstallCompleteness) — the
  platform's one partial-install detector — cannot see a copy at all.
- **The live orphan `rbuergi/OperationRequest` is untouched.** Deleting production data in someone's
  partition is the maintainer's call.

## Related

- [Missing Declared Sources](../MissingDeclaredSources) — what a half-copied NodeType looks like from the compiler, four days later
- [Controls That Cannot Fail](../ControlsThatCannotFail) — why "not determined" and "checked and clean" must never share a shape
- [CQRS and Content Access](../CqrsAndContentAccess) — why the post-condition is built from acknowledgements rather than a read-back
- [Access Control](../AccessControl) — the fold that decides what a subject-scoped enumeration returns
- [Moving Nodes](../MovingNodes) — the sibling operation, whose completeness guard is wired and why
- [Install Completeness](../InstallCompleteness) — the detector a copy is invisible to
