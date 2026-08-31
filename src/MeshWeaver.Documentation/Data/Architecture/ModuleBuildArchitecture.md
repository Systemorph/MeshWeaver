---
nodeType: Markdown
name: Module Build Architecture
category: Architecture
description: The one shape every repo's module CI follows — the platform image is the compiler and the reference set, everything shared is staged once per run, one Roslyn workspace builds the whole graph fail-fast, and every gate compiles against implementation frameworks.
icon: /static/NodeTypeIcons/code.svg
---

# Module Build Architecture

**This is the shape every repo follows** (maintainer directives, 2026-08-31/09-01). The platform
repo carries the reusable lanes and the builder; a satellite carries only its policy. A repo whose
CI deviates from this page is behind, not different.

## The compiler is the platform image

A module never runs against a source tree or a NuGet feed: it is loaded into the platform IMAGE and
bound by the assemblies in there. So the honest compiler is that image's own —
`mw-plugin-test build-project` runs INSIDE the pinned platform container, its `/app` is the
reference set, its runtime is the shared-framework surface. No .NET SDK, no restore, no platform
source checkout. A declared mode that cannot run FAILS; there is deliberately no SDK fallback
(a fallback would make "the container built it" and "the SDK built it" indistinguishable in a
green log).

An additional library resolves from the curated **module-libraries shelf**, and a
`PackageReference`'s compile surface is its **transitive closure** from the shelf's deps.json —
exactly what the SDK hands a consumer (`PackageReference Microsoft.Graph` lets code
`using Microsoft.Kiota…`; offering only the package's own assemblies re-created that gap as a
CS0234 wall on Plugins#1032).

## Everything shared is staged ONCE per run

"Update once at the beginning for everyone, and not by module." A `prepare` job stages what every
pack job consumes identically:

* the **platform image as a per-digest zstd tarball in the actions cache** (GitHub's blob storage,
  colocated with the runners) — pack jobs `docker load` it and touch NO registry on a warm digest.
  The per-job ACR pulls (~52s × N jobs, all at once on a fresh digest) were the stampede that
  helped beat the registry into `connection refused` on 2026-08-31;
* the platform `/app` and tester-app extractions (per-digest caches);
* the **module-pack tool**, published once per platform pin and downloaded per job (~5s) instead of
  `dotnet build` in every job (48–79s measured).

A cache miss in a pack job falls back to pulling the SAME digest-pinned bytes, loudly — a perf
fallback, never a verification fallback.

## One Roslyn workspace, one pass, fail-fast

The builder creates **one reference universe for the whole run**: every project shares the same
`PortableExecutableReference` instance per path, so each assembly is read from the filesystem and
its metadata decoded once. Siblings already built in this run ride `--prebuilt` artifacts — the
mesh is never rebuilt per dependent.

The compile itself is **one body pass, like csc**: parse + declaration diagnostics up front, body
diagnostics from the single `Emit`. (`GetDiagnostics()` before `Emit()` analyzed every method body
twice — measured on MeshWeaver.AI: 82s + 79s for work csc does once. The remaining cost is real:
~97% of that project's compile is Roslyn's nullable flow analysis, dominated by a few very large
methods — an argument for smaller methods, not a builder defect.)

**When a build fails, the run exits**: dependents report `blocked by <it>`, and independent nodes
that have not started refuse their slot naming the first failure. Nothing keeps earning minutes
after the verdict is red.

## Gates compile against IMPLEMENTATION frameworks

`compile-check` extracts the image's `/usr/share/dotnet/shared` beside `/app` and, seeing
`System.Private.CoreLib.dll`, switches to implementation-framework mode: no SDK ref pack, no
`FrameworkReference` — the check compiles exactly what the mesh compiles. This is not a
preference: a container-built module references `System.Private.CoreLib`'s identity directly, and
the ref pack cannot resolve it (11 NodeTypes failed CS0012 the moment the floor bundles were
container-built, while the portal compiled the same nodes fine). Measured: 80/80 in 44s vs 161s
with 11 false regressions on the ref-pack path.

## Roadmap (agreed, not yet landed)

* **After the global build, run tests** — one build job builds the whole graph in the one
  workspace; test jobs consume its artifacts instead of rebuilding.
* **Self-hosted runners with a mounted mirror volume** — a bare git mirror on a persistent drive,
  `git worktree add` per run at the release ref, worktree deleted at the end; docker layer store
  warm on the node so even the once-per-digest pull disappears.
* **`pack-module` and test verbs in the tester** — the last `dotnet` uses on a runner
  (the packer runtime, `dotnet test`) move inside the image.

## Rolling it out

The lanes are `workflow_call` reusables in this repo (`node-repo-module-pack.yml`,
`node-repo-compile-check.yml`, …) — a satellite adopts the shape by bumping its pinned lane SHA
and syncing its `scripts/compile-check.py` copy. Never hand-roll a repo's CI; policy lives in the
caller, mechanics live here. See [ModuleVersioning](ModuleVersioning) and
[NodeTypeCompilation](NodeTypeCompilation) for the versioning and runtime-compilation halves.
