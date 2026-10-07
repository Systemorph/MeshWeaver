---
Name: Platform Versioning (SemVer)
Category: Architecture
Description: How platform builds and images are versioned — the move from the 3.0.0-ci.<run> pre-release notation to plain SemVer 3.<minor>.<run> with floating 3-latest / 3.<minor>-latest tags; the one lineage both notations share, every place the old notation is produced or parsed, the migration order, and what a floating tag means for a deployment record.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20.59 13.41 13.42 20.58a2 2 0 0 1-2.83 0L2 12V2h10l8.59 8.59a2 2 0 0 1 0 2.82z"/><circle cx="7" cy="7" r="1.5"/></svg>
---

# Platform Versioning (SemVer)

> **Policy [`platform-semver-versioning`](../PolicyNotProse).** Continuous platform builds are
> versioned as plain SemVer `<major>.<minor>.<run>`. The patch is the monotonic CD run number. The minor
> changes only when someone bumps it on purpose, and the major changes only on a declared break. Main
> builds carry no pre-release label. Images also carry the floating tags `<major>-latest` and
> `<major>.<minor>-latest`, which the arm step moves forward and never backward. A deployment record
> follows a version **pattern** resolved against the registry. It never names a floating tag.

## 1. The scheme

| shape | example | what it is | run number (`BuildOrdinal`) |
|---|---|---|---|
| `<major>.<minor>.<run>` | `3.1.10050` | a continuous build, the new notation, line ≥ 3.1 | `10050`, the patch |
| `<major>.<minor>.<run>-edge.<n>` | `3.1.311-edge.311` | an unverified edge build (`edge-images.yml`) | `311`, as before |
| `<major>.<minor>.0-ci.0` | `3.1.0-ci.0` | a LOCAL source build of the new notation | `0`, never ordered against a publication |
| `X.Y.Z-ci.<run>` | `3.0.0-ci.9999` | a continuous build, the old notation | `9999` |
| `X.Y.Z-rcN.ci.<run>` | `3.0.0-rc9.ci.7824` | retired labelled line | `7824` |
| `X.Y.0` / a clean `3.0.x` | `3.1.0`, `4.0.0`, `3.0.0` | a floor or a deliberately cut release | none; compared by its numeric core |
| `<major>-latest`, `<major>.<minor>-latest` | `3-latest`, `3.1-latest` | floating pointers | never a candidate |

**Why the patch is the run number.** The rule "higher number ⇒ newer code" is what every self-updater,
retention plan and floor check depends on ([Self-Update Target Selection](../SelfUpdateTargetSelection)).
The run number is the one counter the pipeline never gets wrong. If the patch is that counter, the old
notation and the new one share **one lineage**. The last `3.0.0-ci.<n>` and the first `3.1.<n+1>` are
then ordered by when they were published, and nothing can roll backwards across the cut-over. SemVer
gives the same order, because a minor bump is deliberate and always later than the builds before it.

**The boundary is a declared constant, never inferred.** A plain `X.Y.Z` is read as a build of the new
notation only when its line is at or above `PlatformReleaseOrder.SemVerEraStart` (3.1) **and** its patch
is non-zero. That keeps every tag the old notation published meaning exactly what it meant. A clean
`3.0.0` stays a promotion. The withdrawn slip `3.1.0-ci.7841` is still ranked by its `ci` number, so it
can never outrank the new notation. Floors like `3.1.0` or `4.0.0` are still compared by their numeric
core, so a floor that says "needs the 3.1 line" holds a `3.0.0-ci.*` portal and is met by every
`3.1.<run>`.

**Stable does not start following main.** A new-notation build carries no pre-release label, so to NuGet
it is as "clean" as a release. `VersionSelect.PickTargets` therefore keeps any tag with a run number out
of `Stable`, so Stable keeps following exactly what it follows today: deliberately cut releases
(`3.0.0`, `3.1.0`, …), which `release.yml` promotes. See decision 1 in §6.

## 2. What `3-latest` means for consumers

- **It is a starting point, never a record value.** CD moves `3-latest` and `3.<minor>-latest` at every
  arm (`main-cd.yml` phase D, and `control-first` for `memex-control`), and only forward: a newer
  armed set holds them. A fresh install or a Helm overlay's *seed* image may name `3-latest`, so it
  boots on the newest armed build.
