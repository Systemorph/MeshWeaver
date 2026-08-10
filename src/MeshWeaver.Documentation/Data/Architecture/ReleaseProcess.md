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
`Directory.Build.props` and applies to every
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
([DataSyncSetup.md §3](/Doc/Architecture/DataSyncSetup)). Code and content ship in lockstep.

> 🚨 **The `-rc1` label is not cosmetic — do not drop it from the default.** SemVer
> pre-release identifiers compare ASCII-lexically (§11.4) and `"ci" < "preview1"`, so a
> bare `3.0.0` core would yield `3.0.0-ci.<n> < 3.0.0-preview1` — every CI build sorting
> *below* the already-published `v3.0.0-preview1`. The self-updater picks the **newest**
> version off the registry, so it would pin to `3.0.0-preview1` and never roll forward
> again. `3.0.0-rc1.ci.<n> > 3.0.0-preview1` is correct. The tag must also resolve:
> data-sync pulls content from `v$(PlatformVersion)`, and only `v3.0.0-preview1` and
> `v3.0.0-rc1` exist. Keep a pre-release label on the core until `3.0.0` ships clean.

---

## 2. Two channels — CONTINUOUS vs RELEASED

The `PublicRelease` flag picks the channel (think **CI** vs **CD**):

| | Flag | `Version` / NuGet / Docker | `AssemblyVersion` | `FileVersion` | Use |
|---|---|---|---|---|---|
| **CONTINUOUS** | *(default)* | `3.0.0-rc1.ci.<build>` | `3.0.0.0` | `3.0.0.<build>` | CI pipeline + every local/dev build. Carries a build number so it is **distinguishable** from a release. |
| **RELEASED** | `-p:PublicRelease=true` | `3.0.0-rc1` *(clean)* | `3.0.0.0` | `3.0.0.<build>` | The CD/publish pipeline. Clean version on the package and the container image. |

- **`AssemblyVersion` is STABLE `3.0.0.0`** — never build-numbered. It is the runtime
  **assembly-binding identity**, so it must be identical across every assembly in one
  build; `<build>` is evaluated *per project* at MSBuild time, so a time-based
  `AssemblyVersion` drifts a few seconds between projects in the same run. That is
  exactly issue #143: `Memex.Database.Migration` bound to `MeshWeaver.Documentation,
  Version=3.0.0.280` while the packaged DLL carried another number →
  `FileNotFoundException` at startup → the migration CrashLoopBackOff'd and Space
  creation failed. **Do not reintroduce a per-build `AssemblyVersion`.**
- **`FileVersion` keeps the per-build number** (`3.0.0.<build>`). It does not
  participate in binding, and changing every build is what drives MSBuild's
  `CopyToOutputDirectory` heuristic so test bins don't hold a stale framework DLL.
  Both are numeric — the `-rc1` pre-release label is illegal in either.
- **`<build>` is not one number.** The `.ci.N` suffix uses `$(GITHUB_RUN_NUMBER)` when
  present (**monotonic** — the self-updater picks the newest version, and the
  seconds-since-midnight value resets at midnight); `FileVersion` uses that
  seconds-since-midnight value; **locally both are a stable `0`**, so a dev rebuild
  doesn't regenerate `AssemblyInfo.cs` and destroy incremental builds.
- **`InformationalVersion`** carries `+build.<ticks>` **only under `CIRun=true`**;
  locally it equals `$(Version)`. NodeType ABI identity no longer depends on it —
  `NodeTypeCompilationHelpers.FrameworkVersion` is the **`MeshWeaver.Graph` assembly's
  MVID** (a content hash of the compiled module), so it is stable across rebuilds that
  don't change Graph's bytes and changes whenever they do.
- **`-p:Version=…`** still overrides everything (escape hatch) — and it is what the
  tag-driven release workflows actually pass.

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

### Automated by GitHub Actions — the real release path

You don't run `pack`/`publish` by hand for a release. Pushing a **`v*.*.*` tag**
fires two tag-gated workflows (both derive `VERSION` from the tag):

- `.github/workflows/release-packages.yml`
  → builds with `/p:Version=$VERSION`, packs `--no-build` with
  `/p:PackageVersion=$VERSION`, and pushes to **nuget.org** (clean `3.0.0-rc1`).
- `.github/workflows/release-images.yml`
  → builds the `memex-portal-ai-base` image multi-arch with buildx, then the three app
  images **with `-p:Version=$VERSION`** so the binaries carry the clean tag version, and
  pushes to **GHCR** (`ghcr.io/<owner>/memex-portal-ai`, `memex-portal`,
  `memex-migration`) tagged `$VERSION` + `latest` — then **mirrors all three into ACR**
  (`meshweaver.azurecr.io`) with `az acr import`, so a `Stable`-policy install finds the
  clean release on the same registry it already polls.

Neither workflow passes `-p:PublicRelease=true`: the explicit `-p:Version=$VERSION`
already overrides the channel logic, and the tag is the source of the version.

So **cutting a release = pushing a tag**:

```bash
git tag v3.0.0-rc1 && git push origin v3.0.0-rc1
```

The deployed image's binaries then carry `3.0.0-rc1`, and data-sync pulls content
from that same `v3.0.0-rc1` tag ([DataSyncSetup.md §4c](/Doc/Architecture/DataSyncSetup)) — one tag,
one consistent code + image + content.

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
> force-moved): both the release artifacts *and* the data-sync content key off it, so
> moving a tag silently ships different content under the same version. The tag — not
> the build's own commit — is the sync ref (the chicken-and-egg, see
> [DataSyncSetup.md §4c](/Doc/Architecture/DataSyncSetup)); GitHub resolves tag→commit at sync time.

---

## 5. See also

- [ReleaseStrategy.md](/Doc/Architecture/ReleaseStrategy) — the end-to-end model this versioning
  feeds: merge preconditions, CI producing all images to ACR by version, and the policy-driven
  **self-update** (each install rolls itself per `Admin/UpdatePolicy`).
- [DataSyncSetup.md](/Doc/Architecture/DataSyncSetup) — the platform version doubles as the
  content-version for static-repo / GitHub data-sync.
- [Deployment.md](/Doc/Architecture/Deployment) — where the built images go (AKS vs Container Apps).
