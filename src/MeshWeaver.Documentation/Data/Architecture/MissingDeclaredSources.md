---
Name: Missing Declared Sources
Category: Architecture
Description: Emptiness measured on the UNION is invisible for any NodeType that also draws on a shared library — so a type whose own Source subtree was gone reported itself as broken C#, named three symbols that live in nodes nobody has, and had the identical doomed compile taken again on every pod boot for four days. The per-entry coverage that names it, and why declining to repeat it is a classification rather than a retry cap.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h7l2 2h9v9a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z"/><line x1="9" y1="14" x2="15" y2="14"/><line x1="12" y1="3" x2="12" y2="7" opacity="0.35"/></svg>
---

# Missing Declared Sources

A NodeType declares where its code lives. When one of those declarations matches **nothing**, the
compile still runs — against a source set short of what the type asks for — and Roslyn, shown less
than the whole, reports exactly what a broken program looks like.

```
rbuergi/OperationRequest      compilationStatus: Error   since 2026-09-06
  CS0246  The type or namespace name 'OperationRequestContent' could not be found
  CS1061  'MessageHubConfiguration' has no 'AddOperationRequestControlPlane'
  CS1061  'LayoutDefinition'        has no 'AddOperationRequestLayoutAreas'
```

Every one of those three names is one of the type's **own** `Source/*` nodes, and that partition
does not hold them:

```
search 'namespace:rbuergi/OperationRequest scope:subtree'    → 0
search 'namespace:Essentials/OperationRequest scope:subtree' → 21   (14 Release + 5 Source + 2 Test)
```

