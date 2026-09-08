---
Name: Roll Selection
Category: Architecture
Description: Completeness is a SELECTION criterion, not a post-hoc verdict — an environment picks the latest release that ships all of its plugins, so there is nothing left for a readiness gate to refuse. The algorithm, the denominator that makes it non-vacuous, and the measured boundary of what it can see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/><path d="M4 6h6M4 11h3"/></svg>
---

# Roll Selection

> 🚨 **Rule change, 2026-09-07 (maintainer) — see [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy).** "The latest platform version shipping all plugins" is re-read as "the newest release on which no installed module is UNLOADABLE": a missing bake is a boot compile (reported), a floor is advisory, and only a measured link failure against the target holds. The mechanism described below is what runs until [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651) lands; this page is rewritten by that change.

> **"Whenever a new platform / plugin is published, check for each environment which is the latest
> platform version shipping all plugins, if different from current version ⇒ update."**
> — maintainer, 2026-09-06

## It is an inversion, not a restatement

The fleet used to roll in this order:

```text
 a set is published  ──►  the environment takes the NEWEST  ──►  it boots
                                                                   │
                                                       bakes, and only THEN discovers
                                                       a plugin missing or regressed
                                                                   │
                                                     the readiness gate refuses ──► the rollout stalls
```

Completeness was a **post-hoc verdict**. The new order asks the same question first:

```text
 a set is published  ──►  which release ships ALL of THIS environment's plugins?
                                     │
                        the latest one that does ──► different from current ⇒ update
                                     │
                        none does ──► stay put, and NAME the plugin
```

An environment never targets a release that does not ship all of its plugins, so there is nothing
left to refuse. The [release availability gate](../ReleaseGates) does not go away — it still guards
the roll, and it now guards a roll that was chosen to pass it.

**The quantifier is per environment.** "Ships all plugins" is not a property of a release; it is a
question about *that environment's* plugin list. Two environments looking at one publication may
legitimately choose different targets, and one may correctly stay where it is.

## The algorithm

`MeshWeaver.PluginCatalog.RollSelection.Select` states it once:

```text
1. read this environment's plugin list          ← the DENOMINATOR
   · could not read it            ⇒ Indeterminate  (HOLD, named)
   · read it and it is EMPTY      ⇒ NoPluginsKnown (REFUSE — see "the vacuity refusal")
2. walk the candidate releases, NEWEST FIRST, one at a time
   · for each, ask the SHARED predicate: is any installed module UNLOADABLE on it?
   · a content package with no bake for it ⇒ said on the outcome ("would recompile at boot: …"), never a decline
   · stop at the first with no unloadable module
3. conclude
   · selected == current          ⇒ AlreadyCurrent   (said, not left as a silence)
   · selected is newer            ⇒ Update
   · selected is OLDER            ⇒ BehindCurrent    (reported, never applied)
   · every candidate declined     ⇒ NoCompleteRelease (stay put, name the MODULE)
```

### 🚨 What declines a candidate — since MeshWeaver#3651, a module that cannot load there

The maintainer's rule of 2026-09-07 (the Module Adoption Policy page (`Doc/Architecture/ModuleAdoptionPolicy`)) changed what
"ships all plugins" means. It used to mean *every content-bearing package has a sealed bake under
the target's identity* — and on that day it would have kept every portal on the morning build a
second day, because the satellites had not baked for the new identity yet, although their courses
compile at boot on every pull request. It now means **no installed module is measured unloadable on
the target**:

