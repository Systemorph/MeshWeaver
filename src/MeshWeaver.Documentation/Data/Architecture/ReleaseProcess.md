---
NodeType: Markdown
Name: "Release Process & Versioning"
Abstract: "How MeshWeaver is versioned and released: one central PlatformVersion in Directory.Build.props, two channels (CONTINUOUS for CI/local — build-numbered, vs RELEASED for CD — clean), and the RC→official→next workflow for NuGet and Docker. The same version is the data-sync content-version."
Icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#3949ab'/><path d='M12 4c3 1.5 4.5 4.5 4.5 8l-2 2h-5l-2-2C7.5 8.5 9 5.5 12 4z' fill='white'/><circle cx='12' cy='10' r='1.5' fill='#3949ab'/><path d='M9.5 16l-1.5 3 3-1.5M14.5 16l1.5 3-3-1.5' stroke='white' stroke-width='1.6' fill='none' stroke-linecap='round' stroke-linejoin='round'/></svg>"
Thumbnail: "images/DataMesh.svg"
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "Release"
  - "Versioning"
  - "CI/CD"
  - "NuGet"
---

# Release Process & Versioning

One number, two channels. The whole scheme lives in
[`Directory.Build.props`](../../../../Directory.Build.props) and applies to every
project in the solution.

---

## 1. The one number — `PlatformVersion`

```xml
<!-- Directory.Build.props -->
<PlatformVersion Condition="'$(PlatformVersion)' == ''">3.0.0-rc1</PlatformVersion>
```

This is the single maintained version, set centrally. **Override it at build time**
(Docker image / CI) without editing the file:

```bash
-p:PlatformVersion=3.0.0-rc2     # cut a different RC
-p:PlatformVersion=3.0.0         # graduate to the official release
```

It is also the **data-sync content-version**: a release tagged `v$(PlatformVersion)`
is what a deployed binary syncs its docs/seed nodes from
([DataSyncSetup.md §3](DataSyncSetup.md)). Code and content ship in lockstep.

---

## 2. Two channels — CONTINUOUS vs RELEASED

The `PublicRelease` flag picks the channel (think **CI** vs **CD**):

| | Flag | `Version` / NuGet / Docker | `AssemblyVersion` | Use |
|---|---|---|---|---|
| **CONTINUOUS** | *(default)* | `3.0.0-rc1.ci.<build>` | `3.0.0.<build>` | CI pipeline + every local/dev build. Carries a build number so it is **distinguishable** from a release; a local-deploy build always has one. |
| **RELEASED** | `-p:PublicRelease=true` | `3.0.0-rc1` *(clean)* | `3.0.0.<build>` | The CD/publish pipeline. Clean version on the package and the container image. |

- **`AssemblyVersion`/`FileVersion`** are always numeric `3.0.0.<build>` — the
  `-rc1` pre-release label is illegal there. The `<build>` component changes every
  build so MSBuild's `CopyToOutputDirectory` heuristic always picks up the freshly
  built framework DLL (otherwise test bins silently hold a stale binary).
- **`InformationalVersion`** always carries `+build.<ticks>` so
  `NodeTypeCompilationHelpers.FrameworkVersion` is distinct every build (NodeType
  cache invalidation) — released or continuous.
- **`-p:Version=…`** still overrides everything (escape hatch).

---

## 3. Commands

```bash
# CONTINUOUS — CI pipeline / local. Nothing to add → 3.0.0-rc1.ci.<build>
dotnet build
dotnet pack -c Release                       # → 3.0.0-rc1.ci.<build>.nupkg

# RELEASED NuGet (CD) — clean 3.0.0-rc1
dotnet pack -c Release -p:PublicRelease=true # → 3.0.0-rc1.nupkg

# RELEASED Docker image (CD) — clean 3.0.0-rc1 baked into the binary
dotnet publish memex/aspire/Memex.Portal.Distributed/Memex.Portal.Distributed.csproj -c Release \
  -t:PublishContainer -p:PublicRelease=true -p:PlatformVersion=3.0.0-rc1 \
  -p:ContainerImageTag=3.0.0-rc1
```

(`-p:ContainerImageTag` is the image tag; `-p:PlatformVersion` is the version baked
into the assemblies. Keep them equal for a release.)

---

## 4. The workflow — RC → official → next

We stay in **pre-release** (`-rcN`) and iterate until it's right, then graduate.

1. **Iterate continuous builds** locally / in CI — `3.0.0-rc1.ci.<build>`. Deploy
   these for local testing.
2. **Cut a release candidate** when you want a clean artifact to verify:
   `-p:PublicRelease=true` → `3.0.0-rc1`. Tag the repo `v3.0.0-rc1`. Bump to
   `-rc2`, `-rc3`… (`-p:PlatformVersion=3.0.0-rcN`) as needed.
3. **Graduate to official** when it all works:
   `-p:PublicRelease=true -p:PlatformVersion=3.0.0` → `3.0.0`. Tag `v3.0.0`.
4. **Move to the next line:** bump the central default in `Directory.Build.props`
   to `3.1.0-rc1` (or `3.0.1-rc1`) and repeat. This is the *only* time you edit the
   file — RCs are just build-time overrides.

> **Tagging discipline.** A version tag must be **immutable** (annotated, never
> force-moved) — the data-sync fingerprint trusts it. If you want belt-and-braces,
> also record the resolved commit SHA and pin sync to the SHA while displaying the
> version (tag for humans, gate on the SHA — [DataSyncSetup.md §4c](DataSyncSetup.md)).

---

## 5. See also

- [DataSyncSetup.md](DataSyncSetup.md) — the platform version doubles as the
  content-version for static-repo / GitHub data-sync.
- [Deployment.md](Deployment.md) — where the built images go (AKS vs Container Apps).
