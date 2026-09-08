---
nodeType: Markdown
name: The Release Gate's Denominator
category: Architecture
description: Why the deployment gate's "which packages must be baked" set may never be read from the artifact store it is about to judge — the vacuous-denominator defect that let the fleet roll onto releases Education had not baked.
icon: "<svg viewBox='0 0 24 24' xmlns='http://www.w3.org/2000/svg'><rect width='24' height='24' rx='4' fill='#4a148c'/><path d='M5 8h14M5 16h14' stroke='white' stroke-width='1.8' stroke-linecap='round'/><circle cx='12' cy='12' r='9' fill='none' stroke='white' stroke-width='1.6'/></svg>"
---

# The Release Gate's Denominator

> 🚨 **Rule change, 2026-09-07 (maintainer) — see [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy).** A package that stops shipping content no longer holds a release forever: a missing bake is advisory ("would compile at boot"). The mechanism described below is what runs until [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651) lands; this page is rewritten by that change.

> Maintainer, 2026-09-06: *"let's see that the deploy rolls only when **all** packages of all
> plugins are baked"* — *"we kept rolling without edu being properly baked."*

The [release availability gate](../ReleaseGates) already asked the right question and already held
the roll when the answer was no. It nonetheless let the fleet roll onto releases whose Education
content was not baked, and it did so while reporting a clean pass. This page is why, and what the
rule is now.

> 🚨 **What the denominator DECIDES changed in MeshWeaver#3651; what it SEES did not.** Since the
> maintainer's rule of 2026-09-07 (the Module Adoption Policy page (`Doc/Architecture/ModuleAdoptionPolicy`)) a course with
> no bake for the target is a **cost the verdict names** — *"would recompile at boot on …:
> AgenticPrimer, …"* (`UpdatabilityVerdict.BootCompiles`) — and the roll proceeds; the compile is
> the code path every pull request of that content already proved green, and holding on it is what
> would have kept every portal on the morning build a second day. The hold survives only under the
> opt-in `Modules:RequirePrebuilt`, where the seeder refuses a boot compile and the type would park.
> Everything below about *where the expected set comes from* is unchanged and still load-bearing:
> a denominator that erodes names nothing, and "0 expected, 0 named" is the same vacuity whether the
> verdict then holds or proceeds.

## A completeness gate is only as good as its denominator

Every completeness check is a comparison of two sets: what was **expected**, and what was
**found**. The found set is easy — it is the thing you are looking at. The expected set is the
whole difficulty, because the obvious place to get it from is the same artifact you are judging,
and a denominator taken from there **cannot fail**:

> **0 packages expected, 0 packages baked, green.**

That is the same shape as a skipped job wearing a passing tick, and this repo forbids it in
[CI gates](../ReadingCiSignals) for exactly this reason. The deployment gate had it in the runtime.

