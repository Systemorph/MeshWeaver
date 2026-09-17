---
nodeType: Markdown
name: Module Closure Accounting
category: Architecture
description: What a module bundle must carry and what it may leave to its host — the one rule, why "the reference image has it" is not that rule, and the two fleet-wide outages that came from getting it wrong.
icon: /static/NodeTypeIcons/code.svg
---

# Module Closure Accounting

A module reaches a mesh as **bytes**. Whatever those bytes need at runtime and do not carry, the
host must supply — so "what rides the bundle" is a claim about **every** host the bundle may land
in, not about the one that happened to build it. Getting that claim wrong has now cost two
fleet-wide outages, and both times it looked like the *consumer's* fault from the consumer's side.

## The rule

> **A module bundle carries the transitive closure of its own package references, minus the shared
> framework. The shared framework is the only sound omission.**

The shared framework (`Microsoft.NETCore.App`, `Microsoft.AspNetCore.App`) travels with every host
that can load the module at all — a host without it cannot run the platform either. Everything
else is a composition detail of one particular host, and a bundle that bets on it is a bundle that
works exactly where it was built.

Two corollaries, both deliberate:

* **A diamond RIDES.** A package reachable from the module's own references *and* from the
  platform's is bundled anyway. `Assembly.LoadFrom` resolves the app closure FIRST and only probes
  the module directory for what the app does not have, so a duplicate costs bytes while an omission
  costs a type load. Excluding diamonds instead couples every landed bundle to the platform's
  transitive dependency whims: the platform sheds a dependency — the very point of the module
  split — and every landed module that relied on it breaks until re-packed.
