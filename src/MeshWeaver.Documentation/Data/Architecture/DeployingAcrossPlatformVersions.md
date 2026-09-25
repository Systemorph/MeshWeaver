---
Name: Deploying Across Platform Versions
Category: Architecture
Description: >-
  The deploy procedure under the ladder (policy platform-backwards-compatibility), for both paths —
  a compatible platform build, which needs no plugin seal and rolls platform and plugins as separate
  steps, and a declared compatibility break, where the platform roll is held per installed plugin
  until a replacement covering the target is sealed. What "build complete" means on each path, and
  what CD, the self-updater and the control lane check at each step, each tied to the code that runs.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 20h4v-4h4v-4h4V8h4"/><path d="M4 20V4"/></svg>
---

# Deploying across platform versions

Policy `platform-backwards-compatibility` ([Policy Not Prose](../PolicyNotProse)): **platform builds
are backwards compatible within a compatibility epoch.** Compiled bytes are keyed on the platform
COMPATIBILITY key `c<major>e<epoch>` (e.g. `c003e001`), not on a per-build identity, so every build of
one epoch reads and publishes the same key. Platform and plugins therefore roll as **separate,
independent steps** — the ladder:

```
platform1 + plugin1 → platform2 + plugin1 → platform2 + plugin2 → platform2 + plugin3 → platform3 + plugin3
                     (platform rolls first,  (plugin rolls, built
                      keeps old plugin bytes) against the RUNNING platform)
```

The one rung that is never taken is **platform1 + plugin2** — a plugin produced by a platform build
newer than the one running. Only a DECLARED break (an epoch or major bump) interrupts the ladder.

## Decision table

