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

The compile toolchain itself is **`MeshWeaver.Compiler`** (#1707) — the same code path whether the
portal compiles at runtime, CI gates and bakes, or you run it by hand — and it is distributed
three ways: in every portal image, as the **`MeshWeaver.Compiler.Cli` dotnet tool** (command
`mw-compiler`; version-matched with each platform release, published by the release lane and the
out-of-band `publish-compiler-tool` workflow), and as the `mw-plugin-test` container image.
`MeshWeaver.Plugin.Build` is the packaging tool on top. Everything below is what the pipeline has
to get right to produce an assembly the portal would accept — each item established by a build
that failed in a way that read as author error rather than harness error.

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

The tool calls the framework's own `DynamicMeshNodeAttributeGenerator` (in `MeshWeaver.Compiler`
since #1707 — the toolchain assembly whose dependency closure keys the framework identity) rather
than reproducing it. A second implementation is free to drift, and a skeleton that differs from
the runtime's is worse than none: it compiles, packs, installs, and then behaves differently from
everything that was tested.

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

## 🚨 The framework identity is a build fact, not a version string

`HasUsableBuild` skips a compile only when `CompiledFrameworkVersion` equals the live
`NodeTypeCompilationHelpers.FrameworkVersion` — never a semver. Since
[#1660](https://github.com/Systemorph/MeshWeaver/issues/1660) WS3 that value has three shapes, one
resolution (`FrameworkBuildIdentity`, in `MeshWeaver.Compiler`): hosts shipping a
`meshweaver-surface.manifest` — the portals and the CI bake host — resolve the **API-surface
identity** `s<hash>` (reference-assembly hashes over the canonical content-surface set; full impl
MVIDs for the TOOLCHAIN — `MeshWeaver.Compiler`, `MeshWeaver.NuGet`, and their computed MeshWeaver
dependency closure, since their code shapes every compile's generated input; stable across
internal-only merges, moved by breaking changes and toolchain changes); manifest-less CI processes
resolve the **commit identity** `g<sha>` (stamped as
`AssemblyMetadata("MeshWeaverFrameworkIdentity")`, also everyone's logged provenance); a
**manifest-less local build** resolves the **Module Version Id of the MeshWeaver.Compiler
assembly** — the toolchain anchor, single-file attributable.

None is derived from `AssemblyInformationalVersion`, on purpose. Deriving identity from the
version string once forced `Directory.Build.props` to stamp a fresh version into every build,
which regenerated `AssemblyInfo` every time and destroyed incremental compilation. The identity
decouples the two: the version string serves packages and image tags while ABI invalidation stays
correct — and the surface identity recompiles strictly *less* than every scheme before it (only
breaking changes move it), never more than is safe.

Three things follow.

**A package must record the identity.** `3.0.0-rc2` is not something the runtime ever compares.
Two builds can share a version string and differ in content; the identity is what says whether the
bytes are ABI-compatible with the running process. Producers read it with
`FrameworkIdentity.ReadIdentity` (metadata-only PE read: the stamp when present, the MVID
otherwise) — exactly what the consuming gate resolves in-process.

**An installer that cannot establish the identity must compile, never seed.** A wrong seed is
worse than no seed: the assembly-store key carries the live framework tag
(`v{version}-{FrameworkTag}-{hash}.dll`, `FrameworkTag = FrameworkVersion[..8]`), so seeding
foreign bytes under it suppresses the rebuild that was needed and the mismatch surfaces as a
`TypeLoadException` inside an ALC at activation — no overlay, no diagnostic, nothing to grep.

**Relaxing the check to a compatible semver range is the wrong trade.** It replaces a build fact
with a declared claim, and is only as good as whatever enforces the claim. The alternative needs no
weakening at all: bake against the same commit the image is built from, and the identities match
by construction.

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
├── meshweaver/manifest.json          plugin, version, frameworkVersion, frameworkMvid,
│                                     assemblies[] (each with sourceVersions provenance and its
│                                     per-type DEPENDENCY RECORD — #1707 slice 2)
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

### The module variant (#1664)

A package that delivers a **compiled module** (its root's `content.module` names the entry
assembly) carries the module's closure in the SAME bundle, under its own folder:

```
├── meshweaver/manifest.json          … + module: { assemblyName, assemblies[], minMeshVersion }
├── meshweaver/assemblies/<Unit>.dll  NodeType lane (assembly store, per-node ALC)
└── meshweaver/modules/<File>.dll     module lane (modules/<name>/ beside the app, default ALC)
```

The folders are deliberately separate — the two lanes have different destinations and different
failure modes, and a module DLL seeded as a NodeType assembly fails only at activation. One reader
(`BundleReader`) serves both: `Read` for the NodeType payloads, `ReadModule` for the module files —
both manifest-driven, and the module side is **all-or-nothing** (a NodeType with missing bytes
simply compiles; a module missing part of its closure loads and then faults at first use, so an
incomplete closure yields no files at all).

**The module gate is a `minMeshVersion` FLOOR, not the MVID.** The MVID-equality rule above is
*bake* semantics: a NodeType assembly is compiled in-process against exact framework references,
so only the identical build is known-good. A module is an ordinary assembly binding by **simple
name**; its contract is API compatibility, which the semver floor expresses. So the consumer lands
any bundle whose floor its platform satisfies — one bundle serves every compatible platform build
(nothing is rebundled per CI build), and a module can be installed **ex post** onto a platform
newer than the one it was built with.

🚨 The bundle's built-against `frameworkMvid` is **not** diagnostic and is **not optional** — it
stopped being either when the update decision started reading it (#3154) and #3211 made a bundle
that cannot state one unpublishable. It is still never a LANDING refusal: the one landing gate is
`ModulePlatformFloor.DeclineReason`, applied at the index, at the manifest, at placement and again
at boot. What it decides is whether there is anything new to land — see
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) → "A bundle states what it was
built against".

Producing a module bundle in CI — from any node repo. **One of `--graph-dll` (the identity anchor
assembly of the platform these bytes were compiled against — `MeshWeaver.Compiler.dll`, which on the
image path lives in the extracted `/app`, not beside the module) or `--framework-mvid` (that
identity, stated directly) is required; without one the packer exits 2 rather than writing a bundle
whose consumers can never tell a rebuild from a no-op:**

```
dotnet run --project src/MeshWeaver.Plugin.Build -- module-pack ./bin/Release/net10.0 \
    --module-name MeshWeaver.Social --plugin SocialMedia --package-version 1.2.0 \
    --min-mesh-version 3.0.0 --graph-dll /app/MeshWeaver.Compiler.dll \
    --out ./artifacts/bundles
```

The closure is an explicit statement: `<name>.dll` (+ `.pdb`), plus only the files named with
`--with` — mirroring the `modules/<Name>/` rule that for most modules the DLL alone is the
closure. The portal-served side assembles the module section from its own `modules/<name>/` tree
(the bytes it runs), and the consuming side lands it through `ModuleLandingService`
(restart-as-activation) — see [Modules](/Doc/Architecture/Modules) → "The bundle lane".

#### Which compiler produces the module — the container, not the runner's SDK

> *"we want to use memex build plugin and not dotnet build"* — maintainer, 2026-08-31

A module does not run against a source tree and it does not run against a feed: it is loaded into
the platform **image** and bound by the assemblies in there. So the honest compiler is that image's
own. `node-repo-module-pack.yml` takes it per matrix entry — `"build": "container"` compiles with
`memex build project` (i.e. `mw-plugin-test build-project` inside the pinned image: no .NET SDK, no
NuGet restore, no platform source checkout), and the default `sdk` keeps `dotnet build` +
`dotnet publish` on the runner. See
[In-Mesh Build and Test](/Doc/Architecture/InMeshBuildAndTest) for the builder itself.

**It is a per-module conversion, not a switch.** The builder's sweep over all 54 non-test projects in
`MeshWeaver.Plugins/src` compiled 12 green; the rest need what a *runtime* image does not carry — SDK
source generators, Razor/Blazor, `<Protobuf>`, additional libraries, portal hosts. A blanket swap
would break most bundles, so the split is declared, printed by the build step, recorded in the
receipt, and counted by the lane's `verify` job every run — a split nobody counts is a split that
becomes permanent.

🚨 **And there is no fallback between the two.** A lane that tries the container and quietly drops
back to the SDK makes *"the container built it"* and *"the SDK built it"* indistinguishable in a
green log. A declared mode that cannot run fails; an unknown mode fails.

**Why the container path needs no `--deps-closure`, and how that is proven rather than asserted.**
`--deps-closure` derives a module's private closure from the `deps.json` the SDK emits, and there is
no SDK on this path. What replaces it is a stronger fact: `build-project` **refuses by name** every
`PackageReference` the image does not supply, and the lane passes **no `--extra-refs`** — so a
container build that succeeded proves every assembly the module binds is one the image it lands into
already carries. The bundle inspection then asserts exactly that claim, refusing any non-`MeshWeaver.*`
assembly in a container-built bundle. The in-root `ProjectReference`s the builder compiled from source
are a separate question, answered the same way for both paths: **module-owned** ones ride (`--with`),
**image-shipped** ones (`src/platform-shipped.txt`) must not, and one script computes that set for the
packer, the lane and the inspection alike.

🚨 **Two preconditions gate any entry declaring `container`.** The pinned image must carry the
`build-project` verb at all; and the builder must stamp `<AssemblyVersion>`. It runs no MSBuild
targets, so the SDK's `GenerateAssemblyInfo` never happens and Roslyn's default identity is emitted —
shipping `0.0.0.0` into a `3.0.0.0` process is MeshWeaver#143 exactly: it does not fail at build, it
fails at runtime binding, in another repo, naming a version nobody wrote down. The lane compares the
emitted identity against the one the builder itself read out of the image and fails on drift, so it
cannot ship silently.

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
| `GET /api/plugins/bundles/index.json` | This instance's framework MVID, and every plugin it can serve **the calling instance** |
| `GET /api/plugins/bundles/{plugin}/{version}` | That plugin's assemblies + manifest, if the caller is granted it |

Both are gated by the **instance key** (`mwi_`, as `Bearer` or `Basic`) resolving to the admin-owned
`PluginGrant` — the same gate as `/api/plugins`, deliberately, because that is already what purchases
are recorded against. They **fail closed**: no anonymous escape hatch, unlike the registry's dev-mode
one. These are compiled assemblies for paid modules.

**That is TWO decisions, and for a long time only the first one ran (#1772).** The key
*authenticates* — a valid `mwi_` or 401 — and the grant *authorizes*, per package. Until #1772 the
authenticated caller was stashed on the request and never read back, so any registered instance could
download every installed package's bundle, paid courses included, while this very paragraph said
otherwise. An instance key is issued to every registered installation: it is identity, never
entitlement.

The authorization is `PluginGrantEntry` matched against a `(source, package)` pair, exactly as
`/api/plugins` scopes its listing and `InstallByDefault` scopes its selection. Consequences worth
knowing:

- **The index is scoped too.** An ungranted package is not listed, so a caller cannot learn it is
  installed here. That is what makes the download refusal non-informative.
- **A refusal is byte-identical to "no such bundle"** — same status, same empty body, same headers.
  Bundle URLs are fully predictable, so a distinguishable refusal is an inventory oracle over the
  whole catalogue; `/api/content` closed the same hole in #587. WHICH of the refusals it was goes to
  the **log**, never the response.

### The entitlement anchor is the REGISTRY (#1782 gap 2)

The `source` half of that pair used to come from the **install record** and from nowhere else. Two
things followed, and both were wrong:

1. a package this instance had not itself installed had **no binding at all**, so it could not be
   served however plainly its content sat here — which is the permanent state of a registry that
   provisions its packages as Spaces (memex-cloud never runs the catalog install, so it has no
   install records);
2. "I cannot tell which source this is from" was answered as **"you are not entitled to it"** — a
   check whose inability to answer is indistinguishable from a negative answer, applied to the most
   expensive thing it could be applied to: a purchase, read as no purchase.

So the anchor is the registry's own catalog — the same `PackageSources` listing `/api/plugins`
serves — and the install record is what it always was, a **cache** of that binding:

```
anchor:   the entitlement record at the registry
local:    install record = cache
absent:   "ask upstream" — never "not entitled"
```

`PackageEntitlementAnchor.Resolve` is the whole rule, pure, with **three** outcomes:

| the binding comes from | outcome |
|---|---|
| the **registry** carries the package | `Granted` / `Denied`, `Anchor = Registry` — authoritative, and it overrides a disagreeing cache |
| only a **local observation** does (an install record's stamped source, a published module's declared path) | `Granted` / `Denied`, `Anchor = Cache` |
| **nothing** binds it, and the registry answered in full | `Denied` — its silence about a package IS an observation |
| **nothing** binds it, and the registry could not be asked | 🚨 `Indeterminate` — **UNKNOWN, not a denial** |

The third state withholds the bytes like a denial does; it differs in what it **claims**. An
instance being unable to ask is not a customer failing to buy, and the difference is recorded
(`PackageEntitlementLedger`) and surfaced (`entitlement_anchor` health check, **Degraded** — never
Unhealthy, because serving a previously observed entitlement while the registry is down is the
*correct* answer, and failing readiness over it would turn a brief registry outage into one of
ours).

**#1777 is untouched.** The grant match is the same `AuthenticatedInstance.Allows`, no source is
ever invented, and the route still has exactly two wire outcomes — the bytes, or the one
byte-identical `NoSuchBundle()`. If anything it tightens: a published module's *self-declared*
package path used to be believed outright and is now overridden by the registry's binding whenever
the registry carries the package.

- **Air-gapped is a stated answer, not a silent one.** An instance with no configured sources is
  `Unconfigured` — an authority on nothing, deliberately not "the registry carries no such package".
  Its cached bindings still serve; anything it has never observed is `Indeterminate` and says so.
- **An unstamped `Source` no longer fails dark.** A record written before the field existed simply
  carries no cached binding, so the anchor answers for it. Stamping it still matters
  (`PackageInstaller.SeedSource` carries it forward across re-installs) because it is what keeps the
  answer working when the registry is *not* reachable.
- **A snapshot window is not an entitlement term.** The anchor reuses an authoritative listing for
  60s (`PluginCatalog:AnchorFreshnessSeconds`); its expiry triggers a *read*, never a refusal.
  Entitlements remain eternal.

**The portal serves the bytes rather than handing out storage access.** `BlobAssemblyStore` is
already the durable transport — one blob per `(nodeTypePath, version)`, hydrated into a process-local
cache on demand — so reading through `IAssemblyStore` means the bundle is assembled from the very
bytes this portal loads and runs. A scoped SAS handed to each consumer would be a second entitlement
path to keep honest, and revoking it is not the same operation as revoking the install's grant.

**Portal-served bundles carry assemblies only, no node content.** The consumer already installs
content through the registry (`PackageInstaller`), so shipping it again is weight on every fetch plus
a second copy that can disagree with the one the installer wrote. (A CI-produced package still
carries content — it is a full install artifact, not an increment.)

### The `Release` node is the link (#1751)

Three concerns, three homes — and the third is the one this section is about.

| concern | home |
|---|---|
| **Compilation** — DLL + PDB keyed by node path, with the framework identity and per-type dependency records | the **assembly / bundle** |
| **Node definitions** — what exists, its type, its content | the **mesh**, synced from its repo, unchanged |
| **The link** — which release, which identity, where its assemblies live, per architecture | a **`Release` node** |

A `Release` MeshNode already exists per NodeType at `{nodeTypePath}/Release/{version}` and is minted
by the compile watcher. It now also carries `NodeTypeRelease.Artifacts` — a list of
`ReleaseArtifact` records, each stating **one** `(frameworkIdentity, architecture)` lane and where
that lane's bytes are (`assemblyStoreVersion`, `collection`/`contentPath`, and optionally a routable
`url`). Resolution is "read the release, follow its link", not "ask an index what this instance
happens to have installed".

`ReleaseArtifactResolver.Resolve(releases, identity, architecture)` is the one rule, stated as a pure
function so a producer and a consumer cannot reach different verdicts:

- the identity must match **exactly** — the same ordinal comparison
  `PrebuiltAssemblySeeder.DeclineReason` makes, for the same reason;
- the architecture must match too, case-insensitively, with the single widening that a **producer**
  may declare `any`. A consumer never widens itself: "I do not know" is not "it does not matter";
- later releases win, so a re-bake supersedes its predecessor without anything deleting the old
  record (old releases stay on purpose — a live ALC may still hold the previous DLL);
- there is **no nearest match**. Declining costs one compile; adopting unproven bytes costs a
  `TypeLoadException` inside a collectible ALC at activation, with no overlay and nothing to grep.

**Why the architecture is recorded even though the identity already folds it in.** The four
reference assemblies differ between the amd64 and arm64 variants of one image, so a multi-arch image
resolves **two** framework identities — CD's `publish-bake` job says so and pins the bake to
`--platform linux/amd64`. That makes the identity a sufficient *proof* but an opaque *label*: given
`s1a2b3c…` nobody can tell which lane produced it, so an arm64 install that resolves the other
identity finds nothing and is told only "not adoptable". Recording the architecture beside the
identity turns that silent nothing into a sentence — *this release has `linux-x64` under `s1a2…`;
you are `linux-arm64` under `s9f8…`* — which is the difference between "adoption regressed" and "no
bake exists for my architecture".

🚨 **Several artifacts on one release is how two lanes are published honestly — never how one lane's
bytes are re-labelled.** Each record names the identity *its own* bytes were compiled against.
Publishing one bake under a second identity to "cover" the other architecture is forbidden by the CD
lane and would void the only compatibility proof there is.

The download route takes the consumer's lane:

```
GET /api/plugins/bundles/{plugin}/{version}?identity=<framework-identity>&arch=<portable-rid>
```

Both default to the serving instance's own lane. Two branches, and the order is load-bearing:

1. **The caller is on this instance's own lane** → serve `LastCompiledVersion`, exactly as before the
   link existed. Not a legacy fallback but the *correct* answer: on its own lane the identity claim is
   true by construction, and `LastCompiledVersion` is the CURRENT build. A `Release` record can
   legitimately lag it — `PrebuiltAssemblySeeder` stamps `LastCompiledVersion` on adopt without
   minting a release at all — so resolving this case through releases would quietly ship an older
   assembly than the portal itself runs, while looking perfectly healthy.
2. **The caller is on another lane** → resolve through *that type's* `Release` node. Only a record
   written beside the bytes can prove them for a lane this process is not in, and `LastCompiledVersion`
   is explicitly *not* a fallback here: it is a different lane's build, and handing it over under the
   requested identity is precisely the unprovable adoption the gate exists to prevent.

The served manifest records the resolved `frameworkMvid` and `architecture`, so its identity claim is
always backed by a record the producer wrote — never inferred. A type with no artifact for a foreign
lane contributes nothing and is counted as a **miss**: the manifest carries a `misses[]` array, and
both ends log it. That matters more than it looks. A bundle that quietly arrives with fewer assemblies
than the package has types is indistinguishable from a complete one, and the adopted-vs-compiled count
is the only evidence the whole distribution lane works — a miss that nobody can see is a miss nobody
can count.

### The consuming side

`PluginBundleClient` (in `MeshWeaver.PluginCatalog`) reads the index once per client — a
`PromiseSlot`, so concurrent callers share one run and a fault evicts rather than replaying forever —
compares the framework MVID **once, before any download**, then fetches and seeds each assembly
through `PrebuiltAssemblySeeder` — which additionally validates each assembly's per-type
DEPENDENCY RECORD against this environment (a build binding a module this deployment does not run
declines) and stamps the record on adopt, so an adopted build is judged by the ongoing validity
checks exactly like a locally compiled one (#1707 slice 2).

Consumption is no longer boot-only (#1707 slice 3): every package INSTALL and every git-sync PUSH
runs its written/affected types through the bundle sources first
(`IPrebuiltAssemblyConsumer.SeedForTypes`), and the release-request watcher satisfies a request
that arrives on an already-valid current build — see
[NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) → "Adopt-before-compile".

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
