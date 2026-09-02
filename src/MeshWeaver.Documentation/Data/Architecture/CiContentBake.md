---
nodeType: Markdown
name: CI Content Bake
category: Architecture
description: The CI compile stage bakes the image's shipped content — where the MVID-keyed bundle artifacts come from, how the image ships them, and what boot does with them.
icon: /static/NodeTypeIcons/box.svg
---

# CI Content Bake

Every `.cs` stored in a mesh node compiles **at runtime in the portal** (see
[NodeType Compilation](/Doc/Architecture/NodeTypeCompilation)), and until issue #1660 that was also
the *deploy* path: every image roll changed the framework identity, invalidated every cached
NodeType assembly, and the portal re-compiled the world in-process while users lived inside the
warm-up window. The accepted direction is **load-only runtime**: CI compiles the shipped content
once, and boot *loads* the result.

This page describes workstream 1, step 1 — the pieces that exist and how they fit.

---

## The producer: the content gates persist what they compile

CI already compiles the image's shipped content as a *verdict*: the `doc-gate` job runs
`mw-plugin-test` over `src/MeshWeaver.Documentation/Data` (staged as the `Doc` package) and over the
`samples/Graph/Data` trees. That is the platform's OWN content, and it is the only content the
platform bakes.

🚨 **The platform never syncs or bakes plugin source.** Until 2026-08-27 core CD checked out
`MeshWeaver.Plugins`, Roslyn-compiled every module, and published the result as the `plugins`
source — the same shelf Plugins' own `publish-bake` writes. Two producers, one shelf, and a full
compile of source the platform does not own on every platform push. Removed: Plugins bakes
Plugins and publishes it; the platform's portals *install* it. See
[The Plugin Build Contract](/Doc/Architecture/PluginBuildContract) → "Three rules that follow".

Each of those lanes runs the bake and the gate as **two steps** (`.github/scripts/bake-then-gate.sh`
— the same split `main-cd` makes, described below): `compile <stage> --output <dir>` produces the
bytes, then the gate runs over the same stage with `--seed <dir>` and consumes them. The bake
persists:

- one **prebuilt-assembly bundle** per package — `<package>.zip`, written by `BundleWriter`
  (`MeshWeaver.Plugin.Packaging`): the `meshweaver/manifest.json` manifest (node path → assembly,
  framework identity, source-version provenance, and — #1707 slice 2 — each assembly's per-type
  **dependency record**, which the consumer validates against ITS environment before adopting and
  stamps on adopt) plus each compiled assembly and its symbols;
- `framework-mvid.txt` — the framework identity every bundle in the directory is keyed to.

Only types that reached `CompilationStatus.Ok` contribute. A type the gate's known-debt allowlist
tolerates simply has no entry — the consumer compiles it as it would have anyway. A type that
*claims* Ok while the run's assembly store has no bytes for it faults the run: an artifact stage
that ships less than the verdict claims would be the skip-trapdoor shape CI forbids.

Both gates fail RED when a green run produced no bake identity, produced no bundles, or **adopted
none of them** — each is a postcondition of the verdict, never an optional extra. That last one
matters more than it looks: adoption is invisible in a gate verdict by construction (a type the gate
compiled itself renders and tests exactly like one it adopted), so without an explicit assertion the
entire consuming half could stop working with every run still green. `assert-bake-consumption.sh`
reads the gate's own `seed: adopted N of M` line and requires **M > 0** as well as N ≥ M — because
`BakeSeed.Shortfall()` returns a PASS over an *empty* bake ("adopted everything declared" is
vacuously true of nothing), which is exactly the vacuous green the split exists to make impossible.
N may legitimately EXCEED M: `N` counts adoption events and the gate installs every package twice
(the idempotence pin re-installs the unchanged snapshot), so `adopted 32 of 28` is healthy.

🚨 **What the gates' bake is FOR: proving the bake stage still works, on the PR that breaks it.** It
is *not* the delivery lane. A gate job's bundles are keyed to that job's own binaries, and no
shipped image ever contains those (see "The identity rule" below), so `doc-gate` uploads nothing.
What the portals adopt is baked **inside the shipped image** for the platform's content, and
**published by each plugin repo's own `publish-bake`** for plugins — see "The delivery" below.

## BAKE is a build step; GATE is a mesh run that CONSUMES one

This is how the bake worked until issue #1763: `mw-plugin-test` stood up an in-process mesh
(`new MeshBuilder(...).AddGraph()`), imported the repo's content, let the **mesh** compile every
NodeType, and `--bake-output` collected what the mesh had produced. That is "compile through mesh
nodes" — the thing #1707 forbids — and it is where the minutes went: mesh startup, the hub
scheduler, and one per-type activation for every type in the tree.

#1763 split CD. **#2064 split the PR lane too** — `dotnet-test.yml`'s `doc-gate` and `plugin-gate`
were still fused, so the platform's own PR gate was doing the very thing CD had stopped doing, and
`--bake-output` no longer appears in that workflow at all. The remaining fused caller is
`node-repo-publish-bake.yml` (the satellite lane).

The two concerns are now split, and they are different kinds of thing:

| | what it is | how it runs |
|---|---|---|
| **BAKE** — produce assemblies | a **build step** | `mw-compiler compile <root> --output <dir>`: resolve NodeType sources from the git tree, compile with `MeshWeaver.Compiler`, emit **DLL + PDB**, write the bundle. No `MeshBuilder`, no `AddGraph()`, no import, no hub. |
| **GATE** — prove it works | a **runtime** check | `mw-plugin-test <root> --seed <dir>`: stand up a mesh, **adopt the bake's assemblies**, render each type's default area, execute its `Tests` area. Rendering and running tests are genuine runtime behaviours; producing an assembly is not. |

**The emergency path is untouched.** A live instance with no usable artifact still compiles its own
— #1707 requires it, because there will always be code that never went through CI. That is
*recovery*, not a build lane.

### The gate CONSUMES the bake — `--seed <dir>`

