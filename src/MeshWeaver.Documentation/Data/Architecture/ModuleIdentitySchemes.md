---
Name: Two Identity Schemes, One Comparison
Category: Architecture
Description: A module bundle states the framework build it was compiled against as a COMMIT identity (g<sha>); a portal resolves its own as an API-SURFACE identity (s<hash>). Comparing the two with ordinal equality answers "different" for every pair that exists, so the store copy of every module the image also ships was declined on every boot — and reported as "landed but not yet loaded, a restart activates them", which two operators acted on for nothing. Measured on memex.systemorph.com, 2026-09-16.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M2 12h4l3 8 4-16 3 8h6"/></svg>
---

# Two identity schemes, one comparison

**A framework build identity is written in one of three schemes, and two of them are in live use on
opposite sides of the same comparison.** A module bundle states what its producer read off the
platform's identity anchor — a `g<sha>` commit identity on every CI lane. A portal resolves what it
compiles content against — an `s<hash>` API-surface identity, hashed from the surface manifest
beside its app. Both are correct answers to different questions, and neither can ever equal the
other.

Until MeshWeaver#4550 the boot loader's identity discriminator ([#4161](../ModuleAdoptionPolicy))
compared them with ordinal equality.

## What that produced, measured

`memex.systemorph.com`, 2026-09-16, image `3.0.0-ci.8710`:

```
/health → pending_module_activation: Degraded — 8 module(s) are landed but not yet loaded in
this process — a restart activates them: MeshWeaver.AI, MeshWeaver.Blazor.Chat,
MeshWeaver.Blazor.EntityViews, MeshWeaver.Blazor.Graph, MeshWeaver.Hosting.Instance,
MeshWeaver.Markdown.Collaboration, MeshWeaver.Markdown.Export, MeshWeaver.Mcp
```

The deployment was restarted at 21:17Z on the same image. The new pods reported the same eight at
21:33Z. The pod's own log said what was really happening, in two lines two seconds apart that
contradicted each other:

```
[ModuleActivation] SKIPPED store-installed module 'MeshWeaver.AI': declined: built for another
platform (framework gce971b2cd3c81912748cb3786490ee2035addd29; this deployment runs
s4b2836629f0c58ad4ed0aa683190981a) — the image's own copy runs instead.

[ModuleLoad] STALE PACK: MeshWeaver.AI is loading /app/modules/MeshWeaver.AI/MeshWeaver.AI.dll
(mvid=30950d3e) while the module store holds a NEWER, DIFFERENT copy at
/data/modules/MeshWeaver.AI@fb7a6c25d4abb0ac/MeshWeaver.AI.dll (mvid=f98aa845). … A baseline
Modules:Assemblies entry is loading the image copy because no usable store-installed entry claims
this name … Re-install the module …
```

Three statements, three different remedies, and **none of them true**:

1. `/health` promised a restart. A restart re-runs the same comparison on the same bytes.
2. `STALE PACK` said no usable store entry claims the name. There is one — enabled, landed, DLL
   present — and boot preferred the image's copy on the identity.
3. It asked for a re-install. Re-installing lands the same bytes, which are declined again.

`gce971b2c…` is a core commit (merged 2026-09-15 19:24Z). `s4b2836…` is a surface hash. The
comparison never had a chance of succeeding: **no module bundle in the fleet states an `s<hash>`
identity, and no portal resolves a `g<sha>` one as its `LiveFrameworkMvid`.** So for every module
the image also ships, the registry lane was structurally dead — the reconcile landed generations
forever and the boot declined them forever.

## The three schemes

| scheme | shape | who states it | what it answers |
|---|---|---|---|
| API surface | `s<32 hex>` | a process with a `meshweaver-surface.manifest` (every portal, the bake host) — `FrameworkBuildIdentity.FrameworkVersion` | "has the surface shipped content compiles against changed?" |
| commit | `g<sha>` | CI's `AssemblyMetadata("MeshWeaverFrameworkIdentity")` stamp — what `FrameworkIdentity.ReadIdentity` returns for a stamped anchor, i.e. every module bundle | "which commit built this platform?" |
| content | 32 hex | the anchor's MVID, when it carries no stamp (local builds) | "which bytes of the toolchain?" |

