---
Name: Source-Set Establishment
Category: Architecture
Description: A resolved source set of zero is ambiguous — it reads the same for "this NodeType owns no Code" and for "the discovery pass came back short" — and only a positive witness can tell them apart. The 2026-09-08 stall in which one boot of one portal resolved 91 fewer Code nodes than its neighbours, condemned four healthy NodeTypes, and refused readiness for the startup probe's full three hours.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 19V5a2 2 0 0 1 2-2h9l5 5v11a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2z"/><polyline points="14 3 14 8 20 8"/><line x1="9" y1="17" x2="9" y2="17"/><path d="M8.5 12.5 7 14l1.5 1.5"/><path d="m13.5 12.5 1.5 1.5-1.5 1.5"/></svg>
---

# Source-Set Establishment

A NodeType's compile is only a verdict about its code if the compiler was shown that code. When the
source set is short, Roslyn is still perfectly correct — it reports what it was given — and the
diagnostic it produces is indistinguishable from a genuine break:

```
CS0246  The type or namespace name 'CessionData' could not be found
CS0103  The name 'CessionSampleData' does not exist in the current context
CS1061  'LayoutDefinition' does not contain a definition for 'AddSocialMediaPostLayoutAreas'
```

Every symbol in those three lines is declared in the *same NodeType's own* `Source/` folder. The
code was never broken; the pass that went looking for it did not find it.

The platform already knows this failure mode: `SourceSnapshot` in `MeshWeaver.Compiler` exists to
keep "the set is empty" apart from "the set could not be established", and it says so at length. The
mistake this page is about is not the absence of that idea — it is that the batched bake's
implementation of it had a condition on it that switched it off for almost every NodeType in a real
mesh.

## The three shapes of zero

There is exactly one honest question to ask of a resolved set of size zero: **does the mesh agree?**

| Evidence | What zero means | Verdict |
|---|---|---|
| `CurrentSourceVersions` is `{}` (explicitly empty) | the sources were deleted, or the type is configuration-only | **Established.** Classify `NoSources`; must never gate a rollout — no image can change what a mesh query matches |
| `CurrentSourceVersions` has *n* > 0 entries | the mesh says this type HAS *n* source files | **Unestablished.** The pass contradicts the record, and a contradiction is not a verdict |
| `CurrentSourceVersions` is absent (SQL `NULL`) | no witness either way | fall back to whether the type declares its own queries |

`CurrentSourceVersions` is written by each NodeType's own sources watcher and persists on the
NodeType's `MeshNode`, so it survives a failed compile and it is readable from a cold boot before
anything has been compiled. It is the only independent witness available at discovery time, and it
is used in both directions — the same field, read for both polarities, which is what makes the two
classifications agree by construction rather than by coincidence.

`NodeTypeBatchBake.DiscoveryUnestablished` is the one predicate. When it answers true the whole
batch is abandoned with `SourceDiscoveryFailedException` and the pod falls back to the
activation-driven sweep, which re-resolves each type individually — slower, correct, and impossible
to mistake for a content verdict.

## What went wrong (issue #3663)

The predicate used to require that the type **declare** its own source queries:

```csharp
// before
if (matched.Count == 0 && pending.DeclaresSources && !pending.SourcesKnownDeleted)
```

Very nearly no NodeType declares them. The default queries —
`namespace:{path}/Source scope:subtree nodeType:Code` and the matching `Test` one — are how the
population is authored, which `DynamicTypePreWarmer.ClassifyCompileFailure` had already been
corrected to say in #1391: *"an empty `Sources` does not mean configuration-only — it means uses the
DEFAULT queries."* The same conjunct sat in `Assemble`, in the opposite direction, and it meant the
invariant guarded a tiny minority of types and waved the rest through.

So a short discovery pass produced compile errors, the errors were classified `CompileError` (the
snapshot was populated, so `NoSources` did not apply either), and `NodeTypeBakeGateState` recorded
four regressions on a healthy image.

### The measurement

`BatchBake` logs the size of every pass. On **memex.systemorph.com**, every boot in the window
2026-09-07 22:40Z → 2026-09-08 07:37Z:

| Boot (UTC) | Code nodes resolved | `compileErrors=` | Readiness |
|---|---|---|---|
| 09-07 22:40:02 | 1236 | 2 | granted |
| 09-07 22:43:42 | 1236 | 2 | granted |
| **09-08 00:31:22** | **1145** | **6** | **REFUSED** |
| 09-08 03:32:37 | 1237 | 2 | granted |
| 09-08 03:35:30 | 1237 | 2 | granted |
| 09-08 07:34:44 | 1241 | 2 | granted |
| 09-08 07:36:57 | 1241 | 2 | granted |

