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
- **Staged cross-repo modules are excluded from publication** (e.g. Store is staged so
  `requires` resolve but is owned and published by MeshWeaver.Plugins) — each source directory
  seals independently, which is also why no cross-repo bake ORDERING is needed: a dependent
  repo's publication never contains its dependency's bundles, so there is nothing to wait for.
  The framework-release dispatch fans out to all satellites concurrently.

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
replicas boot) · [Modules](/Doc/Architecture/Modules).