> 🚨 **The limiting case is now refused outright** (#3479). Fixing where `HasContent` comes from made
> the denominator independent of the artifact; it did not stop the denominator being *empty*. An
> install-record read that answers zero on a deployment whose root serves bakes still compared two
> empty sets and passed — and that read has measurably answered zero on a live portal carrying 42
> modules (memex, 2026-08-10). The verdict now HOLDS on it, naming the two possible causes, and
> [Roll Selection](../RollSelection) refuses to select on it. Zero expected is never green.

## The defect

`ReleaseAvailabilityService.RequiredPackages` built the gate's input list from the environment's
**install records** — a mesh query, genuinely independent, and correct. Then it decided, per
package, whether that package must carry a baked bundle:

```csharp
// BEFORE — the denominator is a function of the thing that breaks
var adoptedToday = PublishedBundleCatalogue.SealedBundlesForIdentity(
    publishedRoot, PrebuiltAssemblySeeder.LiveFrameworkMvid, logger);

new RequiredPackage(manifest.Id, manifest.Id, LiveFloorOf(manifest.MinMeshVersion),
                    HasContent: adoptedToday.Contains(manifest.Id))
```

`HasContent` is the denominator. It was read from the **artifact store**, under the identity the
instance is running now — the same store, one directory over, from the publication the gate is
about to judge.

The consequence is not subtle once stated:

- Education's bake stops producing a sealed publication.
- Education's packages therefore leave `adoptedToday`.
- Leaving `adoptedToday` sets `HasContent: false` — *"this package ships no content"*.
- A package that ships no content cannot have a missing bake. **It is exempt.**
- Every subsequent roll is green about precisely the packages that regressed.

The denominator **erodes**, and it erodes exactly where the gate is needed. A package that is
broken today is exempt tomorrow, and can never re-enter the set on its own.

### The limiting case is worse

If the running identity has **no** sealed publication at all — a fresh storage target, a bake lane
pointed at the wrong share, a `BAKE_PUBLISH_TARGETS` that stopped being written — then
`adoptedToday` is empty, *every* installed package is non-content-bearing, and the content half of
the gate checks **nothing** while answering "updatable". A green tick over zero evidence, with no
line anywhere saying the expected set was empty.

## The rule

> **The denominator is read from every framework identity the published root holds EXCEPT by
> reading the target's own publication — and it is MONOTONE.** What this environment has ever been
> able to adopt, it must still be able to adopt.

`PublishedBundleCatalogue.EverSealedBundles(publishedRoot)` answers it:

```csharp
// AFTER — the denominator comes from the OTHER identities, and never shrinks
var floor = PublishedBundleCatalogue.EverSealedBundles(publishedRoot, logger);

new RequiredPackage(manifest.Id, manifest.Id, LiveFloorOf(manifest.MinMeshVersion),
                    HasContent: floor.Bundles.Contains(manifest.Id))
```

Three properties, and each is load-bearing:

| Property | Why |
|---|---|
| **Independent** | The judgement is about the TARGET identity's directory. The denominator is read from the OTHER identity directories — publications written by earlier CD waves, which the run under judgement cannot have produced. |
| **Monotone** | A package that has once shipped a bake can never silently leave the denominator. A bake that regresses to nothing is a HOLD, not an exemption. |
| **Non-freezing** | A package that has *never* sealed a bundle under *any* identity — a module-only or NodeType-less package, which produces no bundle ever — is still not demanded. The exemption is preserved; only its evidence moved from "one identity's answer today" to "any identity's answer ever". |

### It reads the seal's DECLARATION, not every bundle's bytes

The denominator reads each source's `_complete` sentinel and takes the listing at face value. It
does **not** re-check that every listed bundle is still on disk. That is deliberate in both
directions, and the two reasons agree:

- **Semantics.** *"Has this package ever shipped a bake?"* is answered by the publisher's own
  declaration. Whether the bytes are still present is a question about what can be **adopted now** —
  the numerator — and that full check stays exactly where it decides the verdict, on the target
  identity. Being inclusive here is the safe direction: a torn publication that once listed a
  package keeps that package in the set the gate asks about, which can only **hold** a roll, never
  exempt one.
- **Cost, which is a correctness concern.** The denominator reads *every* identity the root holds,
  and that root is a network share (Azure Files over SMB on AKS). Verifying presence would cost one
  stat per bundle, per source, per identity — thousands of round trips per poll tick once
  identities accumulate — against a verdict bounded at 60 s. Blowing that bound answers
  `Indeterminate`, which **holds**: a denominator expensive enough to time out would freeze every
  environment, turning this gate into the outage it exists to prevent. Reading the sentinel alone is
  one file read per source.

### An ABSENT root is a refusal, not an exemption

An empty floor has two causes that look identical and mean opposite things:

| Cause | Answer |
|---|---|
| The root **was read** and holds no sealed publication | `NotEnforced` — the one stated applicability exemption |
| The root is **absent or unreadable** (a volume that did not mount, a mistyped path, an IO fault) | **HOLD** — `Indeterminate`, with the reason |

A deployment that configures `PreWarm:PrebuiltBundleRoot` *declares* that it consumes CI bakes, so a
root that is not on disk is an availability incident, never evidence that nothing is published.
`SealedBundleFloor.Refusal` carries that distinction and callers test it **first** — `ServesBakes` is
false for both, which is exactly why the order matters. Collapsing them would let a mis-mounted
volume clear the gate: the same vacuity this page is about, reintroduced one level up. (Caught in
review on the PR that introduced it — the shape is easy to write by accident.)

### "Serves no bakes" is stated, never inferred

`SealedBundleFloor.Identities` counts how many identity directories carried at least one sealed
source. **Zero is the load-bearing value.** It means the root serves no bakes at all, so "package X
has never been sealed" carries no information about X and must not be read as an exemption.

That case now answers `UpdatabilityVerdict.NotEnforced` **with its reason** — updatable, because an
instance that adopts nothing already compiles its content at every boot and holding it forever
would be its own outage, but *said out loud* and logged by the caller rather than passed off as a
verdict that looked at something. It is the one applicability answer; every other unknown remains
`Indeterminate`, which holds.

### The denominator is printed

```
[ReleaseGate] denominator: 13 of 14 installed package(s) must carry a sealed bake,
              taken from 3 framework identity(ies) this root has published: …
```

The count being non-zero **is** the claim, so it is logged rather than inferred from a pass. A
reader who cannot see the expected set cannot tell this gate from a vacuous one.

## Falsification

`ReleaseGateDenominatorTest` reconstructs the incident: an earlier identity where every source
baked, then the live and target identities with Education's publication torn (directory present,
no `_complete` sentinel — a bake that died before sealing, which the boot seeder and the gate both
refuse to read). Installed: Education's nine courses, two platform packages, two plugin packages,
and one module-only package that has never produced a bundle.

