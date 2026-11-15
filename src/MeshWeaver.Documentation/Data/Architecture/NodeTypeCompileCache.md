---
Name: NodeType Compile Cache
Category: Architecture
Description: "The node-repo gate recompiled every NodeType on every run, because the modules it composes get a new build identity each run. The bake's cross-run compile cache keys each compile on its inputs and on the PUBLIC SURFACE of every reference, so unchanged content against unchanged APIs compiles nothing and any surface change recompiles."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><ellipse cx="12" cy="5" rx="8" ry="3"/><path d="M4 5v6c0 1.7 3.6 3 8 3s8-1.3 8-3V5"/><path d="M4 11v6c0 1.7 3.6 3 8 3s8-1.3 8-3v-6"/></svg>
---

# NodeType compile cache

The node-repo gate (`node-repo-gate.yml`) bakes a repository's NodeTypes before it gates them:
`mw-plugin-test compile` compiles every NodeType against the platform host's `/app` plus the
modules the repository composes. In MeshWeaver.Plugins that was **123 NodeTypes in 5.6–5.9 minutes
of a 15-minute gate shard, on every run** — the last long job of every heavy pull request
([CI profile, November 2026](https://github.com/Systemorph/MeshWeaver/blob/main/src/MeshWeaver.Documentation/Data/Architecture/CiProfile2026-11.md), proposal R3; the page lives on the `main` line only).

Related pages: [Sharding the Node-Repo Gate](../NodeRepoGateSharding) (the prepared bake, which
shares one compile among a run's shards) · [CI Content Bake](../CiContentBake) ·
[Module Build Architecture](../ModuleBuildArchitecture) (the ABI-keyed rebuild of the module lane).

## Why nothing was ever reused

Two reasons, and the second is the one that mattered.

1. **There was no cross-run cache.** The prepared bake is keyed on the run's commit and the whole
   staged tree, so it is shared only by the shards of one run.
2. **The modules' identity changes on every run.** The gate composes four modules
   (`MeshWeaver.AI`, `MeshWeaver.Markdown.Collaboration`, `MeshWeaver.Maps`,
   `MeshWeaver.Payments.Stripe`) that the run builds itself. Every CI build of a `MeshWeaver.*`
   assembly carries a `g<commit>` `AssemblyMetadata` stamp, and the gate builds a fresh merge of the
   head onto the default branch, so the commit, and with it the MVID, is new every run — even when
   the module's source did not change. A cache keyed on the modules' identity could not hit.

Reference-assembly BYTES are no better key: they keep assembly attributes (the stamp again), and
Roslyn's ref emit is byte-stable only inside one build invocation
(`MeshWeaverSurfaceManifest.targets` records 4 of 37 canonical ref assemblies differing between two
builds of one commit).

## The key

`tools/MeshWeaver.PluginTester/CompileCache.cs`. One entry per NodeType compile, keyed on the
sha256 of:

| Part | What it covers |
|---|---|
| the generated-input digest (`NodeSetCompiler.GeneratedInputDigestOf`) | the generated source after every `@@` include, the emit options, the Roslyn build, the generators that run |
| the paths of the sources and includes it consumed | the dependency edges name them |
| the composed module names | they classify the edges (`Module` against `Platform`) |
| the reference set, as `name=surface` pairs | every reference the compile sees: the host's `/app`, its shared frameworks, the composed modules |

A reference's **surface** (`AssemblySurface.cs`) is a deterministic hash of what a compiler can see
of it from outside, read from metadata and written out as names, never as tokens or row numbers:
the assembly identity; assembly- and module-level attributes except the provenance ones
(`AssemblyMetadata`, informational/file version, company, configuration, …); type forwarders; and
for every type visible outside the assembly, its flags, base type, interfaces, generic parameters
and constraints, attributes, layout, and every public, protected and protected-internal member with
its signature, parameter names, defaults, constants and attributes. It also counts every abstract
member (an internal one still decides whether outside code can derive), every instance field of a
value type (definite assignment and managed-ness), and every explicit interface implementation.
An assembly that grants `InternalsVisibleTo` to a NodeType compile (`DynamicNode_*`) is keyed by its
MVID instead. Surfaces are memoised per MVID under `surfaces/`, so the platform's few hundred
assemblies are read once per build of each.

**So the cache misses for every NodeType whenever any reference's public surface changes**, and hits
across a rebuild that changed only private code or a provenance stamp. Every rule errs toward a
different hash: an over-inclusion costs a recompile, never a wrong reuse.

## What a hit reuses, and what it recomputes

- **Reused** (a function of the key): the DLL and PDB bytes, the compile's warnings (the warning
  ratchets judge them as before) and the dependency edges.
- **Recomputed** (a function of the bytes and the current host): the dependency record, through
  `NodeSetCompiler.FromEmitted` against this run's resolver and toolchain — the same computation a
  fresh compile makes after its emit. Every edge's `TargetBuildMvid` is re-stamped with this run's
  build of that reference; an edge naming a reference this run does not hold exactly once makes the
  entry a miss.

Only successful compiles are stored. An entry carries its full key and the sha256 of its files, and
a missing, damaged or partly written entry is a miss — the type compiles, and a damaged entry
is deleted so that compile stores a good one in its place. A type with
`#r "nuget:…"` references is never cached (its resolution is network state). Stored files are gzip'd.
Entries unused for 3 days are pruned, at most once a day per cache.

## Where it runs

`node-repo-gate.yml` (input `compile-cache`, default `true`) passes
`--compile-cache <store>/cache/nodetype-compile/<framework identity>` when the run's artifact store is
a file store (`file:/ci-artifacts`, the share every runner pod mounts) and the pinned tester accepts
the flag. Otherwise it prints why and compiles everything. The bake job of a multi-shard run and a
one-shard gate both go through it; `node-repo-publish-bake.yml` does not use it.

The bake prints one line with the denominator:

```
bake: compile cache: 123 of 123 cacheable NodeType compile(s) reused, 0 compiled (0 damaged entr(ies) ignored), 0 stored, 0 not cacheable (NuGet references)
```

## Measured

Locally, on the 23 packages one Plugins gate staged (the same 123 NodeTypes), against the core
tester with the four modules built from Plugins, `--max-parallel 6`:

| Run | Inputs | Reused / compiled | Bake wall | Compile work |
|---|---|---|---|---|
| cold | empty cache | 0 / 123 (123 stored) | 143.1 s | 683.3 s |
| warm | unchanged | **123 / 0** | **0.6 s** | 3.0 s |
| private rebuild | `MeshWeaver.Maps` rebuilt with one private member added: new MVID, same surface | **123 / 0** | 0.5 s | 2.6 s |
| public change | `MeshWeaver.Maps` rebuilt with one public property added | **0 / 123** | 151.9 s | 739.1 s |
| warm again | the public change, unchanged | 123 / 0 | 0.5 s | 2.4 s |

The warm bake's bundles carry byte-identical assemblies to the cold bake's. One set of 123 entries
takes 119 MB on disk, gzip'd. The local reference set is the tester's own closure plus
`MeshWeaver.Testing.InMesh` (which a portal's `/app` provides), not a portal `/app`, so the absolute
seconds differ from CI's 336.8 s; the hit counts are the claim.

**Estimate for CI:** the bake is 5.6–5.9 minutes of the Plugins gate shard on every run. On a head
whose references' surfaces match an earlier run's, that drops to seconds (plus reading about 120 MB
from the share), so a heavy Plugins PR's critical path shortens by up to ~5.5 minutes (54 → ~49
minutes median), about 320 runner-minutes a day if every run hit.

`BakeCompileCacheTest` pins the behaviour in CI: a second bake reuses every compile with
byte-identical bundles; a rebuild of the module with a new commit stamp and a new private body
(new MVID, same surface) still reuses every compile and re-stamps the edges with the new MVID; a
public change to the module misses for every dependent (the negative control); and a damaged entry
is a miss that recompiles.

## What this does not establish

- **A share writer is trusted.** An entry is checked against the sha256 in its own `entry.json`,
  which catches damage, not forgery: a job that can write `/ci-artifacts` can plant an entry for
  any key. That is the trust boundary the share already has — the same job can overwrite the run
  artifacts, prepared bakes and gate logs the lanes read from it, and the ABI probe's
  `cache/abi-syntax` that narrows test selection. The cache adds no writer the share did not have.

- **The CI hit rate.** It depends on how often the platform set or a composed module changes its
  public surface between two gated heads; that was not measured. A change to the platform's public
  surface misses for every NodeType until the next run against the same set.
- **Container mode.** In container mode the cache is bind-mounted into the tester container. On a
  Docker-in-Docker runner whose daemon cannot see the share, that mount is an empty directory: every
  compile misses and is stored where nothing reads it. Plugins runs its gate in volume mode.
