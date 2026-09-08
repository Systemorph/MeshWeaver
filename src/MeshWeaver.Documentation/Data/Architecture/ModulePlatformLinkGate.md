---
Name: The Module Platform Link Gate
Category: Architecture
Description: A module's platform requirement is not a version string an author writes — it is the set of types its bytes are linked against. This is the gate that measures that requirement against the platform actually running, refuses a module the process cannot load, and parks the generation instead of the portal.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M9 12h6"/><path d="M12 9v6"/></svg>
---

# The Module Platform Link Gate

> 🚨 **Rule change, 2026-09-07 (maintainer) — see [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy).** The link probe becomes the ONLY platform gate on the module lane (the declared floor no longer gates first), and a refused generation no longer leaves the module absent: the previous loadable generation is loaded and named. The mechanism described below is what runs until [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648) and [#3649](https://github.com/Systemorph/MeshWeaver/issues/3649) lands; this page is rewritten by that change.

**A module is adopted on what this process can LOAD, never on a version string.**

Until MeshWeaver#3538 the module lane had exactly one platform gate: the module's declared
`minMeshVersion` FLOOR, compared as SemVer against the running platform's version. That gate is a
*claim* — a string a module author writes by hand. The module's real requirement is not a version
at all. It is **the set of types its bytes are linked against**, which the compiler records exactly,
without anyone having to say anything, in the assembly's own metadata.

Those two are not the same thing, and the gap between them took a production portal down for a day.

## What it cost, measured

memex-cloud ran core `3.0.0-rc9.ci.7693` (main of 2026-09-03). It adopted the pre-installed
**DefaultViews** view pack's `MeshWeaver.Graph.Views`, whose bytes were compiled on 09-06 against a
`MeshWeaver.Mesh.Contract` that carries `CodeOutputCurrency` — a type added to core on 09-04
(`2dc5868b2`). The pack declared `minMeshVersion: 3.0.0-rc8`; the running version satisfied it; the
bytes landed and loaded.

They could not possibly work. Three things followed, and none of them named the install:

- **Every code cell on the deployment rendered an error.**
  `System.TypeLoadException: Could not load type 'MeshWeaver.Mesh.CodeOutputCurrency' from assembly
  'MeshWeaver.Mesh.Contract, Version=3.0.0.0'` — thrown from `CodeViews.BuildContent`, on every
  render, for every user, deterministically. Reported by a paying learner (MeshWeaver#3518).
- **The module INSTALLED cleanly.** Its `MeshNodeProviderAttribute` touches none of the missing
  surface, so MeshWeaver#2234's per-module install isolation — which catches a module whose
  *registration* throws — saw a perfectly healthy module. Nothing was unhealthy until a *render*
  reached the linked type, which is the JIT's first look at it.
- **Nothing rolled back.** The generation stayed active, and the only evidence of what had happened
  lived in a pod log.

## Why the declared floor can never answer this

Three properties make `minMeshVersion` structurally unable to decide loadability:

1. **It is authored, so it can be wrong or absent.** An absent floor is "no constraint" — which is
   right, since inventing one would be a claim the author never made — so a module that says
   nothing is admitted on no evidence at all.
2. **It is coarse.** A floor names a release; a break is a single type or member. A module built
   one commit after a type was added has the same declared floor as one built a month before it.
3. **It is a claim about the FUTURE.** `minMeshVersion: 3.0.0-rc8` asserts that every platform from
   rc8 onward has what the module needs. That assertion is made *before* the platform it will run
   on exists, and this is precisely the direction the module lane deliberately permits: a module
   built against a newer platform is allowed to load on an older one, because that permission is
   what makes an ex-post Store install possible at all.

The recorded `frameworkMvid` cannot substitute for it either. That is an opaque *identity*, not an
ordering: equality proves the same build, and anything else proves nothing. Gating modules on MVID
equality would forbid every legitimate cross-build install — the whole point of the module lane.
See [Module Versioning](../ModuleVersioning) for what the pack lane records and why.

## What the gate does instead: measure

`ModulePlatformLink` (`src/MeshWeaver.Mesh.Contract/ModulePlatformLink.cs`)
reads the module's **type references** straight out of its metadata — no `Assembly.Load`, no module
initializer, no type loading, no side effect — and resolves each one against the copy this process
would actually bind to.

The verdict is a **three-state**, and the third state is the point:

| State | Meaning | What callers do |
|---|---|---|
| `Linkable` | every referenced type in every resolved platform assembly exists | load it |
| `Unlinkable` | a referenced type is absent, or a whole referenced platform assembly is | refuse, naming the type |
| `Indeterminate` | the check could not be MADE (unreadable bytes; a platform copy that would not parse) | refuse |

`ModuleLinkVerdict.MayLoad` is true for `Linkable` **and nothing else**. It is written as an
explicit predicate rather than left to each call site precisely so that nobody can spell the check
as `!= Unlinkable` and quietly admit `Indeterminate`. *"I could not determine whether this loads"*
and *"this loads"* are different facts, and a gate that reports the first as the second is a gate
that cannot fail.

### The denominator is part of the verdict

A clean answer over zero checked references is indistinguishable from a check that never ran, so
the verdict carries what was measured: how many type references were resolved, which platform
assemblies were read, and — named, never silently folded into "fine" — which referenced assemblies
were **not** checked.

An assembly is in the denominator when the *platform* carries it. Two categories are deliberately
outside it:

- **The module's own closure.** Assemblies travelling *with* the module were built together with it;
  their mutual agreement is not this gate's question.
- **A private dependency the deployment does not carry.** That is a different defect — it fails as
  a `FileNotFoundException` at first use, with a different remedy — so it is reported as unchecked
  rather than guessed about.

The one exception: an unresolved assembly whose simple name is the **platform's own**
(`MeshWeaver.`) is refused. That is the whole-assembly shape of the same defect and a certain load
failure.

### What it does NOT see

**Member-level skew.** A method or constructor signature that moved on a type that still exists —
MeshWeaver#2234's original `MissingMethodException` — is invisible here, because this checks TYPE
references. That shape is caught at install by
`IncompatibleModule`, and the two are complementary halves rather than
one check. Stating this explicitly matters: a gate whose blind spot is undocumented gets read as
covering more than it does.

## Where it runs, and what it protects

### At landing — the floor, measured

`ModuleLandingService.LandCore` consults it beside the declared floor, **from memory, before a
single byte touches the disk**. A refusal therefore costs no generation directory, and the
generation already running keeps running.

The two landing paths differ exactly as they already did for the declared floor:

- **Adopt** (`LandModule` — the install funnel, the auto-update reconcile): an unloadable module is
  **refused**. Declined bytes must never reach a disk the next boot loads from.
- **Shelve** (`ShelveModule` — the registry publish endpoint): the same verdict **holds** instead.
  A registry stocks modules for platforms other than its own; the bytes land and serve, consumers
  apply this same measurement against *their* platform, and this process's boot never loads them.
  Refusing here would recreate the 2026-08-22 three-way deadlock the shelf exists to break.

🚨 **The surface here carries the ACTIVE generation of every landed module, and it is rebuilt per
landing.** A wave lands its modules ONE AT A TIME, and a module may legitimately reference a
SIBLING module that landed thirty seconds ago and that this process has not loaded
(restart-as-activation). A surface that knew only `/app` — or one captured at the wave's first
landing — would not carry that sibling, and the platform-prefix rule below would refuse a module
that is perfectly fine. On a real deployment that reads as a feature silently missing after an
upgrade, which is the same class of confidently-wrong verdict this gate exists to replace. The
ACTIVE generation specifically, through the one resolution rule (`ModuleDirectoryFor`) — a
superseded generation is still on the volume until the GC reclaims it and can legitimately lack a
type its successor has, so listing directories would make the verdict depend on the filesystem's
ordering.

### At boot — the generation is parked, not the portal

`MeshBuilder.InstallAssemblies` probes each module **before** `Assembly.LoadFrom`. A refusal is
recorded as an `IncompatibleModule` — the same record a module whose
registration threw produces — so it is reported three ways that already existed: written to stderr
at boot (the only channel that exists before the logging pipeline is up), registered in DI for any
host to surface, and classified `RequiredModuleState.Incompatible` so a module declared under
`Modules:Required` is named on `/health` rather than silently absent.

The blast radius is the point. One module that cannot link costs **that module's contribution and
nothing else** — never every other module, never the portal, never every render on it.

### The previous generation runs when the newest cannot (MeshWeaver#3649)

Refusing a generation is not the same as losing the module. Until #3649 it was, for every module
the image did not also ship: the refused generation was the activation entry's only pointer, the
generation that had loaded last time was unreferenced, and the next GC pass reclaimed it. A shelved
landing built for a newer platform therefore took a working Store-only module away for good. Rule
R1 of the [Module Adoption Policy](../ModuleAdoptionPolicy) is the opposite: **an installation runs
the newest generation of every module that loads, and keeps the one it has until a newer one does.**

- **The landing keeps what it displaces.** `ModuleLandingService.LandCore` records the entry it
  replaces as `ModuleActivationEntry.PreviousDirectory` (with `PreviousVersion` and
  `PreviousFrameworkMvid`). When the displaced generation is itself measured unloadable here and
  holds a fallback of its own, that fallback carries forward — two unloadable landings in a row
  cannot push the loadable generation out of reach. Measured on the bytes, like every other
  decision point; there is no persisted "held" flag to go stale.
- **The loader falls back.** Boot hands `MeshBuilder.InstallModules` a `ModuleInstallCandidate`
  per module: the newest generation, and a *lazy* resolver for the previous one (the portal pins
  a generation to process-local storage before loading it, and pinning every previous generation
  up front would double that copy for a path taken only when something is wrong). When the newest
  is refused before loading or `Assembly.LoadFrom` throws, the previous generation goes through
  the same probe and load; when it succeeds the module is installed from it and a `FallbackModule`
  is registered — never an `IncompatibleModule` — with one
  `[MeshWeaver.Mesh.FallbackModule] '<name>' runs its previous generation v1.2.3 (gen A) because
  v1.3.0 (gen B) cannot load here: …` line on stderr, re-logged as a Warning once the pipeline is
  up. Only when neither loads is the module incompatible, exactly as before. A generation whose
  assembly *loaded* and whose registration then threw (#2234's shape) is never swapped: two
  assemblies of one simple name cannot coexist in the default load context.
- **The GC keeps it.** `CollectGarbage` references `PreviousDirectory` exactly like `Directory`,
  and the running generations the adoption records (below).
- **The set records what runs.** The wave still proposes the head generation; the adoption
  (`ModuleSetStore.RecordAdoption` with `RunningGenerationsOf(set, fallbacks)`) records the
  generation each module actually loaded, `ModuleSetIndex.RunningGenerations` /
  `FallbackGenerations` read it back, and `ModuleSetStore.Describe` names the modules that run a
  previous generation.
- **Uninstall clears both** pointers and deletes both directories.

The fallback is **present, not incompatible**: `Modules:Required` classifies it `Present`, the
readiness probe stays Healthy, and the package card says which version runs. It is also **not
pending**: a restart re-measures the same bytes and falls back again, so `PendingModuleActivations`
subtracts a fallback whose refused generation is still the one the set activates — and counts it
pending again the moment the set moves on to a generation other than the refused one, which a
restart genuinely tries. What makes that restart happen is MeshWeaver#3650 (rule R3).

🚨 **The surface is the application closure PLUS every directory the boot is loading from.** A
store-landed module lives in its own generation directory and may legitimately reference another
module landed beside it; measuring against `/app` alone would report that sibling as an absent
platform assembly and quarantine a module that is perfectly fine. The runtime's own resolution
surface is what has to be measured.

### How it composes with the generation pin

`ModuleGenerationPin` (MeshWeaver#2509) copies the generation a replica
is about to load into process-local storage, so a sibling replica's GC cannot reclaim bytes this
process still lazily loads. The two are sequential and independent: the link probe decides
**whether** a generation may be loaded, and the pin decides **from where**. The probe runs against
the resolved load path before `InstallAssemblies` touches it, so a refused generation is never
pinned and never copied — and a pinned one is measured exactly as the shared one would have been.
Neither changes the other's answer.

## How a quarantined module reads on a surface

`ModuleActivationReport` now carries a **fourth** state beside pending, unresolvable and deferred:
`Quarantined`. It is kept apart for the same reason as the others — the remedies differ, and every
one of these states was previously mis-rendered as "restart required":

| State | What it means | Remedy |
|---|---|---|
| `Pending` | landed on the volume, not loaded here | a restart |
| `Unresolvable` | activated, but its bytes are gone | re-install the package |
| `Deferred` | landed, but in no proposed module set | the landing wave must complete |
| **`Quarantined`** | **refused: its bytes need a platform this deployment is not running** | **a platform update — which is itself the restart that loads it** |
| **`Fallbacks`** (MeshWeaver#3649) | **present and running its PREVIOUS generation; the newest one landed but does not load here** | **none — a build that loads here, or a platform update, takes over by itself** |

A quarantined module must never be reported as pending. Its assembly genuinely is not loaded, so
the pending derivation finds it — and a restart re-runs the same measurement on the same bytes and
refuses again. "Restart required" would be a prompt no restart can clear, which is the same false
promise the held-entry and missing-bytes rules already exist to prevent.

On the package card the person who installed it sees one localized line
(`ui.moduleBuiltForNewerPlatform`) instead of a feature that is silently absent. Platform-owned
chrome, so it follows the **viewer's** language — see [Localization](../Localization).

## What proves it

`test/Memex.Portal.Shared.Test/ModulePlatformLinkTest.cs`. Every test compiles **real** assemblies
with Roslyn and drives the **real** gate; nothing is mocked and no rule is re-derived locally.

The repro compiles a module against a **stand-in `MeshWeaver.Mesh.Contract`** carrying a type the
platform running the test does not have — memex-cloud's shape verbatim: same assembly simple name,
missing type — and lands it through the real `ModuleLandingService`.

| Test | What it pins |
|---|---|
| `AModuleBuiltAgainstANewerPlatform_IsRefusedAtLanding_NamingTheMissingType` | the refusal, the named type, and that nothing landed |
| `AModuleBuiltAgainstThisPlatform_LandsAsBefore` | the gate is a gate, not a wall |
| `AnUnloadableModule_IsShelvedRatherThanRefused_OnThePublishPath` | the shelf still stocks what it cannot run |
| `UnreadableBytes_AreRefused_NotWavedThroughAsUnknown` | `Indeterminate` fails closed |
| `ALinkableModule_ReportsANonZeroDenominator` | a clean verdict actually checked something |
| `AWholePlatformAssemblyThisDeploymentLacks_IsRefused` | the coarse-grained shape, and that a private dependency is *named unchecked* rather than refused |
| `AModuleReferencingASiblingModuleLandedMomentsEarlier_IsNotRefused` | the FALSE-refusal direction: a sibling module is not an absent platform assembly |
| `AnUnloadableModule_IsParked_AndTheOthersStillInstall` | the blast radius: one module, not the portal |
| `AParkedModule_ReadsAsQuarantined_NeverAsRestartRequired` | the false-promise rule |
| `ARequiredModuleRunningItsPreviousGeneration_IsPresent_NeverIncompatible` | a fallback (MeshWeaver#3649) is `Present` — the probe stays Healthy |

The fallback itself is proven end-to-end in
`test/MeshWeaver.Compiler.Pipeline.Test/ConfiguredModuleActivationTest.cs` (the `#3649` section):
a loadable generation is landed, a generation built for a newer platform is shelved over it, and a
boot composed as the portal composes it runs the previous one and reports it; both generations
unloadable stays incompatible; the GC keeps the fallback and reclaims it after an uninstall; the
adoption records what loaded; the projection onto the mesh's set carries the pointer; the
activation report names the row and never calls it "restart required".

## Related

- [Module Versioning](../ModuleVersioning) — what the pack lane records, what the build derives.
- [Module Build Architecture](../ModuleBuildArchitecture) — the one build shape, every repo.
- [NodeType Compilation](../NodeTypeCompilation) — the *other* identity lane, where MVID equality
  IS the gate, and why that is right there and wrong here.
- [The Cross-Repo Pair Gate](../CrossRepoPairGate) — the compile-time half of "two halves must move
  together". This is the run-time half, and it catches what no source gate can see: bytes already
  built, meeting a platform that is not theirs.