Both denominators are computed **on the same fixture, in the same test**, so the negative control
is a measurement rather than a claim:

| Denominator | Verdict (default) | Named as the boot compile | Under `Modules:RequirePrebuilt` |
|---|---|---|---|
| **Old** — sealed under the live identity | `IsUpdatable = true` — the roll proceeds **saying nothing** | 0 | proceeds, saying nothing |
| **New** — ever sealed under any identity | `IsUpdatable = true` — proceeds | 9, each `ContentBakeMissing` + `IsAdvisory`, naming the nine courses | `IsUpdatable = false`, 9 blockers |
| **New**, after Education re-bakes for the target | `IsUpdatable = true` | 0, over 13 content-bearing of 14 installed | proceeds |
| **New**, Education seals 4 of 9 | `IsUpdatable = true` | 5 — exactly the unsealed courses | 5 blockers |

A sixth test pins the declaration reading from both sides: a publication that keeps its seal but
loses a bundle's bytes still counts toward the denominator, while the same source contributes
**nothing** to what can be adopted.

A gate nobody has watched fail is not a gate; the first row is that watch.

## 🚨 What this gate does NOT see

Naming the blind spot is worth more than overclaiming the coverage.

- **A package that has never been sealed under any identity is still exempt.** That is deliberate —
  it is the only thing standing between this gate and a permanently frozen environment — but it
  means a package whose bake has been broken *since before this root ever published* is invisible
  here. The bake lane, not the roll gate, is where that is caught.
- 🚨 **A package that legitimately STOPS shipping content will be NAMED as a boot compile on every
  verdict, and keep being named.** Monotone cuts both ways: once a package has sealed a bundle,
  dropping its last NodeType means it produces no bundle for the target identity and the gate reads
  that as the regression it is designed to catch. Uninstalling the package clears it (the outer set
  is the environment's install records, so a removed package leaves the denominator with it);
  re-baking does not. Under `Modules:RequirePrebuilt` that naming is a hold. This is a deliberate
  trade against the erosion bug, and the direction is chosen on purpose: the old failure was
  **silent** — rolling onto content that was not there — while this one is **loud**, named on the
  policy node and the Updates tab, and re-evaluated every tick. A gate that is visibly wrong can be
  acted on; one that is invisibly wrong cannot.
- **It gates the ROLL, not the BAKE.** It cannot make a bake complete; it can only refuse to roll
  onto an incomplete one. A satellite whose bake is torn still publishes nothing and still needs
  fixing at the source.
- **CD cannot assert satellite bakes at promote time, by construction.** Core's `main-cd` publishes
  the platform's own bake and then asserts availability for `meshweaver-content` only. That is not
  an oversight: the satellites re-bake on the `meshweaver-framework-released` dispatch that this
  very run triggers, so at promote time they *cannot* have baked yet. The completeness question for
  satellites is answerable only later — which is exactly why it is answered at the roll.
- **Content ASSETS are not part of the bundle set.** A package can seal a complete bundle set whose
  `content/**` never published (MeshWeaver#3424). Bundle completeness is assembly completeness.
- **Adoption is not observed here.** A sealed, present, consistent bundle can still be declined at
  install time and compiled in-mesh with no reason logged (MeshWeaver#3429). This gate asserts the
  bytes are *available*; it does not assert they were *used*.
- **Retention.** The denominator is monotone only as long as old identity directories are kept.
  Nothing purges them today. If a retention policy is ever added, it takes this gate's memory with
  it and the erosion returns.

## See also

- [Release Availability Gates](../ReleaseGates) — the predicate, the marker, the three roll paths
- [CI Content Bake](../CiContentBake) — where the sealed bundles and the framework identity come from
- [Reading CI Signals](../ReadingCiSignals) — the same vacuity trap on the CI side
- [Roll Selection](../RollSelection) — the same vacuity trap one level up, where an empty
  denominator would make every release complete and turn the selector back into "take the newest"
- The Module Adoption Policy page (`Doc/Architecture/ModuleAdoptionPolicy`, MeshWeaver#3652) — why a missing bake is a cost and not a hold
- [The Continuous Delivery Contract](../ContinuousDeliveryContract) — the publication this gate reads
