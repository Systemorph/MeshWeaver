---
NodeType: Markdown
Name: "Release Process & Versioning"
Abstract: "The authoritative version scheme: exactly two shapes, X.Y.Z-ci.<n> for every continuous build and clean X.Y.Z for the release — no rc, no preview, no labelled line, ever. One central PlatformVersion in Directory.Build.props naming the NEXT release, a release that is a PROMOTION of a sealed continuous set rather than a rebuild, what may be minted versus what must still be read, and the SemVer ordering that #3554 turned into 42 unsatisfiable module floors."
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

One number, two channels, one set of bytes. The whole scheme lives in
`Directory.Build.props` and applies to every project in the solution.

---

## 1. The version scheme — exactly two shapes

**A MeshWeaver version has exactly TWO shapes. There are no others, and there will be no others.**

| shape | what it is | minted by |
|---|---|---|
| **`X.Y.Z-ci.<n>`** | every continuous and temporary build — each green merge to `main`, each local `dotnet build` | `Directory.Build.props`, on every build |
| **`X.Y.Z`** | the release — no pre-release label at all | `release.yml`, by **retagging** one of the builds above |

> Maintainer, 2026-09-07: *"we will then not introduce the rc line anymore"* · *"let's just use
> version numbers `-ci` for temp then without `-ci` for final version"* · *"write this in docs"*.

```xml
<!-- Directory.Build.props — the ONE maintained number, naming the NEXT release -->
<PlatformVersion Condition="'$(PlatformVersion)' == ''">3.0.0</PlatformVersion>
```

| Build | Version | Where it comes from |
|---|---|---|
| continuous (CI) | `3.0.0-ci.7900` | `main-cd.yml`, every green merge to `main` |
| local | `3.0.0-ci.0` | any `dotnet build` on a developer machine |
| release | `3.0.0` | `release.yml`, on the annotated tag `v3.0.0` — a **promotion** of one of the continuous builds above |

Three rules, not three observations:

- 🚨 **`-ci.<n>` is a CHANNEL MARKER, never a version.** The version is `X.Y.Z`, the line
  `PlatformVersion` names — always the *next* release. `<n>` says only *which publication of that
  line* this is: `$(GITHUB_RUN_NUMBER)`, monotonic per workflow, `0` locally. It reaches
  `$(Version)` — the image tag, the package version and `MESHWEAVER_PLATFORM_VERSION` — and no
  compiled attribute (§2). Two builds of one line differ only in `<n>`, and `<n>` compares
  numerically.
- 🚨 **The release is a PROMOTION of a sealed continuous set, never a rebuild.** `X.Y.Z` names bytes
  that already shipped as `X.Y.Z-ci.<n>` and passed promote, bake and seal; `release.yml` retags that
  manifest and compiles nothing (§3). So there is nothing left for a "candidate" label to mark — the
  candidates *are* the continuous builds, and the one that becomes the release is chosen by tagging
  it. That is what makes the rc line unnecessary rather than merely unwanted: up to `rc13` a tag
  REBUILT, which made every "release candidate" a candidate for a set that did not exist yet.
- 🚨 **No other pre-release label may ever be MINTED** — no `rc`, no `preview`, no `beta`, no
  labelled line, no `.ci.` separator variant. `PlatformVersion` carries no label, so the composed
  `$(Version)` is one of the two shapes by construction. Prose is not enforcement:
  `PlatformVersionSchemeGuard` (`test/MeshWeaver.Documentation.Test`) evaluates both through real
  MSBuild on every PR and reds when either stops being true.

### Minting is one shape; READING still has to accept the retired ones

Retiring a label stops it being *produced*. It does not remove it from the registry, and those are
separate obligations:

| | shapes | where |
|---|---|---|
| **MINT** | `X.Y.Z-ci.<n>` and `X.Y.Z`, nothing else | `Directory.Build.props` composes `$(Version)`; `release.yml` retags |
| **READ** | additionally `X.Y.Z-<label>.ci.<n>` — the retired rc line used the `.ci.` separator — and any historical `rc*` tag | `PlatformReleaseOrder.BuildOrdinal`, `VersionSelect`, `edge-images.yml`, and MeshWeaver.Plugins' `check-platform-pins.py` |

🚨 **Do not narrow a reader to match the minter.** Those images are still addressable (manifests
kept, reachable via `staging-*`), an install can be sitting on one right now, and a reader that stops
parsing `[.-]ci.<n>` reads such a tag as *carrying no run number* — which promotes it into the
promotion-ranked half of the order. That is the #3542 freeze, rebuilt by a tidy-up. The minter is
where the shape is decided; the reader is where history is survived.

### The one thing that looks like a third shape: `edge`

`edge-images.yml` publishes `X.Y.Z-edge.<n>`, and that is **not a counter-example** — it is a
*derived channel re-label*, not a version the scheme mints:

- the lane computes `$(Version)` from this tree first, so what it starts from is already
  `X.Y.Z-ci.<n>`; it then rewrites the **`ci` label to `edge`** and keeps the very same run number;
- `VersionSelect` treats an `edge` tag as **ineligible under every policy** unless a caller opts in
  explicitly (`requireCiGreen: false`), so it is never a self-update candidate for `Continuous`,
  `Stable` or `None`;
- `PlatformReleaseOrder.ChannelLabels` therefore lists `ci` *and* `edge`, and `BuildOrdinal` reads
  the number out of either — the lineage is the publication's, whatever channel it was labelled for.

So the scheme still has two shapes: `$(Version)` — the thing `Directory.Build.props` composes and
`release.yml` promotes, and the thing the guard binds — is always `X.Y.Z-ci.<n>` or `X.Y.Z`. `edge`
is what a *separate, opt-in lane* renames one of those to on its way to an unverified image.
🚨 **A new channel is added by extending `ChannelLabels` and the re-label, never by adding a
pre-release LABEL to `PlatformVersion`** — the second is what §11.4 punishes, and it is the thing
this page forbids.

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
| **Runtime** | `ModulePlatformLink.Check` plus the actual load | **measured, never declared.** [Issue #3648](https://github.com/Systemorph/MeshWeaver/issues/3648) makes the declared floor *advisory* at all eight of its runtime decision points — logged, shown on the module's status row, deciding nothing — so it no longer refuses, holds or skips anything. |

So read the `HOLDING …` line above as a **measurement of what happened on 2026-09-07**, never as the
contract: that hold is exactly what #3648 removes. What survives unchanged is the **pack-time**
question, and there the comparison is not merely retained but pinned — the script must agree with
`NuGetVersionComparer` exactly, which `ModulePlatformFloorScriptParityTest` enforces case by case.

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

- **A Stable install takes the clean release and nothing before it.** `Stable` selects
  `!IsPrerelease`; the only clean tags are the promoted ones.
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

## 2. Two channels — CONTINUOUS vs RELEASED

The `PublicRelease` flag picks the channel, and the split between the *publishable string* and
the *compiled attributes* is what makes a promotion possible at all:

| | Flag | `Version` / image tag | `AssemblyVersion` | `FileVersion` | `InformationalVersion` |
|---|---|---|---|---|---|
| **CONTINUOUS** | *(default)* | `3.0.0-ci.<run>` | `3.0.0.0` | `3.0.0.0` | `3.0.0+<sha>` under `CIRun` |
| **RELEASED** | `-p:PublicRelease=true` | `3.0.0` | `3.0.0.0` | `3.0.0.0` | `3.0.0+<sha>` under `CIRun` |

- **Nothing builds under `PublicRelease` any more.** The flag survives for local experiments; a
  release is a continuous image retagged, so the bytes inside a `3.0.0` image report the
  `3.0.0-ci.<n>` build they are, via `MESHWEAVER_PLATFORM_VERSION` in the image config. That is
  deliberate: the release *is* that build.
- **`AssemblyVersion` is STABLE within a line** (`3.0.0.0`) — the runtime assembly-binding
  identity, identical across every assembly in one build. A per-project time-based number once
  made `Memex.Database.Migration` bind to `MeshWeaver.Documentation, Version=3.0.0.280` while the
  packaged DLL carried another number (#143); binding identity must not depend on wall-clock
  time. It moves with the line (`3.1.0.0` after the next bump), which is fine: module bundles are
  keyed by framework identity and re-baked per set, never bound by assembly version.
- **`FileVersion` is pinned** for the same reason `InformationalVersion` is: both are *compiled*
  attributes, and CI compile inputs are **commit-deterministic**
  ([#1660](https://github.com/Systemorph/MeshWeaver/issues/1660) WS3) so two CI builds of one
  commit produce ABI-identical assemblies — that is what lets the CI NodeType bake seed at portal
  boot.
- **The `-ci.<n>` suffix** uses `$(GITHUB_RUN_NUMBER)` when present, `0` locally. It reaches
  ONLY `$(Version)` — the image tag and `MESHWEAVER_PLATFORM_VERSION` — never a compiled
  attribute. The separator is a literal `-`, unconditionally: `PlatformVersion` carries no label
  (§1), so there is no second case left to branch on. 🚨 Anything that *parses* the build number
  back out of a version must still accept both separators, `[.-]ci.<n>` — the retired rc line
  minted `.ci.` and those images are still addressable. Minting one shape and reading two is the
  split §1 sets out; do not collapse it in either direction.
- **`InformationalVersion`** is the bare `$(PlatformVersion)` under `CIRun=true` (the SDK appends
  `+<commit-sha>`); locally it equals `$(Version)`. NodeType ABI identity is
  `NodeTypeCompilationHelpers.FrameworkVersion` (`FrameworkBuildIdentity`): hosts that ship a
  `meshweaver-surface.manifest` resolve the **API-surface hash** (`s<hash>`), manifest-less CI
  processes fall back to the stamped commit identity (`g<sha>`), manifest-less local builds to
  the identity anchor's MVID. None of them read the version string.
- **`-p:Version=…`** overrides the publishable string and NOTHING else (#3022) — `main-cd.yml`
  passes it to the portal publish so the image reports its own build.

---

## 3. Commands

```bash
# CONTINUOUS — CI and local. Nothing to add → 3.0.0-ci.<run> (3.0.0-ci.0 locally)
dotnet build

# What CI computes for an image (the same call main-cd.yml makes):
dotnet msbuild src/MeshWeaver.Mesh.Contract/MeshWeaver.Mesh.Contract.csproj \
  -getProperty:Version -p:CIRun=true -nologo

# RELEASE — no command builds one. Push an annotated tag on a promoted, sealed commit:
git tag -a v3.0.0 -m "MeshWeaver 3.0.0" <sha> && git push origin v3.0.0
```

### What `release.yml` does on that tag — and what it refuses

The lane **promotes**; it compiles nothing. In order:

1. **Refuses** a version that is not `v<major>.<minor>.<patch>`, a lightweight tag, a commit not on
   `main`, a commit whose `PlatformVersion` differs from the tag, and a version with no committed
   notes page at `Doc/ReleaseNotes/<x_y_z>`.
2. **Resolves the continuous set** for the commit from the tags on `memex-portal-ai:<short-sha>`
   (`3.0.0-ci.<n>` and the `<core>-p<plugins>` pair tag), and refuses a commit `main-cd` never
   promoted — *"wait for CD, confirm `Plugins: bake + seal`, push the tag again"*.
3. **Asserts the set is complete** (`check-image-set.sh`) **and sealed** for both the platform
   content and the Plugins modules (`check-release-availability.sh`).
4. **Records the release marker** `_releases/3.0.0` on every artifact store, holding the same
   framework identity the continuous build recorded, and re-asserts availability under the clean
   name — the very question a Stable install's gate asks ([ReleaseGates](/Doc/Architecture/ReleaseGates)).
5. **Retags** `memex-migration`, `mw-plugin-test`, then `memex-portal-ai` last (`<short-sha>` →
   `3.0.0`, manifest-only, seconds), and mirrors the three to GHCR.
6. **Publishes the GitHub Release** from the notes page.
7. **Opens the pull request** that moves `PlatformVersion` to `3.1.0`.

Everything the lane needs is asserted RED by a `preflight` job — no `continue-on-error`, no
`if: secret != ''` (AGENTS.md: a gate never tests its own inputs).

---

## 4. The workflow — continuous → release → next line

1. **Iterate.** Every green merge ships `3.0.0-ci.<n>` (see the ordering note in §1 for who rolls onto it).
2. **Pick the build to release.** A commit whose CD run has `Promote`, `Verify every image
   shipped` **and** `Plugins: bake + seal` green — read the seal JOB, never the run's conclusion
   ([ContinuousDeliveryContract](/Doc/Architecture/ContinuousDeliveryContract)). Commit its notes
   page, `Doc/ReleaseNotes/3_0_0`, first: the lane will not release without it.
3. **Scan it.** Run the two OWASP ZAP scans — public active, authenticated passive — against the
   deployment serving that build, and write the verdict and every finding's disposition on the
   notes page ([OWASP ZAP Scan — Every Release](/Doc/Architecture/SecurityScanning)). `FAIL-NEW`
   must read 0 on both runs; a `Vulnerable JS Library` WARN blocks the tag; every other WARN is
   fixed or carried with a written reason. The lane cannot check this — it is the operator's gate.
4. **Tag it, annotated.** `git tag -a v3.0.0 -m "MeshWeaver 3.0.0" <sha> && git push origin v3.0.0`.
   The lane promotes the set (§3); Stable installs pick it up on their next check.
5. **Merge the bump.** The lane's pull request moves the line to `3.1.0`; auto-arm enqueues it.
   Until it merges, no continuous build may be relied on to roll a Continuous install forward.

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
published were **retired and unlisted** on 2026-09-07 — forty-three ids, every one stalled at
`rc7`/`rc8`/`rc9`. Exactly two packages survive, both entry points rather than platform bytes:
`MeshWeaver.Aspire.Hosting.Memex` and `MeshWeaver.MemexTemplate`, published from
`publish-packages.yml` on this same `v*.*.*` tag. Unlisting does not erase: existing exact-version
pins keep resolving. See [NuGet Package Retirement](/Doc/Architecture/NuGetPackageRetirement) for the
survivor rule, the retirement tool, and why startup configuration goes on the Aspire adapter instead
of into new packages.

---

## 6. See also

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
