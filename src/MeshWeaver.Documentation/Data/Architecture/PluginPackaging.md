---
nodeType: Markdown
name: Plugin Packaging
category: Architecture
description: Building a plugin's in-mesh C# outside the portal — what a compilation unit is, the three include syntaxes, the generated skeleton, and why the framework identity is an MVID rather than a version string.
icon: /static/NodeTypeIcons/box.svg
---

# Plugin Packaging

C# stored in mesh nodes compiles **at runtime, in the portal** — see
[NodeType Compilation](/Doc/Architecture/NodeTypeCompilation). This page is about compiling the same
source **outside** it: in CI, ahead of time, so the bytes can be shipped rather than recomputed.

`MeshWeaver.Plugin.Build` is the tool that does it. Everything below is what it has to get right to
produce an assembly the portal would accept — each item established by a build that failed in a way
that read as author error rather than harness error.

---

## A compilation unit is a `Source/` directory owned by a NodeType

Not a plugin, and not every `Source/` directory.

UWDeepfield has **eleven** units: its root plus one per NodeType. They compile *separately* at
runtime, which is why two of them may legitimately declare the same type name —
`TaskAssignmentService` exists in both `UwPortfolio/Source` and `UWDeepfieldHome/Source`. Merging a
plugin's units into one assembly produces ~200 spurious `CS0111`s.

Across the four plugin repos there are **774** `Source/` directories and only ~**221** units. Most of
the rest are **shared-source libraries** with no node at all — `Claims/SampleData/Source` is pulled
into its consumers via `shared=@Claims/SampleData/Source`. Building one standalone reports `CS0246`
for every type it legitimately borrows *from* its consumer: a false alarm on healthy content, which
is worse than no check. A unit's owner declares `NodeTypeDefinition` or `PluginContent`; a Markdown
page that happens to sit beside a `Source/` folder does not.

## Includes have three syntaxes

A node's `sources` array mixes them freely:

| Form | Example |
|---|---|
| path | `shared=@Store/Coupon/Source` |
| query | `shared=namespace:UWDeepfield/Source scope:subtree` |
| **aliased** query | `client=namespace:UWDeepfield/ReinsuranceClient/Source scope:subtree` |

The prefix is an author-chosen alias — `shared`, `news`, `client` — so it must not be part of the
match. Every unmatched form is a **silent under-resolution**: the include is skipped, the unit
compiles without it, and Roslyn reports a thoroughly convincing `CS0246`/`CS0103` on a symbol that
does exist. Matching only the path form cost UWDeepfield its 55-file root across six units; matching
only `shared=` cost `IndustryNewsFeed` its `client=` include.

`Test/` belongs to the same compilation: the live `Store/Plugin` node's `compiledSources` lists
`Store/Plugin/Test/*` beside its `Source/*`. Omit it and production code that references a test type
(an area rendering its own results) fails `CS0103` in CI while compiling perfectly in the portal.

## The generated skeleton is not optional

The portal compiles a NodeType as its sources **plus** a generated skeleton
(`AssembleCompilationInputs`): the assembly attribute, the `MeshNodeProviderAttribute` subclass with
its `Nodes` property, and the `ConfigureHub` method wrapping the NodeType's `configuration` lambda.

Two consequences:

- **Without it the assembly is inert.** It carries the user's types and registers nothing, so it
  cannot stand in for a runtime compile however cleanly it built.
- **The lambda is code no compiler has ever seen.** `content.configuration` is C# inside a JSON
  string: invisible to `dotnet build` (node trees are `<None>` content) and to any `*.cs` grep. On
  2026-08-09 the framework deleted `AddTracking()` while `SocialMedia/Post`, `Profile` and
  `PostsHub` each still called it from that field — CI green, then all three production portals hit
  `REFUSING READINESS` on the next framework bump.

The tool calls the framework's own `DynamicMeshNodeAttributeGenerator` rather than reproducing it. A
second implementation is free to drift, and a skeleton that differs from the runtime's is worse than
none: it compiles, packs, installs, and then behaves differently from everything that was tested.

## The ambient environment is not implicit