| Situation | Platform roll | Plugin roll | Seal needed first? |
|---|---|---|---|
| A normal platform build (same key, no ceiling declared) | ✅ as soon as the set is promoted, verified and baked | unchanged — the install keeps its plugin bytes | **No** |
| A new plugin publication, producer ≤ running platform | not involved | ✅ adopted; its sources advance to it | the plugin's own seal only |
| A new plugin publication, producer **>** running platform | not involved | ❌ declined at adoption; its sources are HELD, naming both versions | — (the platform roll releases it) |
| A declared break (target key ≠ installed key, or target above an installed build's ceiling), replacement sealed with floor ≤ target | ✅ | the replacement is adopted with the roll | **Yes — the replacement** |
| A declared break, no replacement for some installed plugin | ❌ **HELD**, the hold names the plugin, the installed key/ceiling and the target | — | the missing replacement is the work |

## The compatible path

**1. Build complete = the platform set is promoted, pullable and verified.** Core `main-cd.yml`:
`promote` (line 2098) tags the set all-or-nothing, `verify-images` (line 3563) asserts every image
and pointer, `publish-bake` (line 2310) bakes the platform's own content and writes
`_releases/<version>` naming the set's key, and `notify-platform-update` (line 2890) posts the build
fact to the control instance. `delivery-verdict` (line 3716) judges THOSE jobs and nothing else —
`PlatformDeliveryNeverWaitsOnPluginsGuard` holds that, transitively. The Plugins re-seal for the new
build (`plugins-modules` / `plugins-bake`, line 3240) is an independent follow-up judged by
`report-plugins-seal` (line 4247); its failure is recorded on the `cd-plugins-seal` issue and the
reconcile's seal probe (`gate`, step `seal`, line 1386) re-attempts it — it never fails, holds or
re-publishes the platform. (Line numbers are `main-cd.yml` as of the ladder change,
[Continuous Delivery Contract](../ContinuousDeliveryContract) → "The ladder".)

**2. Which platform image a portal rolls to.** The self-updater
(`memex/Memex.Portal.Shared/SelfUpdate/SelfUpdateHostedService.cs:959`) asks
`ReleaseAvailabilityService.SelectRollTarget` (`ReleaseAvailabilityService.cs:229`) over the
candidates `VersionSelect.SelectCandidates` (`VersionSelect.cs:313`) admits under the instance's
`Admin/UpdatePolicy`. `RollSelection.Select` (`src/MeshWeaver.PluginCatalog/RollSelection.cs:72`) walks
them newest-first and takes the first on which `ReleaseAvailability.IsUpdatable` holds nothing for
this environment's installed plugins. On the compatible path the ladder rule
(`ReleaseAvailability.PlatformRangeHold`, `ReleaseAvailability.cs:276`) answers null for every package
— the target carries the installed build's key and no ceiling is declared — so the newest promoted
image is selected **with nothing sealed for it**; a missing bake is reported as "would recompile at
boot", a cost, never a hold. The only other hold is a MEASURED one: a landed module that cannot link
against the target's surface.

**3. The roll itself.** A fleet instance hands the selected tag to the control lane
(`SelfUpdateHandover`, `ui.updateHandedOver`). The control instance routes it
(MeshWeaver.Plugins `Hosting/Deployment/Source/SelfUpdateRouting.cs:599`, `Decide`) into a `Roll`
`Hosting/InstanceAction`; `ActionsExecutor.AdmittedUnattended`
(`Hosting/InstanceAction/Source/ActionsExecutor.cs:309`) runs it UNATTENDED when the record's
`updatePolicy` is `Continuous` and the record's own update pattern admits the tag — no pinned tag is
involved; records do not pin an image. The routed Roll carries the target's migration Job first
(Plugins#2219, `InstanceActionPlan`), so the database is migrated to the target before the image
moves.

**4. Which plugin versions it runs afterwards.** Unchanged at first: the bytes it already adopted
under the same key keep serving (`PlatformCompatibility.DeclineReason`, core
`src/MeshWeaver.Compiler/PlatformCompatibility.cs:252` — same key, floor ≤ running ≤ ceiling). Then,
independently, the newest plugin publication whose floor ≤ running ≤ ceiling: module bundles land
through the module-update reconcile (no platform gate, `ModuleUpdateDecision`), NodeType bundles are
adopted at boot or install, and a GitSynced Space's sources follow the publication through the
sealed-sync gate (`SealedPublicationIndex.ApplyLadder`, `SealedPublicationIndex.cs:291`;
`SealedSyncGate`), which holds a publication produced by a NEWER build than the one running and
proceeds on anything older or unknown.

## The breaking path

A break is DECLARED, never discovered: `src/MeshWeaver.Compiler/platform-compatibility.json` is the one
source of the epoch and of every break.

1. **The platform PR declares the break** — bumps `epoch` and appends a `breaks` entry with the
   `previousEpochCeiling` (the last platform build of the old epoch: everything built against it
   "works up to" that build) and the reason. The key of every later build is `c<major>e<epoch+1>`.
2. **Plugins are rebuilt against the new platform**, producing bytes under the new key with a new
   floor (their `producerPlatformVersion`).
3. **The affected plugins are sealed** under the new key.
4. **A platform roll is allowed only where every installed plugin has a sealed build whose range
   covers the target.** `ReleaseAvailability.PlatformRangeHold` compares each installed package's key
   (the running key, `ReleaseAvailabilityService.cs:556`) and any ceiling its bundle declares
   (`PublishedBundleCatalogue.RangesForIdentity`) against the target; behind the break it demands a
   replacement sealed under the target's key whose `DeclineReason` is null for the target. Without one
   the candidate is declined with `PlatformRangeExceeded`, naming the plugin, the installed key and
   ceiling and the target — and because the walk is newest-first, the portal lands on the newest
   release its plugins still cover (the last build of the old epoch) instead of staying put.
5. **The plugins roll** with the platform (the replacements) or after it, exactly as on the
   compatible path.

**Build complete, on this path** = the platform set is promoted, pullable and verified (as above)
**and** every plugin the break affects has a publication sealed under the new key whose range covers
the target. CD does not enforce the second half — it is per ENVIRONMENT (which plugins are installed
where), so the self-updater enforces it per candidate, and a portal whose installed plugin has no
replacement yet is HELD with that plugin named.

## Operational facts

* **A routed Roll carries the target's migration Job** (Plugins#2219) — the plan runs the migration
  to the target's schema before the image moves, so a portal is never rolled ahead of its database
  (`DbVersionGate` would refuse to serve).
* **A `Reconcile` keeps the rolled image and runs its migration** — the precedent is
  `Ops/Actions/reconcile-pearl-20260921-migrate-ci9077`. It is the remedy for a roll that moved the
  image without its migration (a pod crash-looping on `DbVersionGate`).
* **An OPEN routed action blocks later announcements until it settles.** `SelfUpdateRouting.Decide`
  never opens a second Roll or Restart while one is open on the deployment; the announcer re-delivers,
  so the announcement after it settles is the retry. The wait is bounded by the action's durable
  `ConvergeUntil` (Plugins#2319) — an action past it settles rather than blocking forever.
* **Never pin.** A pinned image tag in a record or a chart value is refused by
  `PlatformDeliveryNeverWaitsOnPluginsGuard` (core) and forbidden fleet-wide; the update policy chooses
  the image at roll time, and a compatibility break is DECLARED, never pinned around.

## See also

* [Continuous Delivery Contract](../ContinuousDeliveryContract) → "The ladder"
* [Release Availability Gates](../ReleaseGates) → "The declared-break hold"
* [When a Publication Seal Stops Advancing](../PublicationSealStarvation) → "Under the ladder"
* [Module Versioning](../ModuleVersioning)
