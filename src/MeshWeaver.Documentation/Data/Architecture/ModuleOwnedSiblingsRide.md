---
Name: Module-Owned Siblings Ride
Category: Architecture
Description: The decision behind #3221 — a module-owned MeshWeaver.* sibling that another package DECLARES as its module still rides into a second bundle, and the invariant that closes the double-production hazard is byte equality, not exclusivity.
Icon: /static/NodeTypeIcons/code.svg
---

# Module-Owned Siblings Ride

A module-owned `MeshWeaver.*` sibling **rides** every bundle that references it
([Module Closure Accounting](../ModuleClosureAccounting)). Some of those siblings are *themselves*
another package's declared module. That makes one assembly name reach a mesh from several bundles at
once — two module-side producers of one name — which is the shape that
[#3175](../ModuleBuildArchitecture) exists to be afraid of.

This page records the decision (**it rides**), the evidence, and the invariant that replaces the
exclusivity the question proposed.

## The question

> Should a sibling that is **itself another package's declared module** ride into a second bundle,
> or should the closure rule exclude declared modules and let the landing resolve them from their
> owning package?

`BakeHost.ShippedByHostProblem` already refuses a module composed with `--module` that the *platform
host* also ships. It does not look at whether a *second module bundle* carries a copy of the same
assembly name. The proposed extension — refuse a bundle carrying an assembly another package
declares as its module — is the same shape one hop further out.

## The decision: it rides

**The closure rule is unchanged.** A module-owned sibling rides, whether or not another package
declares it. The refusing guard is **not** built, and the reason is not caution — it is that the
rule it would enforce is unsatisfiable.

### 1. It is the dominant shape, not an exception

Measured over `MeshWeaver.Plugins` `main` on 2026-09-03 — every package's `index.json`
`content.module`, joined against each module project's transitive in-repo `ProjectReference` closure
minus `src/platform-shipped.txt`:

| | |
|---|---|
| Declared modules in the repo | **37** |
| Module bundles riding at least one *other package's declared module* | **19** |
| Bundles riding `MeshWeaver.Markdown.Collaboration` (declared by **Essentials**) | 14 |
| Bundles riding `MeshWeaver.AI` (declared by **AI**) | 12 |
| Bundles riding `MeshWeaver.Maps` (declared by **Maps**) | 4 |

The issue named one instance (`Chat` riding `MeshWeaver.AI`) as a "pre-existing accepted pattern".
It is more than half the fleet's modules.

### 2. Excluding declared modules inverts the package graph

If a declared module may not ride, the landing must resolve it from its owning package — so the
riding package must **depend** on the owning one. It does not, and cannot:

```
AI/index.json         requires: [Store]                    module: MeshWeaver.AI
Essentials/index.json requires: [AI, Store, Export, …]     module: MeshWeaver.Markdown.Collaboration
```

`MeshWeaver.AI` references `MeshWeaver.Markdown.Collaboration`, which **Essentials** declares — and
Essentials `requires` AI. Making AI resolve the sibling from Essentials is a cycle. The same
inversion holds for `Mcp`, `Mail`, `Teams`, `Notifications`, `Observability` and every other package
Essentials requires: each rides an assembly its own dependent declares.

### 3. It would break a supported install

`AI` is installable on its own (`requires: [Store]`). With the sibling excluded, that bundle lands
without `MeshWeaver.Markdown.Collaboration.dll` and nothing supplies it — the platform image does
not (Plugins#1268 removed it from `/app`) and no required package brings it. The result is the
`ReflectionTypeLoadException` on first touch that
[Module Closure Accounting](../ModuleClosureAccounting) was written after: *"Could not load file or
assembly …"*, a red trunk in a repo that changed nothing, for eleven hours.

### 4. The host case and the module case are not the same defect

`ShippedByHostProblem` refuses host-vs-module because the two provenances use **incompatible id
schemes**. The host's copy resolves from the surface manifest as `ref:<hash>`; a composed module
resolves as `mvid:<guid>`. Those never compare equal *even for byte-identical assemblies*, so the
conflict is unfixable by agreement — one producer must go.

Module-vs-module is `mvid:` on **both** sides. Identical bytes compare **equal**. The conflict is
fixable by agreement, and agreement is cheaper than exclusivity.

## The invariant that replaces it

> **One assembly name, one framework identity, one build — across every copy in the sealed set,
> declared or riding.**

The hazard is real and worth an assertion. `MeshWeaver.*` assemblies bind by a strictly synchronised
`AssemblyVersion` (see [Module Closure Accounting](../ModuleClosureAccounting) → "the same-identity
trap"), so two copies under one simple name are **one assembly identity**: `Assembly.LoadFrom` returns
the already-loaded one and ignores the second path. Whichever copy loads first wins the process, and
the loser's bytes are never in memory.

What that costs when the copies differ is exactly the `#3175` incident:
`NodeTypeCompilationHelpers.ModuleMvidsOf` reports the MVID of the assembly that actually loaded, so
every NodeType whose dependency record named the other build is declined at adoption —
*"dependency record mismatch — 'MeshWeaver.Markdown.Collaboration' built against mvid:A, live is
mvid:B"* — and the view renders empty.

Today the copies agree by **accident**: `bake-scope.sh` classifies any change under `src/` as
affecting ALL modules, and `module-build-key.py` folds each entry's whole in-repo `ProjectReference`
closure into its content address, so a sibling's change re-keys every bundle that carries it. Both
are correct and neither is the assertion — they are two mechanisms that happen to preserve the
property. The assertion is:

**`PublishedBundleCatalogue.ArtifactsForIdentity` reads the MVID of every `MeshWeaver.*` assembly
each sealed module bundle carries — entry and riding sibling alike — and any name carried at two
MVIDs becomes a `SealedModuleSet.Conflicts` line naming both producers, their sources, their bundles
and the ROLE of each copy.** `ReleaseAvailability.IsUpdatable` then answers
`PackageAvailabilityKind.SealedSetInconsistent` for every package whose records bind that name: a
HOLD, not a roll. That is the maintainer's requirement — *"we must have a clear confirmation that all
plugins deployed to an instance are available for the correct platform version. If not the case ⇒
nothing goes"* — applied to the copies as well as the declarations.

Two boundaries are deliberate:

* **Only the DECLARED entry defines `MvidByModule`.** An instance registers declared modules as
  `InstalledModuleAssembly`, and that registration is what `ModuleMvidsOf` reports back as "live". An
  assembly that only ever rides is registered nowhere, so letting it define the set would judge
  records against bytes the instance never reports — a false HOLD, which
  [#1754](../ReleaseGates)'s `ReleaseGateApplicabilityTest` already records the cost of.
* **Only `MeshWeaver.*` names are judged.** A third-party diamond rides by design and versions
  independently; it does not collapse to one identity. Judging it would hold every bundle in the
  fleet for a property that was never claimed.

## What this does not close

The **second producer in time** is untouched: an instance that installed a module from the registry's
content-versioned package endpoint holds whatever *that* lane published last, while the sealed
publication carries the bytes the platform release rebuilt. Both sets can be internally consistent
and still disagree with each other. See
[Module Build Architecture](../ModuleBuildArchitecture) → "What the gate still cannot see" for the
two ways to close it (the seal composes the registry's bytes, or the instance adopts module bytes
for its identity from the sealed publication).

See also: [Module Closure Accounting](../ModuleClosureAccounting) ·
[Module Build Architecture](../ModuleBuildArchitecture) ·
[Candidate Release Protocol](../CandidateReleaseProtocol) · [Release Gates](../ReleaseGates)