## The rule

`ModuleFrameworkIdentity.Compare(stated, platformReadings)` is scheme-aware, and a platform states
**more than one reading of itself**:

- `PrebuiltAssemblySeeder.LiveFrameworkMvid` — the surface identity;
- `FrameworkBuildIdentity.ProducerStatedIdentity` — what a packer reading *this* platform's anchor
  would state, which is the scheme every bundle is written in.

| verdict | when | what the boot does |
|---|---|---|
| `Matches` | the stated identity IS one of the readings | the landed generation overrides the image's copy — the ordinary upgrade path (#2548) |
| `Differs` | a reading in the SAME scheme has a different value | the image's copy is preferred, and the reason names both values |
| `NotComparable` | no reading in the stated scheme | the image's copy is preferred, and the reason says *that*, instead of asserting a difference nothing measured |
| `NotStated` | either side stated nothing | decides nothing (rule R2 — absence of evidence) |

**`NotComparable` still prefers the image's copy, deliberately.** Un-declining an uncomparable pack
would reinstate Plugins#1483, where a store pack of `MeshWeaver.Blazor.Views` linked fine, loaded,
and rendered the Subscribe panel's outermost control as a whole-tree `ToString()`. The image's copy
is correct for this platform by construction — it was compiled with it. What changed is that the
state is now *said* correctly, and that a bundle packed by this platform's own build can finally
say so and win.

## The seventh activation state

`ModuleActivationReport` now carries `Declined`, beside pending / unresolvable / deferred /
quarantined / fallback / refused / tier-refused. A declined generation is **not pending**: its entry
is enabled, its landed DLL exists, and the process runs another copy — letter for letter what
`ModuleActivationStatus.NotYetLoaded` tests for, which is why it wore that label. It is subtracted
by name, from the same inputs the boot decided on (the entry's stated identity, the platform's
readings, and whether the image ships this module — `Modules:Assemblies`, read through
`MeshBuilderModuleActivation.BaselineModuleNames`), so the report and the boot cannot reach
different verdicts about one entry.

Its sentence names the only thing that clears it:

> N landed module generation(s) are DECLINED in favour of the copy this image ships — the module
> RUNS, from the image's own copy, and the landed generation is not in effect. 🚨 A RESTART DOES NOT
> CHANGE THIS and neither does re-installing: both measure the same bytes and reach the same
> verdict. It clears when the module is published built against this platform build…

The `STALE PACK` remediation follows the same split: a baseline winning because no store entry
exists is fixed by installing one; a baseline winning because the store entry was DECLINED
(`EffectiveModule.PreferImageCopy`) is fixed by nothing an operator can do on that pod.

## What converges now, and what does not

A module packed by the **same build that produced the image** states that build's commit identity,
which the platform now also states — so it MATCHES and the landed generation runs. That is the
"self-healing" property #4161 claimed and could not deliver.

A module packed against an **earlier** platform build still loses to the image's copy, and for a
module the image ships that is harmless: the same source, compiled by the image's own build, is
running. It is not harmless for a store module released *after* the image with no rebuild against
it — that copy stays declined until it is repacked. Closing that would take the producers stating
the **surface** identity (which is stable across commits that do not move the API surface) rather
than the commit identity, in `node-repo-module-pack.yml` — a fleet-wide change to what every bundle
states, tracked separately from this one.

## Reading the state on a live portal

- `/health` → `pending_module_activation` — the entry prints only when non-Healthy, so absence is
  not evidence (it is equally "the check is not registered in this host").
- The pod log, one grep: `[ModuleActivation] SKIPPED` names every declined module with both
  identities; `[ModuleLoad]` names the file each module actually loaded from.
- `Ops/Status/<id>` on the control instance carries each replica's whole `/health` body.

See also: [The Module Identity Anchor](../ModuleIdentityAnchor) · [Module Adoption
Policy](../ModuleAdoptionPolicy) · [Module Generation Substitution](../ModuleGenerationSubstitution).
