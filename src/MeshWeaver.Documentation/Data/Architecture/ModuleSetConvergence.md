---
Name: Module Set Convergence
Category: Architecture
Description: One module set per mesh at a time — how a landing wave proposes the set, how boot adopts it, why the mid-wave window is bounded and observable, and what makes a half-landed wave a failure rather than drift.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="3"/><path d="M12 2v4M12 18v4M2 12h4M18 12h4"/><path d="M4.9 4.9l2.8 2.8M16.3 16.3l2.8 2.8M19.1 4.9l-2.8 2.8M7.7 16.3l-2.8 2.8"/></svg>
---

> 🚨 **Rule change, 2026-09-07 (maintainer) — see [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy).** A HELD module is no longer skipped at boot by a floor; the set records the generation that actually loaded, including a fallback to the previous one. Implemented in [PR #3661](https://github.com/Systemorph/MeshWeaver/pull/3661) (2026-09-08); the sections below describe the mechanism as it runs now.

Replicas of one deployment used to run **different module sets**, indefinitely, and nothing made
them converge. This page is the design that ends it: a mesh runs **one module set at a time**, a
landing wave is what moves it, and boot adopts what the mesh declares instead of reading a record
that is still moving underneath it.

Read [Modules](/Doc/Architecture/Modules) first for the landing lane itself — generations, the
per-module activation record, the process-local pin.

## The mechanism, corrected

Issue #3395 was filed as *"two NodeType compiles in ONE boot resolved different module sets, 15
seconds apart"*. That premise is **false**, and the correction matters because it moves the fix from
"find the race inside the process" to "there is no race inside the process".

**It is three processes, sixteen minutes apart.** Measured on `memex-cloud`, 2026-09-06: one
Deployment, three portal replicas, one image (`3.0.0-rc9.ci.7693`), started 11:33:39, 11:41:04 and
12:51:21 around a landing wave at 12:18–12:27.

| pod | started | pinned `MeshWeaver.Payments.Stripe` |
|---|---|---|
| `…-b8pfg` | 11:33:39Z | `MeshWeaver.Payments.Stripe@8f251f57` |
| `…-tqhzx` | 11:41:04Z | `MeshWeaver.Payments.Stripe@8f251f57` |
| `…-gqqg7` | 12:51:21Z | `MeshWeaver.Payments.Stripe@458afe55` |

Diffing `/tmp/meshweaver-pinned-modules/*/` across the three: **39 of 40 generations differed**
between the two older pods and the newest. The sidecar named the newest. Only
`MeshWeaver.Social@c000a138` was common to all three.

Inside one process nothing moves: `InstalledModulesFingerprint` is a mesh-scoped singleton whose
hash is computed once in its constructor, `InstalledModuleAssembly` registrations are made once at
`MeshBuilder.InstallAssemblies`, and `MeshNodeCompilationService.meshReferences` is a `Lazy<>`
composed once. What moves is **which process answers**. Those replicas share ONE NodeType node, and
a compile stamps the module set *it* resolved (`CompiledModulesHash` plus the per-assembly
`CompiledDependencies`). `Store/Order` compiled at 12:53:21 on the 12:51 pod; `Store/Plugin` at
13:09:01 on the 11:41 pod. Each replica then reads the other's stamp, `HasUsableBuild` /
`CompiledDependencies.FindMismatch` correctly calls the build stale *for its own environment*, and
rebuilds — so the pair ping-pongs. Where a replica's set genuinely LACKS a module the sources need,
the type does not merely rebuild: it FAILS. Healthy → failed, with no source change, which is
exactly the transition the readiness gate refuses for the whole startup budget.

**And there is a second, sharper shape the incident table cannot show.** A landing wave lands its
modules one at a time and moves each module's activation entry the instant that module's bytes are
down. A replica booting *in the middle* of a wave therefore pins a **torn** set — some modules of
the new wave, the rest of the old — a combination no wave ever produced and nothing was ever tested
against. That is the shape behind "a module that resolved fine fifteen seconds earlier".

## The rule

> **A landing wave does not move what the mesh RUNS. It stages bytes, and when the WHOLE wave is
> done it proposes ONE set. Boot loads the mesh's newest proposal — never its own read of the
> per-module activation record.**

Everything below follows from that one sentence. In particular: **every replica that boots between
two wave completions loads identical bytes**, and no boot can observe a half-landed mix at all.

The stamp stays **one per NodeType**. Fanning `CompiledModulesHash` out per environment was
considered and rejected: it makes the divergence permanent and moves the cost into every read of
every build record, when the divergence itself is what has to go.

## The authority

`ModuleSetStore` (`src/MeshWeaver.PluginCatalog/ModuleSet.cs`) — records under
`modules/sets/` on the deployment's shared volume, beside the module folders they name.

| Record | File | Written by | Says |
|---|---|---|---|
| `ModuleSet` | `<sequence:D9>-<id16>.proposed.json` | the landing wave that completed | "the mesh's module set is N: {module → generation}" |
| `ModuleSetAdoption` | `<sequence:D9>-<id16>.adopted.json` | the first process that BOOTS onto it | "a replica is serving set N" |

- **`Proposed`** = the highest-sequence proposal. This is what a boot loads.
- **`Current`** = the highest proposal that also carries an adoption record — *the mesh is on
  generation N*, as opposed to merely having declared it.
- **`Id`** is the content id: lowercase hex SHA-256 over the ordinal-sorted `name@generation` pairs.
  Two sets with the same id activate the same bytes.

**Why a file and not a mesh node.** Boot is the first reader, and boot runs before the DI container,
before any storage provider is registered and (on PG) before a connection string has been validated
— the same reason the activation record is a file. The set also cannot drift from the DLLs it names:
the landing service writes both onto the same volume.

**Why immutable and monotone.** Each record is written ONCE and never modified, never renamed over.
That removes both defects the activation record was split to remove (#2090/#2189): there is no
read-modify-write to lose an update, and no replace-in-place for a concurrent reader to open into.
`ModuleSetStore.WriteOnce` renames with `overwrite: false`, and a loser whose bytes are identical
treats the refusal as success.

**Conflicts are resolved, not absorbed.** Two replicas can complete a wave at the same instant and
each write sequence N+1. Both files survive, so every reader must resolve the tie the same way
without coordinating: **the ordinally smallest `Id` wins**, and the conflict is REPORTED. Nothing is
lost — a proposal is derived from the activation record, never from the previous proposal, so the
next wave's sequence N+2 carries everything both replicas landed.

**The reader decides from the NAMES and opens only what the decision needs.** Every record's name
carries its sequence and set id (`<sequence:D9>-<id16>.proposed|adopted.json`), so one directory
listing determines the newest proposal and the newest adopted set; `ModuleSetStore.Read` then opens
the proposal files of those two sequences and the one adoption record — nothing else. 🚨 Measured
on memex-cloud, 2026-09-08: 687 records had accumulated under `modules/sets` (the GC that prunes
them had been fail-closing — see [GC must see the set](#gc-must-see-the-set)), the previous reader
opened every one on every call, and on Azure Files that took 10 s — inside
`pending_module_activation`, the health check the startup probe asks every 10 s with a 5 s
timeout. No new pod could pass the probe; the 8059 rollout sat for hours on two ageing replicas and
KEDA could add nothing. A record no decision depends on is neither read nor reported: a corrupt
superseded record is `Prune`'s to remove, not the reader's to announce on every probe, and the
conflict notice names only the sequences that were actually decided on (the historical
"proposed by more than one replica" lines that used to repeat on every boot are gone).

## Boot — converge, don't serve your own

`MemexConfiguration.ConfigureMemexMesh`, in order:

```
ModuleActivationSidecar.Read(moduleRoot)          // what has LANDED — a moving target
ModuleSetStore.Read(moduleRoot)                   // what the MESH runs — stable between waves
ModuleActivationBoot.ProjectOntoMeshSet(...)      // the convergence
ModuleActivationBoot.ComputeEffectiveModuleEntries(...)
ModuleGenerationPin.PinnedLoadPath(...)           // unchanged (#2509); the PREVIOUS generation is pinned lazily
MeshBuilder.InstallModules(...)                   // the set's generation, or the previous one when that cannot load (#3649)
ModuleSetStore.RecordAdoption(...)                // AFTER the install — records what LOADED, see below
```

`ProjectOntoMeshSet` has three rules, and each is a deliberate refusal to guess:

- An enabled entry whose module the set names loads **the SET's generation**, even when the entry
  has since moved past it. That is the convergence.
- An enabled entry the set does **not** name is **landed but unproposed** — a wave that has not
  completed. It is deferred, loudly, and activates at the first restart after the wave proposes.
  Adopting it is exactly the independent per-replica pin this design removes; adopting *half* a wave
  is the torn set.
- A **disabled** entry passes through untouched. An uninstall deletes the folder, so honouring it is
  not optional and never waits for a set.
- The entry's **`PreviousDirectory`** — the generation boot falls back to when the set's does not
  load here (MeshWeaver#3649) — travels with it onto the set's generation, and is dropped only when
  it names that very generation (the mid-wave shape: the entry moved to D with the set's G as its
  fallback, so G is the head now). A fallback that names an *older* generation than the set's — a
  landing that carried it forward past a displaced generation measured unloadable — is kept, and is
  exactly what boot runs if the set's generation does not load either.

🚨 **One degradation, and it is reported.** The set can only pin what is on the volume. If the pinned
generation's bytes are gone — a replica on the PREVIOUS platform build sweeping by the entries alone
during this change's own rollout, a manual deletion, a partial restore — the two candidates are "run
the generation the entry names" and "run nothing", and running nothing is the *worse* half of #3395:
a missing module is what turns a healthy NodeType into a failed one. So the projection falls back to
the entry and says so on its own channel (`onSetGenerationMissing`, separate from the deferred one,
because that one means "running on no replica" and this module IS running). The next completed wave
re-proposes a set whose bytes exist, and the state is unreachable once every replica sweeps with the
GC change below.

A deployment with **no** set records — every deployment until its first wave after this change —
gets the list back unchanged. Pre-#3395 behaviour, byte for byte, is the migration path; the first
completed wave proposes sequence 1 and the mechanism starts.

**Adoption is recorded after `InstallModules`, not after the read.** The claim is *"this set is
running"*, not *"this set was read"* — a boot that dies earlier must not close a convergence window
it never entered. And since MeshWeaver#3649 the claim says *what* is running: the adoption record
carries `Generations` — the set's generation for every module that loaded as proposed, the
**previous** generation for every module the loader fell back on because the set's does not load on
this platform. `ModuleSetIndex.RunningGenerations` reads it back, `FallbackGenerations` lists the
difference, and `ModuleSetStore.Describe` names the modules that run a previous generation, so the
boot line and the status surfaces say what the mesh runs and not only what it proposed. The
proposal itself is unchanged — a wave proposes what it landed; whether that loads is measured at
every boot, so a replica on a newer platform where the set's generation *does* load adopts it
(rule R3).

## The window, and why it is bounded

A running process cannot adopt new module bytes; restart-as-activation is the model and this design
does not change it. So there IS a window in which replicas genuinely differ, and the design's job is
to make it **short, singular and visible** rather than open-ended and silent.

| | before | after |
|---|---|---|
| replicas booting between two waves | each pins its own snapshot | **identical set** |
| a replica booting mid-wave | a torn mix | the previous complete set |
| distinct sets across a 3-pod rollout | up to 3 (measured: 39/40 generations apart) | at most 2, differing by exactly one wave |
| the window's end | unobservable | `ModuleSetIndex.ConvergencePending` flips false |

`ConvergencePending` is the window stated positively: **the mesh has proposed a set no replica has
booted onto yet.** It opens at the wave's proposal and closes at the first restart. A user hitting an
old replica during it is hitting a replica running the mesh's *previous* set — one coherent set, not
a torn one — and every pod says which set it is on:
`ModuleSetStore.Describe` on the boot line and on `PendingModuleActivations.Read().MeshModuleSet`,
with the per-pod half unchanged from #3417 (`Degraded` on `/health`, naming the modules).

## A half-landed wave is a failure, not drift

A wave that dies before it proposes **proposes nothing**. Three consequences, and they are the
answer to "what breaks it":

1. **The mesh stays on the set it was on.** Nothing half-landed is ever adopted, by anybody. There
   is no drift to detect, because the state that used to drift is no longer reachable.
2. **The modules whose bytes DID land are named.** `ModuleActivationReport.Deferred` carries them,
   and `Describe()` says *"N module(s) have landed but are in NO proposed module set — the landing
   wave that brought them has not completed, so they are running on no replica and a restart will
   not change that"*. Compare with the old behaviour: those modules ran on whichever pods happened
   to boot after their own landing and not on the others, and every surface reported `Healthy`.
3. **It is a THIRD state, never folded into "pending".** "Pending" promises that a restart activates
   this. For a deferred module that promise is false — boot loads the mesh's set and the module is
   not in it — and a restart prompt no restart can clear is the same lie as a green tick over a gate
   that never ran. The same rule already separates HELD entries and missing bytes.

The next completed wave supersedes it: the proposal is derived from the activation record, so
whatever the dead wave landed rides the next one.

## GC must see the set

🚨 The convergence deliberately pins an **older** generation than the activation entry while a
wave's landings wait to be proposed — and that older generation is what every running replica
LOADED. `ModuleLandingService.CollectGarbage` therefore unions
`ModuleSetStore.ReferencedGenerations` (both retained sets) into its reference set. Without it the
sweep would reclaim the very bytes the whole mesh is executing: the 2026-08-27 outage, from the
other side. A set directory that cannot be read counts as a read fault for the same fail-closed
reason the entries do.

🚨 Two more references since MeshWeaver#3649, for the same reason: every entry's
**`PreviousDirectory`** — the generation boot falls back to when the head one does not load here,
which for a Store-only module is the only generation that runs — and the **running generations**
the current set's adoption recorded (`ReferencedGenerations` includes `RunningGenerations`). A
sweep that trusted the head pointers alone would reclaim the generation a replica is executing
because a newer one exists that cannot load — the shape #3649 removes.

The same pass prunes set records below the CURRENT set — never below the proposal, which would take
the mesh's own generations out of the reference set.

## Where a wave ends

Exactly two lanes land modules, and each proposes at its own completion:

- `RegistryUpdateReconciler.ReconcileModules` — the auto-update wave, after its per-package
  `Concat` completes. Deliberately outside the `Concat`: proposing per module would publish every
  intermediate combination as a set the next boot could adopt.
- `CatalogLayoutAreas.InstallOrUpdateCore` → `WithModule` — an interactive or default install. A
  one-package install IS a wave, and a wave that does not propose never activates.

…plus the registry's publish endpoint (`PluginBundleEndpoints`), because a `ShelveModule` landing
also writes an activation entry: a HELD module is skipped at boot by the platform-floor gate exactly
as before, but it must still be IN the set for the boot after a platform update to be able to load
it, which is the whole shelf contract.

All three are `ModuleLandingService.ProposeModuleSet()`, on the landing service's cap-1 IO pool, so
a proposal never observes a landing halfway through. Idempotent: a wave that landed nothing derives
the set that is already proposed and writes nothing — which matters because the reconcile runs at
EVERY boot and normally lands nothing.

None of them can fail its lane. A proposal that cannot be written leaves the mesh on its previous
set — every replica still agrees with every other one — and the next wave proposes again.

**How an existing deployment starts.** The reconcile's wave-end proposal IS the bootstrap: the first
pass after this change derives sequence 1 from the activation record as it already stands and the
mechanism is live from the next boot. Deliberately not done at boot, and deliberately not by every
replica: several replicas bootstrapping at once, mid-wave, would each derive a different sequence 1.
Until it happens, a deployment with no set records gets the activation list back unchanged.

## Rejected alternatives

**Fan the stamp out per environment.** Rejected by the maintainer and by the shape of the problem:
it makes the divergence permanent and pays for it on every read of every build record. The stamp
stays one per NodeType and the sets converge instead.

**Seal the set at LANDING time rather than at wave completion + boot adoption.** A set sealed by the
wave has, by construction, no live holder: the replica that landed the bytes has not loaded them.
Any rule keyed on "is this replica on the mesh's set" would then answer *no* for every replica the
moment a wave finishes. Proposal (by the wave) and adoption (by a boot) are two records for exactly
this reason.

**Have a behind replica restart itself, or flip readiness so the rollout replaces it.** Both are
live options on top of this design and neither is taken here: (b) empties a pod that is serving
correctly, and both change deployment behaviour on evidence this change is the first to produce.
They become *decidable* now that "behind" is a named state with a bounded window rather than an
invisible one. 🚨 One of them was decided by #3478 — a process whose own validation REFUSES it now
publishes nothing at all, which is neither of these two: it neither empties a serving pod nor
replaces it, it stops a pod that will never serve from writing what it compiled. See
[Mesh Admission](/Doc/Architecture/MeshAdmission).

**Decline to write NodeType compile records while behind.** This is option (c) from #3395's open
item and it is now SAFE to build — the adoption record guarantees the mesh's `Current` set always
has a live holder, so the rule can never reach zero eligible writers — but it changes who compiles
and belongs in its own change with its own falsification. It is not in this one.

## Related

[Mesh Admission](/Doc/Architecture/MeshAdmission) ·
[Modules](/Doc/Architecture/Modules) · [Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) ·
[Module Versioning](/Doc/Architecture/ModuleVersioning) · [Plugins](/Doc/Architecture/Plugins) ·
[Build Coordination](/Doc/Architecture/BuildCoordination) · [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation)
