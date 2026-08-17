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
`samples/Graph/Data` trees, and the `plugin-gate` job runs the same tester over the vital modules of
the MeshWeaver.Plugins checkout (synced at build time; the checked-out commit is recorded as each
bundle's `sourceSha`).

With `--bake-output <dir>`, the same run also persists what it compiled:

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

Both gates fail RED when a green run produced no bake identity — the bake stage is a
postcondition of the verdict, never an optional extra.

🚨 **What the gates' bake is FOR: proving the bake stage still works, on the PR that breaks it.** It
is *not* the delivery lane. A gate job's bundles are keyed to that job's own binaries, and no
shipped image ever contains those (see "The identity rule" below), so `doc-gate` uploads nothing;
`plugin-gate` still uploads `baked-plugins-<mvid>` for in-run diagnosis. What the portals adopt is
baked **inside the shipped image** — see "The delivery" below.

## BAKE is a build step; GATE is a mesh run that CONSUMES one

The section above describes how the bake worked until issue #1763: `mw-plugin-test` stood up an
in-process mesh (`new MeshBuilder(...).AddGraph()`), imported the repo's content, let the **mesh**
compile every NodeType, and `--bake-output` collected what the mesh had produced. That is
"compile through mesh nodes" — the thing #1707 forbids — and it is where the minutes went: mesh
startup, the hub scheduler, and one per-type activation for every type in the tree.

The two concerns are now split, and they are different kinds of thing:

| | what it is | how it runs |
|---|---|---|
| **BAKE** — produce assemblies | a **build step** | `mw-compiler compile <root> --output <dir>`: resolve NodeType sources from the git tree, compile with `MeshWeaver.Compiler`, emit **DLL + PDB**, write the bundle. No `MeshBuilder`, no `AddGraph()`, no import, no hub. |
| **GATE** — prove it works | a **runtime** check | `mw-plugin-test <root>`: stand up a mesh, render each type's default area, execute its `Tests` area. Rendering and running tests are genuine runtime behaviours; producing an assembly is not. |

**The emergency path is untouched.** A live instance with no usable artifact still compiles its own
— #1707 requires it, because there will always be code that never went through CI. That is
*recovery*, not a build lane.

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
on. Every AKS node is amd64, so the platform bake is pinned to `--platform linux/amd64`; an arm64
install resolves the other identity and compiles locally.

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

## The image: `prebuilt/` beside the app

`Memex.Portal.Distributed.csproj` accepts `-p:PrebuiltBakeDir=<dir>`: the bundle zips are laid into
`prebuilt/` in the publish output (and therefore the container image). Without the property — every
local build, and the CD legs until they bake in-job — the image simply ships no bundles and boot
behaves exactly as today.

## The consumer: boot seeds before the sweep decides

`ShippedPrebuiltBundles.SeedAll` (`MeshWeaver.Hosting`) runs inside the dynamic-type pre-warm
pipeline, **after** the static repo import settles (the nodes a bundle names must exist) and
**before** [the sweep](/Doc/Architecture/NodeTypeCompilation) probes the assembly store:

1. every `*.zip` under `prebuilt/` (override: `PreWarm:PrebuiltDirectory`) is read with
   `BundleReader` — the one codec shared with the registry bundle client;
2. the bundle's framework MVID is checked **once** against the running process; a mismatch declines
   the whole bundle, loudly;
3. one enumeration of the mesh's NodeType nodes filters the entries down to types this deployment
   actually holds (an image ships one content set; a mesh serves a subset) — no per-missing-path
   waits;
4. each remaining entry is adopted through `PrebuiltAssemblySeeder.Seed`: the bytes land in the
   assembly store under the node's **current** version, and the record is stamped exactly as a
   successful compile stamps it.

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
by `.github/scripts/stage-doc-gate.sh`, the same staging the PR gate judges — through
`docker run … mw-plugin-test … --bake-output` against **the `mw-plugin-test` image this very CD run
built and promoted**, and copies the resulting bundles to the portals'
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
| `.github/workflows/node-repo-gate.yml` | the tester gate — `mw-plugin-test` over the (optionally affected-narrowed) mount, cross-repo `requires` staged in |
| `.github/workflows/node-repo-publish-bake.yml` | the main-only bake + publication — full-repo `--bake-output`, staged-module exclusion, OIDC publish via the canonical `publish-bake-bundles.sh` |
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
`github-actions-bake` (RG `memex-aks-rg`) holds *Storage File Data Privileged Contributor* on the
portals' storage account and carries 8 federated credentials — the four satellite repos × the two
GitHub subject formats (classic and immutable; **register both, always** — see
[The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract)) — with the
`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` secrets set on all four repos.
**A red publish-bake was designed debt until 2026-08-17 and is a real failure after it** —
treat any surviving allowlist or "credentials pending" reference to a satellite's publish lane
as historical. The framework-release dispatch that fans a platform release out to the satellites is
still dormant (`BAKE_SUBSCRIBER_REPOS` / `DEPENDENT_DISPATCH_TOKEN` unprovisioned); until it is
armed, a satellite re-bakes on its own `main` pushes only.

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
- **An arm64 install adopts nothing the amd64 lane publishes** — the two architectures of one image
  resolve different identities (see the identity rule above). Local arm64 installs compile at boot
  as they always have; nothing may paper over this by publishing the same bundles twice.

See also: [Plugin Packaging](/Doc/Architecture/PluginPackaging) (the compilation-unit rules and the
MVID rationale) · [Build Coordination](/Doc/Architecture/BuildCoordination) (who bakes when several
replicas boot) · [Modules](/Doc/Architecture/Modules) · [Release Availability
Gates](/Doc/Architecture/ReleaseGates) (who READS these publications, and the
`_releases/<version>` marker that names each release's identity).
