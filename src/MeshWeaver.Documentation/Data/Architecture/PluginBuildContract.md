---
nodeType: Markdown
name: The Plugin Build Contract
category: Architecture
description: Four steps — take the image, install dependencies, build your plugin, test it. What each step means, why "install" must never be a build, and the measured state of every plugin repo against it.
icon: /static/NodeTypeIcons/box.svg
---

# The Plugin Build Contract

Building a plugin is four steps, and they are plain:

1. **Take the MeshWeaver image.**
2. **Install all my dependencies** — as *built artifacts*, served by MeshWeaver.
3. **Build my own plugin.**
4. **Test it.**

Everything on this page follows from one rule inside that list: **step 2 is an INSTALL, never a
build.** A repo that compiles its dependencies has not installed them — it has rebuilt somebody
else's product, once per mesh, on every run, and it is testing bytes nobody will ever ship.

---

## One thing builds and tests

The tool is **`mw-plugin-test`**, shipped in the `mw-plugin-test` image. It is not a gate that
happens to compile; it is the producer *and* the consumer, split deliberately (#1763):

| flag | role |
|---|---|
| `--bake-output <dir>` | **produce**: compile every NodeType in the mount, emit one bundle per package plus `framework-mvid.txt` |
| `--seed <dir>` | **consume**: read a bake *before the mesh boots*, and stand the mesh up on **those bytes** |

`BakeSeed`'s own summary states why the split matters:

> The producer emits assemblies with no mesh; the gate stands one up and proves the **BAKED BYTES**
> render and pass their `Tests` areas — which is a strictly stronger claim than the fused pass ever
> made, because the bytes it judges are the bytes that ship.

So step 3 and step 4 are the same tool, twice: build once, then test what you built. Nothing else in
a plugin repo should compile in-mesh C#.

### The failure this prevents, and how it hides

`BakeSeed` also names the trap, and it is the reason `--seed` must be wired rather than assumed:

> 🚨 A gate pointed at a bake it cannot consume **silently compiles everything and passes** —
> indistinguishable from a gate that consumed the bake perfectly.

A green gate is therefore not evidence that anything was seeded. Only the seed's own report is.
Consuming a bake is checked **before the mesh boots**, and a problem there is a *usage error*
(exit 2), not a red gate — precisely so "I could not consume this" can never be mistaken for
"I consumed it and all is well".

---

## Measured state, 2026-08-27

Counting `--seed` and `--bake-output` across every repo:

| repo | produces (`--bake-output`) | **consumes (`--seed`)** |
|---|---|---|
| **MeshWeaver** (core) | 21 | **9** |
| MeshWeaver.Plugins | 1 | **0** |
| MeshWeaver.SocialMedia | 1 | **0** |
| MeshWeaver.Reinsurance | 1 | **0** |
| MeshWeaver.Education | 2 | **0** |
| MeshWeaver.Manufacturing | *(unmeasured — API rate limit; not to be read as zero)* | |

**Core uses the split. Not one plugin repo does.** Every plugin repo produces a bake and then throws
it away, standing its mesh up on source and compiling the world instead.

What they do at step 2 today, verbatim from their workflows:

- **SocialMedia**, **Reinsurance** — `stage-repo`: the required modules are sparse-checked-out and
  copied into the gate mount **as source**.
- **Education** — *"Stage the required Plugins modules (cross-repo requires of the courses)"*, then
  `build-package-repo.mjs <out> <plugins-checkout> <education-checkout>` fuses both checkouts into
  ONE package repo, which the mesh installs and Roslyn-compiles end to end.
- **Plugins**, **Manufacturing** — nothing to stage (Plugins *is* the dependency source).

None of them reads a built artifact. The package source is `GitPackageSource` — a local git tree —
everywhere.

---

## What it costs, measured

Education's disposable-mesh e2e boots **five meshes per run**, and each one compiles `Store` (17
NodeType assemblies), `Edu` (8), `Publish`, `Video` and every course type from scratch. Those are
someone else's already-built packages.

On 2026-08-27, shard 1 of that suite showed the whole chain in one log:

```
06:33:38  prebuilt bundles: 37 in /home/runner/work/_temp/prebuilt
06:38:40  Store surface: "⏳ Compiling… — Catalog Running Roslyn against 9 source binaries"
06:51:14  Install: prebuilt adoption attempt failed — the installed types compile instead
06:51:14  ShippedPrebuiltBundles: 27 prebuilt assembly(ies) from 37 shipped bundle(s) …
          0 adopted now, 27 already current
```

Read the order: **Store began compiling thirteen minutes before anything tried to hand it a prebuilt
binary.** The bundle existed the whole time — `Store.zip`, 17 assemblies — and the framework identity
matched *exactly* (`s26852fcf0ddfd18497201fb723505c4b` on both the bake and the portal, `DECLINED: 0`).

The downstream symptom is a test failure that names none of this: `the cover must offer the install
step`. The Store cover's CTA is `PluginLayoutAreas.InstallCtaArea` — a plugin area, correctly
authored — and it cannot render because its own type is still in Roslyn.

