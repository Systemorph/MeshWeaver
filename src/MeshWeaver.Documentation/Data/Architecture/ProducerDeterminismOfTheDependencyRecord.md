---
Name: Producer Determinism of the Dependency Record
Category: Architecture
Description: A NodeType's dependency record must not depend on how the producer reached its bytes. The disk-cache hit used to stamp a record without the `!input` content key, so a bundle's guard strength depended on whether the baking machine's cache was warm — and why the fix persists the digest instead of recomputing it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="6" rx="1"/><rect x="3" y="14" width="18" height="6" rx="1"/><path d="M7 7h.01"/><path d="M7 17h.01"/></svg>
---

# Producer Determinism of the Dependency Record

Every dynamic NodeType compile stamps a **dependency record** — the sorted set of
`referenced assembly → surface id` pairs read off the emitted assembly, plus two reserved entries:
`!toolchain` (the producing toolchain's identity) and `!input` (the CONTENT KEY, the hash of the
fully generated compilation input folded with the pruned reference surfaces). The record travels
onto `NodeTypeDefinition.CompiledDependencies` and from there into every bundle baked from that
type. See [Toolchain Re-evaluation Lane](../ToolchainReevaluationLane) for what the two entries decide.

This page states one property of that record and the defect that violated it.

## The property

> **A producer stamps the same record for the same content, however it reached the bytes.**

That is not a nicety. `!input` is what
`CompiledDependencies.FindMismatchAfterReevaluation` uses to DEMOTE `!toolchain` from an
invalidation unit to a trigger: when a consumer regenerates the input and the key matches, a
toolchain move stops being a reason to rebuild. A record **without** `!input` degrades to the
pre-content-key behaviour — nothing is wrong with it, `FindMismatch` simply has nothing to compare.

The trouble is that a consumer cannot tell the two apart in any way that matters. A record with the
key can answer the question directly; a record without it cannot, and it looks exactly like a record
whose producer never had the key to give. If which one you get depends on the state of a machine,
the guard's strength is a property of the build host rather than of the content — and the weaker one
never fires the check it exists to make.

## How it was violated (#3892)

`CompiledDependencies.Compute` writes `!input` only when the caller hands it the stage-1
generated-input digest, and that digest existed at exactly one place: inside
`MeshNodeCompilationService.CompileAsyncCore`, three statements before Roslyn. So:

