---
Name: The Toolchain Re-evaluation Lane
Category: Architecture
Description: Why a toolchain change stopped being a reason to rebake every NodeType — the generated-input content key, the demotion of the toolchain MVID from invalidation unit to trigger, and the one half that is still deliberately gated.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 2v6h-6"/><path d="M3 12a9 9 0 0 1 15-6.7L21 8"/><path d="M3 22v-6h6"/><path d="M21 12a9 9 0 0 1-15 6.7L3 16"/></svg>
---

# The Toolchain Re-evaluation Lane

Every dynamic NodeType carries a **dependency record** — what its compiled bytes bind, and what
they were built from. One entry in that record decided far more than it should have, and this page
is about demoting it.

## The problem, measured

The record's reserved `!toolchain` entry hashes the implementation MVIDs of the **toolchain
closure** — the transitive `MeshWeaver.*` closure of `MeshWeaver.Compiler` and `MeshWeaver.NuGet`.
That closure is 16 assemblies, and it includes `MeshWeaver.Mesh.Contract` and
`MeshWeaver.Messaging.Hub` — the two assemblies nearly every change in the repo touches. Measured
over 30 days, **383 commits** could move it.

The entry is there for a real reason. The compiler's code shapes the **generated input** of every
NodeType compile: the skeleton generator, source-query resolution, `@@`-include expansion, the join
order, the parse and compilation options. A body-only edit to any of that changes what Roslyn is fed
with no API change at all, so surface hashing cannot see it. Hashing the toolchain's MVID does see
it — and sees far more besides.

That makes `!toolchain` a **proxy**: *"the toolchain moved, so the generated input might have
moved."* Every move of the proxy invalidated every stamped build, mints a new framework identity,
empties the assembly share's key-space and rebakes the world — including every type whose generated
input is byte-for-byte what it was.

## The direct observation

The content key (`!input`) replaces the proxy with the fact. It is a hash of the **fully generated
compilation input** — the exact text handed to Roslyn after skeleton generation, source aggregation,
`@@`-include expansion and the `#r` strip — folded with everything else that decides the emitted
bytes given that text: the assembly name, the option set, the Roslyn version, the source generators
that run, and the **pruned** reference surfaces the emitted assembly actually binds.

Two compiles whose generated input hashes equal produce interchangeable bytes, whoever built them
and whenever.

### It was stamped and read by nothing

The key shipped as a write-only field. Every production caller passed the three-argument
`FindMismatch`, so the comparison was skipped 100% of the time and the only four-argument callers in
the repo were assertions inside a test. The maintainer's audit named the shape exactly:

> a *guard that cannot fail*. A content key that is written on every compile and read by nothing
> looks, to anyone inspecting a node, exactly like a content key that is being enforced.

The reason it stayed that way is structural, not an oversight: the key has **no cheap live
counterpart**. Evaluating it means regenerating the compile input, and every rebuild-or-not consumer
in the framework is deliberately metadata-only. The missing half was never a better key — it was a
lane that regenerates.

## The lane

```
       a stale-build verdict has already been formed (metadata-only, cheap)
                                   │
                     the store already holds bytes
                     under the LIVE framework tag?
                        ┌──────────┴──────────┐
                       no                    yes
                        │                     │
                    compile          REGENERATE the compile input
                                              │
                                    compare with the stamped !input
                        ┌─────────────────────┼─────────────────────┐
                     EQUAL                DIFFERENT            INCONCLUSIVE
                        │                     │                     │
              restamp; no compile          compile          pre-lane behaviour;
                                                             never a restamp
```

Three pieces:

| Piece | Where |
|---|---|
| The decision, pure and unit-testable | `ContentKeyReevaluation.Reevaluate` (`MeshWeaver.Compiler`) |
| The demotion itself | `CompiledDependencies.FindMismatchAfterReevaluation` + `LiveContentKeyOf` |
| The regeneration entry point | `MeshNodeCompilationService.RegenerateGeneratedInputDigest` |
| The wiring | the framework-stale kickoff in `NodeTypeCompilationHelpers` |

### How a non-compiling caller forms the live key