| A package that… | Declines the candidate? | What the outcome says |
|---|---|---|
| ships a compiled module the target publishes a build of | no — it will be adopted at the roll | nothing |
| ships a compiled module the target publishes NO build of, whose **landed generation links** against the target's published surface (`platform-surface.json`, [the link gate](../ModulePlatformLinkGate)) | no | nothing |
| …whose landed generation **does not link** — it references a type the target does not carry | **yes** — `ModuleUnloadable`, the one decline | `Declined[].Reason` names the module and the missing type |
| …whose loadability **could not be measured** — the release published no surface, the bytes are unreadable | no | `Advisories`: *"could not be determined …"* — reported, neither clearance nor a decline |
| has no sealed content bake for the target | no — the instance compiles it at boot | `BootCompiles` / `Advisories`: *"would recompile at boot: …"*, and the summary line |
| has a bake whose sealed set is torn or inconsistent (MeshWeaver#3175) | **yes** — unchanged | `Declined[].Reason` |

`Modules:RequirePrebuilt` — the opt-in strict mode in which the seeder refuses a boot compile and
parks the type — makes the missing bake a decline again, which is the arm the older tests below run
on. The predicate is still the one the gate runs; `RollSelection` contributes only the walk.

### What is shared, and what is new

| Piece | Where | Status |
|---|---|---|
| *Does release R ship package P?* | `ReleaseAvailability.IsUpdatable` | **shared** — the predicate [#3441/#3443](../ReleaseGateDenominator) built and falsified |
| *What is published for R?* | `PublishedBundleCatalogue.Read` | **shared** |
| *Which packages must be shipped?* | `ReleaseAvailabilityService.RequiredPackages` | **shared** — the monotone denominator |
| *Which release should this environment be on?* | `RollSelection.Select` | new — the walk |
| *Which releases exist at all?* | `PublishedBundleCatalogue.PublishedReleases` | new — the `_releases/` markers |
| *The answer, readable from outside* | `GET /api/plugins/roll-target` | new |

There is deliberately **no second copy** of the completeness rule. `RollSelection.Select` takes the
per-candidate verdict as a function, so the walk cannot drift from the gate: the poller, the
endpoint and CD all reach the same predicate through the same service method.

### The candidate universe is the release markers

`<root>/_releases/<version>` records the framework identity a release resolves — the only way
anything outside the image can learn it, because the identity is a property of the binaries. A
version with **no** marker has no resolvable identity, every package answers `Indeterminate` for it,
and it could therefore never be *selected*. Enumerating the markers loses no candidate the algorithm
could have chosen, and it lets an environment answer for itself without listing a container registry
it may not be able to reach.

Ordering stays with the caller: `VersionSelect` owns the ordering rules — the sealed-publication
lineage rather than the version string, see
[Self-Update Target Selection](/Doc/Architecture/SelfUpdateTargetSelection) — the tag-shape filters
(a bare git sha parses as `6943991.0.0` and sorts above every real release) and the update policy.
`RollSelection` reads "newer" off the candidate list's **order**, so `MeshWeaver.PluginCatalog` holds
no second opinion about which release is newer.

## The denominator

### What "this environment's plugins" means

It is the **install records** — `Plugins/*`, `nodeType:Package` — the same source
[the deployment gate](../ReleaseGateDenominator) already reads. That choice is not automatic: an
instance's modules also arrive as per-Space `_GitSync` entries, and `InstanceComboReader` folds both
shapes precisely because a reader of one alone under-reports. Two things decide it here:

1. **The predicate is shared, so the denominator must be too.** A selector whose denominator differs
   from the gate's could choose a release the gate then holds — two completeness answers about one
   environment, which is the drift this change exists to prevent.
2. **Only an install record carries the id the bake names its bundle by.** A sync entry names a
   *partition*; the published root holds `<bundle>.zip`. Folding sync entries in would add names
   that can never match a bundle and would hold every environment forever.

The known weakness of that source is that it can answer **empty** — measured on memex on 2026-08-10:
42 sync entries, zero install records. That is now a refusal rather than a pass (below), which is the
whole reason the source is *stated* on every outcome instead of assumed.

### 🚨 The vacuity refusal

With an empty denominator every release ships all zero plugins, so a selector that tolerates the case
degenerates into "take the newest" — exactly the order this algorithm replaces, wearing its own
approval. **"No plugins to check" and "all plugins present" are never spelled the same way here:**

- the list **could not be read** ⇒ `Indeterminate`. A hold. Cannot determine is not clearance.
- the list was read and is **empty** ⇒ `NoPluginsKnown`. Nothing is selected, and the reason says
  what to do: either this environment genuinely deploys no packages — in which case the availability
  gate does not apply to it at all and its published root should not be configured — or its plugin
  list is not being read, which is the failure the refusal makes visible.

🚨 **The same refusal is in the gate, not only in the selector.** `ReleaseAvailabilityService`'s
per-version verdict now holds on an empty denominator too, with the same reason. A rule only the
selector honours would be no rule: the poller re-gates whatever the walk hands back, so a refusal
that lived only in the walk could be waved through one line later.

Every outcome **prints the denominator** — and, since MeshWeaver#3651, the **cost** — whatever it
concluded:

```text
[RollSelect] memex: 77 plugin(s) required (from the install records (Plugins/*, nodeType:Package));
             77 of 77 satisfied by 3.0.0-ci.8090, after declining 1 newer candidate(s) —
             3.0.0-ci.8100: SocialMedia: its landed module MeshWeaver.Social cannot load on
             3.0.0-ci.8100 … references MeshWeaver.Mesh.CodeOutputCurrency …; 2 package(s) would
             recompile at boot: AgenticPrimer, Crm. Different from 3.0.0-ci.8009 ⇒ update.
```

A completeness answer whose expected count nobody can read is one nobody can tell from a vacuous one.
This is the same discipline `[ReleaseGate] denominator: …` established one level up.

## Presence, or presence *and* adoptability?

Both, and each has discriminated on a different real incident — so neither half may be dropped:

| Incident | What the publication looked like | Which half caught it | Since MeshWeaver#3651 |
|---|---|---|---|
| memex.meshweaver.cloud, `3.0.0-rc9.ci.7676` (held 2026-09-03) | 38 of 77 installed packages had **no sealed bundle** under identity `sf61a0f5d75…` | **presence** | **named as the boot compile**, and the release is selected — a decline only under `Modules:RequirePrebuilt` |
| memex-cloud, `3.0.0-ci.7621` ([#3175](../ReleaseGates)) | every package present; a bundle's NodeTypes recorded a module MVID the same identity's sealed module set did not carry | **adoptability** | unchanged — a torn set still declines |
| memex.systemorph.com, `3.0.0-ci.7926` (2026-09-06) | complete **and** consistent | **neither** — see below | unchanged |
| every production portal, `3.0.0-ci.8009` → all eleven candidates (2026-09-07) | complete for the platform's own content; the satellites not yet baked for the new identity; every landed module **would have loaded** | **presence** held it (behind the floors) — wrongly | the **link check** clears every candidate; the unbaked satellites are the named cost |

The predicate carries all three, and `RollSelection` inherits them unchanged.

## 🚨 The boundary: what happened on `3.0.0-ci.7926`

This is the incident that prompted the directive, and it is worth stating exactly, because a reader
will otherwise assume this algorithm covers it.

memex.systemorph.com rolled to `3.0.0-ci.7926` at 19:17Z on 2026-09-06. Its bake gate found
`Feedback/Feedback` regressed and refused readiness — correctly. The refused pod nonetheless kept
running and kept stamping module generations, two images served one mesh for two hours, and
`Crm/Offer` and `Crm/Opportunity` went unloadable: every deal and offer page on the client portal was
dead from 19:28Z.

**A completeness selector would have chosen 7926 anyway, and the evidence is in the CD run.** Run
7926 (`actions/runs/34050278127`, all 26 jobs green) resolved framework identity
`sc273ee39fdccbfc088f9aaf1fc548a9a`, and its bake-and-seal job logged, on both storage targets:

```text
ok  Feedback/Feedback [5 source(s), 9 dependency record entr(ies)]
bake: Feedback → 1 assembly(ies) + 15 node file(s) → /bake/Feedback.zip
[PASS] Feedback (13 node(s), 1 type(s))   ok  Feedback/Feedback: compile=Ok render=ok tests=ok
published: …/prebuilt-bundles/sc273ee39fdccbfc088f9aaf1fc548a9a/plugins/Feedback.zip
sealed:    …/prebuilt-bundles/sc273ee39fdccbfc088f9aaf1fc548a9a/plugins/_complete (36 bundle(s))
```

**7926 shipped Feedback.** What failed was the *instance*: that portal's own copy of the package is
missing one source node — `Feedback/Feedback/Source/FeedbackContent`, absent since 2026-08-26; the
NodeType's compile record shows four matched `Code` nodes and `src=4` where the bake compiled five.
The shortfall had been masked for eleven days by an `AdoptedUnverified` prebuilt assembly. Changing
the framework identity made that assembly stale, the pre-warmer compiled the live — incomplete —
source set, and 135 diagnostics later the readiness gate refused.

So the selector's reach stops at the **publication**. It answers *"has this release published
something every installed package can adopt?"*, and it cannot answer *"will this instance's own
content build against it?"* — the instance's content is not a property of the release. The checks
that do see that are instance-side and already exist:

- the [combo gate](../ComboGateWiring), which runs this instance's modules inside the candidate image
  and refuses on Red — and which the selector consults as a second opinion, so a candidate already
  recorded Red is never chosen;
- the NodeType bake sweep itself, whose refusal was correct both times;
- and #3478's rule that a refused process must not join the mesh, which is what turned a correct
  refusal into a two-hour outage and is not fixed here.

Weakening the selector to try to cover this would be guesswork. Recording the boundary is not, and
`AReleaseThatShipsEveryPluginIsSelectedEvenWhenTheInstanceCannotBuildOne` pins it as a test.

## Rolling backwards is a separate decision

"The latest release that ships all plugins" can be *behind* what an environment runs — which means
the environment is currently running something that does not ship all its plugins. That is worth
knowing, and it is reported as `BehindCurrent`. It is never applied: a schema migration is not
reversible, the roll path carries the image and nothing else
([The Self-Update Schema Wall](../SelfUpdateSchemaWall)), and taking a rollback silently would be a
worse failure than the state it corrects.

## Who asks

All three roll paths reach the same method, for the same reason the availability verdict has one
implementation — a rule only one caller honours is not a rule.

| Path | How |
|---|---|
| The self-update poll | `SelfUpdateHostedService.FirstRollable` → `SelectRollTarget` over the registry's tags. The poller supplies its own candidates (policy-filtered, strictly newer than what it runs) and its own combo second opinion. |
| An operator or a pipeline rolling by hand | `GET /api/plugins/roll-target` — instance-key gated, fails closed, answers `{kind, current, selected, shouldUpdate, requiredPlugins, satisfiedPlugins, declined[]}`. The running version is read from the instance, never taken from the caller, so nobody can ask for a rollback by naming one. |
| A version somebody already chose | `GET /api/plugins/is-updatable?version=…` — unchanged, and what `check-release-availability.sh` still asks at promote time. Confirming a choice and making one are different questions; CD moving to the selector is a separate change. |

🚨 **The gap this closes is not hypothetical.** Both memex portals carry `policy: None`, so the
poller — the only place a walk existed — was not running on either. `is-updatable` can only answer
about a version the caller already named. On 2026-09-06 there was no way, anywhere, to ask *"which
release should this environment be on"*, and the roll to 7926 was made without any completeness
question being asked at all.

## What it does not see

- **The instance's own content**, as above.
- **A release with no published surface.** A publication sealed before MeshWeaver#3651 carries no
  `platform-surface.json`, so no landed module can be linked against it; the outcome carries that
  as an advisory and the release is still selectable. The boot-time probe decides then — and the
  keep-the-previous-generation fallback keeps the portal serving if it refuses.
- **Anything with no release marker.** A release that published no platform content bake cannot be
  selected. That is deliberate — its identity is unknowable — but it means a bake lane that stops
  writing markers silently shrinks the candidate universe rather than failing.
- **Packages that have never sealed under any identity.** The monotone denominator's stated
  exemption, preserved exactly: a module-only or NodeType-less package produces no bundle ever and is
  not demanded, because demanding one would freeze that environment forever.
- **Other node repos' packages that this publication never composed.** Run 7926 discovered 60
  packages from `MeshWeaver.Plugins`; that portal carries plugin roots from `MeshWeaver.Crm`,
  `.SocialMedia` and `.Manufacturing` too. Those are shipped by their own repos' publications, and
  a selection made against one publication is silent about them.

## Related

- [Release Availability Gates](../ReleaseGates) — the predicate, and the verdict as a service.
- [The Release Gate's Denominator](../ReleaseGateDenominator) — why a completeness set may never be
  read from the artifact it judges.
- [Combo Gate Wiring](../ComboGateWiring) — the instance-side question a published artifact cannot
  answer.
- [Module Set Convergence](../ModuleSetConvergence) — what two images in one mesh do to each other.
- [Release & Self-Update Strategy](../ReleaseStrategy) — the policy the poller reads.