- **A record follows a PATTERN, not a tag.** `updatePattern: "3.*"` admits every build of major 3 in
  both notations. The self-updater lists the registry, drops pointers (`VersionSelect.MovingPointer`),
  per-RID images and git-sha tags, and ranks what remains by run number. A floating tag is never
  written into a `Hosting/Deployment` record, because records are never pinned to a tag. A pointer
  is mutable, so a record that named one would describe whatever the pointer happens to hold, not a
  decision.
- **`3.0.0-latest` keeps moving only while the old notation is minted.** The new notation moves no
  three-part pointer, because `3.1.10050-latest` would point at exactly one build. Consumers that
  seed from `3.0.0-latest` (the Memex overlays, `resolve-line-pointer.sh`, `preflight-provision.py`)
  switch to `3-latest` before the minter flips. After the flip `3.0.0-latest` is frozen on the last
  old-notation set.

## 3. Inventory: every place the old notation is produced or parsed

Line numbers are as of this change. **P** = produces the notation, **R** = parses it. "Handled" means
this change teaches the place both notations. "Owed" means a later step of the migration has to change it.

### Core (`Systemorph/MeshWeaver`)

| place | role | status |
|---|---|---|
| `Directory.Build.props:122` `PlatformVersion` = `3.0.0`; `:319` `Version = $(PlatformVersion)-ci.$(_CiBuildNumber)` | **P**: the minter. Every image tag, `MESHWEAVER_PLATFORM_VERSION` and package version comes from here (`main-cd.yml` reads it with `-getProperty:Version`) | owed: minter step (§4 step 4) |
| `test/MeshWeaver.Documentation.Test/PlatformVersionSchemeGuard.cs` | **R**: the "two shapes" guard (`X.Y.Z-ci.<n>` / `X.Y.Z`) | owed, together with the minter |
| `src/MeshWeaver.Plugin.Packaging/PlatformReleaseOrder.cs:68,92,122` | **R**: `BuildOrdinal`, `Compare`, `Newest`, the ONE order every C# caller uses (`VersionSelect`, `PlatformFloor`, `PlatformCompatibility.ProducerIsNewer`, `TargetSet`, `SealedPublicationIndex`, `ShippedPrebuiltBundles`, `PrebuiltBundleRetention`) | **handled**: `SemVerEraStart`, `IsSemVerBuild` |
| `memex/Memex.Portal.Shared/SelfUpdate/VersionSelect.cs:211` | **R**: Stable must not admit a run-numbered clean tag | **handled** |
| `memex/Memex.Portal.Shared/SelfUpdate/VersionSelect.cs` `ResolveChannel` advisory | **R/P**: tells the operator which pattern to set | **handled**: names `3.*` |
| `src/MeshWeaver.Hosting/SelfUpdate/UpdateChannelPattern.cs` | **R**: the glob, pattern-agnostic | unchanged; `3.*` works as is |
| `src/MeshWeaver.Hosting/SelfUpdate/SelfUpdateOptions.cs` `DefaultPattern`; `src/MeshWeaver.Deployment.Contract/DeploymentPortalConfig.cs:603` | **R**: render a record's `updatePattern` as `SelfUpdate__DefaultPattern` | unchanged (pass-through); docs examples still say `3.0.0-ci*` |
| `.github/scripts/platform-version.py` (new) | **R**: the one shell-facing reader: `ordinal`, `max-ordinal`, `line-pointers` | **new**, self-tested |
| `.github/workflows/main-cd.yml:866` | **R**: satellite-compat baseline run number from `mw-plugin-test:main` | **handled** |
| `.github/workflows/main-cd.yml:2083` | **P**: `control-first` line pointers for `memex-control` | **handled**: `3-latest 3.1-latest` |
| `.github/workflows/main-cd.yml:2442,2447` | **R**: promote's never-backwards check before `main`/`latest` move | **handled** |
| `.github/workflows/main-cd.yml:2752,2755,2762` | **R/P**: arm phase D never-backwards check and the line pointers | **handled** |
| `.github/scripts/arm-promoted-set.py:133,151,281` | **R/P**: `run_number_of`, `line_pattern`. The announcement pattern for control's roll lane becomes `3.1.*` | **handled**, self-tested |
| `.github/scripts/resolve-platform.py:217,240,254,264` | **R/P**: set names, freezes, bake receipts, promotion records, notices, registry tags (vendored into satellites; the lanes fetch the canonical copy) | **handled**: `match_set_name`, `compose_set_name`, `notice_set_number`, self-tested |
| `.github/workflows/node-repo-gate.yml:844-847` | **R**: `platform-set` input shape | **handled**, executed by `test-gate-lane-forwards-the-callers-set.py` |
| `.github/workflows/node-repo-publish-bake.yml:2106` | **R**: released-version shape | already accepts `X.Y.Z` |
| `.github/workflows/edge-images.yml:74-76` | **P**: edge tag; for the new notation it falls through to `<v>-edge.<run>` | already correct |
| `.github/workflows/release.yml:197` | **R**: a `v*` tag promotes the newest `X.Y.Z-ci.<n>` of its line | owed, together with the minter: decision 2 in §6 |
| `.github/acr-retention/*`, comments across `main-cd.yml` | prose and fixtures | history; no change |

