---
Name: The Module Platform Link Gate
Category: Architecture
Description: A module's platform requirement is not a version string an author writes — it is the set of types its bytes are linked against. This is the gate that measures that requirement against the platform actually running, refuses a module the process cannot load, parks the generation instead of the portal — and, through the surface every bake publishes, holds a platform roll only when a landed module provably cannot load on the target.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z"/><path d="M9 12h6"/><path d="M12 9v6"/></svg>
---

# The Module Platform Link Gate

> ✅ **Rule change, 2026-09-07 (maintainer) — [Module Adoption Policy](../ModuleAdoptionPolicy), implemented by [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648), [#3649](https://github.com/Systemorph/MeshWeaver/issues/3649), [#3650](https://github.com/Systemorph/MeshWeaver/issues/3650) and [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651).** This page describes the mechanism as it runs after those changes: a declared floor is advisory, a refused generation falls back to the previous one, a new build is adopted eagerly, and a platform roll is held only by a module that provably cannot load on the target.

**A module is adopted on what this process can LOAD, never on a version string.**

The 2026-09-09 maintainer clarification is explicit: different platform/dependency versions are
acceptable while the referenced API is compatible; identical versions are insufficient when the
API is not. `ModulePlatformLinkTest` compiles separate contract assemblies to exercise both
directions of version skew, additive API changes, and removal of a referenced type without a
version change. It also verifies that a refused upgrade leaves the installed generation and its
bytes intact. This probe checks **types**, not member signatures; actual load/install/execution
checks must cover missing methods or changed constructors. A successful type probe is not a
claim that those member checks ran.

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
| `Indeterminate` | the check could not be MADE (unreadable bytes; a platform copy that would not parse; no published surface) | at landing and at boot: refuse. At the **roll gate**: report — see *At the roll* below |

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

### 2026-09-12 — assembly versions are compared, asymmetrically (MeshWeaver#4083)

> This section **narrows** the two paragraphs above it. Where they disagree, this one holds.

**What happened.** On 2026-09-11 core #4012 bumped YamlDotNet 16.3.0 → 18.1.0. `MeshWeaver.AI`
references YamlDotNet directly and versionless, was built on an image carrying 18, and landed
through the registry on portal pods whose image shipped 16. The probe said `Linkable`. Every new
pod crash-looped at hub construction on `FileLoadException 0x80131040` (memex-cloud, 60+ restarts;
Memex#281 carried the pin move). Two independent causes, neither of them the `MeshWeaver.` prefix
filter — that line sits inside the *not carried* branch, and a running portal carries YamlDotNet:

1. **The closure rule excluded the reference.** The bundle shipped its own YamlDotNet, so the
   reference fell under *"built together with the module"* — a premise that is false exactly when
   the platform carries the same simple name, because the platform's already-loaded copy is what
   the loader binds and the bundle's copy never runs. The pair that decides is module ↔ platform.
2. **No version was ever compared.** `ResolveScope` kept only the referenced assembly's *name*;
   the comparison was type-name existence, and both YamlDotNet majors carry the same type names.
   `ModuleLinkState` had no state to put a `FileLoadException` verdict in even if one had been
   computed — `Unlinkable` models the `TypeLoadException` shape.

**The rule now.** For every assembly reference of a module whose simple name the platform surface
carries — third-party included; `MeshWeaver.*` keep the type-identity rule *and* are compared like
everything else — the reference's manifest **version and public key token** are compared against
the **platform's** copy, and the module's own closure no longer short-circuits a reference the
platform's copy would win (`ModulePlatformSurface.IsPlatformBound`: on a running process the
trusted platform assemblies and the application directory; every assembly of a published document
or a file set). The verdict is:

| Reference vs platform copy | Verdict | Why |
|---|---|---|
| **higher** version | **`BindingConflict`** — hard: refused at landing and boot, `ModuleUnloadable` at the roll | the loader refuses the bind itself; the exception is certain, not a risk |
| **lower** version | `Linkable`, with the drift on `Advisories` | the loader rolls **forward** — patch drift is ordinary, and a gate that reds on it is switched off within the week |
| equal | as before | — |
| different **public key token** under the same name | **`BindingConflict`** | a different key is a different assembly to the loader, whatever the versions |
| the surface records **no version** | `Linkable`, advisory | unknown is unknown — never a hard verdict on a document that predates the section |

**Why asymmetric.** .NET binding rolls forward and never back: a request for `16.3.0.0` binds
happily to a loaded `18.1.0.0`, a request for `18.1.0.0` against a loaded `16.3.0.0` is
`FileLoadException`. A symmetric equality check would red every portal whose image is a patch
ahead of a module's build — which is the ordinary state of the fleet between waves — and the gate
would be switched off. The check refuses only what the loader refuses, and *reports* the rest.
`AssemblyVersion` is pinned per line (`3.0.0.0` across the whole 3.0 line), so for `MeshWeaver.*`
the comparison is a no-op today and starts deciding when a module built on the next line meets an
image on this one — which is exactly a bind the loader would refuse.

**Both directions of the incident.** The probe runs on the *same* code (`ModulePlatformLink.Check`)
at every call site, and the new state is honoured at each: **(i) the roll candidate** —
`ReleaseAvailabilityService.SelectRollTarget → Judge → ModuleLinkObservation.Measure` links every
landed module against the *candidate's* published surface; `BindingConflict` is the
`ModuleUnloadable` hold, the roll-forward drift is a verdict advisory. **(ii) the module landing**
— `ModuleLandingService.LandCore` links the incoming bytes, closure and all, against the *running*
surface before anything touches the disk (this path already probed; the closure rule was what hid
the reference), and refuses on `MayLoad == false`, which `BindingConflict` is. The boot probe
(`MeshBuilder.TryLoad`) and prebuilt adoption (`PrebuiltAdoptionPolicy.AfterLink`) refuse the same
way. There is no call site at which a `BindingConflict` reads as anything but a refusal, because
`MayLoad` is true for `Linkable` alone.

**The document.** `platform-surface.json` gains an `identities` sibling of `assemblies`, keyed by
the same names — version and public key token per listed assembly, read from each assembly's
manifest without loading it:

```json
{
  "identity": "s5b8b0e2c…",
  "assemblies": { "YamlDotNet": ["YamlDotNet.Serialization.Deserializer", "…"], "…": [] },
  "identities": { "YamlDotNet": { "version": "16.3.0.0", "publicKeyToken": "ec19458f3c15af5e" }, "…": {} }
}
```

A sibling, not a richer `assemblies` value, because every reader shipped before this date requires
each `assemblies` value to be an array of strings and throws otherwise — and ignores an unknown root
property. An older platform reads the new document as before; a newer platform reading an older
document sees no identities and reports every version comparison as unknown. The producer is
unchanged in kind: `ModulePlatformSurface.ToJson`, written by `BakeOutput.WritePlatformSurface`
and by `mw-plugin-test platform-surface`; `publish-bake-bundles.sh` uploads whatever the bake
wrote.

**What this still does not see.** Member-level skew (below), and the publish side: nothing yet
compares a bundle about to be published against the images the fleet actually runs (#4066). Every
receiving pod measures for itself, with this probe.

Tests: `ModuleLinkVersionTest` (the 09-11 shape verbatim — two stand-in `YamlDotNet` builds with
identical type names — every arm of the table, the closure exception for a module's own superseded
sibling, and both directions on the real landing service and the boot-shaped check) and, in
`ReleaseLinkGateTest`, `AModuleBoundAboveTheTargetsVersion_HoldsTheRoll_NamingBothVersions` /
`AModuleBoundBelowTheTargetsVersion_Clears_WithTheDriftAsAnAdvisory` for the roll-candidate
direction.

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
- **The image-shipped copy is the last step (MeshWeaver#3735).** When the previous generation
  does not load either — or the entry holds none — and the module is one the IMAGE also ships
  (a `Modules:Assemblies` baseline entry the landed store entry displaced), the loader tries the
  image's copy through the same probe and load, and on success registers a `FallbackModule` with
  `RunsImageBaseline` — stderr line `'<name>' runs the image-shipped baseline (<path>) because
  v1.3.0 (gen B) cannot load here: …`, status row `runs the image-shipped baseline; v1.3.0 (gen
  B) landed but does not load here: …`, and `@image` (`FallbackModule.ImageBaselineGeneration`)
  as its running generation everywhere a generation is recorded. The boot union
  (`ComputeEffectiveModuleEntries`) carries the displaced baseline entry on the effective module
  (`EffectiveModule.BaselineEntry`) and the portal resolves it through
  `MeshBuilder.ResolveModulePath` WITHOUT the module root — the image's own closure, never the
  landed tree — onto `ModuleInstallCandidate.ImageBaseline`. Until this step existed the
  substitution happened on the DLL's existence alone, before anything was measured, so a
  refused store generation SHADOWED the image copy that loads by construction: on
  memex.systemorph.com (2026-09-08, image `3.0.0-ci.8079`) a two-week-old store generation of
  `MeshWeaver.Blazor.Views` was refused — `requires 'MeshWeaver.Graph.AnchoredComment
  (MeshWeaver.Graph)'`, a type the platform had since removed — the entry held no previous
  generation, the image's copy was never tried, and every skinned control on the portal rendered
  its fallback HTML. The probe did its job; the fallback order was one step short. An image copy
  that does not load either leaves the module incompatible, with BOTH reasons on the record; a
  generation that LOADED and then failed to install is named on stderr as the shape the image
  copy cannot rescue (one assembly per simple name), never left as an absence that reads like
  "the image had nothing".
- **The GC keeps it.** `CollectGarbage` references `PreviousDirectory` exactly like `Directory`,
  and the running generations the adoption records (below). The `@image` stand-in matches no
  directory by construction, so it reclaims nothing on its account.
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

### At the roll — the surface travels as a document

**A platform roll is held only by a module that provably cannot load on the target, and by
nothing declared** (MeshWeaver#3651; the maintainer's rule of 2026-09-07,
[Module Adoption Policy](../ModuleAdoptionPolicy)). On that day every production portal sat on its
morning build: the declared floors declined every candidate (lifted by MeshWeaver#3648), and the
satellites' missing bakes for the new identity would have held it again the moment the floors were
lifted — while a boot compile of those courses succeeds on every pull request, and every candidate
would have loaded. So the roll gate asks the same question this page's probe asks at boot — *would
these bytes load there?* — about a platform that is **not running anywhere the gate can reach**.

The answer needs the target's type surface at gate time, and the one process that can write it is
the bake, because the bake runs inside the target image. Every bake therefore writes
**`platform-surface.json`** beside `framework-mvid.txt` (`BakeOutput.WritePlatformSurface`, from
`ModulePlatformSurface.ToJson`), `publish-bake-bundles.sh` uploads it beside `_complete` for every
identity, and `PublishedBundleCatalogue` reads it back (`ModulePlatformSurface.FromJson`). The shape
is deliberately minimal — the identity the document is keyed to, and per assembly the full type
names it exports, exactly the set `TypesOf` answers on a running process:

```json
{
  "identity": "s5b8b0e2c…",
  "assemblies": {
    "MeshWeaver.Blazor": ["MeshWeaver.Blazor.BlazorView`2", "…"],
    "MeshWeaver.Mesh.Contract": ["MeshWeaver.Mesh.MeshNode", "MeshWeaver.Mesh.ModulePlatformSurface", "…"],
    "…": []
  }
}
```

An assembly whose surface the producer could not read is **omitted**, never written empty: an empty
list reads as "this assembly has no types" and would report every reference to it as missing. A
document that is not this shape is refused by the reader (`JsonException`) rather than read as an
empty surface — an empty surface refuses every module that binds a `MeshWeaver.*` assembly, which
would be a confidently wrong hold, not a missing measurement. `mw-plugin-test platform-surface
[<app-dir> --shared-frameworks <dir>]` writes the same document by hand for a bake that predates it.

At the gate (`ReleaseAvailability`, fed by `ModuleLinkObservation.Measure` — the one IO step, on the
file-system pool), per installed package that ships a compiled module:

| Situation | Answer |
|---|---|
| The target's sealed module set **declares** a build of the module | it will be adopted at the roll (MeshWeaver#3650); nothing to measure — its consistency is the sealed-set rule's business (MeshWeaver#3175) |
| No such build, and **nothing landed** on this instance | nothing keeps running across the roll; nothing to hold on |
| No such build, and a **landed generation** — the bytes that keep running | `ModulePlatformLink.Check(landed entry DLL, target surface)`: **`Unlinkable` ⇒ `ModuleUnloadable`, the hold**, naming the module and the missing types; `Linkable` ⇒ clear; `Indeterminate` ⇒ **reported** |

🚨 **`Indeterminate` is REPORTED at the roll, not refused — and that is the opposite of what this
page says for boot and landing, on purpose.** At boot and at landing the thing being refused is one
module's *load*, and the fallback keeps the previous generation serving; the blast radius is one
module. At the roll the thing that would be refused is the *whole platform's* update, for every
module, on a publication that simply predates the surface — which is exactly the shape of the
2026-09-07 hold, reintroduced with a better excuse. So a release with no `platform-surface.json`,
a document that does not parse, or landed bytes that cannot be read is written onto the verdict's
**advisories** ("Views: whether its landed module … loads on 3.0.0-ci.8100 could not be determined
— no sealed source publishes platform-surface.json …"), logged, recorded on `Admin/UpdatePolicy`
and shown on the Updates tab — and decides nothing. The safety net after such a roll is this page's
boot-time probe (which DOES refuse), the keep-the-previous-generation fallback (MeshWeaver#3649),
and the readiness stall.

The same verdict carries the missing content bakes as a **cost**, not a hold
(`UpdatabilityVerdict.BootCompiles`, "would recompile at boot on …: education, crm"): the compile
is the code path every pull request of that content already proved green. `Modules:RequirePrebuilt`
— the opt-in strict mode in which the seeder refuses a boot compile and parks the type — keeps it
the hold it used to be everywhere. `RollSelection` therefore walks newest-first and stops at the
first release with no unloadable module; "no complete release" names the module.

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
| **`Fallbacks`, `@image`** (MeshWeaver#3735) | **present and running the IMAGE-SHIPPED copy; no landed generation loads here** — the row reads *runs the image-shipped baseline; vX (gen) landed but does not load here: …* | **none — the same; the health check names it, and a restart measures the same bytes and falls back again** |

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
activation report names the row and never calls it "restart required". The `#3735` section of the
same file proves the image-shipped step with the image copy resolved through the REAL baseline
resolver: the only landed generation is refused and the image copy runs, reported, recorded as
`@image` on the adoption, `Present` for `Modules:Required`, and named on the activation report;
a previous generation is tried BEFORE the image copy; an image copy that does not load either
leaves the module incompatible with both reasons on the record (the negative control on the
branch); an image entry listed but not shipped claims nothing; and a generation that loaded and
then threw at install is not replaced by the image copy.

The roll gate's half (MeshWeaver#3651) is `ModulePlatformSurfaceJsonTest` and `ReleaseLinkGateTest`
in the same project — real modules, a real published root on disk, the running process's own
surface document with one type removed as "the older target":

| Test | What it pins |
|---|---|
| `ToJson_ThenFromJson_CarriesTheIdentityAndEveryAssemblysTypes` | the document round-trips the identity and exactly the set `TypesOf` answers |
| `ARealModule_LinksIdenticallyAgainstTheLiveSurfaceAndTheDocument` | the same module, the same verdict and the same denominator on both |
| `ADocumentThatIsNotASurface_IsRefused_NeverReadAsAnEmptySurface` | fail closed on shape — an empty surface would refuse everything |
| `AModuleUnlinkableAgainstTheTargetSurface_HoldsTheRoll_NamingTheModule` | THE hold: `ModuleUnloadable`, module and type named, never Indeterminate |
| `ALandedModuleThatLinksAgainstTheTarget_Clears` | the gate is a gate, not a wall |
| `AModuleWithABuildPublishedForTheTarget_IsNotHeldOnItsLandedGeneration` | the published build is what will be adopted; the landed one does not decide |
| `AReleaseWithNoPublishedSurface_IsIndeterminate_ReportedNeitherClearanceNorHold` | the roll-time direction of the third state |
| `AMissingContentBake_IsACostTheVerdictNames_AndAHoldOnlyUnderRequirePrebuilt` | both arms of the bake rule on one fixture |
| `TheWalkStopsAtTheFirstReleaseWithNoUnloadableModule` | newest-first, past the unloadable one, naming it; the cost said on the outcome |
| `WhenEveryCandidateCannotLoadTheModule_NothingIsSelected_AndTheModuleIsNamed` | "no complete release" stays put and names the module |

## Related

- [Module Adoption Policy](../ModuleAdoptionPolicy) — the rule this gate is the only instrument of: run the newest thing that loads, keep what you have until then, never let a string decide.
- [Release Availability Gates](../ReleaseGates) — the roll gate that consumes the published surface.
- [Roll Selection](../RollSelection) — the walk that stops at the first release with no unloadable module.
- [Module Versioning](../ModuleVersioning) — what the pack lane records, what the build derives.
- [Module Build Architecture](../ModuleBuildArchitecture) — the one build shape, every repo.
- [NodeType Compilation](../NodeTypeCompilation) — the *other* identity lane, where MVID equality
  IS the gate, and why that is right there and wrong here.
- [The Cross-Repo Pair Gate](../CrossRepoPairGate) — the compile-time half of "two halves must move
  together". This is the run-time half, and it catches what no source gate can see: bytes already
  built, meeting a platform that is not theirs.
