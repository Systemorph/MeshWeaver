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
[Module Versioning](../ModuleVersioning) · [Plugin Build Contract](../PluginBuildContract) ·
[NodeType Compilation](../NodeTypeCompilation)