### MeshWeaver.Plugins

Nothing in Plugins calls `PlatformReleaseOrder`. Every platform-version reader there is hand-written,
and most of them recognise only `ci.N`. That is the largest share of the owed work. The cure is to
delegate to core's order, not to add another regex.

| place | role | on `3.1.N` |
|---|---|---|
| `Hosting/Deployment/Source/SelfUpdateRouting.cs:929` `BuildOrder` (used by `Decide` :883, `Superseded` :652, `RollInsteadOfRestart` :1068) | **R**: own `ci.N` parse | silent: unordered, so the never-backwards guard and supersession turn off |
| `Hosting/Deployment/Source/RollGates.cs:302,316` | **R**: via `BuildOrder` | a gated roll never opens |
| `Hosting/Deployment/Source/ImageLine.cs:52,72,86` (`PointerFor`, `PatternFor`, `LineOf`) | **R/P**: line = text before the first `-` | gives `3.1.10050-latest`, `3.1.10050-ci*` |
| `Hosting/Deployment/Source/InstanceComposition.cs:132` `FleetPattern = "3.0.0-ci*"`, `:163` seed tag | **P**: new instances get the old pattern and `3.0.0-latest` | must become `3.*` / `3-latest` |
| `Hosting/InstanceAction/Source/ActionsExecutor.cs:397,405` | **R**: unattended roll needs the pattern to admit | via data |
| `Hosting/ModuleInventory/Source/ModuleInventoryContent.cs:390-449` (`Ordinal`, `SetsBehind`, `Platform`); `PlatformBuildInbox/Source/FleetTargetIntake.cs:268`; `ReleaseFollowThrough.cs:216` | **R**: own `-ci\.(\d+)` | silent: no fleet target, holds undetected |
| `Hosting/DeploymentStatus/Source/DeploymentStatusLayoutAreas.cs:111,126` | **R**: display | degrades to "≠ tag" |
| `.github/workflows/portal-ai-image.yml:300-313` | **P**: Plugins mints its own `${PlatformVersion}-ci.${BUILD}` from the max ACR tag | owed: minter step |
| `.github/workflows/portal-ai-image.yml:152`, `portal-next-image.yml:124`, `log-watcher-image.yml:141` | **R**: `MW_PLATFORM_REF` set-name freeze regex | a `3.1.N` freeze is treated as a git ref |
| `.github/workflows/ci.yml:4494` | **R**: floor-stamp gate `^3\.0\.0-ci\.[0-9]+$` | red |
| `scripts/platform-requirement.py:78,147,266,343` (+ producers :116…:504) | **R/P**: sets, floors, `Requires-platform:` | red / silent |
| `scripts/mesh-floors.py:78,96,208,220,232,282` | **R/P**: stamps and checks `minMeshVersion = 3.0.0-ci.N` | red |
| `scripts/ceiling-adoption.py:93` | **R**: the resolver's lag sentence | silent |
| `*/index.json` `minMeshVersion` (~90 packages) | data: floors in the old notation | fine: core reads both notations, one lineage |