Stage 2 of the key folds the **pruned** reference surfaces read off the emitted assembly, and a
caller that has not compiled cannot know that set. `LiveContentKeyOf` resolves exactly the names the
**record itself** carries — that *is* the pruned set, as the producer recorded it — against this
environment. Equality therefore proves two things at once:

1. the generated input is byte-identical, and
2. every assembly the build binds still presents the same surface here.

Which is why the demotion is confined to one entry: a module update or a platform surface change
moves the key, so it is still a mismatch and still a rebuild.

### The safety property, and why it is asymmetric

A false **mismatch** costs one rebuild. A false **match** carries stale bytes forward over live
source — the defect class that destroyed four client documents (#2813). So every inconclusive path
takes the rebuild side, and **an absence never reads as equality**:

- no record, or a record with no `!toolchain` entry → not trusted at all;
- a record with no `!input` (an adopted prebuilt, a cache hit, a stamp that predates the key) →
  inconclusive;
- the input could not be regenerated (an unestablished source set, a dead discovery query, a NuGet
  resolve that will not answer inside the bound) → inconclusive;
- with no live key, `FindMismatchAfterReevaluation` is byte-for-byte the metadata-only
  `FindMismatch` it stands beside.

Inconclusive means *the behaviour that existed before the lane*, and in particular it never
restamps.

### Regeneration runs the compile's own code

The digest was taken **inline**, three statements before Roslyn. The lane shares that code rather
than reimplementing it — the same source discovery, the same shaping fold, the same include
expansion, the same skeleton generation and `#r` strip, the same digest function. A second
implementation would drift into a key that never matches, and a key that never matches is a
permanent rebuild: the exact failure the mechanism exists to remove.

## What the lane fixes that is not an optimisation

The store-hit branch used to skip **unconditionally**. Since the kickoff is one-shot per hub
lifetime, a type whose generated input had genuinely moved kept serving the old bytes forever, and
nothing re-drove it — the shape the pinned test
`DeletingTheFullMvidRule_WouldLeaveNothingWatchingTheToolchain` describes, and the shape an
`@@`-included snippet has today (a change to an included-only file moves no source-version snapshot,
so `IsDirty` misses it, but it **does** move the generated input). With the lane that case is
decisive: it compiles.

## 🚨 The half that is still gated

The lane does **not** demote the framework version, and that is a decision, not an omission.

A build's bytes are addressed in the assembly store under a key carrying the framework identity's
first eight characters. After a framework roll the previous generation's bytes are still on the
volume — per-type eviction deliberately never crosses the tag boundary — but they are
**unaddressable**, because the store globs the live tag. Carrying a build across that boundary on
the strength of the content key is a **cross-generation assembly load**:

> A generation belongs to an IMAGE — another pod may be running it, and loading the wrong
> generation's bytes is `BadImageFormatException` → failed grain activations → portal-wide wedge
> (prod 2026-06-20).

The content key is designed to make that sound, but it carries four documented coarsenings (the BOM
drop, the CRLF→LF fold, and the two normalised wall-clock lines). Betting the 2026-06-20 failure
mode on them is a maintainer call, recorded on the issue rather than taken by an implementing
session.

So the lane acts **only where the bytes are already addressable under the live tag** — the designed
pre-bake flow, where a platform release fills the share and portals adopt rather than compiling.
There it needs no byte carry at all: the bytes are at the live key, and only the record is stale.
Closing the remaining half needs one of:

- **the cross-generation read** — `IAssemblyStore` gaining a fetch by `(collection, contentPath)` or
  by explicit tag, which is one method behind the decision above; or
- **the store sidecar** — the dependency record written *beside* the DLL, so a consumer can ask the
  bytes themselves what they were built from instead of asking a record that describes a different
  file.

## Related

- [Node Type Compilation](../NodeTypeCompilation) — how a NodeType becomes an assembly
- [Module Versioning](../ModuleVersioning) — what the build derives and what you author
- [CI Content Bake](../CiContentBake) — the pre-bake that fills the share ahead of a rollout
- [Plugin Packaging](../PluginPackaging) — bundles, the framework identity, and adoption