Nothing said so. The reader was handed three missing symbols and went looking for them in module
surfaces that never carried them — for four days, while every pod boot burned another Roslyn compile
producing the identical diagnostics (issue #3903).

## Why the existing mechanism could not see it

[Source-Set Establishment](../SourceSetEstablishment) is the rule that a compile whose source set was
not *established* is not a verdict about the code, and `PreWarmStatus.NoSources` is the rule that a
source set which is established and **empty** is a fact about the mesh's CONTENT, not about the
image. Both are right. Both measure the **union**.

`rbuergi/OperationRequest` declares six source entries:

```json
"sources": [
  "namespace:Source scope:subtree",       // its own Source subtree  → 0 nodes
  "shared=@Store/Core/Source",            //
  "shared=@Store/Licensing/Source",       //  41 nodes between them
  "shared=@Store/Publishing/Source",      //
  "shared=@Store/Coupon/Source",          //
  "shared=@Store/Install/Source"          //
]
```

The union is 41 nodes. It is established. It is not empty. Every emptiness test in the pipeline reads
healthy, and the one query whose emptiness explains the failure is invisible to a count.

> **Emptiness is a per-QUERY fact and it was being measured on the union.** For any NodeType that
> composes — which is every type that reuses a library — that measurement cannot reach the case it
> exists to catch.

## The unit is the authored ENTRY, not the expanded query

`CodeQueryResolver.Expand` turns one `@X` shorthand into **two** queries: a `path:X` exact match and
a `namespace:X scope:subtree` folder match. The exact one matches nothing whenever the folder node is
not itself a Code node — which is the normal, healthy case. Measured per query, every
`shared=@Lib/Source` entry in the fleet would be reported as half missing and the signal would be
worth nothing.

So the unit is the line the author wrote: *did anything this entry asks for exist?* That is also the
only unit anyone can act on.

**Test entries are excluded.** The default `namespace:{path}/Test scope:subtree` matches nothing for
most types in a real mesh, by design — a type without tests is normal, not broken.

## Three answers, never two

`SourceCoverage.UnmatchedSourceQueries` (in `MeshWeaver.Compiler`) is a pure function over the
declared entries, the type's path and the paths its snapshot resolved. It answers in three shapes,
and collapsing any two of them re-creates the defect
[Controls That Cannot Fail](../ControlsThatCannotFail) is about:

| Answer | Meaning |
|---|---|
| `null` | **NOT DETERMINED** — no established snapshot, no type path, or not one entry the offline evaluator could read. Says nothing about the sources. |
| empty | Determined: every evaluable declared entry matched at least one node. |
| non-empty | These declared entries answered and matched nothing. |

An entry the evaluator refuses — free text, which routes to a vector index a pure function cannot
reproduce — contributes to **neither** side. Reporting it would be an accusation from evidence
nobody has; counting it as satisfied would be the "checked and clean" lie.

The evaluation itself reuses `NodeSetQuery`, the mesh-free evaluator of the closed four-selector
grammar the resolver emits, through a path-only overload that deliberately **ignores** the
`nodeType:` conjunct. Dropping a conjunct can only make a predicate more permissive, so the derived
answer can under-report a missing entry and can never accuse one that was fine.

## What it changes

### 1. The compile says which query matched nothing

- **The activity log** gets a warning above the Roslyn output, keyed
  (`activity.compile.declaredQueryMatchedNone`) and therefore translated, naming the entries and the
  count.
- **`NodeTypeDefinition.CompilationError`** leads with the diagnosis instead of leaving the reader
  with three symbols and no direction. The Roslyn text is kept underneath: it is still the evidence,
  it just is not the explanation.
- **`NodeTypeDefinition.FailedSourceQueries`** carries the structured finding, as authored, so a
  reader (or an agent) gets the answer as data rather than by parsing an error string, and the
  compile-state satellite mirrors it. It is operational state: stripped on export, preserved from
  the live node on every upsert, cleared by a success.

### 2. The doomed compile is not taken again

`PreWarmStatus.DeclaredSourcesMissing` is a **content verdict**, filed exactly like
`PreWarmStatus.NoSources` and for the identical reason its own doc gives:

> which nodes a mesh query matches is a property of the mesh, not of the framework being rolled out,
> so no image caused it and no rollout can fix it

so it never gates a rollout, and dependents inherit `UpstreamContentBroken` rather than the gating
`UpstreamFailed`. And both drivers stop re-attempting it:

- **The activation re-drive** (`HasStaleFailureVerdict` → `IsUnconvergableSourceFailure`) declines.
  Its trigger is "the compile INPUTS moved", and a new framework or module set moved an input that
  cannot change this answer.
- **The batch bake**, which decides what to build from a store probe and maps a standing `Error` to
  `BakeState.PreviouslyBroken` ⇒ `NeedsBake`, returns the classification without running Roslyn — but
  only when **two independent witnesses agree**: the type's persisted snapshot *and* the set this
  bake resolved for itself. The record alone would latch, because the sources watcher that maintains
  it may never have run on a batch-baking pod; the resolved set alone would let a starved discovery
  pass file itself as content drift (#1216). Either witness saying the sources are there compiles.

🚨 **This is a classification, not a cap.** Three properties make the difference, and each one is a
control in `ADoomedCompileIsClassifiedNotRepeatedTest`:

1. **The first attempt always runs.** The condition requires a **standing** `Error` — a verdict this
   deployment has already measured — so the coverage is never used to assert a failure nobody
   observed. A type reaching this state compiles, fails honestly, records the finding, and only then
   stops repeating a measurement whose inputs cannot have moved.
2. **It converges instantly and by itself.** The decision is recomputed from the **live** snapshot on
   every emission. The moment the missing nodes land, the coverage is empty and the type is
   re-driven — sooner than before, because it no longer has to wait for a framework change to be
   noticed. There is no field to clear and no budget to refund.
3. **A second witness keeps the gate intact.** `LastCompileSucceededAt` must be set: the sources must
   have been **lost**, not never present. A type that has never built cannot have lost anything — its
   failure may be its own `Configuration`, and that one keeps earning its attempt and keeps refusing
   readiness. Same discriminator, for the same reason, as `NoSources`.

And it says so out loud. The stuck-type diagnostic no longer offers a remedy that cannot work:

```
NodeType rbuergi/OperationRequest is STUCK at Error with no compiled assembly, and it CANNOT
CONVERGE: MISSING SOURCES: 1 of 6 declared source queries … matched NO nodes on this mesh
('namespace:Source scope:subtree'). … Nothing will retry it until those source nodes exist
(which re-drives it automatically) or someone requests a build — a redeploy or a module update
will not, because neither can supply a symbol that lives in a node this mesh does not hold.
```

## Where the orphan came from — and what is NOT fixed here

A NodeType definition reached a user partition without its `Source/**`. The copy is core's, and both
implementations have the same shape:

- `NodeCopyHelper.CopyNodeTree` (`src/MeshWeaver.Graph/NodeCopyHelper.cs`) — reached by the MCP
  `copy` tool, the UI Copy / Import dialogs and "copy to home". It enumerates the source subtree **as
  the caller**, then writes every node through `.Merge(16)` with **no parent-before-child ordering**,
  **no rollback** on a failing child, and **no post-condition** comparing what landed against what
  was enumerated. One failing child leaves everything already written in place and never attempts the
  rest; a caller-scoped enumeration that returns only the root reports success.
- `MeshExtensions.HandleCopyNodeRequest` writes the root strictly first, then the children as
  independent creates, and its completeness guard (`RequireComplete`, which compares the enumeration
  against `ListDescendantPaths` **before** writing anything) is set only by the Move leg. A plain copy
  has none.

Both are in **this repo**, not in MeshWeaver.Plugins — whose own installer solved these three
problems explicitly (parents-before-children ordering, a System-scoped enumeration because gated
packages hide children from subject-scoped queries, and a batch-shortfall post-condition). And a copy
writes no install record, so `InstallCompleteness` — the platform's one partial-install detector —
cannot see it either.

**That was a separate defect with its own design decisions** (ordering, rollback semantics, the
identity a subtree enumeration runs under). This page's change makes its consequence *legible and
cheap* instead of silent and repeated; it does not stop the half-copy from happening.

**`NodeCopyHelper.CopyNodeTree` is now fixed, and the design is
[Copy Completeness](../CopyCompleteness).** Both halves were reproduced deterministically first, and
only one of them was silent — which is worth recording, because the obvious framing is wrong:

```
A. a descendant the caller may not read
     subject-scoped enumeration = 5 of the 7 nodes the subtree holds
     copy RETURNED count = 5                     <- reported success
     …/Pkg/Restricted   ABSENT                   <- silently left behind
B. one write refused in the middle
     copy THREW, naming ONE of five paths
     …/Pkg/Source         ABSENT
     …/Pkg/Source/Alpha   PRESENT                <- ORPHAN, under a parent that never landed
```

The copy now reads the subtree TWICE from the same query — as the caller, which is the read
row-level security filters, and as System, which is what turns "the listing came back short" from a
silence into a number — and refuses before writing anything when the two disagree, stating counts
rather than the paths it cannot show. Writes then go level by level so a failure leaves a
well-formed tree, and every enumerated node ends with an acknowledgement, so the report names what
did not land instead of one path out of five.

**`MeshExtensions.HandleCopyNodeRequest` still has the shape described above** — its
`RequireComplete` guard remains wired only to Move — and a copy still writes no install record, so
`InstallCompleteness` still cannot see one. Both are named as residuals on that page.

## Residuals, named rather than left to be rediscovered

- **The classification does not run for a type whose path is unknown.** Every production call site
  passes it; the pure overloads without it fall back to the pre-#3903 behaviour, which is the safe
  direction — an extra compile costs a compile, a wrongly declined one strands a type.
- **A configuration-only type that legitimately owns no Code and is broken by an IMAGE change reads
  as content-broken and does not gate.** Inherited knowingly from `NoSources`, whose doc concedes the
  same case for the same reason, and bounded by the same second witness.
- **`ApplyGateSettle` does not restamp the finding.** It settles a Pending flip that never ran Roslyn,
  so the previous verdict's finding is still the true story — the same reasoning `ApplyCompileFailure`
  applies to `BuildProvenance`.
- **The `@@`-include closure is still not covered**, exactly as `NodeTypeSourceFingerprint` records:
  an include pulls a Code node no source query matches, so it is in neither the snapshot nor this
  coverage.

## Related

- [Source-Set Establishment](../SourceSetEstablishment) — the union-level rule this refines: a set that could not be established is not a verdict at all
- [Node Type Compilation](../NodeTypeCompilation) — what a compile does, and what its status means
- [Retiring a NodeType](../RetiringANodeType) — the deliberate version of "the sources are gone"
- [Dangling NodeTypes](../DanglingNodeTypes) — the instance-side sibling: a node whose TYPE resolves to nothing
- [Controls That Cannot Fail](../ControlsThatCannotFail) — why "not determined" and "checked and clean" must never share a shape
- [Copy Completeness](../CopyCompleteness) — the copy that produced this orphan, and the two readings that stop it
