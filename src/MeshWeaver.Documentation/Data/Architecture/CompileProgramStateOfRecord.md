---
Name: The Compile Program — State of Record
Category: Architecture
Description: What the compile-cleanup and baked-DLL programs actually built, measured directive by directive against the code, and exactly what is left. Written so the remaining work is pickable-up cold rather than re-derived from an issue thread.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M16 18l6-6-6-6"/><path d="M8 6l-6 6 6 6"/><path d="M13 4l-2 16"/></svg>
---

Two maintainer-directed programs governed how a NodeType gets from source to executable bytes: the
**compile cleanup** (a rebuild key, a factored-out compiler, consume-prebuilt-everywhere) and the
**baked-DLL design of record** (*"Never get uncompiled/stale state to prod … Remove all module
source from the mesh DB. If Store updates, all dependent packages update as well."*).

Both lived as issue threads. This page replaces them, because a program that exists only as an issue
is invisible to the next session and does not ship with the platform. **Every row below was measured
against the code on 2026-09-01, not read from the issues** — several of the issues' own premises had
gone stale, and those corrections are recorded here rather than silently dropped.

> **How to read this.** *Implemented* means the mechanism exists and something reaches it.
> *Partial* means it exists and a real path bypasses it — that is the interesting state, and the
> row says which path. *Not started* means no binder exists; a grep hit in a comment or a doc is
> not a binder.

## The rebuild key, and the compiler as its own assembly

| Directive | State | Evidence |
|---|---|---|
| Factor the compile pipeline out of `MeshWeaver.Graph` | **Implemented** | `src/MeshWeaver.Compiler` and `src/MeshWeaver.Compiler.Pipeline` exist |
| One shared compile implementation | **Implemented for NodeTypes** | all four consumers funnel into `EmitPipeline`/`GeneratorPipeline`; `NodeSetCompiler` is an orchestrator over them, and `BakeEquivalenceTest` pins the two orchestrators equal |
| Ship the compiler as a dotnet tool | **Partial — published, unproven** | `MeshWeaver.Compiler.Cli`/`mw-compiler` is packed from `tools/MeshWeaver.PluginTester`; **no CI lane consumes it.** Every lane in both repos runs `mw-plugin-test` |

🚨 **A correction worth keeping.** `src/MeshWeaver.Cli` is **not** the compiler tool. Its
`ToolCommandName` is `memex`, it operates the mesh over the REST API, and it contains no Roslyn at
all — its build verbs `docker run` the tester image. Anyone reasoning about the compiler tool from
the project name will reach the wrong conclusion, as one audit of this program did.

The tester's csproj explains why the bake must *not* move to a leaner project: **that csproj's
reference closure IS the content-surface assembly list**, so a split with a different closure would
resolve a different framework identity, and every bundle it baked would be declined by every portal.

## Adopt before compile

This is the half that is genuinely built, and it is worth stating plainly because the surviving log
strings suggest otherwise.

| Path | State |
|---|---|
| Release watcher consults bundle sources before Roslyn | **Implemented** — the prebuilt probe precedes the compile dispatch, and the adoption must actually land (`Ok` + a usable build) before it is believed |
| Git-push parity — an import seeds before it releases | **Implemented** — the sync transaction runs the affected closure, which seeds within a bounded budget, then releases |
| Install bulk path seeds before releasing | **Partial** — the package installer seeds from **local** sources before releasing; the **registry** bundle is fetched only from the boot default-install lane, so a catalog-card install never fetches it |
| An adoption race with the seeder's own hub activation | **Implemented** — a mesh-scoped adoption registry reserves a path before the seeder opens the node stream, and the first-build kickoff waits on it. It **delays, never cancels**: a declined adoption still compiles |

**The fallback strings survive as arguments, not as behaviour.** Lines like *"will compile"* and
*"compiling instead"* are now templated with a consequence argument that changes under the
adopt-only flag. Two paths remain genuinely ungated and will print text that is false on an
adopt-only mesh: a non-refusal exception escaping the default-install absorb policy, and the shipped
bundle sweep's fault/decline branches. Neither produces a silent compile end-to-end — the watcher's
park catches it — but the log lies, which on this platform is its own defect.

## 🚨 The adopt-only gate exists and is switched on nowhere

`Modules:RequirePrebuilt` is real and load-bearing. When true it throws on the install lane and
**parks** on the compile lane with a named reason and an attempt count of zero, and the execute-time
gate refuses to arm an instance whose build was refused.

It appears in **zero** configuration files — no `.json`, `.yaml`, `.yml` or `.sh` under `deploy/`
or `memex/` sets it, and the code's own comments record it as *measured absent on memex and
memex-cloud*. Absent or unparseable means OFF.

So the mandate *"error early when a pre-built DLL is missing — never fall back to compiling"* is
**architecturally provided for and not enforced anywhere.** A new framework identity that outruns
its bundles still compiles; on a readiness-gated pod that compile is batched, and on a serving pod
it is **per-node**, which is the shape that fattened two silos to 20 GB.

Turning it on is a deployment decision, not a code change — and the machinery to survive it
(named park, refusal overlay, provenance) is already built and tested.

## The batch driver is not yet the universal fallback

The in-process batch driver is real: one batched discovery pass, then a bake per type with **no hub
activation and no compile-watcher settle**, stamping through the same field-set the activation path
uses.

Its reach is the limit. It is selected only when the pre-warmer gates readiness, so a **serving pod
keeps the activation-driven sweep**, which flips each type to `Pending` and lets each per-node hub
compile. And the runtime residue — a user editing a source node in the database — never reaches the
batch driver at all; it goes through the per-type compile watcher, one activation per type.

The work-lease has the same shape: a durable, cross-replica compare-and-set claim on a lock row
covers the **boot bake**, while the runtime compile is deduped only by the status field on the
node itself, serialized by the owning hub's action block.

> `CompilationLock.cs` was a file lock that never had a caller. It could only ever have covered one
> shared filesystem, and it was `async`/`Task.Delay`-based, which the house rules forbid. It is
> deleted in the change that introduced this page — it was not the work-lease and reading it as one
> sends you down a dead end.

**Cross-replica in production is closed by accident, not by design.** The per-node hub is a grain
with single-activation semantics, so on the clustered portal the status compare-and-set is de facto
cluster-wide. That does not hold for the monolith, and nothing in the compile pipeline asserts or
tests it.

## 🚨 A Store update does not rebuild its dependents

This is the one live defect the programs contained, and it is the mechanism behind the 2026-08-25
Store outage, in which every Store NodeType recompiled green and the page still went down.

The correct closure exists. `ReleaseAffectedNodeTypes` enumerates NodeTypes **mesh-wide** and
matches a changed path against every type's expanded source and test queries — so it *does* catch a
cross-package `shared=@{package}/…` consumer, ordered topologically.

**It has exactly one production caller, and it is not the installer.** The sync transaction uses it.
The package installer instead releases the paths of the nodes it just wrote that happen to be
NodeType definitions — which can only ever name types **inside the installed package**. So a Store
package update rebuilds Store's own types and leaves every package that compiles Store's sources
into its own assembly on the assembly it already had. The result is two hubs on different compiles
of the same sources disagreeing about the type registry, which reads as `$type` is not registered
and renders as an empty view.

The pieces for the fix are all present: the installer already tracks the paths it wrote and pruned,
and the closure already accepts exactly that input.

> While fixing it, note that the installer selects its release targets with an `is`
> **pattern-match on an `object` payload**. Per the platform's own rule that is a trap-door: content
> that arrived as JSON reads as absent, so the filter would silently select *nothing* and no release
> would be issued at all — a second, quieter way to reach the same outage. Use the content
> accessor.

## What shipped elsewhere in the program, and is fine

- **The release fan-out is armed.** Subscribers are discovered at run time from the App's
  installation rather than a variable, and notifying **nobody is an error, not a pass**. The
  satellite receivers are wired, and the satellite publication bakes affected types *plus their
  transitive dependents plus those dependents' dependencies*, dependencies-first, carrying forward
  what it did not rebuild so the completion marker still lists the whole set.
- **The stale-build self-heal converges.** Given the auto-recycle key, a usable build under a new
  assembly identity posts a single self-dispose after a settle window, and a same-key republish is
  reported as an integrity error rather than converged on — so the one shot is not spent on nothing.

Two names in the old program text are **wrong and should not be resurrected**: the subscriber-repos
variable was deliberately deleted, and the dispatch credential is a GitHub App id and private key,
not a token.

## Source in the mesh database

The mandate's other half — stop persisting source, prove adopt-only, then purge — is **not started**,
and there is a real ordering hazard that must not be discovered the hard way:

**Verification compares a bundle's producer fingerprint against the live mesh source nodes**, and
the bundle carries a fingerprint, not source. So removing source demotes every adoption from
*verified* to *unverified* — which the execution gate deliberately permits. Do that before the
adopt-only gate is enforced and the platform loses its verification silently, with nothing red.

The conflict is already recorded in [In-Mesh Build and Test](../InMeshBuildAndTest), which reads the
mandate flatly as the opposite of that page's design. It needs deciding deliberately, not
discovering.

## See also

- [Module Build Architecture](../ModuleBuildArchitecture) — the unified build shape every repo runs
- [Module Versioning](../ModuleVersioning) — what you author and what the build derives
- [In-Mesh Build and Test](../InMeshBuildAndTest) — the case for source living in the mesh
- [NodeType Compilation](../NodeTypeCompilation) — how a NodeType compiles at runtime
