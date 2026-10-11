---
Name: "NuGet Runtime Assets"
Abstract: "How a NodeType's #r \"nuget:...\" packages bind at run time: compile against lib/, load the runtimes/<rid>/lib group for the running RID, resolve runtimes/<rid>/native for DllImports, and bind any dependency the host already ships to the host's copy. Motivated by Microsoft.Data.SqlClient, whose lib/ assembly is a platform stub."
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "NuGet"
  - "Compiler"
---

# NuGet Runtime Assets

A NodeType (or an interactive cell) that writes `#r "nuget:Package, Version"` gets its package resolved
in-process by `NuGetAssemblyResolver` (`src/MeshWeaver.NuGet`). The resolver produces a
`ResolvedPackageSet` with two different answers, because a NuGet package has two different answers:

| Set | What it is | Who reads it |
|---|---|---|
| `AssemblyPaths` | **compile** assets: each package's `lib/<tfm>/` group (its runtime group if it ships no `lib/`) | Roslyn, as metadata references |
| `RuntimeAssemblyPaths` | **runtime** assets: `runtimes/<rid>/lib/<tfm>/` for the nearest RID that has a compatible framework, else `lib/<tfm>/` | the load context, through `ProbingDirectories` |
| `NativeLibraryPaths` | `runtimes/<rid>/native/` for the nearest RID that has any | the load context's `LoadUnmanagedDll` |

`ProbingDirectories` lists the runtime directories first and the `…/native` directories after them.

## Why the split exists — Microsoft.Data.SqlClient

Measured on partnerre.meshweaver.cloud (linux-x64) on 2026-10-11: a NodeType referencing
`Microsoft.Data.SqlClient` 6.1.1 compiled, then failed at render with
`Could not load file or assembly 'Microsoft.Data.SqlClient, Version=6.0.0.0'`. The package ships

```
lib/net9.0/Microsoft.Data.SqlClient.dll               ← "not supported on this platform" stub (0.9 MB)
runtimes/unix/lib/net9.0/Microsoft.Data.SqlClient.dll ← the real client on Linux/macOS (2.1 MB)
runtimes/win/lib/net9.0/Microsoft.Data.SqlClient.dll  ← the real client on Windows
```

and the resolver used to surface `lib/` only — for compile AND for run time. Every .NET SQL Server
client is built this way (Microsoft.Data.SqlClient 5.2/6.1/7.0, System.Data.SqlClient 4.9), so no
NodeType could talk to SQL Server. NuGet restore's rule, which the resolver now follows
(`RuntimeAssetSelector`): a package's `runtimes/<rid>/lib/<tfm>/` group **replaces** its `lib/<tfm>/`
group at run time. The stub's directory is therefore not probed at all — probing it would load the stub.

## The RID fallback chain

The running RID is `RuntimeInformation.RuntimeIdentifier`; `RuntimeAssetSelector.RidFallbackChain`
walks it nearest-first, a hand-written image of NuGet's runtime graph for the families a portal runs on:

| RID | Chain |
|---|---|
| `linux-x64` (the portal image) | `linux-x64 → linux → unix-x64 → unix → any` |
| `linux-musl-x64` | `linux-musl-x64 → linux-musl → linux-x64 → linux → unix-x64 → unix → any` |
| `osx-arm64` (a developer Mac) | `osx-arm64 → osx → unix-arm64 → unix → any` |
| `win-x64` | `win-x64 → win → any` |
| `ubuntu.22.04-x64` (a distro RID) | `ubuntu.22.04-x64 → ubuntu.22.04 → linux-x64 → linux → unix-x64 → unix → any` |

For each package the first RID in the chain with a framework-compatible `runtimes/<rid>/lib/<tfm>/`
folder wins (`FrameworkReducer.GetNearest` picks the folder); native assets are chosen the same way,
independently. A group holding only NuGet's `_._` placeholder is deliberately EMPTY and still wins —
nothing is loaded from that package there, and no older TFM, farther RID or `lib/` is substituted.
SqlClient on Unix is managed-only; the native path exists for packages that P/Invoke
(SQLite, ONNX runtime, …).