`--seed` points the gate at a bake directory. The wiring is one registration and no new pipeline:
the gate registers an `IPrebuiltAssemblyConsumer` over that directory, and `PackageInstaller`'s
existing adopt-before-compile step (#1707 slice 3) asks it for every NodeType it installs. It
delegates to `ShippedPrebuiltBundles.SeedForTypes` — **the same consumption implementation a portal
runs** — so the framework gate, the per-type dependency-record gate and the already-current skip
that decide the verdict are the ones that ship. A gate with no `--seed` resolves nothing and
compiles exactly as before.

What that buys is not speed, it is *what the gate is judging*: with a seed, the assembly that
renders and runs the `Tests` areas **is the assembly that will ship**. Without one, the gate proves
that a private recompile of the same sources worked and publishes different bytes.

Two refusals guard it, because both failures are otherwise invisible:

- **The address check, before the mesh boots.** A bake keyed to a framework identity this process
  does not resolve would be declined assembly by assembly, and the gate would compile the whole
  tree itself and exit GREEN having judged none of the bytes that ship. `--seed` refuses such a
  directory as a usage error naming both identities. Same for a directory with no bundles, no
  `framework-mvid.txt`, or bundles from mixed producers.
- **The consumption postcondition, after the run.** The gate is RED if the bake declared
  assemblies for types it installed and adopted fewer. Adoption leaves no trace in a gate verdict —
  an adopted type renders and tests exactly like a compiled one — so "the consuming half silently
  stopped working" cannot be noticed unless it is a verdict.

### 🚨 The seal carries the modules it composed — a gate composes from the seal, never the registry

A bake composes external modules with `--module` (this run's module-pack artifacts, or an
upstream's) and seals its NodeType assemblies against **those bytes**: every dependency record
names the module's MVID. The registry's *package* endpoint (`/api/plugins/bundles/<pkg>/<version>`)
serves something else — the module's own lane's last build, under a content version that does not
move when a rebuild changes the bytes. A gate that seeded publication X but composed its modules from
the registry therefore ran assemblies built against one `MeshWeaver.AI` while holding another, and
the boot seeder rightly declined every one: `dependency record mismatch — built against mvid:…, live
is mvid:…`. On 2026-08-29 that was every satellite, red at once, with no diff in any of them (#2698).

**The publication is the unit of consistency.** Since #2707:

- `publish-bake-bundles.sh` seals the composed bundles WITH the publication —
  `prebuilt-bundles/<identity>/<source>/modules/<pkg>.module.nupkg`, listed in `modules/_index`,
  written strictly before `_complete`. A bake that composed nothing seals an **empty** index, so a
  reader can tell "composed nothing" from "predates module sealing"; a sealed publication with no
  index is republished on the source's next bake even when its content is unchanged — that is what
  converges the fleet without anyone re-baking by hand.
- The registry serves the set: `GET …/prebuilt/<identity>/<source>/modules` (the index's list, 404
  *saying* "predates module sealing" for an old seal) and `…/modules/<bundle>` (listed names only).
- `compose-sealed-modules.sh` — called by `node-repo-gate.yml`, `node-repo-compile-check.yml` and
  `node-repo-publish-bake.yml` whenever the repo declares an upstream (`upstream-seed` /
  `upstream-sources`) — takes each `registry-modules` package from the first upstream whose seal
  lists it. **No fallback**: an upstream with no seal for the identity, a seal without a module set,
  or a package no upstream sealed is RED naming the identity. Falling back to the registry would
  reproduce the decline under a green tick. A repo that declares **no** upstream still composes from
  the registry, and the log says so in a `::notice::` — those bytes are the module lane's, and a
  decline against a publication that composed different ones is the reason to declare the upstream.

The registry's package endpoint remains the **runtime** surface — a portal installing `AI@1.2`
gets whatever the module lane published — which is why the platform's own wave seals its module
bundles without publishing them as packages.

### 🚨 An adoption used to lose a race with the first-build kickoff

Adopting a prebuilt assembly writes the NodeType's node, and that write goes through the type's OWN
hub — so `PrebuiltAssemblySeeder.Seed` **activates the hub it is about to stamp**. Activation is
exactly what arms the first-build kickoff (`CompilationStatus is null` + no usable build ⇒ flip
`Pending`), so the seeder's own probe started the Roslyn compile the adoption exists to avoid:

```
54.709  MeshNodeStreamCache: opening shared stream for Widget/Thing   <- the seeder
54.728  First-build kickoff: no usable build - flipping CompilationStatus=Pending
54.7xx  Prebuilt assembly ADOPTED for Widget/Thing ... no compile needed
54.7xx  [ReleaseRequestWatcher] ... satisfied by the existing current build - no compile dispatched
54.8xx  Compiling assembly for Widget_Thing (disk, 0 NuGet refs)      <- overwrites the adoption
```

Every signal said the adoption had worked, because it had. The release request was correctly
*satisfied*; the kickoff simply never asked. So install-time consumption saved nothing anywhere —
on a portal as much as in a gate — and the type was re-stamped over the adopted build milliseconds
later.

`NodeTypeAdoptionRegistry` is the interlock: the seeder reserves the path **before** it opens the
stream, i.e. before the activation that arms the kickoff, and the kickoff waits for the reservation
to clear and then re-evaluates. It **delays, never cancels** — a declined adoption still compiles,
so there is no skip-trapdoor — and the wait is bounded, so a leaked reservation costs a delay rather
than an unbuilt type.

### Source resolution without a mesh

At runtime the mesh performs source discovery: `NodeSources.GetSources` expands the NodeType's
`Sources`/`Tests` queries and asks `workspace.GetQuery`, which reaches the storage adapters. A build
step has none of that, so `MeshWeaver.Compiler` gained a second implementation — `NodeSet` /
`NodeSetQuery` / `NodeSetCompiler` — that answers the same queries against an in-memory node set the
caller assembled from the tree.

That code lives **inside the toolchain assembly on purpose**: which Code nodes a compile consumes is
part of the *generated input* of that compile, exactly like the skeleton generator and the join
order, so it has to sit inside the full-MVID identity boundary. A resolver outside it could change
what a bake consumes without moving the framework identity, and every portal would adopt the changed
bytes as if nothing had happened.

Two rules keep the second implementation honest:

- **Query EXPANSION is not re-implemented.** `CodeQueryResolver.ExpandAll` is the same call the
  runtime makes, so `$self`, the `name=` prefix, the `@`/`@@` shorthand, the bare-namespace rebase
  and the implicit `nodeType:Code` filter cannot fork. The same is true of the `@@`-include walk,
  the dedup/executable filter, the join order, the skeleton and the emit — the tree baker is an
  *orchestrator* of the runtime's own shaping, not a parallel copy of it.
- **Query EVALUATION refuses what it does not understand.** Only `path:`, `namespace:`, `scope:` and
  `nodeType:` are supported — everything `CodeQueryResolver` can emit. Free text (which routes to
  vector search on a real mesh), wildcards, alternations and any other selector make the resolution
  **unestablished**, and the bake then refuses to compile rather than matching less. This is the same
  fail-loud direction `SourceSnapshot` takes at runtime and for the same reason: a source set that is
  short compiles into completely genuine-looking `CS0246`/`CS0103` diagnostics about code that is
  fine.

### The equivalence pin

🚨 **Getting this wrong is silent.** A baker that resolves sources even slightly differently emits
assemblies that are subtly not what the mesh would have built. The bundle is well-formed, the
framework identity matches, every consumer adopts it, and the defect first appears as a page
rendering empty in production — no exception, no log line.

So the equivalence is a **test**, not an argument. `BakeEquivalenceTest`
(`test/MeshWeaver.PluginTester.Test`) bakes one content set BOTH ways and asserts the producers
agree on: the framework identity, the bundle and node-path sets, the resolved source set per type,
the per-type dependency records, and the emitted assemblies' type-and-member surface — over a
fixture that exercises the default `Source/`+`Test/` subtree queries including a nested folder, a
cross-package `shared=@…` query, an `@@` include of a node no query matches, an executable code cell
that must be excluded, and a `// NodeType: Scope` source the `nodeType:Code` filter must exclude.

**Bytes are deliberately not compared** — for a reason that is a property of the platform, not of
this test. See the next section.

## 🚨 A stated property: the mesh-driven bake is NOT byte-reproducible, even against itself

This is not a quirk of the comparison above; it is true of **every bake this platform has ever
produced**, and anyone reasoning about bake artifacts needs it up front.

A NodeType's compile input is the concatenation of its source Code nodes. The mesh discovers them by
folding each query's results into an `ImmutableDictionary<string, MeshNode>` and emitting
`dict.Values` — which is **hash-bucket order over string hashes that .NET randomises per process**.
`NodeCompileShaping.CombineSources` then joins the files in exactly that order. So two runs of the
same mesh-driven bake, over the same commit, on the same machine, concatenate the same sources in
**different orders** and emit **different bytes**. (The generated skeleton independently stamps
`// Generated at: {UtcNow}`, and the emit embeds a temp `pdbFilePath`, so even a fixed order would
not give byte equality.)

Three consequences worth carrying:

- **This is why the framework identity is SURFACE-based, not byte-based.** Hashing emitted bytes
  could never have worked as an identity: it would change on every run without anything changing.
  `FrameworkBuildIdentity` hashes *reference assemblies* — the compiler's own definition of an API
  surface — precisely because that is stable where bytes are not.
- **Any "did these two bakes produce the same thing?" check must compare SURFACE, never hashes.**
  Comparing digests of bake outputs will report differences that do not exist, on every run.
  `BakeEquivalenceTest` compares node paths, resolved source sets, dependency records and the
  emitted assemblies' type-and-member surface; that is the shape such a check has to take.
- **The compiler-driven bake is deterministic** (query order, then ordinal by path). That is strictly
  better and costs nothing, but it does not make the two producers byte-equal — the old lane is the
  non-deterministic one. What the surface comparison proves is that the concatenation order of
  independent top-level declarations does not affect what is emitted, which is why the old lane's
  non-determinism was survivable.

## 🚨 An in-mesh build is the ABSOLUTE FALLBACK — if a pod is sweeping, the bundles are missing

A portal should never compile content at boot. It should ADOPT files CI already produced. When you
see a pod stuck on

```
Health check nodetype_bake: 'NodeType bake in progress — enumerating dynamic NodeTypes'
```

that is not the system working slowly. It is the fallback, and it means **no bundle matched this
image's framework identity**. Treat it as a missing artifact, never as "boots are slow here".

### Where the files actually come from (measured on memex, 2026-08-22)

There are two adoption sources, and only one of them is real today:

| Source | State |
|---|---|
| `prebuilt/` **inside the image** (`ShippedPrebuiltBundles`) | **EMPTY** — `ls /app/prebuilt` returned 0 files on the running portal |
| the published store on the shared volume, `/data/prebuilt-bundles/<identity>/` | **101 identities present** |

So the store is the only lane that feeds adoption. A pod adopts iff its own identity is one of those
directories — and when it is not, it sweeps.

### Why the identity can be missing even though bakes are green

The bake follows releases and the pin governs gates — that separation is right and has been in place
since 2026-08-18. What it does not fix on a busy trunk:

🚨 **`MW_TEST_IMAGE` is `mw-plugin-test:latest`, a MOVING tag.** The bake resolves it at run time;
the instance later rolls to the newest *portal* tag. On a trunk that builds every few minutes those
are different commits, so the bake seals identity A while the instance wants identity B. Three bakes
in one morning published `s429a849…`, `s14290dce…` and `se78f65ed…` while memex held for
`se3bf749…` — every job green throughout.

The half that makes this converge is the CONSUMER: an instance must roll to the newest release that
is actually baked, not the newest release that exists. Newest-only selection can never win the race,
because the newest tag is always the one least likely to be baked yet.

### Checking it, in order

1. `ls /app/prebuilt` on the pod — if empty, the image lane contributes nothing (it does not today).
2. `ls /data/prebuilt-bundles | wc -l` — the store; then whether THIS image's identity is among them.
3. The bake job's `bake published: identity=…` versus the instance's `heldReason` identity. Different
   values are the whole bug.

### Byte-equality IS reachable — through adoption, not through recompilation

Two independent compiles of the same content still cannot be byte-equal, and that is not only the
old lane's fault: `CSharpCompilationOptions` here is not `WithDeterministic(true)`, so Roslyn mints
a fresh MVID per emit, and `DynamicMeshNodeAttributeGenerator` stamps `// Generated at: {UtcNow}`
into the generated skeleton — both are *normalised out of the content key* (`GeneratedInputIdentity`)
precisely because neither can be removed from the bytes. So "compare the digests" remains the wrong
check between producers, and `BakeEquivalenceTest`'s surface comparison remains the right one.

What the split makes byte-equal is something else, and it is the property the gate needs: **the
bytes the gate judges are the bytes the bake produced**, because they are the *same bytes*, adopted
rather than rebuilt. Measured on the Doc tree, comparing the bake's bundle with the bundle a seeded
gate re-emits from its own store:

```
bake/Widget.zip     c97474b87fb87ae4921096ba77c6a3124cddb0d1d2eb72d8d7794bc47a384517
gateout/Widget.zip  c97474b87fb87ae4921096ba77c6a3124cddb0d1d2eb72d8d7794bc47a384517
```

`BakeGateSplitTest` asserts exactly that, with the unseeded gate as its control in the same test:
its bytes must DIFFER, because a compile is not reproducible — which is what makes byte-identity
evidence of adoption rather than of coincidence. If compilation is ever made deterministic, that
control fails loudly instead of the assertion quietly proving nothing.

## The identity rule: adoptable when the SURFACE is unchanged

Adoption is gated by `PrebuiltAssemblySeeder.DeclineReason` on the **framework build identity**
(`NodeTypeCompilationHelpers.FrameworkVersion` / `FrameworkBuildIdentity` — #1660 WS3). For the
hosts that matter here — the bake host and the portals, which both ship a
`meshweaver-surface.manifest` — that identity is the **API-surface hash** `s<hash>`: per compile
reference, the SHA-256 of its *reference assembly* (the compiler's own definition of the API
surface — byte-stable under body-only and private-member edits, changed by any surface change),
hashed over the canonical content-surface set, with the generated-input-shaping exceptions
contributing their full implementation MVID: the toolchain roots `MeshWeaver.Compiler` (THE
compile toolchain since #1707 — skeleton generation, source-query resolution, `@@`-include
shaping, aggregation, options, generator execution, emit; namespace `MeshWeaver.Compiler`) and
`MeshWeaver.NuGet` (the `#r "nuget:"` parser/resolver — what Roslyn is fed and which assemblies a
directive adds), **plus their computed MeshWeaver dependency closure** (Mesh.Contract,
ContentCollections, transitives — the toolchain CALLS into what it links, so a body-only change
in a closure member can change what it emits; the set is derived from the shipped assemblies'
AssemblyRef metadata, so every host computes the identical set and a new toolchain dependency can
never be silently outside the identity). Before #1707 the toolchain lived inside
`MeshWeaver.Graph` and pinned ALL of Graph — the highest-churn assembly — so nearly every merge
rebaked the world; the extraction is what makes "rebuild only when we need to" hold in practice.

Three consequences:

- **a bundle is adoptable across images and internal-only merges** — the bake taken from image X
  seeds at boot on image Y whenever nothing in the content-facing surface changed between them
  ("rebuild only when we need to");
- **a breaking surface change (or any toolchain change) mints a new identity** — every cached and
  published build for the old surface is stale, and the next release bakes fresh;
- **a declined bundle costs exactly what today costs — a compile.** Shipping bundles is strictly
  safe; declines are logged with both identities.

### 🚨 The identity is a property of the BINARIES, not of the source (#1725)

The stability above holds across *rebuilds of the same build invocation*. It does **not** hold
across *different* build invocations of the same source, and a delivery lane was once built on the
belief that it did. Measured on commit `babb3bc` — same sources, same runner path — between the
Build-and-Test job's `dotnet build` output and the `dotnet publish -t:PublishContainer` image the
same commit shipped:

| half of the identity | result |
|---|---|
| implementation MVIDs (`FullMvidAssemblies` — the toolchain closure) | **all differ**, controls (Data, Layout, AI, Utils) included |
| reference-assembly hashes (the other 33 canonical entries) | 29 identical, **4 differ**: `MeshWeaver.Graph`, `MeshWeaver.Hosting`, `MeshWeaver.Kernel`, `MeshWeaver.Markdown.Collaboration` |

The two hosts therefore resolved `sd0d0daa…` and `s377941f…` for one commit. The same four
reference assemblies also differ between the **amd64 and arm64 variants of one multi-arch image**,
so a multi-arch image carries two identities and a bake is valid for the architecture it was taken
on. That is why `publish-bake` is a matrix with **one lane per architecture**, each pinning
`--platform` to its own leg's value and running on that architecture: each lane publishes the
bytes it actually produced, under the identity those bytes resolve. Until then only amd64 was
baked — every AKS node is amd64 — and an arm64 install resolved the other identity and compiled
every NodeType at boot.

None of this is a defect in the identity — it is the identity doing its job. A bake is an ABI
claim about *bytes*, and bytes from another compilation are not the bytes a pod loaded. The
operational rule that follows is absolute:

> **The producer of a bake must be the binaries the consumer runs.** Never publish a bake under
> several identities, never let a pod scan for a "nearest" one, never relax the sentinel or the
> identity check — adopting bytes from an identity you did not resolve is exactly what the check
> exists to prevent.

Manifest-less CI processes (test hosts) fall back to the commit identity `g<sha>` stamped by
`Directory.Build.props`; local manifest-less builds fall back to the identity anchor's MVID
(`MeshWeaver.Compiler.dll` — single-file attributable, which is what lets a packer read it without
loading anything). The commit stamp doubles as provenance everywhere.

### 🚨 …and both hosts must RECORD the same canonical set — the address check (#1814)

The rule above is about the *bytes*. There is a second way for two hosts of one commit to resolve
different identities, and it has nothing to do with bytes: **a canonical assembly that one host's
surface manifest does not record at all.** `ComputeSurfaceIdentity` hashes every name in
`ContentSurfaceAssemblies`, and a name the manifest has no line for contributes the literal
`absent`. So a host that stops *compiling against* an assembly stops recording it — and forks its
identity away from every other host — while its binaries are otherwise identical.

That is what took memex.meshweaver.cloud's course covers down for two hours on 2026-08-17. The
sequence, measured:

* `feat: Excel/CSV import becomes its own module` (`82481e024`, merged 18:46 that evening) moved
  `MeshWeaver.Import` and its private closure — `MeshWeaver.DataSetReader{,.Csv,.Excel,
  .Excel.BinaryFormat,.Excel.OpenXmlFormat,.Excel.Utils}` and `MeshWeaver.DataStructures`, **eight
  canonical names** — out of both portals' compile reference graphs into the `modules/<Name>/`
  runtime lane. Correct on its own terms: the module lane still contributes `MeshWeaver.Import` to
  the in-mesh compile reference set.
* The manifest is written from `@(ReferencePathWithRefAssemblies)` — a host's **compile**
  references — so those eight lines vanished from the portal's manifest only. `mw-plugin-test`,
  the bake host, still referenced them.
* Measured on the shipped images of `3.0.0-rc4.ci.4276`, both `--platform linux/amd64`:

  | image | manifest | resolves |
  |---|---|---|
  | `mw-plugin-test` (bakes) | 38 lines | `s7293e54297ec28e213bd82f30d59e709` |
  | `memex-portal-ai` (runs) | 54 lines | `sa6d587a25d64d11774f22348664bca0c` |

  The **29 shared entries had byte-identical hashes**. Presence, not drift, was the entire
  difference — and the net line counts (38 vs 54) hide it, because the portal legitimately carries
  25 Blazor/Orleans/hosting names that are outside the canonical set by design.
* Consequence: every bake was published, intact, under an address no pod ever opened. Publication
  succeeded, the CD job was green, `check-release-availability.sh` passed — and each deploy's first
  pod logged `compiled=269 alreadyBaked=0` and spent 10 m 29 s (+1598 MB working set) recompiling
  what CI had already compiled. During that window ~12 instance hubs latched a compilation-fallback
  card and served it to anonymous visitors long after the compile finished.

The manifest itself is emitted by `MeshWeaverSurfaceManifest.targets` at the repo root — a separate file,
imported by the root `Directory.Build.props` **and** by the plugins repo's `src/Directory.Build.props` against
`$(MeshWeaverRoot)`, because the portal hosts live there since #2293. While the targets sat inline in the
props they were invisible to that repo: the first portal image built from it (`3.0.0-rc8.ci.5768`) declared
`MeshWeaverSurfaceManifest=true` and shipped **no** manifest — the fallback identity below, which no bake
matches (#2395). The plugins import is deliberately unconditional: a core checkout without the file is an
MSB4019 error, not a manifest that silently never appears.

**Two checks now stand where nothing stood.**

1. **Offline, at PR time.** `CanonicalContentSurface_IsRecordedByEverySurfaceManifestHost`
   (`FrameworkBuildIdentityTest`) recomputes, from the csproj graph, the compile closure of **every** project
   that sets `MeshWeaverSurfaceManifest=true` and fails naming any canonical assembly a host does
   not record. `CanonicalList_MatchesTheTesterClosure` had always pinned one side of that equality;
   this pins the other, which is the half whose absence let a one-line-per-host change ship.
2. **On the artifact, at release time.** `main-cd`'s `publish-bake` job resolves the identity of the
   **promoted `memex-portal-ai` image** and compares it with the identity the bake published under,
   **before** publishing. The comparison runs the bake image's own `framework-identity` verb:

   ```
   mw-plugin-test framework-identity <app-dir> [--expect <identity>]
   ```

   which resolves the identity of *another* host's `/app` from that directory's manifest and
   assemblies as **files** — nothing is loaded, so one container answers for another image. It
   refuses to answer for a directory with no usable manifest rather than degrading to the fallback
   identity: two manifest-less hosts of one commit resolve the same fallback, and a comparison that
   passes on degraded input is a check that cannot fail. On a mismatch it prints the canonical
   assemblies the target does not record, because "the hashes differ" is not actionable and the real
   defect was eight named assemblies.

Both pulls pin `--platform` to **this lane's** architecture, out loud — never the runner's
default: the identity is per-architecture, so comparing across legs would be meaningless. The bake
above pinned the same value, which is what makes the two sides of this comparison describe the same
bytes. Before the lane was split per architecture that per-arch difference was the *second*
independent way to mint an unread address (`memex.localhost` is arm64 while the bake published
amd64); the same guard covers it either way, because it compares the values two concrete hosts
resolve rather than assuming which architecture either of them is.

### 🚨 …and the bake must compile against the PORTAL, not against the process it runs in (#3022)

The two checks above make the two hosts *record* one identity. They say nothing about what the
bake *compiles against*, and the third outage came from there. After #3041 the identity gate was
green — `mw-plugin-test` and `memex-portal-ai` of `3.0.0-rc9.ci.7534` both resolve
`s8fe4902c0b2f5974f824be2867221dbd`, every one of the 25 assemblies they share is byte-identical —
and the platform's `plugins-bake` was red on four NodeTypes with
`CS0234 'Maps' does not exist in the namespace 'MeshWeaver'`. The bake took its reference set from
the tester image's own `/app` (88 assemblies); the portal's has 219, and `MeshWeaver.Maps.dll` is
one of the **21 `MeshWeaver.*` assemblies that exist only in the portal**. Nothing in the verdict
named a reference — it named `Cornerstone/Pricing` and three map galleries, whose source nobody
had touched. No seal, so no dependent was woken and no portal could adopt any release since.

Since #3022 the node-repo lanes (`node-repo-publish-bake.yml`, `node-repo-gate.yml`) take
**`platform-image` + `platform-image-digest`** (required — the portal), assert with the tester's
`framework-identity /portal --expect <tester identity>` that the two images are **one build**,
compose the **gate host** (`compose-gate-host.sh`: the portal's `/app` with the tester CLI laid
beside it — the portal's bytes win, the tester's manifest and `deps.json` never ride), and run both
verbs from it on the **portal image's own `dotnet`**: `compile … --app /app --shared-frameworks
/usr/share/dotnet/shared` compiles against the portal's `/app` plus its implementation frameworks,
keys `framework-mvid.txt` to the identity **the portal's directory resolves**, computes every
dependency record against the portal's manifest and MVIDs, and **refuses** a host whose compile
toolchain the process is not running (the closure's MVIDs must match member by member — the one
invariant that makes recording the portal's identity honest). The gate's `--app /app` is the
precondition that the process *is* the portal host; a gate running as another host would decline
every bundle and pass. A CS0234/CS0246 the reference set explains is named in the verdict —
`reference set lacks <assembly> (portal-shipped, not composed: modules/…)` — never left reading as
a content bug. The full shape, the measurement table and the cost are in
[Module Build Architecture](/Doc/Architecture/ModuleBuildArchitecture) → "The NodeType bake and its
gate run AS the platform image too".

The address check this section describes stays where it is meaningful: the platform's own Doc bake
(`main-cd.yml` `publish-bake`) still bakes inside the tester image and compares against the
promoted portal. For the node-repo lanes the bake's identity is the portal's by construction; the
lane keeps "the bake is keyed to what the portal's `/app` resolves" as a cheap invariant after the
compile, and the comparison that can lose — the two images being one build — runs before anything
is composed.

🚨 **The fix direction is always "give the host the reference back", never "shrink the canonical
list".** Removing a name would make the two hosts agree by making the identity blind to that
assembly's surface — an under-invalidation, which is how a portal ends up adopting NodeType
assemblies compiled against a framework that has since shifted underneath them: a silent
`TypeLoadException` inside an ALC at activation, the failure mode with no diagnostic and no overlay.
`Memex.Portal.Distributed` therefore declares the eight as compile-only references
(`Private="false" ExcludeAssets="runtime" PrivateAssets="all"`): the manifest records them, while
the bits still ship only via `modules/MeshWeaver.Import/` and nothing downstream inherits the
declaration.

## The image: `prebuilt/` beside the app

`Memex.Portal.Distributed.csproj` accepts `-p:PrebuiltBakeDir=<dir>`: the bundle zips are laid into
`prebuilt/` in the publish output (and therefore the container image). Without the property — every
local build, and the CD legs until they bake in-job — the image simply ships no bundles and boot
behaves exactly as today.

## The consumer: boot seeds before the sweep decides

`ShippedPrebuiltBundles.SeedAll` (`MeshWeaver.Hosting`) runs inside the dynamic-type pre-warm
pipeline, **after** the static repo import settles (the nodes a bundle names must exist) and
**before** [the sweep](/Doc/Architecture/NodeTypeCompilation) probes the assembly store:

1. every `*.zip` under `prebuilt/` (override: `PreWarm:PrebuiltDirectory`) has its **manifest** read
   with `BundleReader.ReadManifest` — a few KB at a known entry, **no assembly decompressed**;
2. the bundle's framework MVID is checked **once** against the running process; a mismatch declines
   the whole bundle, loudly;
3. one enumeration of the mesh's NodeType nodes filters the entries down to types this deployment
   actually holds (an image ships one content set; a mesh serves a subset) — no per-missing-path
   waits. The enumerated **nodes** are kept, not just their paths: they carry the record that
   answers step 4;
4. each remaining entry is asked whether adopting it would change anything —
   `PrebuiltAssemblySeeder.IsAlreadyAdopted`, which defers to the same `NodeTypeBakeStatus.Classify`
   the sweep's probe uses, plus one store probe at the record's `LastCompiledVersion`. An entry the
   store already backs is **skipped entirely**;
5. only the **deviating** entries are extracted (`BundleReader.Read(stream, nodePaths)`) and adopted
   through `PrebuiltAssemblySeeder.Seed`: the bytes land in the assembly store under the node's
   version and the record is stamped exactly as a successful compile stamps it.

> 🚨 **Step 4 is not an optimisation detail — adoption is expensive.** `Seed` opens the type's own
> mesh-node stream, which **activates its per-node hub**, then re-uploads the bytes and writes the
> node. Before the skip, memex-cloud re-adopted all 43 of its assemblies on every boot — 43
> activations, 43 uploads, 43 writes, **13.5 s of a 101 s warm-up** — to establish that nothing had
> changed since the previous pod did the same. The framework identity is an API-surface hash and is
> stable across internal-only merges, so that is the *common* roll. It also grew the assembly cache
> by a whole generation per boot: `Seed` stamps the version it read *before* its own write, so each
> re-adoption uploaded the same bytes under a new key that nothing ever read.

The skip stays **level-triggered on the store**: the record's claim is believed only when a probe
confirms the bytes are still at that key, so a cleared, remounted or stale-restored assembly volume
re-seeds exactly as before (`BakeState.BytesMissing`). The reported count is `adopted +
already-current`, so the coverage signal below does not collapse to zero on a healthy steady-state
boot.

The sweep's store probe (`NodeTypeBakeStatus`) then classifies each adopted type `Baked` — for fully
covered shipped content the boot log reads `pending=0` and no Roslyn runs. Everything here is
best-effort *and loud*: a corrupt bundle, an unadoptable identity, or a seed that cannot settle is
logged and skipped, and the sweep compiles that type as it always has. Nothing certifies anything
from this path — the bake gate keeps probing the store, which only ever holds what was actually
adopted.

## Adopt, then compile on demand — the boot bake is retired everywhere

**Adoption and its coverage report are unconditional; only compiling is configurable.** Every boot
seeds both bundle sources and then probes the assembly store:

1. both bundle sources seed as above (`SeedAll`, then `SeedPublishedRoot`);
2. `DynamicTypePreWarmer.ProbeDynamicTypes` enumerates the mesh's dynamic NodeTypes and asks the
   assembly store about each — the same `NodeTypeBakeStatus.Probe` the sweep uses to decide what to
   build, stopping at the answer;
3. the boot log reports `adopted=N uncovered=M`, and **every uncovered type is NAMED at warning**,
   with the `BakeState` and detail saying *why*.

`PreWarm:DynamicTypes` then decides only whether the leftover is **also** compiled at boot. It is
`false` everywhere, including the fleet.

> 🚨 These two used to be one switch, and the fusion was a real defect: the chart's default is
> `DynamicTypes=false`, so the ordinary deployment adopted **nothing** — it ignored bundles sitting
> in its own image and lazily compiled every type instead. Splitting them is what makes "no boot
> bake" cheap rather than a regression.

Why the sweep is retired rather than tuned: adoption made it redundant. Once the satellite repos
published under the live identity, a prod boot measured `compiled=0 alreadyBaked=84` — the sweep
compiled nothing and still charged 32.1 s of warm-up (64.8 s when adoption was broken). On a *miss*
it was worse than useless: it blocked readiness on compiling types no user had asked for. On a
laptop it was pure waste — one developer machine had **15 generations** under
`/data/assembly-cache/.generations`, fifteen full sweeps that rebuilt what CI had already built.

An uncovered type is still correct: it builds on **first access**, via
`NodeTypeEnrichmentHelpers.WaitForCompileSettled`, when someone actually reaches it. Measured cost
~2.0–2.1 s per type locally (~2.4 s on the fleet), paid once, by that type's first visitor. That is
also the deliberate escape for content with no CI bake *by construction* — a NodeType someone is
authoring on a laptop has no published bundle and never will. What the report refuses is the silent
version: a gap you only discover when a page renders empty.

### What readiness means now

The bake gate certified a bake by **compiling** every type and refusing readiness when one that used
to build no longer did. With nothing compiling at boot there is no such verdict, so
`PreWarm:GateReadiness` is turned **off** rather than left armed — an armed gate with no sweep behind
it reports healthy on every rollout and protects nothing, which is the exact failure it exists to
prevent. (The portal already says so at Critical; `NoValuesFileArmsTheBakeGateWithoutTheSweepBehindIt`
pins it at build time.)

**What that gives up, precisely:** a NodeType that regresses on a new image is no longer caught at
rollout; it surfaces when a user first reaches it. **What replaces it:** the boot coverage report —
a broken bake lane (an identity mismatch declines every bundle wholesale, #1725) shows up as a
coverage collapse in the logs of the *first* pod of a bad roll.

> 🚨 **Do not "fix" this by gating on full adoption coverage.** `uncovered > 0` is the normal steady
> state of a real portal: users author NodeTypes in their own partitions, and those have no CI bake
> by construction — the live `memex` share holds two such types under `rbuergi`. A coverage gate
> would never go ready.

> 🚨 **A `PreWarm__*` key in a values file does nothing until the configmap renders it.**
> `deploy/helm/templates/memex-portal/config.yaml` enumerates keys explicitly — it does not iterate
> `.Values.config` — so an untemplated key is dropped with no warning from helm, kubectl, or the
> portal. `PreWarm__PrebuiltBundleRoot` was set in `values.aks.yaml` from the day this lane shipped
> while the configmap never rendered it, so every chart-deployed portal ran with the consuming half
> inert and recompiled content CI had already baked for it. Adding a key to a values file is half
> the change; `PlatformBakeLaneGuard.EveryPreWarmKeyInValues_IsTemplatedInTheConfigMap` asserts the
> other half.

## The delivery: main-cd bakes IN THE IMAGE, then publishes

`main-cd`'s **`publish-bake`** job runs the content the image itself embeds — the `Doc` tree, staged
by `.github/scripts/stage-doc-gate.sh`, the same staging the PR gate judges — in **two steps against
the `mw-plugin-test` image this very CD run built and promoted**:

```
docker run … --entrypoint /app/mw-plugin-test "$IMAGE" compile /repo/doc \
  --output /bake --allow /repo/doc-gate.allow --source-sha "$SHA"     # the BAKE — no mesh
docker run … --entrypoint /app/mw-plugin-test "$IMAGE" /repo/doc \
  --allow /repo/doc-gate.allow --seed /bake                            # the GATE — consumes it
```

The gate still fails the job: the platform's own shipped content failing to render or execute its
`Tests` areas against the image that ships it is a release defect, and splitting the steps must not
lose that. `PlatformBakeLaneGuard` pins both halves — `--bake-output` (the mesh-driven bake) is
banned in that job, `--seed` is required, and the bake must come first. The job then copies the
resulting bundles to the portals'
shared storage (`.github/scripts/publish-bake-bundles.sh`), laid out
`prebuilt-bundles/<identity>/<source>/<bundle>.zip`, sealed by a `_complete` sentinel written
strictly LAST. Each booting pod seeds ONLY its own identity's SEALED source directories
(`ShippedPrebuiltBundles.SeedPublishedRoot`, config `PreWarm:PrebuiltBundleRoot`) before its
sweep — an unsealed or torn publication (a publish that died mid-way) is refused loudly and the
sweep compiles instead. "Rebuild only when we need to" applies to the publish too: when the
identity's directory is already sealed — an internal-only merge resolves the same surface
identity as its predecessor — the script skips with a notice instead of re-uploading. See
[The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract)
for the job's preflight discipline and the dependent-repo dispatch.

### 🚨 CD compiles ONLY what the image embeds — everything else is adopted

The bake is scoped to `src/MeshWeaver.Documentation/Data`, the one tree every portal ships inside
itself (`Memex.Portal.Shared` references `MeshWeaver.Documentation`). Nothing else, on purpose:

| Content | Who bakes it | Why not CD |
|---|---|---|
| node-repo content (Plugins, Education, Reinsurance, SocialMedia) and **Store** packages | each repo's own `node-repo-publish-bake` lane, against the same image ⇒ the same identity | it arrives **already compiled** and is adopted; `main-cd.yml` checks out no other repository, so it could not compile them even by accident |
| `samples/Graph/Data` | nobody — compile-**gated** only | no deployment embeds them, and memex receives them over the GitHub link into the `MeshWeaver` partition, where node paths read `MeshWeaver/samples/Graph/Data/ACME/…` while bundles are keyed `ACME/…`. The seeder matches by node **path**, so the bundles are inert everywhere. Measured: 7 packages / 24 assemblies per CD run for bytes nothing can adopt |

So the CD bake is **1 package / 4 assemblies**, down from 8 / 28 when it also baked the samples.
Correctness of the samples content is unaffected — `dotnet-test.yml`'s doc-gate still compiles,
renders and tests both trees on every PR. What changed is only that CD stops *shipping* assemblies
no deployment can use. `PlatformBakeLaneGuard` pins both halves of this: the Doc tree must be baked,
the samples tree must not, and the workflow must check out no other repository.

The end state this serves is a boot that compiles nothing: with the four satellites publishing under
the pods' identity, a prod portal boot reached `compiled=0 alreadyBaked=84` — everything adopted,
nothing rebuilt. The platform's own `Doc` types are the remaining slice, and this lane is what
delivers them.

Three properties fall out of baking in the image rather than shipping a CI artifact across jobs:

- **the identity always matches**, by construction — producer and consumer are the same binaries,
  so there is no compatibility question left to get wrong;
- **the bake is a stronger gate**, not just a producer: it proves the platform's shipped content
  compiles, renders and passes its `Tests` areas against the binaries that actually SHIP. A red
  bake fails CD loudly (the images are already promoted; nothing silently ships less);
- **there is no "nothing to publish" state.** The old lane had one — a reuse-green Build-and-Test
  run produced no artifact and the publish warned and skipped — which is the shape that let a lane
  publishing to an unusable identity look healthy for a whole release train.

The platform deliberately does not call `node-repo-publish-bake.yml` even though the two lanes are
the same idea: it authenticates to the registry by OIDC rather than the reusable workflow's
username/password secrets, and it bakes **two** trees against **two** known-debt ratchets in one
bake directory, where the reusable workflow bakes one mount. Both run the identical publish script,
which is the part that must never drift.

## Node repos run the same lane — as reusable workflows

Every satellite content repo (MeshWeaver.Plugins, MeshWeaver.Education, MeshWeaver.Reinsurance,
MeshWeaver.SocialMedia) bakes and publishes its own content through the SAME contract, and since
#1707 the jobs live HERE, as reusable `workflow_call` workflows the satellites call instead of
vendoring. Adoption is **per job and still in progress**: the target is that every repo calls
`node-repo-publish-bake` (the lane whose script contract must not drift), while a repo whose
variant of a gate carries repo-specific machinery (Plugins' Tests-area ratchet, Education's
course checks) keeps that job vendored until the machinery generalizes. As of 2026-08-17
**MeshWeaver.SocialMedia and MeshWeaver.Plugins are merged and green end-to-end including
publish-bake** (SocialMedia calls the full set, Plugins calls publish-bake only);
MeshWeaver.Reinsurance and MeshWeaver.Education are in flight; MeshWeaver.Manufacturing is
deliberately deferred.

| Workflow | Job it unifies |
|---|---|
| `.github/workflows/node-repo-validate.yml` | JSON/manifest shape gate (`scripts/validate-repos.py`, `gen-manifests.py --check`, main-only `--check-versions`) |
| `.github/workflows/node-repo-compile-check.yml` | the compile gate — every NodeType's resolved Source vs the assemblies of the digest-pinned platform image |
| `.github/workflows/node-repo-gate.yml` | the tester gate — `mw-plugin-test` over the (optionally affected-narrowed) mount, cross-repo `requires` staged in; since #3022 executed by the tester **as the portal** (`platform-image`, composed gate host, `--app /app`) |
| `.github/workflows/node-repo-publish-bake.yml` | the main-only bake + publication — `compile --output` then `--seed` over the full repo or (opt-in) the affected closure, staged-module exclusion, OIDC publish via the canonical `publish-bake-bundles.sh`; since #3022 the bake compiles against and is keyed to the **portal** (`platform-image` + `platform-image-digest`, both required-or-explicit exactly like the tester's) |
| `.github/workflows/node-repo-tag-modules.yml` | the `<Module>/vX.Y.Z` tag publisher (`scripts/tag-modules.py`) |

The design rules the extraction preserves:

- **Every externally-provisioned value is an explicit input/secret** — nothing implicit, so the
  publish-bake preflight can assert the full set and fail RED naming what to provision. The
  caller keeps its own `preflight` job (and the fork exemption) for the gate lane, and gates run
  unconditionally behind `needs:` — no input-shaped `if:` anywhere (no skip-trapdoors).
- **The publish script has one home** — `publish-bake-bundles.sh` in this repo, next to the
  `ShippedPrebuiltBundles` constants its `_complete` sentinel must keep matching. The reusable
  publish-bake checks this repo out (by `platform-ref`, default `main`) and runs it, retiring
  the per-repo vendored copies. This repo is public, so private satellites call the workflows
  and read the script with their default token.
- **Repo-specific policy stays in the caller**: the digest pin (`MW_IMAGE_DIGEST`) and its bump
  cadence — an unpinned image is an explicit `allow-unpinned` opt-in, never a silent fallback —
  gating (`if:`/`needs:` on the `uses:` job), the `repository_dispatch` receiver, the
  module-bundle job of mixed packages, and each repo's `scripts/` (validate / compile-check /
  affected-modules / tag-modules stay caller-side — they encode the repo's own layout).
- **Adoption renames the required checks**: a reusable-called workflow's check runs report as
  `<caller job> / <name>`, so each repo's required-status-check contexts are renamed in the same
  change that adopts a workflow — a context left at the old name would wait forever. On a
  protected repo this is a required step of the adoption, not an afterthought: SocialMedia's
  contexts are now `validate / Validate node repos`,
  `compile-check / Compile every NodeType (vs core)` and
  `test-repos / Compile + render node repos (MeshWeaver from ACR)`.
  🔒 A later **`@ref` bump does NOT rename anything** — it changes neither the caller's job id nor
  the inner job's `name:` — so the contexts stay valid across bumps. Only renaming a job in the
  reusable workflow would break them, which is why that rename is itself a breaking change to
  every caller's branch protection.
- **The caller PINS the workflow ref** — `@<40-char commit sha>`, never `@main`. See below; this
  is the same rule as the image digest, applied to the CI logic instead of the CI runtime.
- **Staged cross-repo modules are excluded from publication** (e.g. Store is staged so
  `requires` resolve but is owned and published by MeshWeaver.Plugins) — each source directory
  seals independently, which is also why no cross-repo bake ORDERING is needed: a dependent
  repo's publication never contains its dependency's bundles, so there is nothing to wait for.
  The framework-release dispatch fans out to all satellites concurrently.

### Rebuild only what a change AFFECTS — `narrow-by-affected`

> *"When updating plugins, we should check from git history which modules are affected, and we
> should rebuild only these."*

The bake used to be the whole repo, every time — every main push and every scheduled release
poll ran ~40 minutes of Roslyn over every package to republish bundles that, for all but a
handful of them, were the ones already sealed in storage. The stated reason was the atomicity of
the publication, and it was a real constraint applied to the wrong half of the job:

> the `_complete` sentinel is written LAST and lists the whole bundle set, and a portal seeds
> **only what the sentinel lists** — so publishing a delta would not ADD bundles, it would
> REPLACE the sentinel and shrink what every portal adopts.

That constrains what is **uploaded**. It says nothing about what must be **recompiled**. With
`narrow-by-affected: true` the two are separated:

| | |
|---|---|
| **compile** | only the modules the diff affects (+ their dependencies, because the tester's fresh mesh installs what it mounts) |
| **publish** | still the complete set — every bundle not rebuilt is carried forward from the current publication before the seal |

Three pieces, in order:

1. **`bake-scope.sh`** decides. 🚨 **Its baseline is the PUBLICATION, not `github.event.before`** —
   the sealed directory carries `source-commit.txt`, and that is the only commit that answers
   "what changed since the bundles that are actually out there". Diffing the push instead would
   silently under-build after any run that did not publish: a cancelled run, a superseded push, a
   red gate, a re-run of an older commit. Reading the baseline off the publication is
   self-correcting — whatever was published is what we diff against, however many runs it took.
2. **The caller's `scripts/affected-modules.py`** answers. It is the caller's file because the
   dependency edges are the caller's content, and it mirrors the runtime's own resolution 1:1
   (`LocalNodeRepo.CollectDependencies`): changed modules → transitive **dependents** → their
   dependencies, emitted dependencies-first. It ships its own `--self-test`.
3. **`carry-forward-bundles.sh`** keeps the publication whole: every bundle the sealed listing
   names that this bake did not produce is downloaded into the bake directory, so
   `publish-bake-bundles.sh` runs **completely unchanged** over a full set and the resulting
   publication is indistinguishable from a full bake's.

**The bias is toward a full bake, always, and out loud.** Narrowing a build is the shape that
produces a *silent* under-build — a module that should have been recompiled and was not becomes a
stale assembly every portal seeds at boot, and the evidence of the miss is the absence of
evidence. So each of these resolves to a FULL bake, naming itself in the log and the job summary:

- the caller ships no `scripts/affected-modules.py`;
- a `repository_dispatch` (a framework release mints a NEW identity — everything is recompiled
  against it), or any event with no meaningful content diff;
- no sealed publication yet for this identity, a malformed target, or an unreadable sentinel;
- **publish targets that disagree** on the published source or bundle set (one narrowed bake
  carries forward one publication, so it cannot serve two);
- a baseline commit this checkout cannot resolve, or one that is not an ancestor of HEAD
  (history rewritten);
- the selector refusing — an **empty diff** is a broken range, never "nothing to do";
- the selector answering ALL modules: a change under `scripts/`, `.github/`, a repo-root file, or
  **a module directory that no longer exists**. That last one is why a DELETED module still
  shrinks the publication correctly.

And one verdict that is neither: **`scope=none`**, when the sealed publication already records
*this* commit for *this* identity. It is a positive finding — read from every target, logged with
both shas and an explicit green step, never a grey skip — and it is what the every-30-minutes
release poll hits almost every time it runs. `publish-bake-bundles.sh` already skipped the upload
in that case; nothing had ever gated the 40-minute build in front of it.

A **missing** carried-forward bundle is fatal: the only alternatives are "publish less than is
published today" and "stop", and shrinking is the one that fails silently. The job goes red with
the bundle named, and the existing publication stays sealed and intact because nothing has been
written yet.

`narrow-by-affected` is mutually exclusive with `pre-bake-script` (a hook that builds its own
mount owns its composition) — asserted in preflight, not resolved silently at run time. Both
scripts carry `--self-test`, run on every platform PR from `dotnet-test.yml`'s preflight job, and
both are proven non-vacuous by mutation: delete any single fallback and the step goes red.

**Still full, deliberately:** a framework release. A new identity has no publication to carry
forward and every module must be compiled against the new binaries — which is also what makes
that bake the gate that catches an API removal (the 2026-08-09 `AddTracking` outage) before every
portal parks at its next restart.

### 🔒 The workflow ref is PINNED — and bumping it is a deliberate act

Every satellite calls these workflows at an **immutable commit**, never at `@main`:

```yaml
  test-repos:
    needs: [preflight, validate]
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-gate.yml@731620dc6be030c964aa2c6a1e87ac11a1e6bfc4
```

**Why.** The platform *image* is pinned by digest so CI is reproducible and an image regression
lands on the commit that bumps the pin. The CI *logic* needs that for the same reason and with a
**wider blast radius**: on `@main`, a single edit to a reusable workflow changes **every
satellite's gates at once**, no satellite's PR can reproduce yesterday's behaviour, and *"did my
change break this, or did the shared workflow move under me?"* stops being answerable — that exact
question cost a full day on MeshWeaver.Education. Pinned, the answer is in `git log` of the
caller's own `ci.yml`.

**Why a SHA and not a version tag** (`node-repo-workflows-v1` and friends):

- A tag is **mutable**. Moving it changes all satellites simultaneously with **no commit in any
  satellite** to attribute the change to — the blast radius stays exactly as wide as `@main`, only
  the trigger moves from "someone edited a workflow" to "someone moved a tag". A SHA is what makes
  the bump *be* a satellite commit, which is the entire point.
- It is the digest's analogue: a SHA is to a workflow what a digest is to an image; a tag is
  `:latest`.
- It matches what the callers already do — `MW_PLATFORM_REF` is a full 40-char platform SHA with
  this same rationale beside it. One convention, not two.
- `node-repo-tag-modules` exists to guarantee a module tag is never *"silently moved under everyone
  who pinned it"*. A moving `uses:` tag would be precisely that, for CI logic.

GitHub does **not** allow the `uses:` ref to come from an input, an `env`, or any expression — it
must be a literal — so a `workflow-ref` input is impossible and the SHA lives literally on each
`uses:` line in each caller.

**A reusable-workflow change does not reach the satellites until each one bumps. That is the
point, not a bug**: it is what turns a shared-workflow regression from a simultaneous four-repo
outage into one satellite's PR that goes red and is trivially attributable.

#### How a bump is triggered

The pin is bumped **by the person who changes a reusable workflow**, as the last step of that
change — the platform PR lands first, then one follow-up PR per satellite. Concretely:

1. Merge the `node-repo-*.yml` change to the platform's `main`; note the merge commit.
2. In each satellite that calls the changed workflow, replace the SHA on **every**
   `Systemorph/MeshWeaver/.github/workflows/node-repo-*.yml@…` line — all of them, in **one**
   commit, so a repo never runs two different revisions of the shared contract.
3. Open the PR and let the repo's own gate suite run against the new logic. This is where a bad
   shared workflow surfaces: on the bumping PR, in the repo it affects, attributable to the bump.
4. Repeat per satellite. They may lag each other; each bump is independently revertable.

Adopting a *new* workflow additionally renames that repo's required contexts (previous bullet); a
plain bump does not.

#### Staleness is surfaced, not scheduled

A pin nobody bumps is worse than a moving ref: the satellites diverge in silence and the shared
workflow's fixes never land anywhere. So each caller's **`preflight` job prints the pin's age on
every run** — in the job someone already opens when a gate goes red:

```
workflows pinned to 731620dc6…, cut 3 days ago (2026-08-17T09:45:53Z)
```

Past `STALE_AFTER_DAYS` (30) the step summary adds a **"a bump is due"** callout pointing back
here. This is the same instinct as the known-debt allow-lists — surface staleness at the point of
use rather than inventing a place people must remember to check — and it is deliberately *not* a
scheduled job that opens issues.

Two properties that make the age trustworthy rather than decorative:

- **The SHA is read back out of the caller's own `uses:` lines**, never kept as a second copy that
  could drift from the pin actually in force. A **partial bump** (one caller moved, the rest left
  behind) is therefore reported as *"pins disagree"* instead of averaged into one age.
- **It is a reporter, so it never fails the run** — a red preflight would block every gate on a
  GitHub API blip. Every miss is announced (`::warning::` plus an explicit *age UNKNOWN*): it can
  report nothing, but it cannot fake freshness. 🚨 Note `gh` writes its error body to **stdout**,
  so an emptiness check is not enough to detect an unresolvable SHA — the step validates the
  timestamp's shape.

#### The one ref that still floats, on purpose

`node-repo-publish-bake`'s **`platform-ref` input still defaults to `main`**. It selects the
checkout of the canonical `publish-bake-bundles.sh`, whose bundle layout and `_complete` sentinel
must keep matching the `ShippedPrebuiltBundles` constants in the **portal that consumes** the
bundles — and the portals self-update from `main`. Pinning that to a satellite's cadence would let
a satellite publish in a layout the live portals no longer read. It is the publish *script*
tracking its consumer, not CI logic tracking a moving trunk; pin it (the input exists) only to
bisect a script regression.

The satellites' OIDC publish is **provisioned** (2026-08-17): the Azure managed identity
`github-actions-bake` (in the cluster's resource group) holds *Storage File Data Privileged Contributor* on the
portals' storage account and carries 8 federated credentials — the four satellite repos × the two
GitHub subject formats (classic and immutable; **register both, always** — see
[The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract)) — with the
`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` secrets set on all four repos.
**A red publish-bake was designed debt until 2026-08-17 and is a real failure after it** —
treat any surviving allowlist or "credentials pending" reference to a satellite's publish lane
as historical.

### 🚨 Following the release is a POLL in every satellite — and only one repo had it

**This is the single defect that makes a fleet boot on an identity nobody baked, and it has been
rediscovered at least four times. Read this before touching a bake trigger.**

Two cadences produce the bundles and consume them, and they do not match:

| | minted by | how often (measured 2026-08-22) |
|---|---|---|
| a framework **identity** | every platform `main` merge (`promote` phase C arms `memex-portal-ai:<version>`) | **1733** release-shaped portal tags |
| a **bundle** for that identity | a satellite `publish-bake` run | a handful of pushes per repo per day |

An instance self-updates to the newest release tag. If no satellite baked against *that* release,
`prebuilt-bundles/<identity>/<source>/` does not exist, every pod falls back to the in-mesh Roslyn
sweep, and the boot takes minutes instead of seconds. **The bundles are not missing because
publication is broken — they are missing because nothing told the satellites a release happened.**

So a satellite must **follow the release**, and it must do so by POLLING. The push
`repository_dispatch` fan-out is not the mechanism and must not be relied on:

- it was **never armed** (`BAKE_SUBSCRIBER_REPOS` / `DEPENDENT_DISPATCH_TOKEN` unprovisioned), so
  `notify-dependents` reported SUCCESS while notifying nobody — a green tick over a no-op;
- it required a human-minted cross-repo PAT on four satellites, which was **removed deliberately on
  2026-08-21**. Credentials that let one repo drive another's CI are not the architecture we want.

A poll needs nothing provisioned and nobody to remember. It uses the ACR credentials the satellite
already has for its gates.

#### The four parts — a repo has ALL of them or it follows nothing

1. **`on: schedule:`** with a cron. Stagger the minute per repo so four repos do not wake together.
2. **A `framework-release` concurrency lane** that `schedule` shares with `repository_dispatch`:

   ```yaml
   concurrency:
     group: ${{ github.workflow }}-${{ (github.event_name == 'repository_dispatch' || github.event_name == 'schedule') && 'framework-release' || github.ref }}
     cancel-in-progress: true
   ```

   🚨 **The schedule MUST be in that lane.** A scheduled run carries the DEFAULT BRANCH ref, so left
   in the `github.ref` group it shares the main-push group and — with `cancel-in-progress` — a
   half-hourly poll can cancel a main run part-way through `tag-modules`, work the scheduled
   replacement does not do and nothing else recovers. Release polling supersedes release polling;
   it never supersedes a push.
3. **A `FOLLOW_RELEASE` bake-target resolution** in `preflight`: on a release trigger resolve the
   digest `MW_TEST_IMAGE` currently points at and pass THAT down; on a push keep the pin (that bake
   certifies the bits the gates just ran). It must **fail loud** when the digest cannot be resolved
   — a silent fall back to the pin republishes an already-published identity and leaves the instance
   held, reporting success.
4. **`schedule` in the `publish-bake` job's `if:`** — parts 1–3 do nothing if the bake itself is
   still gated to pushes and dispatches.

Use `docker manifest inspect -v … | jq '… .Descriptor.digest'`. **Not**
`imagetools inspect --format '{{.Manifest.Digest}}'`: that template reads a member an OCI manifest
does not carry, so it yields nothing and every scheduled run takes the fail-loud branch.

#### Measured 2026-08-22 — three of four repos followed nothing

Counting occurrences in each repo's `origin/main:.github/workflows/ci.yml`:

| Repo | `schedule:` | `FOLLOW_RELEASE` | followed releases? |
|---|---|---|---|
| **MeshWeaver.Plugins** | 1 | 2 | ✅ |
| MeshWeaver.Education | 0 | 0 | ❌ pushes only |
| MeshWeaver.Reinsurance | 0 | 0 | ❌ pushes only |
| MeshWeaver.SocialMedia | 0 | 0 | ❌ pushes only |

All four follow releases as of 2026-08-22 (Education #195, Reinsurance #80, SocialMedia #46), on
staggered crons in dependency order: Plugins `17,47`, SocialMedia `7,37`, Education `32` (hourly —
a run there boots four disposable meshes), Reinsurance `22,52`.

🚨 **MeshWeaver.Education satisfies part 3 differently, and the grep below reports it as a
ZERO.** It has no pin to diverge from: it deliberately tracks `mw-plugin-test:main`, so its existing
"Resolve the bake image" step already targets the current release on a poll and it carries no
`FOLLOW_RELEASE` variable at all. That is correct, and `:main` IS the newest promoted release —
promote phase B moves the tag in the same job that arms the release in phase C. **Do not "fix" it by
adding the variable**; check that the repo resolves a released image on its poll, by whatever means
its bake target is chosen. Counting a string is a shortcut, and this is the row where the shortcut
lies.

All three carried the `repository_dispatch` receiver and a comment calling it *"DORMANT until the
platform provisions…"* — so the wall of green ticks was truthful about the gates and silent about
the fact that nothing had baked for the current release in weeks.

🚨 **A dormant receiver reads exactly like an armed one.** That is the whole trap, and it is the
same shape as `if: ${{ vars.X != '' }}` on a gate: *the evidence that it did not run is the absence
of evidence.* Do not conclude a repo follows releases because it has a `repository_dispatch:` block.

**Re-measure before acting on that table** — it is a snapshot, and this is exactly the kind of
thing that gets fixed in one repo and left in three:

```bash
for r in MeshWeaver.Plugins MeshWeaver.Education MeshWeaver.Reinsurance MeshWeaver.SocialMedia; do
  printf "%-26s " "$r"
  c=$(gh api repos/Systemorph/$r/contents/.github/workflows/ci.yml --jq .content | base64 -d)
  echo "schedule=$(echo "$c" | grep -c '^  schedule:')" \
       "bake-runs-on-poll=$(echo "$c" | grep -c "event_name == 'schedule'")"
done
```

`schedule=1` and a non-zero `bake-runs-on-poll` are the two that hold for **every** repo. How the
bake TARGET is resolved is the third part and it varies (see the Education note above), so read that
one rather than counting it.

#### The production signature

When this is missing you do not see a red gate. You see:

- an instance **held** on an old version, or rolling and then taking minutes per pod to boot;
- `/data/prebuilt-bundles` holding **many** identities (101 on memex-cloud, 2026-08-21) and **none**
  of them the one the booting pod resolves;
- `/app/prebuilt` **empty** — the image lane contributes nothing, so the store is the only source;
- boot logs showing a full Roslyn sweep (`compiled=<N>`) instead of `adopted … from … sealed bundles`.

Every one of those reads as "the bake is broken". The bake is fine. Nothing invited it.

See also [Release Availability Gates](/Doc/Architecture/ReleaseGates) → "How a fleet goes
stale while every check is green" — the instance-side half of the same defect, and why the
dispatch was REMOVED rather than provisioned: a publisher must not know its readers, and the
credential to tell them is not worth holding.

**Measured in production, 2026-08-17** — for satellite content the lane is not a design any more,
it is observed behaviour. On `memex` running `3.0.0-rc4.ci.4049` (identity
`s377941f549f721e01ac764e0fb8db84a`), boot
**adopted 68 prebuilt assemblies from 31 sealed bundles in 18.9 s**
and Roslyn-compiled **zero** healthy types (warm-up 32.1 s, `compiled=0`, `alreadyBaked=84`).
The comparable boot before any satellite bake existed did **80 compiles in 64.8 s**.

🚨 That measurement is **satellite content only**. The platform's own publication is NOT adoptable
today — issue #1725: the platform bakes from CI build output while the pod resolves its identity
inside the shipped image, so the identities differ and every boot recompiles the platform's shipped
types. The satellites escape it precisely because they bake INSIDE the image.

## What this step does not do yet

- **DB-resident types** (user/partition content CI cannot see) stay on the runtime bake.
- **A bundle is matched to a deployment by node PATH**, so a portal that mounts a tree somewhere
  other than its canonical root adopts nothing from it. That is why CD no longer bakes the samples
  trees at all (see "CD compiles ONLY what the image embeds"): memex holds them under
  `MeshWeaver/samples/Graph/Data/ACME/…` while a bundle from that tree is keyed `ACME/…`. If a
  deployment ever wants them prebuilt, the fix is to agree one canonical path per shipped tree — a
  content-layout question, not an identity one.
- **A narrowed bake still recompiles the affected closure's DEPENDENCIES.** `mw-plugin-test`
  takes no module list — the mount IS the filter — and it has no seed-from-directory seam: it
  compiles every package it discovers under `/repo`. A dependency is mounted because the fresh
  gate mesh must install it for the affected package to activate and for `shared=@…` to resolve,
  and it is then compiled too. Almost every package requires `Store`, so `Store` is recompiled on
  nearly every narrowed run. The win is still large (3–5 packages instead of ~40); closing the
  rest needs the tester to ADOPT a published bundle for a mounted-but-unaffected package — the
  same `PrebuiltAssemblySeeder` path a portal takes at boot, which is #1707 item 5 ("at install,
  check whether a pre-built lib exists and consume it") pointed at the gate.
- **The platform's OWN content bake (`main-cd.yml`'s `publish-bake`) is not narrowed.** It bakes
  `src/MeshWeaver.Documentation/Data` inside the shipped image and fuses gate and bake in one
  `mw-plugin-test` run; its content moves with the platform commit it is baking, so a
  content-diff baseline is a different question from the node repos' one.
- **The cross-repo rebuild cascade is a separate axis.** `narrow-by-affected` answers "which of
  THIS repo's modules changed". "Which repos must rebuild, when an upstream publishes" is the
  `upstream-sources` gate plus each repo's own **`schedule`** — there is no dispatch and no
  dependent list. A repo missing the schedule never rebuilds for a release, and the only symptom is
  an instance HELD on bundles from that repo's source.
- **An arm64 install adopts nothing the amd64 lane publishes** — the two architectures of one image
  resolve different identities (see the identity rule above). Local arm64 installs compile at boot
  as they always have; nothing may paper over this by publishing the same bundles twice.

  🚨 **That rule is now ENFORCED, not just stated** — and it had to be, because it holds only for
  *part* of the identity space. `FrameworkBuildIdentity` resolves **surface identity (`s<hash>`) →
  stamped commit identity (`g<sha>`) → MVID set**. The first is architecture-sensitive (the four
  reference assemblies above genuinely differ), so the two lanes get different directories and
  cannot collide. **`g<sha>` is not**: it is the same string for every CI build of a commit,
  whatever it was built on. Under that identity a second architecture would either be told
  "already published" by the content×framework sealed-skip and ship *nothing* — leaving its pods
  adopting the other architecture's bytes — or unseal and *overwrite* the incumbent. Both silent.

  So `publish-bake-bundles.sh` records the producing architecture beside the content marker
  (`architecture.txt`) and **refuses** a publication whose architecture differs from the one
  already under that identity, rather than skipping or overwriting. An incumbent with no marker
  predates the recording and can only be the amd64 lane, so a non-`linux-x64` bake refuses that
  too. Set `BAKE_ARCHITECTURE` (`linux-x64` | `linux-arm64`) in any lane that is not amd64.

  This is what a per-architecture publish needs *first*: adding an arm64 lane without it does not
  produce two lanes, it corrupts one.

See also: [Plugin Packaging](/Doc/Architecture/PluginPackaging) (the compilation-unit rules and the
MVID rationale) · [Build Coordination](/Doc/Architecture/BuildCoordination) (who bakes when several
replicas boot) · [Modules](/Doc/Architecture/Modules) · [Release Availability
Gates](/Doc/Architecture/ReleaseGates) (who READS these publications, and the
`_releases/<version>` marker that names each release's identity).

## A reconcile must know which release it heals

The release version (`3.0.0-rc8.ci.<run#>`) is minted by `portal-image` **per run**, and a
bake-only reconcile skips that job — so its output is empty there. The first reconcile that ever
fired (2026-08-27, run 33063843072, once #2491 gave the cron its own concurrency lane) published
the bake correctly and then failed the availability assert on an empty argument; quietly, it had
also written **no release marker**, which is the one thing a reconcile exists to write.

The version is not recomputable, but it is **recorded**: promote's Phase C arms the release as
`memex-portal-ai:<version>` on the same digest Phase A tagged `<short-sha>` — the tags
`SelfUpdateHostedService` rolls from. So `publish-bake` resolves the version ONCE, in a `release`
step (this run's output, else the promoted image's tag set via `az acr manifest list-metadata`),
and both the publish and the assert read that step. A sha with no version tag was never armed as
a release; the step stops loudly rather than publishing a marker for an invented version.
Pinned by `PlatformBakeLaneGuard.PlatformBake_ResolvesTheReleaseVersionOnce_AndEveryConsumerReadsIt`.

