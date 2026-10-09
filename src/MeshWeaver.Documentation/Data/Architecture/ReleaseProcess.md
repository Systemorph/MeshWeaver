---
NodeType: Markdown
Name: "Release Process & Versioning"
Abstract: "The authoritative version scheme: every main CD build is a plain SemVer release <major>.<minor>.<run> (the patch is the monotonic CD run number), the release is a PROMOTION that adds the clean <major>.<minor>.0, unpublished builds are <major>.<minor>.0-dev, and no channel word is ever minted. One central PlatformVersion line in Directory.Build.props, what may be minted versus what must still be read, and the SemVer ordering that #3554 turned into 42 unsatisfiable module floors."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#3949ab'/><path d='M12 4c3 1.5 4.5 4.5 4.5 8l-2 2h-5l-2-2C7.5 8.5 9 5.5 12 4z' fill='white'/><circle cx='12' cy='10' r='1.5' fill='#3949ab'/><path d='M9.5 16l-1.5 3 3-1.5M14.5 16l1.5 3-3-1.5' stroke='white' stroke-width='1.6' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Release"
  - "Versioning"
  - "CI/CD"
---

# Release Process & Versioning

Support lifetime and official artifact retention follow the
[Release Support Policy](/Doc/Architecture/SupportPolicy).

One number, two channels, one set of bytes. The whole scheme lives in
`Directory.Build.props` and applies to every project in the solution.

---

## 1. The version scheme — every main build is a release version

> **Policy [`platform-semver-versioning`](../PolicyNotProse)**, manual
> [Platform Versioning (SemVer)](../PlatformVersioning). This section replaced the "two shapes"
> scheme (`X.Y.Z-ci.<n>` for every continuous build, clean `X.Y.Z` for the release), which no build
> mints any more. Its tags are still in the registries and are still read; see below.

| Build | Version | Where it comes from |
|---|---|---|
| a main CD build | **`<major>.<minor>.<run>`**, e.g. `3.1.10340` | `main-cd.yml`: `Directory.Build.props` composes it from the line and `-p:PlatformBuildNumber=$GITHUB_RUN_NUMBER`, which only main-cd passes |
| the release promotion | **`<major>.<minor>.0`**, e.g. `3.1.0` | `release.yml`, on the annotated tag `v3.1.0`: the clean tag is ADDED to the main build main-cd published for that commit |
| an unverified edge build | `<major>.<minor>.0-edge.<run>` | `edge-images.yml` (manual dispatch) |
| any other build (pull request, local, a satellite lane compiling core) | `<major>.<minor>.0-dev` | `Directory.Build.props` with no build number: the build publishes no platform version and says so |

```xml
<!-- Directory.Build.props — the ONE maintained number: the LINE, patch always 0 -->
<PlatformVersion Condition="'$(PlatformVersion)' == ''">3.1.0</PlatformVersion>
```

Four rules, not observations:

- 🚨 **The patch is main-cd's run number, handed in BY NAME.** `GITHUB_RUN_NUMBER` is monotonic per
  workflow and the CD run counter is the one number the pipeline never gets wrong, so the builds of
  the retired notation (`3.0.0-ci.<run>`) and the new builds are ONE lineage, and SemVer gives the
  same order across the boundary (`3.0.0-ci.10330 < 3.1.10331`). Every other workflow has its own
  `GITHUB_RUN_NUMBER`, which is why `Directory.Build.props` never reads it implicitly: a pull-request
  run that minted `<major>.<minor>.<its own run>` would wear a clean release version from the wrong
  counter. main-cd asserts the composed string is `<major>.<minor>.<this run>` before it tags anything.
- 🚨 **No channel word is ever MINTED** — no `ci`, no `rc`, no `preview`, no labelled line. The
  `-dev` of an unpublished build says what it is (PlatformReleaseOrder reads it as run 0, never
  ordered against a publication); `edge` marks the opt-in unverified lane. `PlatformVersionSchemeGuard`
  (`test/MeshWeaver.Documentation.Test`) evaluates `PlatformVersion` and the composed `$(Version)`
  through real MSBuild on every pull request and reds on any other shape.
