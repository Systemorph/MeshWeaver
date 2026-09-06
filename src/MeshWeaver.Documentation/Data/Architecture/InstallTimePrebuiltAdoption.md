---
Name: Install-Time Prebuilt Adoption
Category: Architecture
Description: The only lane that can hand a package installed AFTER boot the assemblies CI already built for it — and the four answers its zero has to keep apart, because a silent non-adoption is indistinguishable from a successful one.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 16V8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16z"/><path d="m3.3 7 8.7 5 8.7-5"/><path d="M12 22V12"/></svg>
---

# Install-Time Prebuilt Adoption

A NodeType's assembly can arrive two ways: the mesh **compiles** it with Roslyn, or it **adopts**
bytes some producer already compiled against this exact framework identity. Adoption is strictly
cheaper — and on a saturated mesh the difference is the whole budget of a run.

This page is about the lane nobody watches: **adoption for a package that installs AFTER the process
booted.** That is not an edge case. It is the normal case for every consumer of a registry.

## Why the boot lane cannot cover it

`ShippedPrebuiltBundles.SeedAll` / `SeedPublishedRoot` run at boot and enumerate the NodeType nodes
**this mesh holds**. A package installed later brought types that were not in that snapshot, so the
boot pass could not have seeded them even in principle — it filters every bundle entry against a set
those types were absent from.

The lane that CAN serve them is the install itself:

```
PackageInstaller.InstallCore
  └─ SeedPrebuiltAssemblies(hub, packageId, nodeTypePaths, logger)      ← names the package
       └─ IPrebuiltAssemblyConsumer.SeedForTypes(nodeTypePaths)
            └─ ShippedPrebuiltBundles.SeedForTypes(mesh, paths, …)      ← knows the sources
                 ├─ the image's own  prebuilt/            (PreWarm:PrebuiltDirectory)
                 └─ the CI-published <root>/<identity>/   (PreWarm:PrebuiltBundleRoot)
```

The same composition serves the git-push recompile (`NodeTypeRecompileExtensions.ReleaseAffectedNodeTypes`)
and the on-demand compile watcher — all three converge on `SeedForTypes`, which is why the reasoning
below lives there rather than in three copies.

## 🚨 The defect: a zero that says nothing

MeshWeaver#3429, measured on a MeshWeaver.Education e2e run. Forty bundles were mounted at
`/bundles` — including `Edu.zip`, which the bake had adopted **10/10** under the very framework
identity the harness was running.

| time | event |
|---|---|
| 14:17 | `Package 'Store': 188 nodes installed` |
| 14:20 | `bundle Store.zip: adopted 5/5 prebuilt assembly(ies); 12 were already current` |
| 14:32 | `Package 'Edu': 99 nodes installed` |
| 14:32 | in-mesh compiles begin for `Edu/Exercise`, `Edu/Lesson`, … |
| — | **no** adoption line, **no** decline line, **no** reason |

The run's own consumption verdict read `bundles mounted: 40 · assemblies backed by bundles: 17 ·
declined: 0 · in-mesh compiles: 14`. The 17 were Store's. Edu's ten were neither adopted nor
declined — they were never candidates — and Edu's types paid Roslyn on a mesh holding their bytes.

**The bug is not "it did not adopt". The bug is that a failed adoption and a successful one produced
the same log.** `SeedPrebuiltAssemblies` logged only `adopted > 0` and a timeout; `SeedForTypes`
returned quietly on an empty type list; `SeedBundles` logged an absent or empty directory at `Debug`,
which is invisible where it matters (CI and prod both run at `Information`). Every one of those is
the same shape as a CI gate that skips on missing input: *the action not happening* and *the action
succeeding* render identically.

## The four answers a zero must keep apart

`ShippedPrebuiltBundles.DescribeShortfall` is a **pure** function over the pass's own counters. It
emits nothing when coverage is complete, and otherwise names the uncovered type paths plus exactly
one of:

| # | The pass observed | What it means | Level |
|---|---|---|---|
| 1 | no source directory exists | this deployment has no bundle lane; check `PreWarm:PrebuiltDirectory` / `PreWarm:PrebuiltBundleRoot` | `Information` |
| 2 | sources exist, no bundles | CI published nothing **for this framework identity** — the fix is a bake, not a volume | `Information` |
| 3 | bundles read, **no entry named these types** | ← **Edu's shape.** The bake did not cover this package, or the paths it baked differ from the paths the install wrote | `Information` |
| 4 | entries **did** name these types and coverage fell short | the bytes were here and did not land: a per-type decline, a whole-bundle identity decline, a hollow bundle, a fault | `Warning` |

The discriminator between 3 and 4 is `EntriesMatched` — how many bundle entries named a **requested**
NodeType path, counted before anything can decline them. Without it a shortfall line can only guess,
and guessing is what cost #3429 its investigation.

Answer 4 is also where MeshWeaver#3472 lands: a portal adopting bytes stamped for a *different*
framework identity is worse than adopting none, so a wrong-identity adoption and a missing one must
both be distinguishable from success — see [Bake Identity Mismatch](../BakeIdentityMismatch).

### And the install says it too, naming the package

The seeder knows which sources it consulted; it does not know which package asked. So the install
lane emits its own line, unconditionally, on every outcome:

```
Install: Edu: adopted 3 prebuilt assembly(ies) for 10 installed type(s) — …        Information
Install: Edu: adopted NO prebuilt assembly for any of 10 installed type(s) — …      Warning
Install: Edu: the install recognised no NodeType definition among the nodes it wrote  Information
Install: Edu: NO prebuilt assembly was adopted … this host registers no
             IPrebuiltAssemblyConsumer — it consumes no bundle source at all         Warning