### Memex (`Systemorph/Memex`)

| place | role | on `3.1.N` |
|---|---|---|
| `mesh/Deployments/{build,control,memex,memex-cloud,pearl}.json` `updatePattern: "3.0.0-ci*"` | data: the records | refuses every `3.1.N`; must become `3.*` **before** the minter flips |
| `deployments/aks/*/values.*.public.yaml` `SelfUpdate__DefaultPattern: "3.0.0-ci*"` | data: overlay mirror of the record | same, in the same change |
| the same overlays' `portal.image` / migration seed `…:3.0.0-latest` | data: seed pointer | `3-latest` |
| `scripts/check-no-pins.py:56` `FLEET_PATTERN` | **R**: records must equal it | change with the records |
| `scripts/resolve-line-pointer.sh:35,66` | **R/P**: `latest` → `3.0.0-latest`; picks the newest concrete tag by a 4th dot field | `3-latest`; the sort must use the run number |
| `scripts/resolve-portal-image.py:25-33` `build_ordinal` | **R**: requires `[-.]ci\.N` | red |
| `scripts/image-contains.py:60`, `deployments/aks/ci-runners/ci-platform-refresh.py:834` | **R**: `[.-]ci\.N` | not recognised / silent |
| `.github/scripts/aks-ops-classify.py:301,334-360` | **R**: unattended CI roll needs the pattern to admit | via data |
| `scripts/preflight-provision.py:473` | **P**: default `--tag 3.0.0-latest` | `3-latest` |
| `Directory.Build.props:32-35` | **P**: Memex's own `-ci.` build version | optional |
| `Memex.Portal.Shared/SelfUpdate/VersionSelect.cs` (frozen copy) | **R**: NuGet SemVer | already orders `3.1.N` above `3.0.0-ci.N` |

The agent-facing prose that teaches the old notation (`AGENTS.md` and skills in Plugins and Memex,
Memex `docs/*`) is updated in the step that changes the behaviour it describes.

## 4. Migration order

Readers go first, then the data, then the minter. Each step is safe on its own. The tests in §5 show it.

1. **Core readers** (this change): `PlatformReleaseOrder`, `VersionSelect` (Stable), the resolver,
   `arm-promoted-set.py`, `platform-version.py`, `main-cd.yml`'s readers, the gate lane. It merges
   and **rolls to every portal, the control instance included**. Until a portal runs this reader it
   ranks `3.1.N` below every `3.0.0-ci.*`, because a clean tag without a run number used to fall into
   the promotion band.
2. **Plugins readers**: `SelfUpdateRouting.BuildOrder`, `RollGates`, `ImageLine`,
   `ModuleInventoryContent`, `DeploymentStatus` delegate to `PlatformReleaseOrder`.
   `InstanceComposition` seeds `3.*` / `3-latest`. The freeze regexes, `platform-requirement.py`,
   `mesh-floors.py`, `ceiling-adoption.py` and the `ci.yml` floor-stamp gate accept both notations.
   Rolled to the control instance (it hosts the operator and the roll lane).
3. **Memex data**: every record's `updatePattern` → `3.*`, overlays' `SelfUpdate__DefaultPattern` →
   `3.*`, seeds → `3-latest`, `check-no-pins.py`, `resolve-line-pointer.sh`,
   `resolve-portal-image.py`, `image-contains.py`, `ci-platform-refresh.py`, `preflight-provision.py`.
   Widening a pattern to `3.*` before any `3.1.N` exists changes nothing. The withdrawn
   `3.1.0-ci.7841` matches the glob but is ranked 7841. The record change goes through the governed
   record path. Nobody edits live records by hand.
4. **Core minter**: `PlatformVersion` → `3.1.0`, `Version` → `<major>.<minor>.<run>` under CI and
   `<major>.<minor>.0-ci.0` locally, with the scheme guard rewritten to the new shapes. From this
   merge on, CD mints `3.1.<run>`, arm moves `3-latest`/`3.1-latest`, and `3.0.0-latest` freezes.
5. **Plugins minter**: `portal-ai-image.yml` mints the new notation. Floors are stamped as
   `3.<minor>.<run>`.