- 🚨 **The minor moves by a merged pull request, the major only on a declared break.** In practice
  the minor moves with the next-line pull request `release.yml` opens after a release. The run number
  keeps increasing across a bump, so the order is unaffected. The binding identity
  (`AssemblyVersion`) stays `<major>.0.0.0` across a minor bump, so module bytes sealed on a newer
  minor still load on a lagging portal of the same compatibility key.
- 🚨 **The release is still a PROMOTION, never a rebuild.** `release.yml` adds `<major>.<minor>.0` to
  bytes that already shipped as `<major>.<minor>.<run>` and passed promote, bake and seal, and
  compiles nothing (§3). Stable installs follow those clean tags only; a run-numbered build carries no
  label but is never a Stable candidate (`VersionSelect.PickTargets`).

### Minting is one shape; READING still has to accept the retired ones

Retiring a notation stops it being *produced*. It does not remove it from the registry:

| | shapes | where |
|---|---|---|
| **MINT** | `<major>.<minor>.<run>`, `<major>.<minor>.0`, `<major>.<minor>.0-edge.<run>`, `<major>.<minor>.0-dev` | `Directory.Build.props`, `release.yml`, `edge-images.yml` |
| **READ** | additionally `X.Y.Z-ci.<n>`, the retired rc line's `X.Y.Z-<label>.ci.<n>`, and any historical `rc*` tag | `PlatformReleaseOrder.BuildOrdinal`, `VersionSelect`, `platform-version.py`, `resolve-platform.py`, and MeshWeaver.Plugins' readers |

🚨 **Do not narrow a reader to match the minter.** Those images are still addressable, an install
can be sitting on one right now, and a reader that stops parsing `[.-]ci.<n>` reads such a tag as
*carrying no run number* — which promotes it into the promotion-ranked half of the order. That is the
#3542 freeze, rebuilt by a tidy-up. The minter is where the shape is decided; the reader is where
history is survived.

### History: the two-shape scheme and the `edge` re-label

From 2026-09-07 until the SemVer minter landed, every continuous build was `X.Y.Z-ci.<n>` and the
release the clean `X.Y.Z`. `edge-images.yml` then derived its tag by rewriting the `ci` label to
`edge`; it now spells `<major>.<minor>.0-edge.<run>` from the line directly. The ordering analysis
below is kept because the retired tags are still ordered against everything else.

### What retiring `rc` fixes in the ORDERING — and the half it does not

SemVer 2 orders the *strings* like this, and that is still what every package consumer sees:

```
3.0.0-ci.7900  <  3.0.0-preview1  <  3.0.0-rc9.ci.7818  <  3.0.0-rc13  <  3.0.0  <  3.1.0-ci.1
```