## Host unification

A package's dependency graph names the versions it was built against: SqlClient 6.1.1 pulls
`Microsoft.Extensions.Caching.Memory`, `System.Configuration.ConfigurationManager`, MSAL and
`Azure.Identity` (for `Authentication=Active Directory Managed Identity`), and with them older
`System.Text.Json` / `Microsoft.Extensions.*` than the portal runs. Loading those beside the host's
copies splits type identity — a `JsonSerializerOptions` or `IServiceProvider` from one copy is not the
other's — which is the clash the first SqlClient attempt also hit.

`PackageAssemblyProbe.Resolve`, which the NodeType's `NodeAssemblyLoadContext.Load` calls after the
platform (`MeshWeaver.*`) bind and before the module registry, decides for a name the packages supply:

1. **The host ships the same identity at an equal-or-newer version** (an assembly already loaded in
   the default context, else the trusted-platform-assembly list — which the host builds from its
   `.deps.json`, so it names every shipped dependency whether loaded yet or not; same culture and, for
   a strong-named reference, same public key token) → bind the host's copy, and log
   `Unified NuGet dependency {Assembly} {Requested} with the host's {Host}`.
2. **The host ships an older version** → load the package's copy in isolation and log a warning
   (`… is newer than the host's …: loading the package's copy in isolation`) — its types are distinct
   from the host's, which is worth knowing when something crosses the boundary.
3. **The host does not ship it** → load the package's runtime asset from the probing directories.

A satellite reference (`Pkg.resources, Culture=de`) is probed in the culture subfolder of each runtime
directory. Native libraries are tried candidate by candidate (`X`, `libX.so`, …) and, in a NodeType's
collectible context, loaded through the context's own `LoadUnmanagedDllFromPath`, so the handle is
released when the context unloads rather than pinned for the life of the process.

The interactive kernel's probe hooks `AssemblyLoadContext.Default.Resolving`, which the runtime raises
only for names the host cannot bind, so unification is implicit there; it skips native directories
for managed loads, tries the next candidate when one fails to load, and hooks
`ResolvingUnmanagedDll` for native ones. Each submission's probing directories are kept as one
closure, and a native import is answered only from the closure(s) whose managed directories hold the
requesting assembly — a later submission's different version of the same native library is never
offered to an earlier one's assembly.

**Limitation — the kernel loads into the default context.** That context is process-wide: one managed
path is one loaded assembly with one native binding, whoever asked first, and packages come from the
shared global cache. Two sessions that resolve the SAME managed package version but different versions
of its native dependency therefore share the first binding. Only a NodeType's own collectible context
isolates fully (its natives are loaded through, and released with, that context).

## What pins it

- `NuGetRuntimeAssetTest` (offline): RID chains; the SqlClient layout selects `runtimes/unix` on
  linux-x64 and `runtimes/win` on win-x64 while compiling against `lib/`; nearest RID and native
  selection; a package with no `runtimes/`; a host assembly sitting in a package directory binds to
  the host's own `Assembly`; a package-only assembly loads from its directory.
- `NuGetAssemblyResolverTest.Resolve_SqlClient_…` (live feed, skips when unreachable): resolves
  Microsoft.Data.SqlClient 6.1.1, compiles a snippet against the compile set, loads it into a context
  that binds like a NodeType's, constructs `SqlConnection`s with
  `Authentication=Active Directory Managed Identity` and `Active Directory Default` (no server contacted), asserts the loaded client
  is the `runtimes/` asset, and asserts nothing the host ships at an equal-or-newer version was loaded
  a second time.

## Related

- [NuGet Packages in Node Types](/Doc/DataMesh/NodeTypeWithNuGet) — the author's walkthrough.
- [NuGet Packages](/Doc/DataMesh/NugetPackages) — the same directive in interactive cells.