> **A plugin's UI is only as live as its assembly. Starve the assembly and the symptom appears in the
> UI, three layers from the cause.**

---

## Why mounting bundles is not enough

Bundles reach a mesh through two lanes, and **neither serves a package that is not installed yet**:

- **Boot lane** — `ShippedPrebuiltBundles.SeedAll`, from `DynamicTypePreWarmerHostedService`. It
  filters each bundle to **the NodeTypes the mesh already holds**. On a fresh mesh that is a handful
  (the platform's own `Doc` content). `Store`, `Edu` and every course type arrive *later*, through
  the install — so their bundles are skipped, and **zero adopted is the correct reading**, not a
  failure.
- **Install lane** — `PackageInstaller.SeedPrebuiltAssemblies`, the only lane that can serve a
  package as it arrives.

So the install lane is load-bearing, and it carries a guard that inverts its own purpose:

```csharp
return consumer.SeedForTypes(nodeTypePaths)
    .Take(1)
    .Timeout(TimeSpan.FromSeconds(60))
    .Catch<int, Exception>(ex => {
        logger?.LogWarning(ex, "Install: prebuilt adoption attempt failed — the installed types compile instead");
        return Observable.Return(0);
    })
```

🚨 **The cap does not save time; it duplicates work.** It abandons the *result*, not the *work* — the
seed runs on and reports its coverage afterwards, as the log above shows. Meanwhile the install has
already fallen back to compiling the very types the seed was about to deliver. A timeout on an
operation that is by construction **cheaper than its own fallback** can only ever make things worse:
when it fires, the system does both.

That is the same inversion `DisposeHubsReactive` removed one layer down (#1317) — *a join must not
out-run the answer it is joining*. A bound belongs here as a **stall detector**, not as a duration
cap shorter than the alternative it guards.

The observable consequence is variance that looks like flakiness: across one run of five meshes, on
identical bundles and identical pins, coverage read **17 · 19 · 19 · 17 · 0**. That is not the
seeding being unreliable — it is how far each mesh got before a 60-second stopwatch fired.

---

## Where the build is served from

Step 2 says *served by MeshWeaver*, and that is the half still missing.

- **Compiled .NET modules** (`MeshWeaver.*.dll`) already have this lane: `Plugin Catalog CI`'s
  `modules` job packs each module and POSTs it to `/api/plugins/bundles` on the registry
  (`memex.meshweaver.cloud`). That works.
- **NodeType assemblies** — the Roslyn output of in-mesh `Source/` — are a *different artifact*, baked
  to the portals' storage as `prebuilt-bundles/<identity>/<source>/*.zip`, sealed with `_complete`.
- **No plugin repo fetches either one.** The consumer code exists — `RegistryPackageSource` and
  `PluginBundleClient` in `MeshWeaver.PluginCatalog` speak `/api/plugins/bundles` — but nothing in
  a plugin repo's CI calls it: the gates read `GitPackageSource`, a local git tree, and the e2e
  meshes fuse checkouts into one. (An earlier draft of this page attributed the gap to core #805
  removing the catalog UI; #805 is about space write access, and the consumer was never removed.
  The gap is that it is not *wired*, which is a smaller and more fixable claim.)

So today the artifacts exist, the identities match, the consumer exists, and no plugin repo's step
2 is wired to *fetch* rather than *rebuild*.

🚨 **And the gate cannot fetch on a pull request until a credential exists for it.** The OIDC
identity that reads the portals' storage (`github-actions-bake`) holds one federated credential
per satellite, every one scoped to `ref:refs/heads/main` — correct for publish-bake, which runs on
main. A gate on a PR presents the subject `repo:Systemorph@<org>/<Repo>@<id>:pull_request`, and
Entra answers `AADSTS700213: No matching federated identity record`. Measured 2026-08-27 on the
first PR to wire `upstream-seed`. The fix is a credential per repo for the `pull_request` subject
(both subject formats, as the existing `main` pair) — a system change that belongs to a
maintainer, not to a workflow edit.

**The target:** a plugin's build is published to MeshWeaver, and every consumer — another plugin
repo's gate, a disposable e2e mesh, a production portal — **installs it as a plugin at image boot**,
the same way any other plugin arrives. Not staged as source. Not recompiled.

---

## The contract, restated as checks

For any plugin repo, these are answerable and should be answered:

| step | the check |
|---|---|
| 1. Take the image | the image is pinned by **digest**, not a moving tag |
| 2. Install dependencies | the gate passes `--seed`, and the seed **reports** what it covered — a green gate alone proves nothing |
| 3. Build my plugin | exactly one `--bake-output` run, over this repo's own packages |
| 4. Test | the gate stands up on the **baked bytes**, so what is tested is what ships |

A repo that cannot answer (2) with a number is compiling its dependencies, whatever its workflow
says.

---

## See also

- [CI Content Bake](/Doc/Architecture/CiContentBake) — the producer, and how the image ships what it bakes
- [NodeType Compilation](/Doc/Architecture/NodeTypeCompilation) — what the runtime compile actually does
- [Build Server](/Doc/Architecture/BuildServer) — image-only CI and the dependency cascade
- [Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — how a build reaches an instance