```

The third line matters more than it looks: it separates *"no bundle matched"* from *"the install
recognised no types at all"*, which the install's own `N written, M unchanged` summary cannot say.

## 🚨 Registering the consumer is a decision of its own

Until #3429 the **only** way to obtain an `IPrebuiltAssemblyConsumer` was
`AddDynamicTypePreWarming()`, which also registers a hosted service requiring
`IHostApplicationLifetime`. A composition without a generic host therefore had **no consumer**, the
installer's `GetService` answered null, and adoption was skipped — silently, no matter how many
bundles were mounted beside it.

Those are two unrelated decisions, and they are now two calls:

```csharp
services.AddPrebuiltAssemblyConsumption();   // adopt what CI built, at install / push / first access
services.AddDynamicTypePreWarming();         // …and additionally front-load compiles at boot
```

`AddDynamicTypePreWarming` calls the first, so every existing host is unchanged. A host that only
wants adoption no longer has to take the hosted service to get it — and if it takes neither, the
install now says so instead of adopting nothing quietly.

## Reading it in a log

```
grep 'ShippedPrebuiltBundles: adopted no prebuilt assembly for'   # the reason, with the type paths
grep 'Install: .*: adopted NO prebuilt assembly'                  # the package that paid for it
grep 'Install: .*: adopted [0-9]* prebuilt assembly'              # the healthy path, per package
```

`assert-bake-consumption.sh` exists because *adoption is invisible in a gate verdict by construction*
— a NodeType the gate compiled itself renders and runs its `Tests` area exactly like one it adopted.
These lines are the only place the difference is stated.

## What this does NOT cover

**Store MODULES — the .NET assemblies under `/data/modules` — are pinned at process start and never
swapped.** The portal copies what it resolves at boot into `/tmp/meshweaver-pinned-modules/` and
loads from there; a module staged later sits on disk unloaded until the pod restarts. `ls
/data/modules/` says nothing about what runs — only the `[ModuleLoad]` lines do, and
`Modules:AutoRecycleOnStaleBuild` is about in-mesh NodeType builds converging, not about store
assemblies. That lane's gap is a pod restart, by construction, and it is a different lane from this
page's. See [Modules](../Modules) and [Module Build Architecture](../ModuleBuildArchitecture).

## Where it is pinned

- `test/MeshWeaver.Hosting.Test/PrebuiltShortfallSpeaksTest.cs` — the four answers, deterministically,
  with no mesh, no bundle and no disk, plus the level each one carries.
- `test/Memex.Portal.Shared.Test/InstallTimePrebuiltAdoptionTest.cs` — a real monolith mesh, a real
  bundle written with `BundleWriter`, a package installed **after boot**. Two arms: the positive
  control (mounted bundle ⇒ adopted, and the install says so) and the defect (bundle mounted for
  somebody else's types ⇒ the log names the package, the uncovered type and the reason). The control
  arm is not decoration — without it the silence arm would pass just as well against a harness in
  which adoption cannot happen at all.

## See also

- [CI Content Bake](../CiContentBake) — what produces the bundles this lane consumes.
- [Bake Identity Mismatch](../BakeIdentityMismatch) — why a green CD can publish a bake no portal adopts.
- [Node Type Compilation](../NodeTypeCompilation) — what happens to everything adoption did not cover.
- [Rebake Waves](../RebakeWaves) — why a roll rebakes the world anyway, and what one rebake writes.
