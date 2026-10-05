---
Name: The Module Update Ladder — Rule Table
Category: Architecture
Description: Every rule that decides what an instance runs as platform and modules move independently — written as one precise table (platform roll, module publication, store copy against image copy, prebuilt adoption, seals, source sync, the control instance), each row tied to the policy that states it, the code that decides it and the tests that hold it, including the rows that fail today.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M7 3v18"/><path d="M17 3v18"/><path d="M7 7h10"/><path d="M7 12h10"/><path d="M7 17h10"/></svg>
---

# The Module Update Ladder — Rule Table

This page is the **test contract** for the ladder. The ladder itself is policy
[`platform-backwards-compatibility`](../PolicyNotProse) ([The Platform Compatibility Ladder](../PlatformCompatibilityLadder),
[Deploying Across Platform Versions](../DeployingAcrossPlatformVersions)):

```
P1+M1 → P2+M1 → P2+M2 → P2+M3 → P3+M3 → …      each step changes ONE side
```

The other pages explain *why*. This one states *what must hold* at each step, as rows a test can
check, and names the test that checks each row. A row whose test is red is a defect, not a
description to soften.

## Words

| Word | Meaning |
|---|---|
| **P** | The running platform build (`PlatformVersion`, `X.Y.Z-ci.<n>`; ordered by the run ordinal). P+1 is a later build of the same compatibility key. |
| **M@N** | Module (package) M at package version N. |
| **floor** | The platform a module bundle declares it needs (`minMeshVersion`, the bundle's `producerPlatformVersion`). |
| **compatible** | floor ≤ running P (and running ≤ ceiling, which is open unless a break is declared). |
| **lands** | The bundle is downloaded and written as a new generation by `ModuleLandingService`. |
| **activates** | The running process serves N+1: by a live swap, or by exactly one restart when the module declares boot-time infrastructure. |
| **declined by name** | Nothing is downloaded or activated, and the record or request names the module, its floor and the running platform. |
| **control** | The control instance. It always runs the newest platform and the newest modules. |
| **ordinary** | Any other instance. It follows its own update policy. |

## The rules

The **Lives in** column says where the deciding code is today. "main" means merged. A pull-request
number means the behaviour exists only on that open branch. The row's tests sit on that branch,
stacked, so they land with it.

### A — Platform fixed, module moves

| Row | Given | Then | Policy | Lives in | Tests |
|---|---|---|---|---|---|
| A1 | P fixed. M@N runs. M@N+1 is published, compatible. | N+1 lands and activates **live**: no restart, no platform roll, no image patch. | `packages-auto-update`, `module-live-update-default` | #6124 (live seam) / #6123 (real swap) | #6139 `ModuleUpdateLadderMatrixTest` steps 2 and 3; `ModuleReloadLiveTest.ASwappableModule_GoesLive_WithoutARestart` |
| A2 | As A1, but M declares boot-time infrastructure. | N+1 lands and activates by **exactly one** restart. No platform roll. | `module-live-update-default`, `packages-auto-update` | #6124 | #6139 matrix step 4; `ModuleReloadByRestartTest.ARunningVersion_ReloadsToTheNewestCompatible_ByExactlyOneRestart` |
| A3 | N+1 then N+2 are published before the restart happens. | Both land. The second wave **rides** the one restart. | `packages-auto-update` | #6124 | `PackagesAutoUpdateTest.ANewerCompatibleVersion_IsInstalledAndActivated_WithNoHumanStep_IndependentOfSyncAndSeal` |
| A4 | M@N+3 is published with floor > P. | **Declined by name** (floor and P named on the record). Nothing is downloaded. N+2 keeps serving. | `package-min-mesh-version` | main (floor), #6124 (lane) | #6139 matrix step 5 (no download, N+2 keeps serving); `PackagesAutoUpdateTest.AnIncompatibleFloor_IsDeclinedByName_AndTheRunningVersionKeepsServing` (content moved too); **the naming half FAILS today on a module-only publication** — `ModuleUpdateLadderDeclineIsNamedTest` |
| A5 | As A4, while sibling M_b@K+1 (compatible) is published. | M_b lands and activates in the same wave. One module's decline holds no other module. | `packages-auto-update` | #6124 | #6139 matrix step 5 |
| A6 | The package is pinned (`updatePolicy: None`) or an administrator chose `Notify`. | Nothing lands; the opt-out is kept and named. This is the only package-side hold. | `packages-auto-update` | #6124 | `PackagesAutoUpdateTest.ADeliberateOptOut_IsKept_AndNothingLands`; #6139 `ModuleUpdateLadderMatrixNegativeControlTest.APinnedPackage_FailsTheMatrixAtTheFirstModuleStep` |

### B — Platform moves, modules unchanged or waiting

| Row | Given | Then | Policy | Lives in | Tests |
|---|---|---|---|---|---|
| B1 | P → P+1 (same key). Modules unchanged. | Every landed module keeps serving: the boot takes the same generation, with no skip and no advisory. No module is rebuilt or re-sealed. | `platform-backwards-compatibility` | main | `PlatformRollKeepsModulesServingTest.ALandedModule_KeepsServingAcrossTwoPlatformRolls`; #6139 matrix steps 1 and 6; `PlatformCompatibilityLadderTest.ThePluginClimbsTheLadder_TypingAndServingOnEveryRung_RebuildingOnlyWhenThePluginChanges` (NodeType bytes) |
| B2 | A module built against P−k (older, same key) runs on P+1. | It loads. Its floor is below the running build, so it is not even an advisory. Platform assemblies bind roll-forward. | `platform-backwards-compatibility` | main | `PlatformRollKeepsModulesServingTest.AModuleBuiltOnAnOlderPlatform_LoadsOnANewerOne_WithNoAdvisory`; `PlatformCompatibilityTest.SameEpoch_DifferentBuild_IsAdopted_AndNotStale`, `…APluginCompiledAgainstALowerPlatformAssemblyVersion_Binds_AHigherOneDoesNot` |
| B3 | M's floor is P+1 while P runs. | **Waits**: declined by name at adoption (nothing downloaded). Once P+1 runs, the next reconcile lands it with no other step. Control rolls first, so control takes it first. | `package-min-mesh-version`, `platform-backwards-compatibility` | main (floor), #6124 (lane) | #6139 ordinary step 6 and control step 5 (`ModuleUpdateLadderControlMatrixTest`); `PlatformCompatibilityTest.AProducerNewerThanTheRunningBuild_IsDeclinedLoudly_NamingBoth`; `PlatformCompatibilityLadderTest.APluginProducedOnANewerPlatform_IsDeclinedLoudly_NamingBothVersions` |
| B4 | A landed module whose floor is above P is present at boot (for example a mixed roll). | The boot does **not** skip it. The floor is an advisory naming both versions, and the link probe decides (#3648). | `platform-backwards-compatibility` | main | `PlatformRollKeepsModulesServingTest.AFloorAboveTheBootingPlatform_IsAnAdvisory_NeverASkip` |
| B5 | A declared break: the running P is above a module's ceiling, or the epoch moved. | Declined, naming both versions. Only a replacement built on the new epoch is adopted. | `platform-backwards-compatibility` | main | `PlatformCompatibilityTest.ARunningBuildAboveTheCeiling_IsDeclined_AtTheCeilingItIsNot`, `…AnEpochBump_IsADeclaredBreak_DeclinedAndStale`; `PlatformCompatibilityLadderTest.AnEpochBump_RequiresARebuild_TheOldEpochsBytesAreRefused`, `…APlatformAboveThePluginsCeiling_IsDeclinedLoudly`; `DeclaredBreakRollHoldTest` (10) |

### C — Store copy against image copy (#6103)

| Row | Given | Then | Lives in | Tests |
|---|---|---|---|---|
| C1 | Store copy is OLDER than the image's own copy. | The image copy runs; the store copy is reported declined. | main | `ImageCopyVersionDiscriminatorTest.AnOlderStoreRelease_IsDeclined` |
| C2 | Store copy is the SAME version. | The image copy runs. | main | `…AStoreCopyAtTheImagesOwnVersion_IsDeclined_AndTheImagesCopyRuns` |
| C3 | Store copy is STRICTLY newer. | The store copy runs (the registry ships a fix without an image). | main | `…ANewerStoreRelease_StillOverridesTheImage` |
| C4 | No image stamp, a stamp with no version, or a store entry with no version. | Decides nothing (the identity rule applies). | main | `…NoImageStamp_DecidesNothing`, `…AStampWithoutAVersion_OrForAnotherModule_DecidesNothing`, `…AStoreEntryWithoutAVersion_DecidesNothing` |
| C5 | A module only the store ships. | Never declined by this rule. | main | `…AStoreOnlyModule_IsNeverDeclined` |
| C6 | The pending report for a declined store copy. | Says DECLINED, never "a restart activates it". | main | `…TheReport_CallsItDeclined_NeverRestartRequired` |

### D — Prebuilt adoption and NodeType compiles (#6116)

| Row | Given | Then | Lives in | Tests |
|---|---|---|---|---|
| D1 | A prebuilt NodeType was compiled against module M@N+1. M@N is installed. | The prebuilt is **refused** (`FloorNotMet`, both package versions named). The type compiles from source instead. The `min:3.0.0.0` informational version never satisfies a package floor. | #6116 | `PrebuiltBindsModulePackageVersionTest` (6); `ModuleUpdateLadderPrebuiltTest.APrebuiltBoundToANewerModule_IsRefused_AndTheTypeCompilesFromSource` (through `PrebuiltAssemblySeeder.SeedDetailed` on a mesh) |
| D2 | The installed module is the same or newer than the prebuilt's record. | The prebuilt is adopted. | #6116 | `PrebuiltBindsModulePackageVersionTest`; `ModuleUpdateLadderPrebuiltAdoptsTest.APrebuiltBoundToTheInstalledModuleVersion_IsAdopted` |
| D3 | A NodeType's source uses a member that only M@N+1 has. | While N is active, the compile **fails cleanly**: a named diagnostic, no crash, nothing half-bound. Once N+1 is active, it compiles and the member works. | #6116 (on main's compiler) | `ModuleUpdateLadderPrebuiltTest.ATypeUsingAMemberOnlyTheNewerModuleHas_FailsNamedBefore_AndCompilesAfter` (CS0117 naming `Group`, nothing emitted; then it compiles and runs) |

### E — Seals never gate delivery

| Row | Given | Then | Policy | Lives in | Tests |
|---|---|---|---|---|---|
| E1 | No seal for the running identity. The module's partition is sync-owned and its sync is still at OLD content. | Modules still land and activate (rows A1–A5 hold unchanged). | `packages-auto-update` | #6124 | #6139: both matrix walks run on that premise, and re-inserting the retired seal coupling fails both at step 2; `PackagesAutoUpdateTest.ANewerCompatibleVersion_…IndependentOfSyncAndSeal` |
| E2 | A compatible platform build with no plugin seal. | The platform rolls. A missing bake is a cost ("recompiles at boot"), never a hold. | `platform-backwards-compatibility` | main | `DeclaredBreakRollHoldTest.AnOpenCeiling_SameKey_RollsWithoutAnySeal`; `RollSelectionTest.SelectsTheNewestRelease_NamingThePackagesItRecompilesAtBoot` |
| E3 | Sources: no seal for the running build. | Sources still import (a publication from an older or unknown producer proceeds). | `sources-sync-on-push`, `module-sync-per-manifest-hash` | main + #6122 | `SealedSyncFollowsTheLadderTest.APublicationProducedByAnOlderBuildOfTheSameKey_Proceeds_WithNoSealForTheRunningBuild`; #6140 `ModuleUpdateLadderSyncTest.APush_ImportsAtThePushedCommit_WithNoSealForTheRunningIdentity_AndNoGreenBuild`, `…ASealForAnOlderCommit_NeitherHoldsNorRedirectsThePush` |

### F — Source sync (#6122, #6111)

| Row | Given | Then | Policy | Lives in | Tests |
|---|---|---|---|---|---|
| F1 | A push to the sync source's branch. | Every source imports **at the pushed commit**. | `sources-sync-on-push` | #6122 | #6140 `ModuleUpdateLadderSyncTest.APush_ImportsAtThePushedCommit_WithNoSealForTheRunningIdentity_AndNoGreenBuild`; `BuildTriggeredSyncPinsTheBuiltCommitTest` (push rows) |
| F2 | The repository's build is red. | The push still imports. A red build holds nothing. | `sources-sync-on-push` | #6122 | `BuildTriggeredSyncPinsTheBuiltCommitTest.APush_ImportsAtThePushedCommit_EvenThoughTheBranchsBuildIsRed` |
| F3 | One module's declared floor is above P (or it requires a newer module than this instance runs). | That module alone is declined, both versions named. Its siblings sync. | `sources-sync-on-push`, `module-sync-per-manifest-hash` | #6122 / #6111 | `BuildTriggeredSyncPinsTheBuiltCommitTest.AnIncompatibleModuleIsDeclinedAlone_WhileItsSiblingSyncs`, `ModuleSyncDecisionTest.ADeclinedSibling_NeverHoldsAnotherModule`; #6111 `ModuleSyncDecisionTest.ARequirementAboveTheRunningModule_DeclinesThatModule_AndNamesBothVersions`, `…ARequirementDecline_HoldsNoSibling` — decision level only (see below) |
| F4 | A node was created at runtime in a synced partition. | A source import never prunes it. Only nodes in the prior import manifest are prunable. | `prune-requires-provenance` | #6122 | #6140 `ModuleUpdateLadderSyncTest.ARuntimeNode_SurvivesAPushImport_WhileARetiredSourceNodeIsPruned`; `ARuntimeNodeSurvivesAnImportTest`; `StaticRepoImporterSyncModeTest` |

### G — The control instance

| Row | Given | Then | Lives in | Tests |
|---|---|---|---|---|
| G1 | Control runs a platform older than the newest promoted build by more than its bound (two of its own CD cycles). | The stale-portal alarm is RED, naming both builds and the bound. On the newest build: measured clean. | MeshWeaver.Plugins main (`RollAlarms.StalePortal`) | Plugins#2901 `RollAlarmsTests.TheControlInstance_IsGreenOnTheNewest_AndRedOnlyPastItsOwnBound` (also: exactly at the bound is clean; a declared hold `updatePolicy: None` raises nothing) |
| G2 | Control runs a module older than the newest published compatible version by more than a bound. | An alarm is RED, naming the module and both versions. | **partial** on Plugins main (`ModuleInventoryContent`: a warning once a RECORDED hold is older than 6 h); complete in open Plugins#2888 (`ReleaseFollowThrough`: red check run, 60-minute bound, control first) | Plugins#2901 `ModuleInventoryTests.TheControlInstance_ModuleBehindTheNewestCompatible_BreachesOnlyPastTheBound` pins main's partial rule and its gap; Plugins#2888 `Installed_IsGreen_AndOneVersionBehindIsRedPastTheBound` |

### H — The 2026-10-05 incident, end to end

| Row | Given | Then | Lives in | Tests |
|---|---|---|---|---|
| H1 | Store: `MeshWeaver.AI` 1.20.4. The image ships a newer AI (1.21) that has `ThreadPreparation.Group`. A Hosting type sets `Group`. A Hosting prebuilt records AI 1.21. | Before the fix: the store copy runs, the prebuilt is adopted against 1.20.4, and the first call throws `MissingMethodException set_Group`. After the right step — a restart that applies the image rule (C1/C2) — the image's AI runs, the Hosting type compiles and runs, and the thread starts. While 1.20.4 is still active, the prebuilt is refused (D1) and the source compile fails **named**, never a `MissingMethodException`. | main (#6103) + #6116 | `ModuleUpdateLadderIncidentTest.TheIncident_TheStoreCopyRunsAndTheThreadCannotStart_UntilTheImageRuleRuns_ThenItStarts` — reproduces `MissingMethodException set_Group` through the pre-#6116 resolver, then the named refusal, then the start once the image rule runs |

## The matrix — one ordered scenario per instance

Policy rows A, B and E interact, so they are also checked as a **sequence**. Each matrix row is
(platform, published module set) together with what must hold after it: the loaded version of
every module, how each one activated (live / restart / none / declined), and whether a platform
roll happened. Two modules take part: `M_a`, which swaps live, and `M_b`, which declares boot-time
infrastructure. Their floors differ.

Ordinary instance (it follows its policy; it rolls a promoted platform one step after control does):

| Step | Platform | Published | Expected |
|---|---|---|---|
| 0 | P1 | M_a 1.0 (floor P1), M_b 1.0 (floor P1) | both 1.0 loaded |
| 1 | **P2** (roll) | unchanged | both 1.0 keep serving; no module restart (B1) |
| 2 | P2 | M_a 1.1 (floor P2) | M_a 1.1 live, 0 restarts (A1) |
| 3 | P2 | M_a 1.2 (floor P1, built on an older platform) | M_a 1.2 live (A1, B2) |
| 4 | P2 | M_b 1.1 (floor P2) | M_b 1.1, exactly one restart (A2) |
| 5 | P2 | M_a 1.3 (floor **P3**), M_b 1.2 (floor P2) | M_a declined by name, 1.2 keeps serving (A4); M_b 1.2 lands with one restart (A5) |
| 6 | **P3** (roll) | unchanged | M_a 1.3 lands live with no other step (B3); M_b 1.2 keeps serving (B1) |

Control instance (always on the newest): the same publications, but it rolls to P3 at step 5, when P3
is promoted. M_a 1.3 therefore lands there at step 5 (B3, "control first"). It lands in the same
wave as M_b 1.2, and a wave activates live only when EVERY module in it can swap. Because M_b
declares boot-time infrastructure, that wave's one activation is a single restart. At step 6
there is nothing left for control to do.

The scenario runs on the premise of E1: no seal, and a sync source still at old content. **The
negative control is part of the suite.** The same ordinary table runs with `M_a` pinned. The runner
must report a failure at step 2 and at no earlier step. This proves that the matrix fails at the
exact step when a module stops moving. The second negative control was run once, by hand, and #6139 reports its result: re-inserting the
retired coupling ("a sync-owned module waits for the seal its content does") fails both the ordinary
and the control table at step 2.

## Rows that fail today

* **A4, naming half: a module declined for its floor on the auto-update lane is named on no node.** The
  decline itself holds: nothing is downloaded, and the running version keeps serving. But
  `PluginBundleClient.AdoptModuleOutcome` only logs the verdict at Information. The install record's
  `heldUpdate` is written by the CONTENT lane alone (`PackageUpdateReconciler`), and that lane is
  idle when the package's content identity did not move. The explicit reload path does name it.
  Pinned by `ModuleUpdateLadderDeclineIsNamedTest.AModuleDeclinedForItsFloor_IsNamedOnTheInstallRecord`
  (#6139). The test is skipped with this reason; un-skipped it fails with "declined, but no node names
  it — the install record's heldUpdate is ''".
* **F3 is pinned at the decision level only — a TEST gap, not a failing row.** The import compares
  floors against the process-wide `PlatformBuildInfo.RunningPlatformVersion`; GitSync has no per-mesh
  running-platform seam (the plugin catalog has `RunningPlatformVersionOverride`), and a local `-ci.0`
  build orders no floor, so no end-to-end decline can be produced in a test.
* **G2 on `main`: a module behind with NO recorded hold never escalates.** Main's only module-lag rule
  warns once a recorded hold is older than 6 h, and only as a triage warning. The complete red alarm
  is in open Plugins#2888.

Every other row above is green on the branch named in its **Lives in** column. A row that lives
in an open pull request is not true on `main` until that pull request merges. E1 in particular
FAILS on `main` today: the module lane still waits for a sync-owned partition's seal (#4355 gate
1b) until #6124 lands.

## Related

* [The Platform Compatibility Ladder](../PlatformCompatibilityLadder) — the key, floor, ceiling, epoch, and the static link checks
* [Deploying Across Platform Versions](../DeployingAcrossPlatformVersions) — the deploy procedure on both paths
* [Module Adoption Policy](../ModuleAdoptionPolicy) — keep what you have until a newer one can run here
* [Module Versioning](../ModuleVersioning) — what decides a package's version
* [Module Sync per Manifest Hash](../ModuleSyncPerManifestHash) — per-module source sync