Two of those errors are the portal's standing baseline — types already at `Error` before the deploy,
which `MarkOutcome` correctly refuses to let gate. The four extra are the entire `Doc` partition's
NodeTypes (`…/BusinessRules/Cession`, `…/PythonPandasNode/PandasExplorer`, `…/SocialMedia/Post`,
`…/SocialMedia/Profile`) — four of four, not four of forty — every one of which uses the default
queries and carries a populated `CurrentSourceVersions`.

**The denominator is 29 boots across both portals in fifteen hours, and exactly one of them refused.**
The image was identical on three of the six boots that granted readiness. Nothing about the image
explains the difference; the size of one query pass does.

> **The prebuilt bytes were already there.** The same boot logged
> `ShippedPrebuiltBundles: bundle Doc.zip: adopted 4/4 prebuilt assembly(ies)` at 00:31:08 — ten
> seconds before the sweep enumerated `204 of 209 … need building — 5 already on the share` and set
> about recompiling them. Adoption seeding and the sweep's own store probe disagreeing on a cold
> boot is what put those four types on the compile path at all; it is a separate seam and it is not
> fixed here.

### Why it lasted three hours, and why that is the dangerous part

A recorded regression is not sticky by design — `NodeTypeBakeGateState.RetractRegression` is
level-triggered, and a type observed reaching a usable build on the same image has its regression
withdrawn (issue #1214). But retraction is driven by the type *recompiling*, and nothing recompiled:
the content never changed, so the park registry's source-change retry never fired. The verdict was
therefore correct-and-permanent for as long as the process lived.

What ended it was the kubelet. The portal's `startupProbe` is `failureThreshold: 1080` at
`periodSeconds: 10` — **three hours** — and the container was killed at 03:29:52Z, 2 h 59 m after it
started at 00:30:23Z. The replacement boot's discovery pass came back complete and the pod went
Ready at once.

🚨 **A rollout that stalls for hours and then succeeds is a worse failure than one that never
succeeds.** It presents as a slow deploy, its recovery looks like the system healing itself, and the
next occurrence reads as flakiness. The window is bounded by a probe budget, not by anything that
understands the defect.

### What did NOT cause it

- **Not `MeshNodeContentDegradedException`.** It is constructed in three places, all of them as the
  exception *argument* to `logger.LogWarning`; `throw new MeshNodeContentDegradedException` appears
  nowhere in `src/`. A logged exception renders with the same `Namespace.Type: message` prefix as a
  thrown one, which is what makes the two indistinguishable by eye in a pod log.
- **Not a failed bake.** The CD bake for the set logged `compile: 4/4 NodeType(s) compiled`,
  published under framework identity `sb43f9287dbd6922a7937bd24be103937`, and both the previous and
  the current set share that one publication.
- **Not the module-set reader.** [A portal on a large module volume](/Doc/Architecture/ModuleSetConvergence)
  is a real, separate, simultaneous stall — memex.meshweaver.cloud's startup probe timing out on a
  ten-second health check over 687 set records — and it is the one that fixed *that* portal. It
  cannot be what cleared this one: the recovery happened at 03:33Z, **47 minutes before** that fix
  merged, on an image that by construction could not contain it. Falsifiable, and falsified: had it
  been the cause, the recovery would have had to postdate the merge and arrive on a later image.

## Re-measuring this

One Loki query answers whether a pass was short, and it needs no pod to still exist:

```logql
{namespace=~"memex|memex-cloud"} |= "source discovery resolved"
```

Read the Code-node count per boot and compare it against its neighbours **on the same portal** — the
two portals hold different content and their absolute numbers are not comparable. A boot whose count
sits below its neighbours' resolved a short set, and every compile verdict from that boot is
suspect. Pair it with:

```logql
{namespace=~"memex|memex-cloud"} |= "warm-up complete"
```

whose `compileErrors=` is the portal's standing baseline plus whatever that boot invented.

Always write explicit `start`/`end` in nanoseconds — see
[Measuring a Live Portal Read-Only](/Doc/Architecture/MeasuringALivePortalReadOnly), whose first trap
(`since=` is silently ignored) applies to both queries above.

## Related

- [Node Type Compilation](/Doc/Architecture/NodeTypeCompilation) — how a NodeType compiles, and what
  the bake gate does with the verdict
- [Rebake Waves](/Doc/Architecture/RebakeWaves) — why a roll puts the whole population on the compile
  path in the first place, which is the precondition for a short pass to matter
- [Bake Identity Mismatch](/Doc/Architecture/BakeIdentityMismatch) — why a green CD can publish a
  bake no portal adopts
- [Measuring a Live Portal Read-Only](/Doc/Architecture/MeasuringALivePortalReadOnly) — the
  instruments, and why an absence needs a coverage fact before it counts as evidence
