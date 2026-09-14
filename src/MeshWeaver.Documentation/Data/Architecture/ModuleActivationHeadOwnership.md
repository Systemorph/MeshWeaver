---
Name: Module Activation Head Ownership
Category: Architecture
Description: Which generation of a module a deployment runs is a decision every replica writes to one shared file, and two replicas landing different content of one module a few seconds apart lose each other's decision. The measured shape of that defect, why routing the write to an owning hub is not available, why the head must be DERIVED rather than stored, and exactly what the derivation costs.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 2v6"/><path d="M5 8h14a2 2 0 0 1 2 2v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4a2 2 0 0 1 2-2z"/><path d="M7 20h10"/><path d="M9 16v4"/><path d="M15 16v4"/></svg>
---

# Module Activation Head Ownership

A deployment records which generation of each landed module it runs in one file per module,
`modules/activation.d/<Name>.json`, on the RWX `/data` volume every replica shares. A landing reads
that file at its start, decides against what it read, and replaces it at its end. The replace is
atomic and **unconditional**.

Within one process that is safe — landings are serialised on a cap-1 `IIoPool` slot. Across
replicas it is a lost update, and this page is the measured shape of it, the two closing designs,
and why only one of them is available. The issue is
[#4026](https://github.com/Systemorph/MeshWeaver/issues/4026).

## The mechanism, at file and line

Measured on `main`, 2026-09-14:

| | |
|---|---|
| `ModuleLandingService.cs:724` | `var landedBefore = ModuleActivationSidecar.Read(baseDirectory, …)` — read **once**, at the start. Every decision below is made against that snapshot: `displaced`, `keepsNewerHead`, `PreviousToKeep`, `ShelfOnlyEntry` |
| between them | the link probe, the staging writes and a `Directory.Move` — seconds of IO on the shared volume |
| `ModuleLandingService.cs:1068` | `ModuleActivationSidecar.WriteEntry(baseDirectory, entry)` |
| `ModuleActivation.cs:673` → `:853` | `WriteAtomic` = write a temp file beside the target, then `File.Move(temp, path, overwrite: true)` — an atomic **replace** that never compares what it replaces |

Two replicas inside that window each decide against a head neither has seen the other move, and the
later `File.Move` wins silently.

### The harm is narrower than "two replicas landing the same module"

🚨 **The same-content case is already benign**, and that is worth stating because it is the common
one. [#3656](https://github.com/Systemorph/MeshWeaver/issues/3656) made the generation leaf
content-addressed — `name@<16 hex of SHA-256 over the bytes>` — so two replicas landing the *same
bundle* compute the same generation, the same entry and the same bytes; the second `File.Move`
writes what is already there. Every replica reconciles the same feed at boot and on every
`ModulePublished` broadcast, so this is the shape that actually happens all day, and it is a no-op.

What remains is **two DIFFERENT contents of one module name landing concurrently on two replicas** —
two publishes seconds apart reaching different replicas. The
[#3996](https://github.com/Systemorph/MeshWeaver/issues/3996) incident (Mail 1.7.0 at 02:49Z, 1.6.1
at 03:00Z) was eleven minutes apart and is fixed by the version rule; this is the concurrent residue
under it.

### 🚨 And the regression PROPAGATES into the mesh module set — #3395 delays it, it does not bound it

This is the fact most likely to be read the wrong way, because boot does **not** load the generation
the record's head names. `MemexConfiguration.cs:280` projects the record onto the mesh's module set
(`ModuleActivationBoot.ProjectOntoMeshSet`), which replaces `entry.Directory` with
`meshSet.Generations[name]` whenever the set names one — deliberately, so that a pod booting
mid-wave cannot pin a torn mix ([#3395](https://github.com/Systemorph/MeshWeaver/issues/3395)).

It is tempting to read that as a bound on this defect. It is not. `ModuleSetStore.Propose`
(`ModuleSet.cs:471`) derives the set it proposes from `GenerationsOf(landed)` — **the activation
record**. So a regressed head is carried into the next proposed set, and from there into every
replica that boots after it. What the projection buys is the interval between the regression and the
next `Propose`, during which boot still loads the set that was live. That is a delay, not a barrier.

## Why "route the write to an owning hub" is not available as stated

The actor-model answer to a lost update is to stop sharing the cell: give the module a mesh node and
let the owning per-node hub serialise `GetMeshNodeStream(path).Update(...)`, which is
cluster-singleton by construction. For the WRITE, that works.

🚨 **It does not work for the READ, and the read is the reason the file exists.**
`ModuleActivationSidecar.Read` is called at `MemexConfiguration.cs:265` — **inside the
`MeshBuilder` configuration**, before the mesh exists. The record decides which assemblies are
installed into the mesh being built, so a design in which the record lives on a mesh node is a
cycle: the mesh cannot be built without the record, and the record cannot be read without the mesh.

So the record stays a file that boot can read with no mesh, and the strongest form of the hub answer
is *"the hub owns the write and projects it into the file"*. That buys cluster-wide serialisation of
the decision, and it costs:

- **a mesh dependency on a service that is a pure file-system service today** (`ModuleLandingService`
  is called from boot reconciliation as well as from the publish endpoint), and
- **a fallback question with no good answer**: a landing that cannot reach the owning hub must either
  fail — a module does not ship — or write the file itself, which is the defect unchanged. A gate
  whose failure mode is "the thing you were guarding happens anyway" is the shape this repository
  refuses elsewhere.

That is the design finding: shape A is not *bigger*, it is *unavailable in the form the issue states
it*, and its available form carries a fallback that reintroduces what it closes.

## Why no small fix exists either

Three shapes were considered and each is a narrowing rather than a fix, which is what
[#4026](https://github.com/Systemorph/MeshWeaver/issues/4026) already says and this page confirms
rather than re-derives:

- **Re-read the entry immediately before the rename** and write the rank-max of the two. The write
  becomes monotone, so a lost update can only lose a *lower*-ranked write — except inside the
  interval between that re-read and the `File.Move`, which is exactly the original race, shorter.
- **A file lock or a lease.** There is no lease primitive on this store, and a hand-rolled async gate
  is forbidden here on separate grounds.
- **An atomic compare-and-swap on the file.** `rename(2)` is atomic but unconditional; the
  conditional primitive (`link(2)`, which fails `EEXIST`) has no .NET surface and no dependable SMB
  semantics. So the store offers no CAS to build on.

## The shape that closes it: DERIVE the head

Stop storing the decision and store the **facts**, one file per landing, never replaced:

```
modules/activation.d/<Name>/<generation>.json      one landing's own record — disjoint by construction
modules/activation.d/<Name>.json                   the legacy single record, read as one more candidate
```

`ModuleActivationSidecar.Read` then ranks the candidates and returns the same
`ModuleActivationEntry` it returns today — head plus fallback — by the ranking the code **already
implements**: loadability on THIS platform first (`ModulePlatformLink.Check`), then version
(`NuGetVersionComparer`), which is `keepsNewerHead` and `KeepsRecordedFallback` turned inside out.

Two replicas then write disjoint files and nothing is ever replaced, so there is no lost update to
have. It is [#2090](https://github.com/Systemorph/MeshWeaver/issues/2090)'s move — *remove the shared
cell rather than guard it* — one level down: that change split one shared `activation.json` into a
file per module; this one splits a file per module into a file per landing.

🚨 **A second property falls out, and it is an improvement rather than a side effect.** Today one
shared file records a head chosen by whichever replica happened to write last, including its
*loadability verdict*, measured against that replica's platform. A derived head is computed by each
reader against its own platform. Every replica of a deployment runs one image, so the two agree in
the normal case — but the abnormal case (a rolling update, two images live) stops being a shared
file's coin toss.

### What it costs — measured, not estimated

| | measured on `main`, 2026-09-14 |
|---|---|
| `ModuleActivationSidecar.Read(` call sites | **82 occurrences in 19 files** — 5 under `src/` + `memex/`, 14 test files |
| `ModuleActivationSidecar.WriteEntry(` call sites | **6 occurrences in 4 files, and exactly ONE in production**: `ModuleLandingService.cs` |
| `.PreviousDirectory` readers | 27 occurrences in 6 files |

So the write surface is one call site and the read surface is one method — the 82 call sites go
through `Read`, which is where the derivation lives. What is genuinely not local is the rest:

1. **The GC's reference set.** `CollectGarbage` reclaims a generation no entry references. If the
   head is derived FROM the present generations, "unreferenced" stops existing as a concept and
   retention needs an explicit rule instead. Getting that wrong deletes the generation that is
   serving. 🚨 That failure mode is **not hypothetical and not measured here**: the 2026-09-13
   reading on [#4026](https://github.com/Systemorph/MeshWeaver/issues/4026) attributes it to #2509
   and #2303, and it is carried forward as attributed rather than re-derived — check it before
   pricing the rule, not after.
2. **Enabled / uninstalled state is per MODULE, not per generation.** `RemoveModule` needs a marker
   of its own; the existing `.unloadable` / `.refused` / `.tier-refused` marker pattern fits, but it
   is another file and another thing `Read` must union.
3. **A migration** for every deployment already carrying `activation.d/<Name>.json` — met by reading
   the legacy file as one more ranking candidate, which costs nothing and never needs a rewrite
   pass.

### What does NOT have to change

`ModuleSet.GenerationsOf` reads `ModuleActivationList` and is unaffected: it consumes `Read`'s
answer, so a derived head flows into `Propose` exactly as a stored one does. The boot projection
(`ProjectOntoMeshSet`) is likewise untouched.

## Standing verdict

The defect is real, reachable only by two different contents of one module landing concurrently on
two replicas, and unbounded once it happens (it propagates into the proposed module set). **Deriving
the head is the only one of the named shapes that adds no new cell two replicas can both write**, and
routing the write to an owning hub is not available in the form the issue states it, because boot
reads the record before the mesh exists.

What is left is a scope call the maintainer has not made: **whether the on-disk activation record
changes shape on a live fleet now**, given that the GC retention rule has to be rewritten in the same
change and that the last four changes in this area were each written after an incident in which a
module silently stopped shipping.

## See also

- [Module Adoption Policy](../ModuleAdoptionPolicy) — the head rule this page's cell records, and the
  "not covered: two replicas at once" paragraph that points here
- [Module Set Convergence](../ModuleSetConvergence) — the mesh module set the record feeds
- [Modules](../Modules) — the landing lane end to end