* **`MeshWeaver.*` never rides.** Platform assemblies reach a module as `ProjectReference`s and
  bind by a strictly synchronised `AssemblyVersion`; a bundled copy is the same-identity trap
  (MeshWeaver#143). The deliberate exception is a **module-owned** `MeshWeaver.*` sibling — one
  whose source lives in the module's own repo and is therefore nowhere in `/app` — which rides,
  **and whose own package closure rides with it**.

## Why `/app` is not the boundary

The unified build compiles a module **inside the platform image**, and its `/app` is the reference
set ([Module Build Architecture](../ModuleBuildArchitecture)). That is right for the *compile*: a
module binds the assemblies of the image it is loaded into. It is wrong for the *bundle*, and the
difference is not a nuance:

**the reference image is a PORTAL, and a portal with a module compiled into it carries that
module's private package dependencies.**

Measured on `memex-portal-ai` (2026-09-01) — 334 assemblies in `/app`:

```
/app/MeshWeaver.AI.dll
/app/Microsoft.Agents.AI.dll
/app/Microsoft.Agents.AI.Abstractions.dll
```

`Microsoft.Agents.AI` is referenced by **no platform project at all**; it is in that image only
because `MeshWeaver.AI` is built into it. The tester image from the same promoted wave carries 100
assemblies and none of them. So a builder that reads "`/app` has the file" as "the platform
supplies it" concludes that the AI module's own SDK need not ride — and the published manifest says
exactly that:

```json
"module": {
  "assemblyName": "MeshWeaver.AI",
  "assemblies": ["MeshWeaver.AI.dll", "MeshWeaver.AI.pdb", "MeshWeaver.Markdown.Collaboration.dll"]
}
```

Three files, no third-party closure. It loads on that one portal and throws everywhere else:

```
idempotence: re-install failed: ReflectionTypeLoadException: Unable to load one or more of the
requested types.
Could not load file or assembly 'Microsoft.Agents.AI, Version=1.17.0.0, …'
```

## The same boundary, read the other way round: what a NodeType bake COMPILES against

The compile rule ("the image is authoritative") has a mirror image that cost a release wave on
2026-09-02 (#3022): the NodeType bake and gate compiled against the **tester** image's `/app`, and
the tester's `/app` is a strict **subset** of the portal's — 88 vs 219 assemblies on
`3.0.0-rc9.ci.7534`, with 21 `MeshWeaver.*` assemblies (`Maps`, `AI`, `ContentCollections.Indexing`,
the Blazor and hosting halves) present only in the portal. Content binding one of them compiles in
every portal and failed the bake with a CS0234 naming the content. Since then both lanes compile
against the **portal** image's `/app` and its implementation shared frameworks, from a host
composed of the portal's `/app` plus the tester CLI, and a reference-set gap is named in the
verdict — see [Module Build Architecture](../ModuleBuildArchitecture) → "The NodeType bake and its
gate run AS the platform image too". The two rules are the same rule: **what is compiled binds the
host it is loaded into; what is bundled carries its own closure.**

## The blast radius is the consumer's trunk, not the producer's gate

That is the reason this page exists rather than a comment on a workflow. The bundle is published
from one repo and **installed** in others, so an incomplete closure surfaces as a red gate in a
repo that changed nothing:

| Where | What it looked like |
|---|---|
| The producer's own bake-publish lane | `GATE FAILED — idempotence: Store, Feedback, Hosting, Providers, RemoteControl, RolePlay, Voice, Essentials` — 8 packages "re-bake differently" |
| A satellite's `main` (MeshWeaver.Reinsurance) | `[FAIL] Store … idempotence: re-install failed` — its own trunk red for ~11 h, every other job green |

On the satellite's failing run the ONLY failing job was `publish-bake`; `compile-check`,
`test-repos` and the e2e enumeration all passed. Every gate that could see the repo's own content
was green, and only the job installing the *external* bundle failed. **When one repo's gate fails
on a bundle another repo publishes, look at the publisher's closure before looking at the
consumer.**

## Where the accounting lives

One producer, one reader:

1. **`PrivateClosure.Derive`** (in the builder) walks the module's declared `PackageReference`s over
   the dependency records of the image's `/app` deps.json and the module-libraries shelf. For every
   assembly it reaches: `MeshWeaver.*` is skipped, a shared-framework name is *recorded as
   omitted*, and everything else must resolve to a file — the image's copy first (those are the
   exact bytes the compile bound against), the shelf otherwise. An assembly neither source has is
   reported, never silently dropped.
2. **The union is taken over the module's in-tree graph**, not over the entry project alone. A
   module-owned sibling rides the bundle, so its private dependencies ride with it — the same
   omission one hop further out. (The SDK path got this for free: `dotnet publish` materialises the
   whole project graph's package assets.)
3. **`module-libs.txt`**, beside the built module, is the written provenance. The pack lane turns
   each line into a `--with`, and the bundle inspection refuses any non-`MeshWeaver.*` assembly the
   manifest does not name — so a bundle can never carry something the builder cannot account for.

The SDK lane's `--deps-closure` derives the same set from a publish folder. **The two lanes agree
by construction, and that is the point**: converting a module from `sdk` to `container` must not
change what its bundle contains.

## 🚨 What the closure deliberately does NOT carry — and the RID the finding forgets

Two shapes a flat closure cannot express are reported rather than dropped, in one spelling shared by
both lanes (`UncarriedAssetFindings`), because the arming decision for the container lane is a
**grep across a full wave** and two lanes wording one finding two ways would let a wave read clean on
one while the other said it in words nobody searched for:

| finding | what it means |
|---|---|
| `declares a RID-specific MANAGED asset the bundle does not carry` | the package declares `assetType: "runtime"` under `runtimeTargets`; the flat closure has one slot per assembly name and no way to choose a RID at pack time |
| `declares a native asset at '…', which is NOT the layout the module loader probes` | a native at anything but exactly `runtimes/<rid>/native/<file>` — bytes at a path nothing looks at read as shipped and behave as absent |

**Neither finding is scoped to the RID the bundle actually targets**, and that is the whole reason
the container lane's refusal is still unarmed (#4445).

### The measurement — scheduled full waves, 2026-09-17

| repo | run | modules with a derived closure | unprobed native | RID-specific managed |
|---|---|---:|---:|---:|
| MeshWeaver.Plugins | `35051691749`, job `104654803583` | **38** | 0 | **6** |
| MeshWeaver.SocialMedia | `35178266255`, job `105065263652` (pin `3.0.0-ci.8767`) | 1 (7 closure lines) | 0 | 0 |

All six are the **same package and the same file** —
`System.Security.Cryptography.ProtectedData`, at
`runtimes/win/lib/netstandard2.0/System.Security.Cryptography.ProtectedData.dll` — reached through
six modules: `MeshWeaver.Blazor`, `MeshWeaver.Azure.Blob`, `MeshWeaver.SelfUpdate.Aks`,
`MeshWeaver.AI.AzureFoundry`, `MeshWeaver.AI.OpenAI`, `MeshWeaver.Mail.MicrosoftGraph`.

The only RID paths anywhere in that wave's log are those six `runtimes/win/` reports and the
`runtimes/linux-x64/` assets the bundles **do** carry. So the flagged file is the Windows
implementation of a package whose cross-platform façade under `lib/netstandard2.0/` is carried
already, in bundles that only ever load inside a Linux container.

### Why that blocks arming, rather than being six things to fix

Omitting a Windows implementation from a Linux-only bundle is the **correct** outcome, not a drop.
Arming the refusal against today's rule would red six modules over an asset whose absence is right.

And the remedy #4445 prescribes for a victim — *move the module to `"build": "sdk"` and declare the
carrier* — cannot lift these: the SDK lane derives the finding from the same unscoped rule, so it
refuses the same file, and there is no carrier to name because a flat closure has neither a slot for
a foreign-RID copy nor any need for one.

**So the criterion has to be corrected before it can be met:** a RID-specific asset for a RID the
bundle does not target is not an uncarried asset, and both lanes should say so — which needs a
target RID to compare against, a value neither derivation is given today. That is a design change,
deliberately not smuggled in with a bug fix.

## What this is not

* **Not an allow-list.** Naming `Microsoft.Agents.AI` somewhere to quiet a gate leaves the bundle
  incomplete and moves the failure to whoever installs it next.
* **Not "the tester image should carry what the portal image carries".** That narrows one gate's
  divergence and still publishes a bundle nobody else can install.
* **Not "the compose step tops up from the portal image".** That fixes only the composers who
  remember to, and a bundle fetched straight from the registry has no compose step.

## The check that would have caught it

An assertion that a bundle's manifest lists more than its entry DLL is not enough — three files is
a plausible-looking manifest. The property is comparative: **the same module, packed by the SDK
lane and by the container lane, must declare the same non-framework closure.** Anything else is a
lane divergence pretending to be a build decision.

See also: [Module Build Architecture](../ModuleBuildArchitecture) ·
[Module-Owned Siblings Ride](../ModuleOwnedSiblingsRide) ·
[Module Versioning](../ModuleVersioning) · [Plugin Build Contract](../PluginBuildContract) ·
[NodeType Compilation](../NodeTypeCompilation)
