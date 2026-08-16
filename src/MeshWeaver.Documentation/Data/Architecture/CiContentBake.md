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
  framework MVID, source-version provenance) plus each compiled assembly and its symbols;
- `framework-mvid.txt` — the framework identity every bundle in the directory is keyed to.

Only types that reached `CompilationStatus.Ok` contribute. A type the gate's known-debt allowlist
tolerates simply has no entry — the consumer compiles it as it would have anyway. A type that
*claims* Ok while the run's assembly store has no bytes for it faults the run: an artifact stage
that ships less than the verdict claims would be the skip-trapdoor shape CI forbids.

The workflow uploads the directories as **`baked-assemblies-<mvid>`** (Doc + samples) and
**`baked-plugins-<mvid>`** (vital plugin modules), and fails RED when a green gate produced no bake
identity.

## The identity rule: adoptable when the SURFACE is unchanged

Adoption is gated by `PrebuiltAssemblySeeder.DeclineReason` on the **framework build identity**
(`NodeTypeCompilationHelpers.FrameworkVersion` / `FrameworkBuildIdentity` — #1660 WS3). For the
hosts that matter here — the bake host and the portals, which both ship a
`meshweaver-surface.manifest` — that identity is the **API-surface hash** `s<hash>`: per compile
reference, the SHA-256 of its *reference assembly* (the compiler's own definition of the API
surface — byte-stable under body-only and private-member edits, changed by any surface change),
hashed over the canonical content-surface set, with the generator-bearing exception
(`MeshWeaver.Graph`, whose code shapes the *generated input* of every NodeType compile)
contributing its full implementation MVID.

Three consequences:

- **a bundle is adoptable across CI runs, images, and internal-only merges** — the bake for
  commit X seeds at boot on the image of commit Y whenever nothing in the content-facing surface
  changed between them ("rebuild only when we need to");
- **a breaking surface change (or any Graph change) mints a new identity** — every cached and
  published build for the old surface is stale, and the next Build-and-Test run bakes fresh;
- **a declined bundle costs exactly what today costs — a compile.** Shipping bundles is strictly
  safe; declines are logged with both identities.

Manifest-less CI processes (test hosts) fall back to the commit identity `g<sha>` stamped by
`Directory.Build.props`; local builds fall back to the Graph MVID. The commit stamp doubles as
provenance everywhere.

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

## The delivery: main-cd publishes, boot seeds

Since #1660 WS3, `main-cd`'s **`publish-bake`** job downloads the Build-and-Test run's
`baked-assemblies-*` artifact for the promoted commit and copies the bundles to the portals'
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

## What this step does not do yet

- **DB-resident types** (user/partition content CI cannot see) stay on the runtime bake.
- Test lanes do not consume the artifact yet — it is named stably (`baked-assemblies-<identity>`)
  precisely so they can start.

See also: [Plugin Packaging](/Doc/Architecture/PluginPackaging) (the compilation-unit rules and the
MVID rationale) · [Build Coordination](/Doc/Architecture/BuildCoordination) (who bakes when several
replicas boot) · [Modules](/Doc/Architecture/Modules).
