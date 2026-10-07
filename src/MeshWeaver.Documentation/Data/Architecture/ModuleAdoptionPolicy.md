---
Name: Module Adoption Policy
Category: Architecture
Description: The one rule for what an installation runs — keep the version you have until a newer one can run here, use a package version only when the running platform satisfies its declared minMeshVersion (and install the newest such version), and switch the moment a new version ships. Why the floor was advisory after 2026-09-07, why it holds again after 2026-09-27, and the one comparator that keeps both incidents closed.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="M3.3 7 12 12l8.7-5"/><path d="M12 22V12"/></svg>
---

# Module Adoption Policy

**Maintainer directive, 2026-09-07:** *if no plugin version is shipped, we use the old one. We make
it load despite a newer dependency on the platform or on another module. As soon as a new module
version ships, we start using it. We must become far more robust during deployments.*

This page is the authority on what an installation runs. Every other architecture page that
describes a floor, a hold, a skip or a decline on the module lane defers to it; where such a page
still describes the older mechanism, it says so in a banner that names the implementing issue.

## The three rules

> **Who may adopt unattended is a PER-PACKAGE choice** since 2026-09-14 — `updatePolicy` on the
> install record (`Auto` / `Notify` / `None`), seeded from `PluginCatalog:DefaultUpdatePolicy`, and
> independent of the platform's image policy on `Admin/UpdatePolicy`. The rules below say WHAT an
> installation runs once a version is adopted; [Plugin Update on Green Build](@/Doc/Architecture/PluginUpdateOnGreenBuild)
> says whether the adoption happens by itself.

**Maintainer clarification, 2026-09-09: compatibility follows the used API, not a platform pin.**
Keep a module usable across platform and dependency releases when the contracts it uses remain
compatible. A different version, build commit or MVID is not evidence of incompatibility. A
removed type/member, changed required signature or another actual linking/loading failure is.
Conversely, identical version strings do not make incompatible bytes safe.

This is a runtime contract. Reproducible compiler inputs and content-addressed cache keys record
what produced an artifact; they must not become an exact-version requirement for running a
compiled module. A NodeType bake with another build identity is not reused blindly: compile its
source against the current platform. That cache miss is not a declaration that the feature is
incompatible. Explicit strict-prebuilt policy remains an operator choice.

The compatibility regression suite must include positive and negative controls: unchanged used
APIs across different assembly versions, additive APIs/body changes, removal of a referenced API
even with an unchanged version, incompatible member signatures, and preservation of a working
generation when an upgrade fails. Exercise real metadata/loader paths; tests that compare version
strings alone cannot prove these claims. `ModulePlatformLinkTest` covers the type measurement and
rejected-upgrade continuity. Member compatibility also needs actual loading/execution: the current
type-level probe cannot establish it. Do not describe a successful type probe as proof that every
method call is compatible.

