---
Name: "NuGet Runtime Assets"
Abstract: "How a NodeType's #r \"nuget:...\" packages bind at run time: compile against lib/, load the runtimes/<rid>/lib group for the running RID, resolve runtimes/<rid>/native for DllImports, and bind any dependency the host already ships to the host's copy; the build records its packages so every load path, on any replica, re-resolves them. Motivated by Microsoft.Data.SqlClient, whose lib/ assembly is a platform stub."
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

Measured on client-a.meshweaver.cloud (linux-x64) on 2026-10-11: a NodeType referencing
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
closure, and a native import is answered only from the FIRST closure whose managed directories hold the
requesting assembly (the resolution that owns that loaded assembly) — a later submission's different version of the same native library is never
offered to an earlier one's assembly.

**Limitation — the kernel loads into the default context.** That context is process-wide: one managed
path is one loaded assembly with one native binding, whoever asked first, and packages come from the
shared global cache. Two sessions that resolve the SAME managed package version but different versions
of its native dependency therefore share the first binding. Only a NodeType's own collectible context
isolates fully (its natives are loaded through, and released with, that context).

## Compile references: the host wins

Host unification decides what LOADS; the compile has to agree with it. Measured on
an enterprise client portal (platform 3.1.10932) on 2026-10-15: a client NodeType declares
`#r "nuget: Microsoft.Data.SqlClient, 6.1.1"` and failed to compile with `CS0433` (`JsonElement` in
both System.Text.Json 10 and 9), `CS0121` (`GetRequiredService<T>` in both copies of
Microsoft.Extensions.DependencyInjection.Abstractions), `CS0121` on `Enumerable.MaxBy`, a cascade of
inference errors and hundreds of `CS1701`s. Three defects, three rules:

1. **One assembly per name — the copy that will run.** Every compile path — the bake
   (`NodeSetCompiler.Compile`), the runtime compile and its language-service inputs
   (`MeshNodeCompilationService`), the speculative compile, the script compile and the kernel — composes
   its references through `CompileReferences.WithPackageAssemblies`, which applies the load context's
   own binding rule (host unification, above) to every name the package closure and the host set
   (TPA or the container's `/app` + shared frameworks, plus modules) both hold: same identity with the
   host equal or newer → the host's reference, the package copy dropped; same identity with the
   package newer → the package's copy REPLACES the host's (the NodeType load context loads it in
   isolation — the kernel and the node-config script, which bind through the DEFAULT context and
   cannot load a second version, keep the host's: `packagesLoadInIsolation: false`);
   a different identity (another publisher's key) → both kept. Names the host lacks are added; a name
   appearing twice in the closure keeps its first path. Pure and order-preserving, so the bake and
   the runtime agree for the same host; the generator candidates and the stage-1 content key
   (`GeneratedInputDigestOf`) keep the FULL resolution. A speculative compile rebuilds the package half
   from the EFFECTIVE sources' directives onto the recorded host set
   (`CompilationInputs.HostReferences`), so a proposed body that changes a package version is checked
   against the version it pins.
2. **Generator discovery loads only assemblies that can hold a generator.** `SourceGeneratorLoader`
   used to `Assembly.LoadFrom` every `#r` assembly into the default context to look for `[Generator]`
   types — including SqlClient's `lib/` stub, which the NodeType context then unified with as if the
   host shipped it (rule 1 of host unification), so `new SqlConnection` threw
   `PlatformNotSupportedException` even with the `runtimes/` asset selected. It now reads the
   metadata first and loads only an assembly that references `Microsoft.CodeAnalysis` — plus that
   generator's own dependency closure among the candidates (a helper package its type derives from),
   dependencies first, skipping what the host ships, each from its package's RUNTIME asset
   (`runtimes/<rid>/lib/` where shipped) and recursing over that runtime image's references. The
   content key hashes those runtime assets too (`GeneratorPipeline.DigestedGeneratorPaths`), so two
   runtime helpers behind one compile stub never share a key.
   An installed MODULE counts as host-shipped too: the NodeType context binds a module that satisfies
   the requested version AHEAD of a package's copy of the same name, matching the compile's choice.
3. **A package's reference assembly is the compile surface where the toolchain carries one.** In-mesh
   C# compiles against the host's implementation assemblies, and System.Interactive's implementation
   keeps `EnumerableEx.MaxBy`/`MinBy` (namespace `System.Linq`) for binary compatibility while its
   reference assembly hides them from every net6+ compile — so the BCL's `Enumerable.MaxBy` was
   ambiguous in every NodeType, `#r` or not. `MeshWeaver.Compiler` embeds the `ref/` assembly of each
   `CompileSurfacePackage` (the build fails if one stops reaching its compile), and
   `CompileReferences.HostReference` compiles against it in place of the host file of the SAME identity
   (name, version, public key token), materialised as a content-addressed file holding exactly those
   bytes (so the emit-failure capture rereads the image Roslyn used); a host carrying another build
   keeps its own file.

## Every load path: the build carries its package closure

Compiling is one process; loading is every process. Measured on an enterprise client portal (platform
3.1.10992, which carries the host-wins compile fix above) on 2026-10-16: a NodeType declaring
`#r "nuget: Microsoft.Data.SqlClient, 6.1.1"` compiled Ok, and then on BOTH replicas, dozens of times,
`Failed to extract NodeTypeConfigurations from /data/assembly-cache/…/v33-….dll: Could not load file or
assembly 'Microsoft.Data.SqlClient, Version=6.0.0.0'` — at the start-up registration pass
(`DynamicContentTypeRegistration`), at `EnrichWithNodeType` and after a recompile attempt. The probing
directories were registered IN MEMORY by the emit alone (`RegisterProbingDirectories`), so every other
reader of the same bytes — a replica that never compiled the type, the compiling one after a restart,
a portal adopting a CI bake, a compile attempt that hit an existing build instead of emitting — loaded it
with nothing to probe, and `GetTypes()` failed.

The fix makes the closure part of the BUILD, not of the process that built it:

1. **Recorded in the bytes.** Both producers (the runtime compile's `PrepareGeneratedSource` and the
   bake's `NodeSetCompiler.ResolveInputs`) stamp one
   `[assembly: System.Reflection.AssemblyMetadata("MeshWeaver.NuGetPackage", "Id, Version")]` per
   `#r "nuget:"` into the generated skeleton (`NuGetBuildRecord.Stamp`). The bytes travel wherever a
   build travels — assembly store, blob, bundle — so the record does too. Package PATHS are deliberately
   not recorded: they name the compiling process's package folder, which another replica does not share.
   The stamp is part of the generated text, so the content key covers it: a build of the same sources
   from before the stamp has a different key and is REBUILT rather than adopted. A NodeType with no
   `#r "nuget:"` gets a byte-identical source and keeps its key and its builds.
2. **Re-resolved before every load.** `BuildPackageProbing` reads the record from the file's metadata
   (a header read, nothing loaded), resolves it through `INuGetAssemblyResolver` (memoized per process;
   a warm package folder answers without the network), and registers the probing directories for THAT
   path (`ICompilationCacheService.RegisterBuildProbingDirectories`). The build's closure REPLACES
   directories a context got from the node-wide registration (whatever this process last compiled,
   possibly another generation) and is never displaced by it. Reading a registration never CONSUMES
   it: a context evicted between the arming and the load (hub teardown, a superseding publish, a
   scan-pin retry) is re-created from it, and a `GetOrAdd` factory that loses its race cannot strip it
   from the winner. An entry lives as long as its build FILE. Build files are deleted mostly outside
   the process (the store's retention sweep runs on a shared volume, from any replica), so the bound
   is an AMORTIZED sweep on the file-system pool: once the registry has doubled since the last sweep,
   the entries whose file is gone are dropped — O(1) existence checks per new registration, none on a
   re-arm, never on the arming caller's thread. The sweep removes an entry only while it is still the
   registration it checked, so a republish of the same path re-armed mid-sweep survives. The metadata read runs on a blocking
   pool, never the caller's signalling thread; the resolution runs on the HTTP lane through the pool's
   cold `Invoke`, so its bound cancels the wait and frees the slot, and it never queues behind the
   Compile lane a kernel script holds while waiting for a NodeType to activate. Resolution is
   offline-first (see [Offline-first resolution](#offline-first-resolution) below), so a pinned build
   whose packages are on disk loads during a feed outage. It runs on every load of an already-built assembly: the post-compile scan in
   `CompileAndGetConfigurations`, the hydration shortcut `GetConfigurationsFromExistingAssembly` (the
   funnel for start-up registration, `EnrichWithNodeType`, the data-model and contract areas and adopted
   builds), and the cell surface. The load context then binds exactly as in the compiling process —
   platform first, host unification, the package's `runtimes/<rid>/lib` asset (never the `lib/`
   placeholder) — so the host-wins rule holds unchanged.
3. **A resolution that fails or does not answer within `AssemblyLoadTimeout`** is a WARNING naming the
   packages and the cause, and the load goes ahead and fails with the loader's own message — the verdict
   the record carries. A version-less `#r "nuget: Id"` re-resolves to whatever is newest on the reading
   process; pin a version when the build must load the same package everywhere.

## Offline-first resolution

`NuGetAssemblyResolver` asks the package folder first and a feed only for what the folder cannot
answer (#6863):

- **An exact version** (`#r "nuget: Id, 1.2.3"`, and every transitive dependency, which the walk takes
  at its range's minimum version) is looked up in the global-packages folder at its v3 path
  (`{id}/{version}`, both lower-cased). When the package is installed COMPLETE there, its own `.nuspec`
  answers: the identity must match, and the dependencies are the nuspec group nearest the target
  framework, which is what a feed's dependency info would have returned. No feed is contacted, so the
  same closure resolves online and offline.
- **Complete** means NuGet's own completion marker is present: `.nupkg.metadata`, or the older
  `{id}.{version}.nupkg.sha512`. Both are written after extraction. The `.nuspec` must also declare
  exactly that id and version. ONE check (`ReadCompleteInstall`) decides this for the dependency walk
  AND for the install step. A folder it refuses (half-extracted, empty, or holding another package)
  is never handed to asset selection: it is deleted and the package is installed again, from the
  persistent cache first and then the feed. A persistent-cache hydration is extracted into a STAGING
  folder beside the install path, checked there by the same rule, and published with one directory
  rename. A cache writes entries in archive order, so extracting in place could expose the nuspec and
  the marker before the assemblies. The install path therefore either does not exist or is complete.
- **Repair runs under one lock per package**, shared by every resolution in the process and every
  process on the same package folder. The lock is NuGet's own file lock
  (`ConcurrencyUtilities.ExecuteWithFileLockedAsync`, named by the install directory), the primitive
  its restore and extractor use. The check runs again INSIDE the lock, so a folder another
  resolution completed while this one waited is used, never deleted. A complete folder is never
  deleted, which is why the check before the lock may skip the lock.
- **A floating or ranged ROOT request** (`#r "nuget: Id"`, `#r "nuget: Id, [1.0,2.0)"`) still asks the
  feed which version to take. Answering it from the folder would make the version depend on what the
  reading process happens to hold, so the feed stays authoritative for the choice. Pin a version for a
  build that must load without a feed.
- **When the folder cannot answer**, every configured feed the source mapping allows is asked in
  turn. A failing feed does not stop the search: the next feed is asked, and the failure is logged
  against that feed when a later one answers. If no feed supplies the package and at least one
  failed, resolution fails with an `InvalidOperationException`. The error names the package and
  version, the package folder, and each failing feed with what it reported. A feed failure never
  surfaces when the folder could answer, because no feed is asked.

The pins: `NuGetOfflineFirstResolutionTest` (offline) lays out packages in a private package folder.
The first configured feed refuses every connection, and some cases add a local folder feed behind it.
- A two-package closure resolves with a fresh resolver and no feed call. Without the folder read,
  this case fails with the named error, because the feed is asked.
- Negative control: with the dependency removed from the folder, resolution fails with the named error.
- With the unreachable feed first, the local feed behind it still supplies the missing package.
- A half-extracted folder, and a folder whose nuspec and assembly belong to another package, are both
  reinstalled from the feed. The wrong assembly never reaches the compile set.
- Two resolvers share a dependency whose folder starts incomplete. One is parked between its check
  and the lock while the other installs the package and returns. The parked one then resumes and
  must use that install, never reinstall it.
- A cache hydration is paused after writing the nuspec and the marker but before the assembly. A
  second resolver reading in that window must not accept the half-written folder, and it ends with
  the finished package.
- A folder that passes the existence checks and is deleted by a concurrent repair before its
  manifest is read counts as a local miss, never a failure: resolution goes on to the feed and the
  install lock.
- Every interleaving wait in these tests is asserted. A missed window fails the test instead of
  passing without having exercised it.

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
- `NuGetPackageHostWinsTest` (live feed, skips when unreachable): the client NodeType's shape —
  `#r "nuget: Microsoft.Data.SqlClient, 6.1.1"` with `JsonElement`/`JsonObject`,
  `GetRequiredService<T>`, `MaxBy` and a `SqlConnection` — compiled through the bake AND the runtime
  compile service, then RUN: the emitted image references the host's System.Text.Json and
  DependencyInjection.Abstractions, SqlClient loads from `runtimes/`, and the default context never
  holds a package's compile asset. Its offline case compiles `Enumerable.MaxBy` against the host set.
- `NuGetPackagesLoadOnAFreshReplicaTest` (live feed, skips when unreachable): a SqlClient NodeType whose
  type DERIVES from a package type is built by the runtime compile and by the bake, copied to a path
  this process never loaded (the store copy another replica reads), and loaded through a FRESH
  compilation cache + compilation service — an empty probing registry and resolver memo, which is what a
  replica or a restart has. The configuration scan succeeds, `SqlConnection` is constructed and
  SqlClient is the `runtimes/` asset. Red before the fix with the production message.
  `NuGetBuildRecordTest` (offline): no package ⇒ byte-identical source; each reference round-trips
  through an emitted image.

## Related

- [NuGet Packages in Node Types](/Doc/DataMesh/NodeTypeWithNuGet) — the author's walkthrough.
- [NuGet Packages](/Doc/DataMesh/NugetPackages) — the same directive in interactive cells.