In-mesh code compiles against every assembly the portal has loaded, plus the skeleton's using
preamble. A `.csproj` supplies neither, so correct code fails in ways that look like author error:

- omit `MeshWeaver.Domain` and `[MeshNode(…)]` binds to the `MeshNode` **record** in
  `MeshWeaver.Mesh` rather than `MeshNodeAttribute` — `CS0616 'MeshNode' is not an attribute class`,
  which accounted for 47 failing units;
- reference only `MeshWeaver.Graph` and Store fails `CS0246` on `IPackageSource` and
  `PluginCatalogOptions`; adding the rest of the portal's package set takes it from 40 errors to 1.

Note this is **not** `MeshScriptEnvironment.Imports` — that set belongs to the kernel's *script*
environment, is narrower, and reintroduces the `CS0616` above.

---

## 🚨 The framework identity is an MVID, not a version string

`HasUsableBuild` skips a compile only when `CompiledFrameworkVersion` equals the live
`NodeTypeCompilationHelpers.FrameworkVersion` — and that value is the **Module Version Id of the
MeshWeaver.Graph assembly**, not a semver.

It is a *content* identity on purpose. Deriving it from `AssemblyInformationalVersion` forced
`Directory.Build.props` to stamp a fresh version into every build, which regenerated `AssemblyInfo`
every time and destroyed incremental compilation. The MVID decouples the two: the version string can
stay stable across local rebuilds while ABI invalidation stays correct. For a deployed build it is
fixed in the shipped DLL and changes only when framework content actually changes — so it recompiles
*less* than a per-build-version scheme, not more.

Three things follow.

**A package must record the MVID.** `3.0.0-rc2` is not something the runtime ever compares. Two
builds can share a version string and differ in content; the MVID is what says whether the bytes are
ABI-compatible with the running process.

**An installer that cannot establish the MVID must compile, never seed.** A wrong seed is worse than
no seed: the assembly-store key carries the live framework tag
(`v{version}-{FrameworkTag}-{hash}.dll`, `FrameworkTag = FrameworkVersion[..8]`), so seeding
foreign bytes under it suppresses the rebuild that was needed and the mismatch surfaces as a
`TypeLoadException` inside an ALC at activation — no overlay, no diagnostic, nothing to grep.

**Relaxing the check to a compatible semver range is the wrong trade.** It replaces a content fact
with a declared claim, and is only as good as whatever enforces the claim. The alternative needs no
weakening at all: build plugin packages against the framework build that ships in the image, and the
MVIDs match by construction.

## Why pre-filling the store works

`NodeTypeCompilationHelpers` already asks the assembly store before rebuilding a framework-stale
type, and skips the rebuild on a hit — the store's key carries the live framework tag, so a hit *is*
a usable build. Its own comment names the case this enables: "another replica, or **a dedicated bake
service that pre-fills the share ahead of a rollout**".

So a prebuilt assembly does not need a new load path. It needs to land in the store under the key the
runtime will look up, with an MVID that matches. The load side already exists
(`IAssemblyStore.TryGetAssemblyPath` → `GetConfigurationsFromExistingAssembly`).

---

## Package layout

```
MeshWeaver.Plugin.<Name>.<Version>.nupkg
├── MeshWeaver.Plugin.<Name>.nuspec
├── meshweaver/manifest.json          plugin, version, frameworkVersion, frameworkMvid, assemblies[]
├── meshweaver/assemblies/<Unit>.dll  one per compilation unit, embedded PDB
└── meshweaver/content/**             the plugin's node files, verbatim
```

**Assemblies are deliberately not under `lib/net10.0/`.** There, NuGet would surface every unit as a
compile-time reference of any consumer — colliding the duplicate type names that separate units
legitimately declare, and unifying CLR identity the runtime keeps apart. They are payload for the
assembly store, not a reference set.

The nuspec is a **projection of the mesh manifest authors already write**: `PluginContent` carries
`version`, `minMeshVersion`, and caret-ranged dependencies (`"requires": ["Store@^1.0.0"]`), which
become `<dependency id="MeshWeaver.Plugin.Store" version="[1.0.0,2.0.0)" />`. Nothing is invented.