| The producer reached the bytes by… | Digest in hand | `!input` in the record |
|---|---|---|
| a fresh Roslyn emit | yes | **yes** |
| a **disk-cache hit** (before #3892) | no — the method never ran | **no** |
| the assembly-hydration shortcut (`GetConfigurationsFromExistingAssembly`) | no | no |

The cache-hit row is the defect. It is a full producer path — the compile watcher and the batch
baker both stamp whatever `CompileAndGetConfigurations` returns, and `ApplyCompileSuccess` writes
`result.CompiledDependencies ?? def.CompiledDependencies`, so a later cache-hit compile REPLACES a
complete record with an incomplete one. Nothing throws, nothing logs, and the bundle is well formed.

The hydration row is not a defect: that path is a READER. It loads bytes an `IAssemblyStore` handed
over, supplies a hub configuration from them, and is never stamped onto a NodeType — it also carries
no `CompiledSources`, which is why stamping it would be catastrophic and nothing does.

### How it surfaced

`BakeEquivalenceTest.MeshDrivenAndCompilerDrivenBakes_ProduceTheSameArtifacts` bakes one content set
both ways and compares the two per-type records strictly. It went red on **one CI shard** and passed
locally at both the merge base and the PR head: in that shard the mesh-driven bake had reached its
bytes through a warm cache while the compiler-driven bake compiled fresh, so one record carried
`!input` and the other did not. Everything else matched exactly.

🚨 **Relaxing that assertion to compare "modulo `!input`" would have made the red go away and left
the producer non-determinism exactly where it was.** The symptom was a strict comparison noticing a
real difference — which is what it is for.

## The fix: persist the digest, do not recompute it

The stage-1 digest is written into the emit's **staging directory** as `{nodeName}.inputdigest`,
beside `{nodeName}.dll` and `{nodeName}.pdb`, before the directory rename that publishes the
artifact under the cache's discovery glob. A cache hit reads it back and stamps the record with it.

Three properties follow from where it is written:

- **Publication stays atomic.** The rename publishes bytes and provenance together, so no reader can
  observe one without the other.
- **A write fault fails the compile.** `EmitToDiskWithRetry` discards the staging directory and the
  exception propagates — the same verdict a lost DLL write gets. An artifact whose provenance cannot
  be recorded is not published.
- **An artifact without one is not a cache entry.** `CompilationCacheService.TryGetLatestCachedDllPath`
  refuses it, exactly as it already refuses a set with no PDB. That costs at most ONE recompile per
  type, for artifacts published by a build predating the sidecar — and the framework-timestamp check
  beside it usually invalidates those anyway.

### Why not recompute the digest at the hit site?

It is available: `GeneratedInputDigestOf(assemblyName, source, nugetAssemblyPaths)` is a pure
function of values the pipeline already has there, and `RegenerateGeneratedInputDigest` exists to
compute exactly this without compiling. Measured on this repo's own probe type — one source node,
no `#r "nuget:"`, warm in-process mesh — regeneration costs **0.11–0.18 ms** against **0.023–0.14 ms**
for the sidecar read, and both produce the identical `g…` digest. So cost is not the argument.

**The argument is that a recomputed digest answers a different question.** The digest folds
`EmitPipeline.OptionsFingerprint`, `GeneratedInputIdentity.CompilerIdentity` and the file identities
of the **generator assemblies on disk** — properties of the process doing the computing, not of the
bytes. Recomputing it describes *"what a compile RIGHT HERE would be fed"* and stamps that onto bytes
some earlier process emitted.

That difference is reachable. The cache's validity rules are source-mtime and framework-mtime; they
do not see a `#r "nuget:"` resolving to a different generator version, and they do not see a process
restart on the same image. In either case a recomputed key would claim an equality that was never
established — and because a matching content key DEMOTES the toolchain entry, it would license an
adoption across precisely the change the toolchain entry exists to catch. Reading the producing
compile's own digest off the disk cannot say anything the producing compile did not.

Cost, meanwhile, is *not* uniformly small for the recompute option: on a real mesh the same
regeneration performs source discovery (measured ~0.25 s per query on memex, four queries per type)
and, for any type declaring `#r "nuget:"`, a NuGet restore — network IO, on every cache hit, per
type at boot.

## Where the code lives

| Concern | Where |
|---|---|
| The sidecar (name, write, read) | `src/MeshWeaver.Compiler.Pipeline/GeneratedInputDigestFile.cs` |
| Written into the staging dir | `MeshNodeCompilationService.CompileAsyncCore`'s emit callback |
| Restored on a cache hit | `MeshNodeCompilationService.GetAssemblyLocationWithLog` |
| Refused when absent | `CompilationCacheService.TryGetLatestCachedDllPath` |
| The control | `test/MeshWeaver.Compiler.Pipeline.Test/CacheHitStampsTheSameRecordTest.cs` |

🚨 **None of it is in `MeshWeaver.Compiler`, and that is deliberate.** That assembly is a full-MVID
toolchain root (`FrameworkBuildIdentity.ToolchainRoots`), so under deterministic builds ANY edit to
it — a comment included — moves the framework identity, invalidates every stamped record and every
published bundle's adoptability on every mesh, and rebakes every NodeType. The cache-directory layout
is already split between the two assemblies (the emit writes the directory, the pipeline discovers
it), so the sidecar lives on the pipeline side, where the same fix costs nothing. One consequence to
know when reading the code: the `OPTIONAL` paragraph on `CompiledDependencies.ContentKey` still lists
the disk-cache hit among the producers that carry no entry. That sentence is now narrower than
reality and was left alone on purpose — correcting a comment there would rebake the fleet. See
[Graph / Compiler Layering](../GraphCompilerLayering) for the size rule that makes this trade.

## The control, in both directions

A control for this cannot exercise only the fresh-compile path — that is the path that always
worked. `CacheHitStampsTheSameRecordTest` compiles one type twice on a real Monolith mesh, handing
both calls the same node objects and the same source snapshot so the ONLY variable is how the
compiler reached the assembly, and it asserts the route before it asserts the record: the first
call's activity must say *Compiled assembly written to* and the second must say *Cache hit*.

Measured on the pre-fix code it fails at the record, having passed both route assertions:

```text
Expected dictionary to contain key "!input" because 🚨 the dependency record is a PRODUCER
artifact: it is stamped onto the NodeType and ships inside every bundle baked from it …
```

and passes after. `CompilationCacheServiceTest.IsCacheValid_ReturnsFalse_WhenTheGeneratedInputDigestIsMissing`
pins the other half — including a positive control that the same fabricated artifact set IS valid
while the digest is present, so the refusal cannot pass for an unrelated reason.
