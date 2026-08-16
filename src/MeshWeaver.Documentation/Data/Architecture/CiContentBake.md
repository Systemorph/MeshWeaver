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

## The identity rule: one compile, one MVID

Adoption is gated by `PrebuiltAssemblySeeder.DeclineReason` on the **MeshWeaver.Graph MVID** — a
content identity of the compiled framework, not a version string. A bundle is adoptable only by a
process whose Graph.dll came from the *same compilation* that produced the bundle.

Within one Build-and-Test run that holds everywhere by construction: the solution builds **once**,
and every lane — test shards, doc-gate, plugin-gate — reuses that build's binaries. So the artifacts
are immediately consumable by the run's own lanes.

Across builds it does *not* hold today: the CI run number is a compile input
(`Version` → assembly attributes), so `main-cd`'s image legs — which re-publish from source under
their own run number — mint a *different* Graph MVID than the test workflow's artifact, and the
seeder correctly declines the whole set. That is deliberate ABI safety, not a defect; two things
follow from it:

- until framework-identity determinism lands (#1660 workstream 3), an image can only ship bundles
  **baked in its own publish job**, with the same version inputs;
- a declined bundle costs exactly what today costs — a compile. Shipping bundles is strictly safe.

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

## What this step does not do yet

- **`main-cd` does not consume or produce bundles yet.** The image legs recompile from source, so
  the test workflow's artifact is not adoptable there (identity rule above). The next increment is
  either baking inside the portal-image leg (same job, same MVID) or — after workstream 3 — the
  thin containerize step that consumes the compile stage's publish + bake artifacts directly.
- **DB-resident types** (user/partition content CI cannot see) stay on the runtime bake; that is
  workstream 2's pre-roll bake Job.
- Test lanes do not consume the artifact yet — it is named stably (`baked-assemblies-<mvid>`)
  precisely so they can start.

See also: [Plugin Packaging](/Doc/Architecture/PluginPackaging) (the compilation-unit rules and the
MVID rationale) · [Build Coordination](/Doc/Architecture/BuildCoordination) (who bakes when several
replicas boot) · [Modules](/Doc/Architecture/Modules).
