# MeshWeaver.Plugin.Build

The build-side tool behind the module lane:

```
meshweaver-plugin-build module-pack <moduleOutputDir> [options]   # a module's publish output → a registry bundle
meshweaver-plugin-build module-fetch <package> [options]          # a registry bundle → a local directory
```

Run either verb with `--help` for its options. `module-pack` writes the bundle
(`MeshWeaver.Plugin.<Package>.<Version>.module.nupkg` — a zip in NuGet layout with the module's
assemblies under `meshweaver/modules/` and a manifest naming the entry assembly, its closure, its
`minMeshVersion` floor and the framework identity it was built against) that
`node-repo-module-pack.yml` uploads and, on a trunk push, POSTs to the registry.

## What is deliberately NOT here

Until 2026-08-30 the bare verb packed a **node package** (a Store/Plugin with in-mesh `Source/*.cs`)
by emitting a `.csproj` that referenced the `MeshWeaver.*` NuGet packages at a "floor" — the newest
version every referenced package was published at — running `dotnet build` on it and writing a
`.nupkg`. That is gone, for two reasons the maintainer stated:

- **In-mesh source runs inside the portal image.** The only honest reference set is that image's
  `/app` assemblies plus the modules its publication was sealed against — which is exactly what
  `node-repo-compile-check.yml` type-checks against and what the gates run. A NuGet floor is a
  different, older reference set (it stopped at rc7 the day `MeshWeaver.AI` left the platform repo),
  and compiling against it recreated the very skew #2707 removed.
- **Nothing a node repo builds goes to a package feed.** Consumers fetch bundles assembled from a
  sealed publication (`/api/plugins/bundles`), never packages from NuGet — so a `.nupkg` per node
  package had no consumer, and a NuGet publish token had no place in CI.