🚨 §11.4 compares pre-release identifiers as **TEXT**, so `"ci" < "rc"` and `3.0.0-ci.<n>` ranks
below `3.0.0-rc8` **for every n** — and, for the same reason one hop on, a clean `3.0.0` floor is
unsatisfied by a `3.0.0-ci.<n>` platform, because a pre-release ranks below its own release. Measured
2026-09-07 (#3554): **42 packages** declared rc-line or clean-`3.0.0` floors naming a platform that
did not exist and could not be built; the registry held **1268 tags — 48 `3.0.0-ci.*`, ZERO `rc*`,
ZERO `3.1.0-*`**; every open pull request in MeshWeaver.Plugins went red; and both AKS portals spent
the day logging, per module, two versions that look like they are in the right order:

```
HOLDING 3.0.0-ci.7989 — AI: the module requires platform 3.0.0-rc8 or newer
                         but this deployment runs 3.0.0-ci.7989
```

### 🚨 Where a floor is compared is being split in two — cite the right half

A `content.minMeshVersion` floor is a platform version, so the ordering above lands on it. **But the
two places it is compared are diverging, and only one of them keeps a verdict:**

| | who compares | verdict |
|---|---|---|
| **Authoring / pack time** | `.github/scripts/check-module-platform-floor.py`, MeshWeaver.Plugins' `check-module-floors.py` | **an ERROR.** A floor above the platform the bundle is built against is unsatisfiable by construction, and the lane refuses it. This is where the ordering above bites, and it is the gate that reddened the Plugins pull requests. |
| **Runtime** | `PlatformFloor`, then `ModulePlatformLink.Check` plus the actual load | **a COMPARABLE floor above the running platform holds the version** (policy `package-min-mesh-version`, [Module Adoption Policy](../ModuleAdoptionPolicy) R2): it is not landed or installed, and the installed version keeps running. A floor that cannot be ordered against the running version (an rc label against a ci build, the case this section measured) stays an advisory, as it has been since [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648). The link probe at landing and at boot, plus the load itself, still gate what passes the floor; a generation that cannot load falls back to the previous one (#3649). |

So read the `HOLDING …` line above as a **measurement of what happened on 2026-09-07**, never as the
contract: that hold is precisely what #3648 is removing, and this page deliberately does not restate
a runtime rule that another change owns. What survives unchanged either way is the **pack-time**
question, and there the comparison is not merely retained but pinned — the script must agree with
`NuGetVersionComparer` exactly, which `ModulePlatformFloorScriptParityTest` enforces case by case.
That is the half the ordering rule on this page is about.

**Retiring the rc line removes the LABEL half of the trap by construction** — with one pre-release
identifier in the whole scheme there is no label left to sort against.

🚨 **It does not remove the other half, and the other half is not going anywhere.** A clean `X.Y.Z`
genuinely outranks its own `X.Y.Z-ci.<n>` pre-releases. That is SemVer working correctly, it is
load-bearing, and it must keep working:

- a **Stable** install running `3.0.0-ci.7977` reaches the clean `3.0.0` *because* the release
  outranks the pre-release
  ([Self-Update Target Selection](/Doc/Architecture/SelfUpdateTargetSelection) §2) — an ordering
  #3648 does not touch;
- a **pack-time floor** of `3.0.0` must give the OPPOSITE answer for the same two strings: it has to
  be *satisfiable* by the `3.0.0-ci.7977` the bundle is built against, or a package declaring the
  current line cannot be packed at all while the whole fleet runs that line (measured: the last
  column of that page's §4 table — every `3.0.0-ci.N` against a clean `3.0.0` floor).

Same two strings, opposite required answers — so the shareable part is the **key**
(`PlatformReleaseOrder.BuildOrdinal`), never the predicate. Do not read *"there is no rc line any
more"* as *"pre-release ordering is no longer a hazard"*, and do not read *"floors are advisory at
runtime"* as *"a floor can say anything"*.

Two consequences of the same ordering, both load-bearing:

- **An install takes the clean release and nothing before it — by default.** `Stable` selects
  `!IsPrerelease`; the only clean tags are the promoted ones. Continuous builds are taken only
  under a version PATTERN (next subsection).
- **The number must move the day a release is tagged.** `3.0.0-ci.7950` sorts *below* `3.0.0`, so a
  continuous build stamped with an already-released number would stop every Continuous install from
  rolling forward. `release.yml` opens the pull request that moves the line to `3.1.0` itself; rc6
  shipped with the props still reading rc6, and rc10–rc13 were tagged with them on rc9, which is why
  that bump is no longer a human step.

🚨 **The self-updater does not order candidates by the version string at all** (#3542). It ranks by
`<n>` — the sealed-publication lineage — and uses the version string only between tags that carry no
`<n>` (the promotions). `<n>` must therefore never be replaced by anything that can reset: the old
seconds-since-midnight build number made a morning build sort below the previous evening's. See
[Self-Update Target Selection](/Doc/Architecture/SelfUpdateTargetSelection) for the two different
keys the *ordering* and the *is-this-newer* questions use.

### Which build an install takes — clean releases by default, continuous builds by pattern

> Maintainer, 2026-09-08: *"by default we will not upgrade as long as no version without `-ci…` is
> labelled ⇒ we want to have a clean label `3.0.1` to upgrade. If we want to get the `-ci…` we have
> to specify the pattern `3.0.1-ci*` or something. At the moment it is `3.0.0-ci*` as we have not
> released anything else."*

The version scheme has two shapes, and **self-update reads them as two channels with one default**:

| `Admin/UpdatePolicy` | `pattern` | what the install rolls to |
|---|---|---|
| `Stable` — **the default** | *(none)* | the newest clean `X.Y.Z`, and nothing before it |
| `Stable` | `3.0.*` | the newest clean release of that line only |
| `Continuous` | `3.0.1-ci*` | the newest sealed `3.0.1-ci.<n>` — by run number, so `ci.7845 < ci.8059` numerically and a retired `rc` label never outranks a later run |
| `Continuous` | *(none)* | **the same as `Stable`** — a pre-release is eligible only when a pattern admits it; the poller says so once at Warning, naming the record and the pattern to set |
| `None` | *(ignored)* | nothing |

Three rules that follow, each pinned by `VersionSelectTest`:

- 🚨 **A pattern is a glob over the registry tag and admits exactly what it names.** `3.0.1-ci*`
  matches `3.0.1-ci.30` and NOT `3.0.2-ci.1` — and not the clean `3.0.1` either. So `3.0.0-ci*` can
  **never** select `3.0.1`: following one line's continuous builds *ends* by the pattern matching
  nothing new, and that is the intended way for it to end. `*` matches any run of characters, `?`
  one; the whole tag must match; case does not matter.
- **The order under a pattern is still the lineage** (`PlatformReleaseOrder.BuildOrdinal` — the CD
  run number), never the version string: `ci < rc < release` holds where a wide pattern admits all
  three, and two builds of one line compare by `<n>`.
- 🚨 **The moving image pointers are never candidates.** CD re-points `<major>-latest`,
  `<major.minor>-latest` and `<major.minor.patch>-latest` at every publication of the line
  (`3-latest`, `3.0-latest`, `3.0.1-latest`) so that a fresh install *starts* from a pointer and
  self-updates from there. They name different bytes tomorrow; `VersionSelect` drops them before
  any policy or pattern is applied, and a pattern that would match one textually still selects
  nothing.

**How we do semver, in one line:** `<major>.<minor>.<patch>[-ci.<n>]` — the *family* is the major
line (`3`), a *release* is a clean label on it, a *continuous build* is a release-in-progress
distinguished only by its run number. A clean label is the only thing the default channel moves on,
which is why it is what "releasing" means here.

🚨 **The fleet's setting while no clean release above `3.0.0` exists: `policy: Continuous`,
`pattern: 3.0.0-ci*`** on every fleet instance — they follow the line's sealed sets. **Change
it the day `3.0.1` is tagged**: either remove the pattern (the install then waits for clean
releases — the default) or move it to `3.0.1-ci*` to keep following the next line's builds. A
record that still reads `3.0.0-ci*` after the tag is not broken, it is finished: it selects nothing
new, which the Updates tab shows as "no newer release".

The record an operator writes (Settings → Updates edits the same fields):

```json
{ "$type": "UpdatePolicyContent", "policy": "Stable" }                                   // clean only — the default
{ "$type": "UpdatePolicyContent", "policy": "Continuous", "pattern": "3.0.1-ci*" }      // follow one line's builds
```

`SelfUpdate__DefaultPolicy` / `SelfUpdate__DefaultPattern` seed a NEW install's record (a dev/test
host that should track the line sets `Continuous` + `3.0.0-ci*`). When `SelfUpdate__DefaultPolicy`
is SET — which is what a fleet deployment record's `updatePolicy` renders — the self-updater also
converges an EXISTING record's `policy` and `pattern` to those values at every start (policy
`self-update-record-authoritative`, [Why the Fleet Stopped Rolling Itself](../SelfUpdateFreeze));
with it unset, an existing record is edited on the record, never by configuration.

> 🚨 **There is no rc line and there will be none** (maintainer, 2026-09-05; restated and settled
> 2026-09-07). `3.0.0-rc1` … `3.0.0-rc13` were tagged and rebuilt on tagging, which made each
> "candidate" a candidate of nothing (§4), and SemVer sorted `rc13` below `rc2`, so nuget.org listed
> `rc9` as the newest pre-release for the whole run. The release is cut **only when every open issue
> is closed** (maintainer, same day); until then main keeps producing `3.0.0-ci.<n>` sets.

It is also the **data-sync content-version**: a continuous build syncs its docs and seed nodes
from the commit stamped into its assemblies, a release from the tag `v$(PlatformVersion)` that
names the same tree ([DataSyncSetup.md §4c](/Doc/Architecture/DataSyncSetup)). Code and content
ship in lockstep.

---

## 2. The build version and the compiled attributes

The split between the *publishable string* and the *compiled attributes* is what makes a promotion
possible at all:

| | How invoked | `Version` / image tag | `AssemblyVersion` | `FileVersion` | `InformationalVersion` |
|---|---|---|---|---|---|
| **main CD build** | `-p:CIRun=true -p:PlatformBuildNumber=<run>` | `3.1.<run>` | `3.0.0.0` | `3.1.0.0` | `3.1.0+<sha>` |
| **RELEASED** | `-p:PublicRelease=true` | `3.1.0` | `3.0.0.0` | `3.1.0.0` | `3.1.0+<sha>` under `CIRun` |
| **any other build** | *(default)* | `3.1.0-dev` | `3.0.0.0` | `3.1.0.0` | `3.1.0+<sha>` under `CIRun`, else `3.1.0-dev` |

- **Nothing builds under `PublicRelease` any more.** The flag survives for local experiments; a
  release is a main build retagged, so the bytes inside a `3.1.0` image report the `3.1.<run>`
  build they are, via `MESHWEAVER_PLATFORM_VERSION` in the image config. That is deliberate: the
  release *is* that build.
- **`AssemblyVersion` is STABLE within a MAJOR** (`3.0.0.0`) — the runtime assembly-binding
  identity, identical across every assembly in one build. A per-project time-based number once
  made `Memex.Database.Migration` bind to `MeshWeaver.Documentation, Version=3.0.0.280` while the
  packaged DLL carried another number (#143); binding identity must not depend on wall-clock
  time. It does NOT move with the minor: module and NodeType bytes sealed on a 3.1 build are adopted
  by a lagging 3.0 portal of the same compatibility key, and the default load context refuses a
  reference to a higher assembly version than it carries.
- **`FileVersion` is pinned** for the same reason `InformationalVersion` is: both are *compiled*
  attributes, and CI compile inputs are **commit-deterministic**
  ([#1660](https://github.com/Systemorph/MeshWeaver/issues/1660) WS3) so two CI builds of one
  commit produce ABI-identical assemblies — that is what lets the CI NodeType bake seed at portal
  boot.
- **The run-numbered patch** comes from `-p:PlatformBuildNumber`, which only `main-cd.yml` passes
  (`$GITHUB_RUN_NUMBER`). It reaches ONLY `$(Version)` — the image tag and
  `MESHWEAVER_PLATFORM_VERSION` — never a compiled attribute. 🚨 Anything that *parses* the run
  number back out of a version must still accept the retired notations, `[.-]ci.<n>` — those images
  are still addressable. Minting one shape and reading several is the split §1 sets out; do not
  collapse it in either direction.
- **`InformationalVersion`** is the bare `$(PlatformVersion)` under `CIRun=true` (the SDK appends
  `+<commit-sha>`); locally it equals `$(Version)`. NodeType ABI identity is
  `NodeTypeCompilationHelpers.FrameworkVersion` (`FrameworkBuildIdentity`): hosts that ship a
  `meshweaver-surface.manifest` resolve the **API-surface hash** (`s<hash>`), manifest-less CI
  processes fall back to the stamped commit identity (`g<sha>`), manifest-less local builds to
  the identity anchor's MVID. None of them read the version string.
- **`-p:Version=…`** overrides the publishable string and NOTHING else (#3022) — `main-cd.yml`
  passes it to the portal publish so the image reports its own build.

---

### The moving pointers: `<major>-latest`, `<major.minor>-latest`, `<major.minor.patch>-latest`

**Maintainer, 2026-09-08:** *"platform will be anyway self-updating ⇒ should point to latest image
to start"*, *"let's offer all variants ⇒ we fix 1 digit, 2 digits, or even 3 digits"*, and for the
adapter's default *"3 latest and 4 latest — I am for the latter"*.

Every armed set moves the line pointers on `memex-portal-ai` and `memex-migration`, in ACR and in
GHCR, derived from the set's version (`.github/scripts/platform-version.py line-pointers`):

| pointer | moves to | never touched by |
|---|---|---|
| `3-latest` | every armed set of major 3, across minors | any 4.x set |
| `3.1-latest` | every armed set of the 3.1 line (`3.1.<run>`) | 3.2.x |
| `3.0.0-latest` | FROZEN on the last `3.0.0-ci.*` set: a `<major>.<minor>.<run>` build moves no three-part pointer, since `3.1.10340-latest` would name exactly one build | every new build |

CD writes them in **Phase D, after the arming PUT** (`memex-portal-ai:<version>`, CD's last write
before this), so a fresh install that resolves a pointer never sees a set whose migration exists
and whose portal does not. `release.yml` moves NO pointer when it promotes a `<major>.<minor>.<run>`
build (CD already moved them to it or past it; a release-side move could only move them backwards),
and keeps the old behaviour for a set of the retired notation. The self-updater ignores them — it selects on `^\d+\.\d+\.\d+` tags — so a
pointer is only ever a **first-start** address.

**The rule this makes clear:** a package of major N names `N-latest` and is otherwise independent
of the image. `MeshWeaver.Aspire.Hosting.Memex` defaults `ImageTag` to `<its own major>-latest`,
derived from its assembly version rather than typed, so a 4.x adapter cannot ship still naming
`3-latest`; a consumer that wants a narrower line passes `WithImage(tag: "3.0-latest")` or an
exact version. The package version moves only when the adapter's surface does — 3.0.x for fixes,
3.1.0 when the API grows — never per image.

## 3. Commands

```bash
# Local or pull-request build. Nothing to add → 3.1.0-dev
dotnet build

# What main CD computes for an image (the same call main-cd.yml makes, with its own run number):
dotnet msbuild src/MeshWeaver.Mesh.Contract/MeshWeaver.Mesh.Contract.csproj \
  -getProperty:Version -p:CIRun=true -p:PlatformBuildNumber=10340 -nologo   # → 3.1.10340

# RELEASE — no command builds one. Push an annotated tag on a promoted, sealed commit:
git tag -a v3.1.0 -m "MeshWeaver 3.1.0" <sha> && git push origin v3.1.0
```

### What `release.yml` does on that tag — and what it refuses

The lane **promotes**; it compiles nothing. In order:

1. **Refuses** a version that is not `v<major>.<minor>.<patch>`, a lightweight tag, a commit not on
   `main`, a commit whose `PlatformVersion` differs from the tag, and a version with no committed
   notes page at `Doc/ReleaseNotes/<x_y_z>`.
2. **Resolves the build** for the commit from its `<major>.<minor>.<run>` tag (or a retired `3.0.0-ci.<n>` one) on the manifest
   that carries its identity (`<short-sha>`, or the build's `staging-<short-sha>-<run id>` when a
   rebuild moved the bare tag; the retired `<core>-p<plugins>` pair tag is no longer required), and
   refuses a commit `main-cd` never promoted — *"wait for CD, confirm `Plugins: bake + seal`, push the tag again"*.
3. **Asserts the set is complete** (`check-image-set.sh`) **and sealed** for both the platform
   content and the Plugins modules (`check-release-availability.sh`).
4. **Records the release marker** `_releases/3.1.0` on every artifact store, holding the same
   framework identity the continuous build recorded, and re-asserts availability under the clean
   name — the very question a Stable install's gate asks ([ReleaseGates](/Doc/Architecture/ReleaseGates)).
5. **Retags** `memex-migration`, `mw-plugin-test`, then `memex-portal-ai` last (`<short-sha>` →
   `3.1.0`, manifest-only, seconds), and mirrors the three to GHCR.
6. **Publishes the GitHub Release** from the notes page.
7. **Opens the pull request** that moves `PlatformVersion` to `3.2.0`.

Everything the lane needs is asserted RED by a `preflight` job — no `continue-on-error`, no
`if: secret != ''` (AGENTS.md: a gate never tests its own inputs).

---

## 4. The workflow — continuous → release → next line

1. **Iterate.** Every green merge ships `3.1.<run>` (see the ordering note in §1 for who rolls onto it).
   > 📅 **2026-09-12 — "ships" means: promoted and sealed, then validated by the next daily runs.**
   > A continuous build no longer wakes every node repository (the per-build
   > `meshweaver-framework-released` wave is off by default — `Hosting:PlatformBuilds:BroadcastFrameworkReleases`,
   > Plugins#1707 — and no satellite lists the event). Each node repo builds and tests against the
   > newest SEALED set on its own pushes and once a day; that daily run is the full run, and its
   > green on every catalogue is the only "validated" the fleet has — there is no tag for it, and
   > the clean `X.Y.Z` of step 4 remains the only tag-shaped promotion. Turning the wave on is the
   > MAJOR-bump exception. The recommended setup, end to end:
   > `Hosting/BuildAndReleaseProcess` (MeshWeaver.Plugins; `get Hosting/BuildAndReleaseProcess` over the mesh MCP).
2. **Pick the build to release.** A commit whose CD run has `Promote`, `Verify every image
   shipped` **and** `Plugins: bake + seal` green — read the seal JOB, never the run's conclusion
   ([ContinuousDeliveryContract](/Doc/Architecture/ContinuousDeliveryContract)). Commit its notes
   page, `Doc/ReleaseNotes/3_1_0`, first: the lane will not release without it.
3. **Scan it.** Run the two OWASP ZAP scans — public active, authenticated passive — against the
   deployment serving that build, and write the verdict and every finding's disposition on the
   notes page ([OWASP ZAP Scan — Every Release](/Doc/Architecture/SecurityScanning)). `FAIL-NEW`
   must read 0 on both runs; a `Vulnerable JS Library` WARN blocks the tag; every other WARN is
   fixed or carried with a written reason. The lane cannot check this — it is the operator's gate.
4. **Tag it, annotated.** `git tag -a v3.1.0 -m "MeshWeaver 3.1.0" <sha> && git push origin v3.1.0`.
   The lane promotes the set (§3); Stable installs pick it up on their next check.
5. **Merge the bump.** The lane's pull request moves the line to `3.2.0`. A Continuous install keeps
   rolling either way — the run number orders it — but Stable sees the next clean release only once the
   line has moved.

> **Tagging discipline.** A version tag must be **immutable** (annotated, never force-moved): the
> images, the release marker and data-sync all key off it, so moving a tag silently ships different
> content under the same version. Patch lines are not a thing under continuous delivery from
> `main`: a fix is the next continuous build, and the next release is the next minor.

---

## 5. Retired: the rc line and NuGet

Up to `3.0.0-rc13` (2026-08-31) every `v*` tag also ran `dotnet pack` and pushed every packable
project to nuget.org. That stopped with the rc line: **nothing in the fleet restores a MeshWeaver
package** — in-mesh source compiles against the platform *image*, module bundles carry their own
closures, and satellite repositories build inside `mw-plugin-test`
([PluginPackaging](/Doc/Architecture/PluginPackaging),
[ModuleBuildArchitecture](/Doc/Architecture/ModuleBuildArchitecture)). The packages already
published were **retired and unlisted** on 2026-09-07 — forty-three ids, thirty-nine stalled at
`rc13`, three at `rc8` and one at `rc7`. Exactly two packages survive, both entry points rather than platform bytes:
`MeshWeaver.Aspire.Hosting.Memex` and `MeshWeaver.MemexTemplate`, published from
`publish-packages.yml` on this same `v*.*.*` tag. Unlisting does not erase: existing exact-version
pins keep resolving. See [NuGet Package Retirement](/Doc/Architecture/NuGetPackageRetirement) for the
survivor rule, the retirement tool, and why startup configuration goes on the Aspire adapter instead
of into new packages.

---

## 6. What's New is per RELEASE, not per change

Policy [`whatsnew-cadence`](/Doc/Architecture/PolicyNotProse). A What's New entry is written when a
**release stream completes**, not when a change merges. It says what that release finished, and it **links to the
doc page** that explains it. The explanation lives in the doc; What's New is the announcement.

### Why this changed

Under the previous rule — *add a What's New entry when user-facing* — the folder reached **1,292
entries** in under three months: 40 in July 2026, 662 in August (≈21/day) and 590 over the first 20
days of September (≈30/day). At that rate it is not a changelog, it is the commit log with nicer
titles. Nobody reads twenty entries a day, so the effort
of writing them was wasted twice over: once in the writing, and again because the one entry that
mattered was buried among nineteen that did not.

The failure is structural rather than editorial. A per-merge changelog has the granularity of the
thing that produced it (a pull request), not of the thing the reader cares about (a release). Every
entry is individually defensible and the aggregate is unusable.

### What to write now

| | |
|---|---|
| **One entry per release** | `WhatsNew/<yyyy-MM-dd>-<slug>.md`, written when the release is cut |
| **Named by the SHIP DATE, not the version** | `WhatsNewEntryIntegrityTest` enforces `^(?<date>\d{4}-\d{2}-\d{2})-.+$` and derives `Order` (`-YYYYMMDD`) from that date, so a version-prefixed filename is rejected from the feed. The version goes in `Name`/`Description`. |
| **It names what COMPLETED** | features and fixes that landed in that stream — the user-visible outcome, not the pull requests |
| **It links out** | each item points at the doc page that explains it; that page is the durable form |
| **It does not explain** | if an item needs more than a sentence, the sentence belongs in the doc and the link belongs here |

### What replaces the per-change note

Nothing is lost, because the durable form never was the What's New entry — the
`conserve-work-products` rule in `AGENTS.md` (described in
[Shared Rule Blocks](/Doc/Architecture/SharedRuleBlocks)) already requires the **doc page** in the
same change set. For how to write that page, see
[Authoring Documentation](/Doc/Architecture/AuthoringDocumentation). A merge that changes behaviour updates its doc page, as before. What it no
longer does is mint a dated file nobody will open.

> The existing 1,292 entries are history and stay where they are. This rule governs what is written
> from here on; a sweep that rewrites the archive would cost more than it returns.

Which items a release announces follows from
[Issue Taxonomy and the Release Readiness Gate](/Doc/Architecture/IssueTaxonomy) — the issues the
release closes are already labelled by `feature:`, and that grouping is the outline of the entry.

---

## 7. See also

- [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy) — the end-to-end model this versioning
  feeds: merge preconditions, CI producing all images to ACR by version, and the policy-driven
  **self-update** (each install rolls itself per `Admin/UpdatePolicy`).
- [ContinuousDeliveryContract.md](/Doc/Architecture/ContinuousDeliveryContract) — what a promoted,
  sealed set is, and why the seal job is the signal.
- [ReleaseGates.md](/Doc/Architecture/ReleaseGates) — the availability verdict the release marker
  feeds.
- [DataSyncSetup.md](/Doc/Architecture/DataSyncSetup) — the platform version doubles as the
  content-version for static-repo / GitHub data-sync.
- [Deployment.md](/Doc/Architecture/Deployment) — where the built images go (AKS vs Container Apps).
- [SecurityScanning.md](/Doc/Architecture/SecurityScanning) — the OWASP ZAP scans every release
  runs before its tag, and the findings each release carried.