One reserved id prefix (`MeshWeaver.Plugin.`) is what lets `packageSourceMapping` pin every plugin to
a private feed with a single rule — without it a typo'd id silently resolves against nuget.org.

**The assembly entry path is the node path verbatim**, not slash-replaced. Sanitising is not
injective — `A/B/C` and `A_B/C` both become `A_B_C`, and mesh paths do contain underscores — so two
NodeTypes would land on one archive entry and the second would silently adopt the first's bytes. Zip
entry names take slashes natively and nothing extracts to disk (consumers read entries into memory),
so there is no traversal concern to trade against it. `NuGetPackageWriter.EntryPathFor` states the
rule once. **A consumer still reads the node path from the manifest, never from a file name** — that
is the writer's guarantee to change, not the reader's to assume.

---

## Distribution: bundles served by the portal

A consumer needs three things to skip a compile: the bytes, the framework identity they were built
against, and which node each belongs to. One bundle carries exactly that, over two routes.

There is no package protocol involved. **Nothing in NodeType compilation restores** — the bake runs
in-process with Roslyn against `MetadataReference`s, and the only `restore` in the tree is
`MeshWeaver.NuGet` resolving `#r "nuget:…"` directives against the framework's own baked feed. A
service index and dependency ranges would be surfaces that can drift with no client to read them.

| Route | Serves |
|---|---|
| `GET /api/plugins/bundles/index.json` | This instance's framework MVID, and every plugin it can serve |
| `GET /api/plugins/bundles/{plugin}/{version}` | That plugin's assemblies + manifest |

Both are gated by the **instance key** (`mwi_`, as `Bearer` or `Basic`) resolving to the admin-owned
`PluginGrant` — the same gate as `/api/plugins`, deliberately, because that is already what purchases
are recorded against. They **fail closed**: no anonymous escape hatch, unlike the registry's dev-mode
one. These are compiled assemblies for paid modules.

**The portal serves the bytes rather than handing out storage access.** `BlobAssemblyStore` is
already the durable transport — one blob per `(nodeTypePath, version)`, hydrated into a process-local
cache on demand — so reading through `IAssemblyStore` means the bundle is assembled from the very
bytes this portal loads and runs. A scoped SAS handed to each consumer would be a second entitlement
path to keep honest, and revoking it is not the same operation as revoking the install's grant.

**Portal-served bundles carry assemblies only, no node content.** The consumer already installs
content through the registry (`PackageInstaller`), so shipping it again is weight on every fetch plus
a second copy that can disagree with the one the installer wrote. (A CI-produced package still
carries content — it is a full install artifact, not an increment.)

### The consuming side

`PluginBundleClient` (in `MeshWeaver.PluginCatalog`) reads the index once per client — a
`PromiseSlot`, so concurrent callers share one run and a fault evicts rather than replaying forever —
compares the framework MVID **once, before any download**, then fetches and seeds each assembly
through `PrebuiltAssemblySeeder`.

Two ordering rules, both of which fail silently when broken:

1. **Adopt AFTER the content install, never before.** The seeder re-keys each assembly under *this*
   instance's own node version, so the NodeType node must exist. Run it earlier and every seed
   declines — not corrupting, but a no-op indistinguishable from a registry serving nothing.
2. **Take the version from the INDEX, not from the install record.** `PackageManifest.ReleasedVersion`
   is written by the installer *after* the module's `manifest.lock` arrives, so at the moment an
   install would ask for a bundle it is routinely absent. Asking with it makes adoption a permanent
   silent no-op. The serving instance is authoritative about what it can serve anyway.

**Nothing here can fail an install.** Zero adopted is the normal outcome whenever the registry runs a
different framework build, and compiling is the always-available fallback — so a bundle that is
missing, refused, or unreadable is logged and stepped over.

The other producer/consumer pair of the same bundle format — CI baking the image's own shipped
content, adopted at boot from `prebuilt/` — is described in
[CI Content Bake](/Doc/Architecture/CiContentBake).
