---
NodeType: Markdown
Name: "Release Process & Versioning"
Abstract: "How MeshWeaver is versioned and released: one central PlatformVersion in Directory.Build.props naming the NEXT release, continuous builds as <version>-ci.<n>, and a release that is a PROMOTION of a sealed continuous set — never a rebuild. No rc line, no NuGet. The same version is the data-sync content-version."
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

## 1. The one number — `PlatformVersion`

```xml
<!-- Directory.Build.props -->
<PlatformVersion Condition="'$(PlatformVersion)' == ''">3.1.0</PlatformVersion>
```

The single maintained version names the **next release**. Every continuous build derives its
version from it by appending the CI run number as the one and only pre-release identifier:

| Build | Version | Where it comes from |
|---|---|---|
| continuous (CI) | `3.1.0-ci.7900` | `main-cd.yml`, every green merge to `main` |
| local | `3.1.0-ci.0` | any `dotnet build` on a developer machine |
| release | `3.1.0` | `release.yml`, on the annotated tag `v3.1.0` — a **promotion** of one of the continuous builds above |

The ordering is what the self-updater relies on (`VersionSelect`, SemVer 2 via `NuGetVersion`):

```
3.0.0-rc9.ci.7818  <  3.0.0-rc13  <  3.1.0-ci.1  <  3.1.0-ci.7900  <  3.1.0  <  3.2.0-ci.1
```

Three consequences, all load-bearing:

- **Continuous installs always move forward.** `3.1.0-ci.<n>` outranks every `3.0.0-*` by its
  minor number, and `<n>` is the GitHub Actions run number, monotonic per workflow. 🔴 Do not
  replace it with anything that can reset (the old seconds-since-midnight number made a morning
  build sort below the previous evening's).
- **A Stable install takes the clean release and nothing before it.** `Stable` selects
  `!IsPrerelease`; the only clean tags are the promoted ones.
- **The number must move the day a release is tagged.** `3.1.0-ci.7950` sorts *below* `3.1.0`, so
  a continuous build stamped with an already-released number would stop every Continuous install
  from rolling forward. `release.yml` opens the pull request that moves the line to `3.2.0` itself;
  rc6 shipped with the props still reading rc6 and rc10–rc13 were tagged with them on rc9, which is
  why that bump is no longer a human step.

> 🚨 **There is no rc line.** The `3.0.0-rc1` … `3.0.0-rc13` labels were retired on 2026-09-05 and
> `3.0.0` was never cut clean. Two reasons, one of them a trap worth remembering: SemVer §11.4
> compares pre-release identifiers as text, so `rc13 < rc2`, and nuget.org listed `rc9` as the
> newest pre-release for the whole run; and a "candidate" that is rebuilt on tagging is not a
> candidate of anything (§4). A clean line has neither problem: the build number is the only
> pre-release identifier, and it compares numerically. Do not "fill in" `v3.0.0`.

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
| **CONTINUOUS** | *(default)* | `3.1.0-ci.<run>` | `3.1.0.0` | `3.1.0.0` | `3.1.0+<sha>` under `CIRun` |
| **RELEASED** | `-p:PublicRelease=true` | `3.1.0` | `3.1.0.0` | `3.1.0.0` | `3.1.0+<sha>` under `CIRun` |

- **Nothing builds under `PublicRelease` any more.** The flag survives for local experiments; a
  release is a continuous image retagged, so the bytes inside a `3.1.0` image report the
  `3.1.0-ci.<n>` build they are, via `MESHWEAVER_PLATFORM_VERSION` in the image config. That is
  deliberate: the release *is* that build.
- **`AssemblyVersion` is STABLE within a line** (`3.1.0.0`) — the runtime assembly-binding
  identity, identical across every assembly in one build. A per-project time-based number once
  made `Memex.Database.Migration` bind to `MeshWeaver.Documentation, Version=3.0.0.280` while the
  packaged DLL carried another number (#143); binding identity must not depend on wall-clock
  time. It moves with the line (`3.2.0.0` after the next bump), which is fine: module bundles are
  keyed by framework identity and re-baked per set, never bound by assembly version.
- **`FileVersion` is pinned** for the same reason `InformationalVersion` is: both are *compiled*
  attributes, and CI compile inputs are **commit-deterministic**
  ([#1660](https://github.com/Systemorph/MeshWeaver/issues/1660) WS3) so two CI builds of one
  commit produce ABI-identical assemblies — that is what lets the CI NodeType bake seed at portal
  boot.
- **The `-ci.<n>` suffix** uses `$(GITHUB_RUN_NUMBER)` when present, `0` locally. It reaches
  ONLY `$(Version)` — the image tag and `MESHWEAVER_PLATFORM_VERSION` — never a compiled
  attribute. 🚨 Anything that parses the build number back out of a version must accept both
  separators, `[.-]ci.<n>`: the retired rc line used `.ci.` and its tags are still in ACR.
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
# CONTINUOUS — CI and local. Nothing to add → 3.1.0-ci.<run> (3.1.0-ci.0 locally)
dotnet build

# What CI computes for an image (the same call main-cd.yml makes):
dotnet msbuild src/MeshWeaver.Mesh.Contract/MeshWeaver.Mesh.Contract.csproj \
  -getProperty:Version -p:CIRun=true -nologo

# RELEASE — no command builds one. Push an annotated tag on a promoted, sealed commit:
git tag -a v3.1.0 -m "MeshWeaver 3.1.0" <sha> && git push origin v3.1.0
```

### What `release.yml` does on that tag — and what it refuses

The lane **promotes**; it compiles nothing. In order:

1. **Refuses** a version that is not `v<major>.<minor>.<patch>`, a lightweight tag, a commit not on
   `main`, a commit whose `PlatformVersion` differs from the tag, and a version with no committed
   notes page at `Doc/ReleaseNotes/<x_y_z>`.
2. **Resolves the continuous set** for the commit from the tags on `memex-portal-ai:<short-sha>`
   (`3.1.0-ci.<n>` and the `<core>-p<plugins>` pair tag), and refuses a commit `main-cd` never
   promoted — *"wait for CD, confirm `Plugins: bake + seal`, push the tag again"*.
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

1. **Iterate.** Every green merge ships `3.1.0-ci.<n>`; Continuous installs roll onto it.
2. **Pick the build to release.** A commit whose CD run has `Promote`, `Verify every image
   shipped` **and** `Plugins: bake + seal` green — read the seal JOB, never the run's conclusion
   ([ContinuousDeliveryContract](/Doc/Architecture/ContinuousDeliveryContract)). Commit its notes
   page, `Doc/ReleaseNotes/3_1_0`, first: the lane will not release without it.
3. **Tag it, annotated.** `git tag -a v3.1.0 -m "MeshWeaver 3.1.0" <sha> && git push origin v3.1.0`.
   The lane promotes the set (§3); Stable installs pick it up on their next check.
4. **Merge the bump.** The lane's pull request moves the line to `3.2.0`; auto-arm enqueues it.
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
published stay listed as history; `MeshWeaver.Hosting.PostgreSql`, `MeshWeaver.AI` and
`MeshWeaver.Blazor` stopped at `rc7` when they moved to MeshWeaver.Plugins, which publishes bundles
and no packages.

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
