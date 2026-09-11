---
Name: Module Adoption Policy
Category: Architecture
Description: The one rule for what an installation runs — keep the module you have until a newer one loads, load on what is measured rather than on what is declared, and switch the moment a new version ships. Set by the maintainer on 2026-09-07 after a day in which every production portal was held on a morning build by version strings, and the plan that implements it.
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
| **R2 — measured, never declared** | Whether a module loads is decided by **measurement** — the type-level link probe against the running platform (`ModulePlatformLink`, core #3552/#3538) and the actual load — never by a version string. A declared `minMeshVersion`, or a dependency on another module's version, is **advisory**: logged, shown on the status row, decides nothing. | `minMeshVersion` compared with `NuGetVersionComparer` at eight decision points, each of which refused, held or skipped on a miss. Because that comparator ranks `ci < rc < clean`, an `rc` or `3.0.0` floor on an installed record could never be satisfied by any `ci` build — which is how every production portal was held on the morning build for all of 2026-09-07 while every candidate would have loaded. |
| **R3 — eager adoption** | The moment a new module version ships — a newer version on the registry, or the same version rebuilt for this platform's identity — the installation adopts it, if it loads. If it does not load, R1 applies and the row says so. | Adoption ran at boot and on catalog opens; a fallback generation was never re-examined when a loadable build for the same version appeared. |

The rules compose into one sentence: **run the newest thing that loads, keep what you have until then, and never let a string decide.**

## What "loads" means

Two lanes, two measurements, one fallback:

- **Compiled modules** (`modules/<name>@<gen>/`): `ModulePlatformLink.Check` links the entry assembly's type references against the running platform's surface before any load; the load itself is the second measurement. A refusal names the missing type. This gate exists since core #3552 and is unchanged by the policy — the policy makes it the *only* gate.
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

The rule was measured against on 2026-09-08 (memex.systemorph.com, image `3.0.0-ci.8079`): the
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
`MeshBuilder.ResolveModulePath` does, on purpose. Two things this fallback cannot reach: a riding
copy that *does* load shadows the image copy, so the fallback never runs; and the sealed-set
conflict below still HOLDS the roll for the whole fleet whatever one process does at boot.

## A declined bundle risks stale TYPES, not just a slower boot

The bullets above treat *no adopted bundle* as a cost paid in boot time, because the content then
compiles in the mesh. Measured on 2026-09-10 (memex.systemorph.com, `3.0.0-ci.8238`, core
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

R1–R3 describe the consumer. A registry instance is also where producers publish: CI uploads module bundles to it (`POST /api/plugins/bundles/{plugin}` → `ModuleLandingService.ShelveModule`), and since #3461 two lanes publish the same module — core CD's `plugins-bake` at the gate's Plugins commit, and the module repository's own publication. Core CD runs take 40–50 minutes, so the **older** build routinely arrives **last**. Until #3996 every accepted upload moved the registry's activation head. Measured on memex.meshweaver.cloud on 2026-09-11: `MeshWeaver.Mail.MicrosoftGraph` 1.7.0 landed at 02:49Z and 1.6.1 displaced it at 03:00Z; the activation entry read `Version=1.6.1 PreviousVersion=1.7.0`, so the next restart would have silently un-shipped a merged change. `MeshWeaver.AI` showed the same shape that night.

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

**A record that already regressed** — the 2026-09-11 memex.meshweaver.cloud state, head `1.6.1` with `1.7.0` as its fallback — is not rewritten by an older or equal upload; the newer generation stays retained as the fallback. It heals on the next publish of `1.7.0` or higher, which the next core CD cycle delivers once its gate carries that version.

**Not covered: two replicas at once.** Landings are serialised within one process. Two replicas landing the same module within the same few seconds each decide against the entry they read before the other wrote, and the later write wins — [#4026](https://github.com/Systemorph/MeshWeaver/issues/4026). The #3996 incident was sequential (eleven minutes apart); what remains is the concurrent case.

## What the platform roll gates on

The self-updater and the CD post-promote gate select **the newest release on which no installed module is unloadable**. Concretely, per installed package: a build published for the target identity exists (it will be adopted), *or* the landed generation links against the target's surface, *or* neither can be shown — which is reported as *indeterminate*, never as clearance and never as a hold. Declared floors do not enter. A missing content bake does not enter (it is reported as "would compile at boot: …"). The sealed-set consistency check (#3175/#3221) stays: two builds of one platform assembly in one identity is a torn publication, and torn publications are refused whole.

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
- **Pack-time floor lint** (`check-module-floors.py`, `check-module-platform-floor.py`): a module built against pin X that declares a floor above X is an authoring error and still fails the pack. The floor is documentation for humans; the runtime does not read it as a gate.

## Why the version string was the wrong instrument

A `minMeshVersion` is a claim written before the platform it names exists. It is authored, so it can be absent or wrong; it is coarse, so a `3.0.0` line cannot express "after commit X"; and it is compared by a total order (`ci < rc < clean`) that encodes a release *policy*, not compatibility. Every one of those properties produced an outage this year: rc7 floors deadlocking a registry against its own roll (2026-08-22), a `3.0.0-rc14` floor naming a platform that never existed, and the 2026-09-07 hold. The measured link probe has none of them: it reads the bytes, it answers per type, and it cannot be out of date because it is computed against the platform actually running. What the string was *for* — telling an operator which platform a module was built against — is served by the identity the bundle already records (`frameworkMvid`) and by the status row, not by a gate.