6. **Retire** the `3.0.0-ci*` examples in docs, skills and AGENTS files. Leave `3.0.0-latest` frozen
   until no overlay seeds from it.

Do not reorder steps 3 and 4. A record still on `3.0.0-ci*` admits no new-notation build, so every
Continuous install freezes on its last old-notation set. Its only signal is "no newer release", and
that verdict is easy to misread.

## 5. What the tests prove

- `PlatformReleaseOrderTest.TheOldAndTheNewNotation_InterleaveByPublicationOrder`:
  `3.0.0-ci.9999 < 3.1.10000`, while a later old-notation set `3.0.0-ci.10002` still outranks
  `3.1.10001` (the run decides, not the notation). The slip stays at 7841. A mixed set sorts in one
  total order, and `TheTotalOrder_IsTransitive_AcrossBothNotations` checks it for every permutation.
- **Negative control:** with the new-notation reading reverted (`SemVerBuildPatch` returning `null`),
  the interleave, transitivity and `BuildOrdinal` cases fail, and so do three `VersionSelect` cases.
  Without the reader, `3.1.N` lands in the promotion band and is never chosen.
- `VersionSelectTest.TheOldFleetPattern_NeverSelectsTheNewNotation`: why step 3 comes before step 4.
- `VersionSelectTest.WideningThePattern_BeforeTheCutOver_ChangesNothing`: why step 3 is safe early.
- `VersionSelectTest.Stable_NeverSelectsANewNotationBuild`: decision 1 in §6, as implemented.
- `AFloor_IsOrderedAcrossTheCutOver`: a floor in either notation is ordered against a build in the
  other.
- The script self-tests (`platform-version.py`, `arm-promoted-set.py`, `resolve-platform.py`,
  `test-gate-lane-forwards-the-callers-set.py`) check the same table. A negative control on the
  resolver (moving its boundary to 9.9) fails exactly the new-notation cases.

## 6. Decisions taken (maintainer can override)

These were open when the scheme was drafted. Each was settled the conservative way: nothing already
published is renamed, the minor moves only by an explicit, governed action, and Stable keeps
following what it follows today. Overriding one is a follow-up change to this page and the code it
names. None of them affects the one-lineage order in §1.

1. **Stable keeps following deliberately cut releases.** Stable excludes every run-numbered tag
   (`VersionSelect.PickTargets`, pinned by `VersionSelectTest.Stable_NeverSelectsANewNotationBuild`),
   so a Stable install, the seeded default, never starts following main. It reaches a new-notation
   line only through a release (decision 2). Not chosen: a `stable` channel pointer
   ([Release Channels](../ReleaseChannels)), or Stable as "Continuous with a minor-pinned pattern".
   Either would change what every Stable install follows on the day it lands.
2. **`release.yml` keeps promoting, and renames nothing.** A `v<major>.<minor>.0` tag on a sealed
   commit adds the clean tag `<major>.<minor>.0` to that commit's already-published
   `<major>.<minor>.<run>` set, copies its release marker and opens the next-line pull request,
   exactly as it does for `v3.0.0` today. The `<major>.<minor>.<run>` tag stays where it is. In the
   new notation the release moves no `-latest` pointer: the set it promotes was armed by CD, which
   already moved `3-latest`/`3.<minor>-latest` to it or past it, so a release-side move could only
   move them backwards. GHCR `latest` still follows releases. This is implemented with the minter
   (step 4). Not chosen: retiring the promotion step, or a git-tag-only release with a channel
   pointer.
3. **The minor is bumped only by a merged pull request that edits `PlatformVersion`.** In practice that
   is the next-line pull request `release.yml` opens after a release (`3.1.0` → `3.2.0`), which goes
   through the same review and required checks as any other change. No workflow, script or
   self-updater edits `PlatformVersion` on its own. The run number keeps increasing across the bump,
   so the order is unaffected.

**A known risk, not a decision:** the counter is `GITHUB_RUN_NUMBER` of `main-cd.yml`. Renaming or
recreating the workflow resets it. That risk exists today. With the patch as the run number it
becomes visible in every version, so it is now a SemVer regression as well as a lineage one.