| # | Rule | What it replaces |
|---|---|---|
| **R1 — continuity** | An installation always runs *some* version of every module it has installed: the newest one that **loads**. If nothing newer ships for the platform it runs, the version it has keeps running. Nothing removes a working module because a newer one exists but cannot load. | A landed generation that does not load leaves the module **absent** (only image-shipped modules had a baseline to fall back to); a shelved landing overwrote the only reference to the loadable generation. |
| **R2 — declared floor, then measured** | A package version declares `minMeshVersion`. An installation **uses a version only when its running platform is at or above that floor**, and normally installs the newest such version (policy [`package-min-mesh-version`](../PolicyNotProse)). The floor is decided by ONE comparator (`PlatformFloor`, below); a floor that cannot be ordered against the running version proceeds as **advisory**. Whether a version that passes the floor LOADS is then **measured** — the type-level link probe (`ModulePlatformLink`, core #3552/#3538) and the actual load. | R2 as first written — *"measured, never declared"* — made the floor advisory everywhere after 2026-09-07. On 2026-09-27 that let packages built for a newer platform sync onto older ones — see *R2: the declared floor* below. |
| **R3 — eager adoption** | The moment a new module version ships — a newer version on the registry, or the same version rebuilt for this platform's identity — the installation adopts it, if it loads. If it does not load, R1 applies and the row says so. | Adoption ran at boot and on catalog opens; a fallback generation was never re-examined when a loadable build for the same version appeared. |

The rules compose into one sentence: **run the newest version whose floor this platform meets and that loads, keep what you have until then, and never let an unorderable string decide.**

## R2: the declared floor

The rule (policy [`package-min-mesh-version`](../PolicyNotProse), which carries the maintainer's
words and the date): *source must ask for which min version; any version above the minimum is used;
normally we attempt to install latest.* It replaces R2's first wording, *"measured, never
declared"*, for package and version selection. R1 and R3 are unchanged, and the link probe stays as
a second measurement; it is no longer the only gate.

### Why the floor holds again — 2026-09-27

Store 1.16 used `IPaymentProvider` billing-portal members (core `deef2cfa15`) and Hosting used
`DeploymentContent.AnnouncementKeySecret` (core `be5d157273`). Both synced onto instances running
`3.0.0-ci.9412`/`9414`, images that predate those commits, and failed to compile: `CS0117`/`CS1061`,
10 Store and 4 Hosting NodeTypes with no usable assembly. Both packages declared the platform they
needed. Nothing read it:

- the **source-content lane** (`PackageUpdateReconciler`, `CatalogLayoutAreas.InstallOrUpdate`,
  `PackageInstaller.Install`) decided an update by the manifest hash alone;
- the **compiled-module lane** (`ModuleUpdateDecision`) computed the floor and never branched on it;
- the **link probe** measures a compiled module's assembly. It cannot see NodeType **source**, which
  compiles in the mesh after it has landed. That was the failure.

The tests that existed asserted the opposite. `ModuleFloorAdvisoryTest` pinned "advisory
everywhere", and no test drove the content lane with a floor at all. A newer package landing on an
older platform was the tested behaviour.

### One comparator: `PlatformFloor`

`MeshWeaver.Plugin.Packaging.PlatformFloor.Evaluate(floor, running)` is the only floor decision.
Every consumer calls it: the module update decision, the content reconcile, the install
orchestrator, the installer, the per-module GitSync decline and the prebuilt-adoption policy.
`OneFloorComparatorGuard` fails when a `src/` file compares a declared floor with SemVer anywhere
else. The running version has one reader, `PlatformBuildInfo.RunningPlatformVersion`: the image's
`MESHWEAVER_PLATFORM_VERSION` with build metadata stripped. `ModulePlatformFloor.RunningVersion` and
`PrebuiltAdoptionPolicy.RunningPlatformVersion` both delegate to it. (The module lane used to read
MeshWeaver.Graph's assembly stamp, which carries no run number.)

**Only a comparable floor strictly above the running version holds.** That rule is what keeps the
2026-09-07 trap closed. SemVer ranks `ci < rc < clean`, so an `rc` or clean floor can never be met by
a `ci` build.

| Floor vs running | Verdict |
|---|---|
| no floor | none |
| both carry a run number (`3.0.0-ci.N` vs `3.0.0-ci.M`) | **held iff N > M**. The version line is ignored, so a mislabelled line loses. |
| either side is `-ci.0` (a local source build) | advisory: proceeds |
| floor or running version unreadable (`latest`, `unknown`, blank) | advisory: proceeds |
| otherwise, by numeric core | a higher core is **held**, a lower core is satisfied. An equal core with a clean floor (`3.0.0` vs `3.0.0-ci.N`) is satisfied. An equal core with a labelled floor (`3.0.0-rc8` vs `3.0.0-ci.N`) is unordered: advisory, proceeds. |

`PlatformFloorTest.TheSeptember7Trap_NeverHolds` pins the second half of that table, and
`TheSeptember27Shape_Holds` pins the first. `ModulePlatformFloor.DeclineReason` keeps its SemVer
comparison for two jobs that decide nothing at runtime: the wording of the status-row advisory, and
the pack-time lint's parity oracle.

### Phase 1: the consumer holds (done)

| Lane | On an unmet floor |
|---|---|
| Compiled module (`ModuleUpdateDecision`, via `PluginBundleClient.AdoptModule`) | `SkipPlatformBelowFloor`. The bundle is not even downloaded, and the landed generation keeps running. The floor applies to a `Land` answer only, so it never hides a more specific skip. |
| Source content: an update (`PackageUpdateReconciler`, `CatalogLayoutAreas.InstallOrUpdate`) | Held before any file is fetched. The installed version keeps running. The install record carries `heldUpdate` (*"held: 1.16.0 needs platform ≥ 3.0.0-ci.9494, running 3.0.0-ci.9412 — updates when the platform rolls"*), and the update applies on the first reconcile after the platform rolls. A record already at the candidate's hash is not held: re-landing what is already there replaces nothing. |
| Source content: a fresh install (`InstallOrUpdate`, `PackageInstaller.Install`) | Refused with `PackagePlatformFloorException`, which names both versions. Nothing is fetched or written. |
| Source content: any other caller of the installer over an EXISTING record (a maintenance `RefreshModules`) | Refused the same way unless it re-installs the content already recorded (`AllowedOverExistingRecord`, 2026-10-04). Before, an existing record waved any version through. |
| GitSync (`ModuleSyncDecision`) | That one module is declined through the same `PlatformFloor` decision. Its siblings still sync. |
| Prebuilt adoption (`PrebuiltAdoptionPolicy`) | Declined through the same decision, and the content compiles instead. This path used to hold a private SemVer copy, which kept the 2026-09-07 trap live. |

The self-update roll gate (`ReleaseAvailability`) is unchanged, and no roll waits on a floor.

**A hold ends by itself (2026-10-04).** "Updates when the platform rolls" is kept: every roll is a
boot, the boot reconcile re-decides each held candidate, and the safety-net timer and every
module-published broadcast do the same. The first pass on which the floor is met CLEARS the record's
`heldUpdate`/`heldSince` and decides the update again. Tonight's AI hold did not apply after the
roll for a SECOND reason the sentence never named: its partition is sync-owned, so the decision then
degraded to the reminder, and no lane ever landed a sync-owned package's module — see
[One Partition, One Bookkeeping](../OnePartitionOneBookkeeping), gate 1e, for how that now converges.
The target such a hold converges to is defined once in [One Promotion Gate](../OnePromotionGate) →
*The target set*.

### Blocking tickets

A hold is correct only while the working version keeps serving and the hold is visible. Nobody
acts on a silent hold. When an update is held, the instance files **one blocking ticket** through
the dispatch channel it already has: `IPackageHoldDispatch`. The portal registers
`PackageHoldHandover`, which posts a signed `package-update-held` event with `severity: blocking`
into the control inbox on the self-update announcement's own route (`Hosting:ControlInbox:Url` +
`Hosting:ControlInbox:Secret`). The event carries the package, the held version, the floor, the
running version, the version that keeps running, and what unblocks it.

- **One ticket per held state.** The key is `heldUpdate`, which names the package, the version, the
  floor and the running platform. A ticket is re-sent only when that state changes, once a day while
  it lasts, or when the last attempt was not accepted (`heldUpdateDispatchedAt` stays unset). A
  reconcile tick never sends one on its own.
- **No route is a finding.** On an instance with no control inbox, or no dispatch registered, the
  record says `heldUpdateDispatch: "NOT dispatched: …"`, naming what is missing, and the log says
  it at Warning. Nothing crashes.
- 🚨 **The control-plane half is owed by MeshWeaver.Plugins.** Until it lands, the control inbox
  stores the event and the watcher drops it. `PlatformBuildInboxWatcher` admits only a self-update
  announcement under a deployment's own key, and `TriageIntake.IsTriageKind` does not list
  `package-update-held`. What Plugins must add:
  1. admit `package-update-held` under the deployment key, as `SelfUpdateHandoff` does for
     `self-update-*`;
  2. route it to triage as a **blocking** `Hosting/TriageItem`, keyed on (deployment, package,
     held version, floor), so that a re-send updates the same item.

### Phase 2 (not done)

- The **registry retains older versions with their floors**. Today it serves only the head and one
  fallback.
- Installers pick the **newest version whose floor is satisfied**, instead of holding the head.
  That completes *"normally we attempt to install latest"*: latest *satisfiable*.
- GitSync resolves the **newest satisfiable commit** of a module, instead of only declining the head.

## What "loads" means

Two lanes, two measurements, one fallback:

- **Compiled modules** (`modules/<name>@<gen>/`): `ModulePlatformLink.Check` links the entry assembly's type references against the running platform's surface before any load; the load itself is the second measurement. A refusal names the missing type. This gate exists since core #3552 and is unchanged by the policy. It runs on a version that has passed the declared floor (R2).
- **NodeType content** (`prebuilt-bundles/<identity>/<source>/`): a baked assembly is adopted only for the exact framework build identity it was baked against (`PrebuiltAssemblySeeder.DeclineReason`, ordinal equality). That is also unchanged — a mismatched bake would be worse than none. What changes is the consequence of *no* bake: **the content compiles in the mesh**, which is the same code path every pull request already proves green. A missing bake is a cost (boot time), not a reason to hold a roll. `Modules:RequirePrebuilt` remains the opt-in strict mode for installations that prefer a named park to a compile.
- **Fallback**: when the newest generation of a module does not load, the previous generation that did is loaded instead, reported as such, and kept from garbage collection. When that one does not load either — or the installation holds none — and the **image ships a copy of the same module** (the `Modules:Assemblies` baseline entry the store generation displaced), the image's copy runs, reported as such ([#3735](https://github.com/Systemorph/MeshWeaver/issues/3735)). Only when *nothing* loads is the module absent — and that is the readiness probe's business (the rollout stalls on the pod, the previous pods keep serving), which is the last safety net and the one that has never failed.

**"Loads" includes "installs".** A generation that is refused by the link probe, that faults in
`Assembly.LoadFrom`, or whose provider attribute throws while its contributions are materialised
has not loaded in the sense of R1 — it contributes nothing, and a module that contributes nothing
is *worse* than one that is absent when it has displaced a copy that would have worked. The order
of the fallback is fixed and every step is **measured by the same probe and load as the one
before it**: the newest generation → the previous landed generation → the image-shipped copy →
absent, reported. The one shape the order cannot reach is a generation whose assembly *loaded* and
whose install then threw: the default load context holds one assembly per simple name, so neither
the previous generation nor the image copy can be loaded beside it. That shape stays absent and
reported, and the report names the image copy it could not substitute, so nobody reads the absence
as "the image had nothing" (the mechanism is on
[The Module Platform Link Gate](../ModulePlatformLinkGate)).

The rule was measured against on 2026-09-08 (the control instance, image `3.0.0-ci.8079`): the
module set pinned a two-week-old store generation of `MeshWeaver.Blazor.Views`, an assembly the
image also ships. The boot's link probe refused it correctly — it referenced
`MeshWeaver.Graph.AnchoredComment`, a type the platform had since removed — but the boot union had
already substituted the store entry in place of the baseline entry, so the image's copy was never
tried, the module "contributed nothing", and every skinned control on the portal rendered through
its fallback HTML. The probe was not the gap; the fallback order was, and this section is what
closes it.

🚨 **This is the CONSUMER half, and it masks the producer's defect rather than removing it.** A
portal that falls back to the image copy renders correctly while its publication still carries two
builds of one assembly name, so *"does it render"* is not evidence a bundle is clean. The producer
half — a module bundle never carrying a `MeshWeaver.*` copy the platform already ships, measured off
the image rather than declared in a list — is
[The Platform-Shipped Witness](../PlatformShippedWitness), and it probes the same two locations
`MeshBuilder.ResolveModulePath` does, on purpose. One thing this fallback still cannot reach: the
sealed-set conflict below HOLDS the roll for the whole fleet whatever one process does at boot. The
other — a riding copy that *does* load, shadowing the image copy so the fallback never runs — is
what the next section closes.

## Two copies of one module: the FRAMEWORK IDENTITY decides (#4161)

The fallback above runs when the store generation does **not** load. The harder case is the one
where it **does**: the image ships a copy of a module, the store holds another copy of the same
name, both link, and the wrong one wins. Until #4161 the boot union
(`ModuleActivationBoot.ComputeEffectiveModuleEntries`, pass 1) made *every* enabled store entry
whose landed DLL existed an override of the same-named `Modules:Assemblies` baseline entry. The
DLL's existence was the only gate.

Measured on memex-local, 2026-09-08/09 (Plugins#1483): the portal was built from source that day
(`3.0.0-ci.0`) and its image shipped its own `MeshWeaver.Blazor.Views`, written `2026-09-07
08:30:50Z`, mvid `8c833e20`. A store pack of the same module, built `2026-08-27 14:00:29Z`, mvid
`a87ef06f`, displaced it:

```
[ModuleLoad] MeshWeaver.Blazor.Views ← /tmp/meshweaver-pinned-modules/…@96af4c59/…
             (source=store, mvid=a87ef06f, written=2026-08-27 14:00:29Z)
```

The link probe passed, the module loaded, and its **view registrations** no longer matched the
control types the platform emits — so the Subscribe panel's outermost control rendered as
`StackControl { … }`: a whole-tree `ToString()`. Not a missing-view error, not a log line, just a
page that reads as broken. **"Loadable" and "correct for this platform" are different properties,
and the link probe only answers the first** — it asks whether the types a module *references* exist,
and a view pack that registers views against types the platform no longer emits references nothing
that is missing.

**The rule.** When a store entry's recorded framework identity
(`ModuleActivationEntry.FrameworkMvid`) differs from the identity the booting platform reports
(`PrebuiltAssemblySeeder.LiveFrameworkMvid`) **and the image ships a copy of that same module**, the
image's copy runs and the store copy is reported *declined: built for another platform* on the boot
skip channel. The image's copy is correct for this platform by construction — it was compiled with
it.

**Three bounds, each of which is the rule and not a caution:**

| Case | Verdict | Why |
|---|---|---|
| Identity **unrecorded** on either side | Unchanged — the store copy wins | R2: an unrecorded identity is absence of evidence, not evidence of difference, exactly as an unrecorded version is to the landing service. Reading it as a difference would decline every module landed before identities were recorded at all. Deliberately *not* `PrebuiltAssemblySeeder.DeclineReason`, which declines an absent identity — declining is the safe answer there and the damaging one here. |
| **Store-only** module (the image ships none) | Unchanged — the store copy wins | There is nothing to prefer it to. A declined module is an **absent** module, which is strictly worse than one whose identity does not match; this is the same trade "an unusable one must not override" already makes. |
| Identity **matches** | The store copy wins **if it is a newer release than the image's copy** — see the next section | The ordinary upgrade path (#2548) and the whole point of installing a module. The discriminator decides on a *difference*, never on being a store copy. |

**This is not the declared floor.** The floor is a string a module's author *wrote*. Since policy
`package-min-mesh-version` it holds a version only when it is comparable with the running platform
and above it (R2). The identity is what the producing toolchain **measured** about the bytes it
emitted, compared against what this process measures about itself. It only chooses between two
copies the deployment already holds, and never where there is just one.

**It is self-healing, and it cannot tear a replica set apart.** R3 lands a bundle whose served
identity differs from the landed one at the same version (`ModuleUpdateDecision`), so the moment the
registry serves this module built against this platform it lands and wins again — no operator step,
no re-install. And every replica of a deployment runs one image, so every replica states one
identity and reaches one verdict (#3395 holds).

**The decline has to reach the loader, not just the log.** A baseline entry resolves through
`MeshBuilder.ResolveModulePath`, whose probes are **landed root → image → app closure** and whose
landed probe looks in the *fixed* `modules/<name>/<name>.dll`. Generation landing writes
`modules/<name>@<gen>/`, so that probe misses on its own — but an entry from before generation
landing carries no `Directory` and its bytes sit in exactly that folder, so the resolver would hand
back the copy pass 1 had just declined, silently, with the decline line already printed. The
baseline emitted in place of a declined store copy therefore carries
`EffectiveModule.PreferImageCopy`, and `ResolveLoadPath` resolves it with **no landed root** —
image → app closure. Every other baseline entry keeps the probe order it always had.

The seam is explicit: the identity is a **parameter** of the pure computation, not something it
reads from the process. The six-argument overload states none and declines nothing, which is what a
host compiled against the previous platform binds — an optional parameter is a compile-time default
and not a binary one, so a discriminator that silently read the process's own identity would change
what those hosts do at their next boot with nothing in their diff. `ModuleIdentityDiscriminatorTest`
(`test/MeshWeaver.Compiler.Pipeline.Test`) lands every copy through the real landing service and
carries all three bounds as negative controls.

## When the identity cannot decide: the image copy's VERSION does (#6044)

Under the compatibility key (policy `platform-backwards-compatibility`) every build of one platform
epoch states the **same** framework identity, so the rule above stopped discriminating: a store copy
built from *older* sources than the image stated the image's own identity and overrode it. Measured on
memex.meshweaver.cloud, 2026-09-28: the image `3.0.0-ci.9554` was built from Plugins `8931656f`, and
the module store held `MeshWeaver.AI` 1.16.3 and `MeshWeaver.Hosting.Instance` 1.0.6 built from the
older `6d21172e` / `84f5e5f1`. Both stated identity `c003e001`, both overrode the image's own copies,
and a correct image rendered the pre-`8931656f` admin page. The image's locks were not yet settled,
so its copies carried the **same** version numbers as the store copies over newer sources.

**The record.** A module DLL carries no package version of its own (its informational version is the
platform's, #3732), so the image build writes it down: the closure lane
(`memex/MeshModulesPublish.targets`, `WriteMeshModuleSeedStamps`) finds the package that declares
each closure module (`<package>/index.json` → `content.module`) and writes
`modules/<Name>/module.seed.json` beside the DLL — `version` and `moduleVersion` from that package's
`manifest.lock` at the commit the image is built from. `ImageModuleSeed` reads it.

**The rule.** A store copy of an image-shipped module overrides the image's copy only when its
recorded version is a **strictly newer** release than the stamp's (`ImageModuleSeed.DeclineReason`).
Otherwise it is declined exactly as an identity mismatch is: named on the `[ModuleActivation] SKIPPED`
line, the baseline emitted with `PreferImageCopy`, and reported by `PendingModuleActivations` as
**declined**, never as "a restart activates it".

**Why EQUAL loses.** An equal version is the case that happened. Publication follows a settled lock,
and settling bumps the version, so a store copy at the image's own version carries the same sources or
older ones — never newer. The image's copy was also compiled against this very platform. Preferring it
is right in every equal case there is.

| Case | Verdict |
|---|---|
| Store version **newer** than the image stamp | Store copy wins — the upgrade path |
| Store version **equal or older** | Image copy runs; store copy declined, named |
| **No stamp** (image from before it, module no package declares), stamp without a version, or store entry without one | Unchanged — decided by the identity rule alone (R2) |
| **Store-only** module | Unchanged — nothing to prefer it to |

It is self-healing in the same way: the next published release of the package is newer than the
image's stamp, lands, and wins. The pure rule is pinned by `ImageCopyVersionDiscriminatorTest`
(`test/MeshWeaver.Compiler.Pipeline.Test`), whose control first proves the store copy wins without the
stamp.

## A declined bundle risks stale TYPES, not just a slower boot

The bullets above treat *no adopted bundle* as a cost paid in boot time, because the content then
compiles in the mesh. Measured on 2026-09-10 (the control instance, `3.0.0-ci.8238`, core
`2287decb`, identity `s546f29f9…`) that is not the whole story: a module whose bundle is declined
can keep serving **types built from source older than the source the instance has installed**,
while the package's own install record reads installed, current and up to date.

`MeshWeaver.AI` had gained `ModelDefinition.ReasoningEffort` (Plugins#1556, 09-09 12:41Z) and
`ThreadMessage.Timing` (Plugins#1552, 11:59Z). The install record carried main's own file hashes —
`src/MeshWeaver.AI/ModelDefinition.cs` = `89fd0a2264…`, byte-identical to `origin/main`, which
declares the property — so the *sources* were current. The *types* were not: `get
@…/schema/ModelDefinition` listed no `reasoningEffort`, and a `patch` setting it wrote a new node
version with the property silently dropped. The instance's own health said why:

```text
bundle_adoption: Degraded — 25 adoption attempt(s), 0 assembly/assemblies adopted, 25 MISS(es) —
content the registry was meant to serve is compiled here instead: AI: FrameworkDeclined
(built against framework s72c27afab89c1007…, live framework is s546f29f90e235e61…)
```

The registry's bytes were therefore never in play. What served was the previously resolved build,
kept alive by the same-MAJOR **stale-but-serving** rule recorded on
[The Execute-Time Interlock](../ExecuteTimeInterlock) (#3844): a moved source fingerprint is
deliberately *not* a refusal, so `1.5.0 → 1.5.1` kept the old build. That is why a Store
`RefreshModules` re-land and two restarts each reloaded the identical generation and changed
nothing. What finally let fresh types through was a new `{package}@{version}` shelf entry —
`AI/index.json` 1.5 → 1.6 with **no source change** (Plugins#1586) — for which no previously
adopted build existed. Two minutes after that merged the instance installed `AI 1.6.0`, and the
property appeared on the served schema and survived a write.

Three consequences worth carrying:

- **"Installed, current version, sources current" is not "running these types."** The install
  record describes the *content* lane. Which assembly answers `schema/<Type>` is the *module* lane,
  and on a declined bundle the two can sit days apart with nothing red anywhere.
- **Re-landing is not re-building.** `RefreshModules` re-lands content; it does not dislodge an
  adopted build that the compatibility rule still accepts.
- **A version bump is a delivery lever.** Where a stale adopted build is serving, moving
  `{package}@{version}` is the supported way to force a fresh one — and it doubles as the
  experiment that separates "the shelf holds stale bytes" from "an adopted build is being kept".

🚨 **Do not read stale served types as the registry serving bad bytes.** The two are
indistinguishable from the consumer: same `[ModuleLoad]` line, same generation directory, same
mvid, and `[ModuleLoad]` never names the commit a bundle was built from. The discriminator is
`bundle_adoption` on `/health` — `FrameworkDeclined` means the registry's copy was never adopted,
so a fix aimed at the shelf would be aimed at bytes this instance never ran.

## The registry shelf: arrival order is not version order (#3996)

R1–R3 describe the consumer. A registry instance is also where producers publish: CI uploads module bundles to it (`POST /api/plugins/bundles/{plugin}` → `ModuleLandingService.ShelveModule`), and since #3461 two lanes publish the same module — core CD's `plugins-bake` at the gate's Plugins commit, and the module repository's own publication. Core CD runs take 40–50 minutes, so the **older** build routinely arrives **last**. Until #3996 every accepted upload moved the registry's activation head. Measured on the plugin registry instance on 2026-09-11: `MeshWeaver.Mail.MicrosoftGraph` 1.7.0 landed at 02:49Z and 1.6.1 displaced it at 03:00Z; the activation entry read `Version=1.6.1 PreviousVersion=1.7.0`, so the next restart would have silently un-shipped a merged change. `MeshWeaver.AI` showed the same shape that night.

The rule is the consumer's "never roll back unattended", applied to the publish route: **the head is the highest version the shelf holds, never the last upload to arrive.** The order is `NuGetVersionComparer`'s — the comparer `SkipOlder` uses — so a registry's head and its consumers' update decisions cannot disagree about which version is newer.

| The upload, relative to the current head | What happens |
|---|---|
| Strictly **older**, and the head's bytes are present | **Shelf-only.** The bytes land, the head does not move, and the publish answers 200 with `shelfOnly: true` and the reason. |
| **Equal** version | The head **moves**: a rebuild of unchanged source against a newer platform republishes under the same version (Plugins#931). Identical bytes resolve to the same content-addressed generation, so re-publishing them is a no-op. |
| **Newer** | The head moves; the displaced generation becomes the fallback (#3649). |
| Pre-release vs stable | SemVer order: `1.7.0` outranks `1.7.0-rc1`, and `3.0.0-ci.3758` outranks `3.0.0-ci.900` (numerically). A pre-release published after its stable is shelf-only. |
| Either version **unknown**, or not a SemVer version at all | The head moves, as before. An absent or unparseable version is no evidence of order (R2) — and `NuGetVersionComparer` reads an unparseable part as 0, so without this a `nightly` label would rank below every real version. |
| A newer head whose **entry DLL is gone** | The head moves. A record without bytes is not a version this registry holds, and refusing the one upload that could heal it would turn the rule into a self-sealing outage. A re-publish of the head's **own bytes** (the content address ignores the version label) resolves to the head's own directory instead: the landing restores the files it lost, and the head keeps its label. |
| A newer head that **does not link on the registry's own platform** | The head **stays**. The shelf warehouses modules for newer platforms, and boot runs the fallback when the head does not load there (R1). Letting an older loadable upload take the head would move the registry onto the older version the moment its own platform caught up — #3996 by another road. |

**What happens to a shelf-only upload's bytes.** It competes for the head's **fallback** slot (`PreviousDirectory`), which the modules GC never reclaims. It takes that slot when the slot is empty or its bytes are gone, when it loads on this platform and the recorded fallback does not, or — at equal loadability — when it ranks higher. Otherwise it is not retained: the publish response says `retainedAsFallback: false`, and the GC reclaims the generation. There is one fallback slot, so a registry holds at most two generations of a module (head and fallback) — the #3649 design, not a new limit. If a shelf-only upload moves the fallback while the head does not load here, the restart flag **is** raised, because boot runs the fallback and a restart genuinely changes what loads.

**Resolvable by version.** The bundle index lists the head and a retained fallback as separate entries at their own versions, newest first, and `GET /api/plugins/bundles/{plugin}/{version}` serves the generation that version names (`ModuleBundleSource.CollectVersion`). The index readers (`PluginBundleClient` and `mw module fetch`) take the first entry for a package, so they still get the head. A consumer that asks for a retained older version by name gets those bytes, never the head's bytes under another label. Versions match by their exact text, as the index advertises them — not by the SemVer comparer, which would make any two non-SemVer labels equal. An older upload whose bytes are identical to the head's is the head's generation: it is not listed under its own label, and its bytes are served as the head's.

**Deliberate rollback.** Re-publishing an older version no longer rolls a registry back: the publish route has no operator, only build jobs, and a build job never intends a rollback. Roll forward instead (publish the fix under a higher version), or uninstall the module on that registry first (`ModuleLandingService.RemoveModule` disables the entry, so the next publish is a first landing). The Store's adopt path (`LandModule`) does not carry this rule; the unattended lane that could reach an older version is already refused by `SkipOlder`.

**A record that already regressed** — the 2026-09-11 state of the plugin registry instance, head `1.6.1` with `1.7.0` as its fallback — is not rewritten by an older or equal upload; the newer generation stays retained as the fallback. It heals on the next publish of `1.7.0` or higher, which the next core CD cycle delivers once its gate carries that version.

**Covered: two replicas at once (#4026).** Landings are serialised within one process, and across replicas the rule used to be last-writer-wins: two replicas landing the same module within the same few seconds each decided against the entry they read before the other wrote. A landing no longer decides that way. It writes an immutable record of its own facts — its lane included, so the shelf's "never regress" and the adopt path's "an operator asked for it" both survive — and the head and fallback are DERIVED from every record present by replaying this rule in arrival order, so every replica reaches the same answer whatever order the writes landed in. [Module Activation Head Ownership](../ModuleActivationHeadOwnership) carries the on-disk format, why it stays readable by images already deployed, and the retention rule.

## What the platform roll gates on

The self-updater and the CD post-promote gate select **the newest release on which no installed module is unloadable**. Concretely, per installed package: a build published for the target identity exists (it will be adopted), *or* the landed generation links against the target's surface, *or* neither can be shown — which is reported as *indeterminate*, never as clearance and never as a hold. Declared floors do not enter the ROLL: they decide which package version an installation takes (R2), never whether the platform rolls. A missing content bake does not enter (it is reported as "would compile at boot: …"). The sealed-set consistency check (#3175/#3221) stays: two builds of one platform assembly in one identity is a torn publication, and torn publications are refused whole.

The safety net after a roll is boot-time, in this order: the link probe refuses what cannot load; the fallback keeps the previous generation; a module with no loadable generation makes the pod unhealthy and the rollout stalls, with the previous pods serving. That is what "robust during deployments" buys: the worst outcome of a wrong roll is a **stalled rollout with a named module**, never a portal that serves nothing and never an installation stuck on a month-old build because a string said so.

## The implementation plan

Four changes, in this order; each is one pull request against core with its tests, and each rewrites the page(s) it makes true.

| Step | Issue | Change | Pages it rewrites |
|---|---|---|---|
| 1 | [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648) | Floors become advisory at every runtime decision point (`ModuleUpdateDecision`, `LandFromBundle`, `LandCore`, boot, `ReleaseAvailability`, the status surfaces). Pack-time floor lint stays as authoring hygiene. **This also resolves the 2026-09-07 self-update deadlock without a production write.** | Modules, ReleaseGates, SelfUpdateTargetSelection, ModulePlatformLinkGate, PluginPackaging, ReleaseGateDenominator |
| 2 | [#3649](https://github.com/Systemorph/MeshWeaver/issues/3649) | Keep the previous generation: `ModuleActivationEntry.PreviousDirectory`, boot fallback in `MeshBuilder`, GC keeps it, the module set records what loaded, status rows name it. | ModuleSetConvergence, ModulePlatformLinkGate, Modules |
| 3 | [#3650](https://github.com/Systemorph/MeshWeaver/issues/3650) | Eager adoption: a fallback re-examines every new build for its version; a `ModulePublished` broadcast triggers the package's reconcile; a pending restart rolls the same image within the interval rules. | PluginUpdateOnGreenBuild, Modules |
| 4 | [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651) | The roll gate on measured loadability: the publication carries `platform-surface.json` per identity, `ReleaseAvailability` links landed generations against it, `ContentBakeMissing` is advisory, `RollSelection` stops at the first release with no unloadable module. | RollSelection, ReleaseStrategy, CiContentBake, ReleaseAvailability pages, SelfUpdateSchemaWall |

All four steps are implemented: 1 is [#3661](https://github.com/Systemorph/MeshWeaver/pull/3661), which carries 2 and 3 merged into it, and 4 is stacked on it as the pull request for [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651). The pages each step names now describe the mechanism as it runs once that change set ships, and their banners say so. The one-time exit from the 2026-09-07 hold was a pipeline roll (helm-release), taken by the maintainer's decision on the same day, with the satellites baked for the target identity first so the roll adopted their bundles rather than compiling them.

## What stays exactly as it is

- The **content bake identity rule**: a NodeType assembly adopts only for the identity it was baked against. Wrong bytes are worse than no bytes. 🚨 The per-type DEPENDENCY RECORD beneath it changed on 2026-09-10 and this rule did not: a module entry is now a FLOOR over the module's version rather than its MVID, so a module REBUILD stops declining a bundle while a genuinely older module still does — [The Dependency Record Floor](../DependencyRecordFloor). That is the same instinct as R2 one layer down (a different build is not evidence of incompatibility), and it relaxes nothing about the framework identity, the toolchain entry or the content key.
- The **seal**: a publication is real when `_complete` is written last and every listed file exists; a torn publication is refused whole (#3461, #3401).
- **Never roll back unattended**: an older served version is never adopted over a newer landed one (`SkipOlder`) — and on a registry, an older *published* version never displaces a newer landed head (#3996, the registry-shelf section above).
- **Never swap a module in a running process**: a new generation loads at the next restart; the policy makes that restart happen (step 3), it does not make the swap live.
- **Sources follow the seal** (Plugins#1430, core #3600): a module-bearing repository's sources advance only to the commit sealed for the instance's own identity.
- **Pack-time floor lint** (`check-module-floors.py`, `check-module-platform-floor.py`): a module built against pin X that declares a floor above X is an authoring error and still fails the pack. At runtime the floor holds a version (R2).

## Why the version string was the wrong instrument (2026-09-07), and what changed

A `minMeshVersion` is a claim written before the platform it names exists. It is authored, so it
can be absent or wrong; it is coarse, so a `3.0.0` line cannot express "after commit X"; and it was
compared by a total order (`ci < rc < clean`) that encodes a release *policy*, not compatibility.
Every one of those properties produced an outage that year: rc7 floors deadlocking a registry
against its own roll (2026-08-22), a `3.0.0-rc14` floor naming a platform that never existed, and
the 2026-09-07 hold.

Two things are different now. Floors are written by the producing build (`3.0.0-ci.N`, one shape,
policy `version-shapes`), not typed by hand against an rc line. They are compared by `PlatformFloor`,
which decides only what it can order and waves everything else through as advisory. The comparator
was the wrong instrument; the claim was never wrong in kind. Without it, 2026-09-27 showed that the
link probe alone lets source for a newer platform land on an older one.
