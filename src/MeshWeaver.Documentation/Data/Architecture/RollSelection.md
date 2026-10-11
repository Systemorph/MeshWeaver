---
Name: Roll Selection
Category: Architecture
Description: An environment selects the newest release its update policy admits — the platform rolls independently of its packages. What the packages say about that release is asked once and reported on the outcome as advisories, with the denominator, never used to walk past it. The algorithm, what each package verdict becomes, and the measured boundary of what the selection can see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 6 9 17l-5-5"/><path d="M4 6h6M4 11h3"/></svg>
---

# Roll Selection

> 🚨 **The platform rolls independently of its packages** — policy
> `platform-rolls-independently-of-packages` ([Policy Not Prose](../PolicyNotProse)). The selection
> takes the **newest admitted candidate, always**, and asks the package predicate about it exactly
> once. Whatever that verdict says — a missing bake, an unresolvable framework identity, an
> unloadable module, an unread plugin list — is **reported on the outcome** (`Advisories`, the
> summary line) and never moves the choice. Packages catch up through their own update policy.
> [Release & Self-Update Strategy](../ReleaseStrategy) has the measured deadlock this removed.

> **History.** The selection was built (#3479) as *"the latest platform version shipping all
> plugins"* and refined by the [Module Adoption Policy](../ModuleAdoptionPolicy)
> ([#3648](https://github.com/Systemorph/MeshWeaver/issues/3648)–[#3651](https://github.com/Systemorph/MeshWeaver/issues/3651)),
> under which only a module that provably could not load declined a candidate. Under policy
> `platform-rolls-independently-of-packages` nothing declines one. The measurements those changes
> rest on are kept below as evidence; where a paragraph describes the retired behaviour it says so.

## Two questions, kept apart

Before policy `platform-rolls-independently-of-packages` the selection asked one question —
*which is the latest release that ships all of this environment's plugins?* — and walked past
every release that did not:

```text
 a set is published  ──►  which release ships ALL of THIS environment's plugins?
                                     │
                        the latest one that does ──► different from current ⇒ update
                                     │
                        none does ──► stay put, and NAME the plugin
```

That made the platform wait for its packages, while every package update waits for its platform
floor — and the two waits met (measured on one deployment; see
[Release & Self-Update Strategy](../ReleaseStrategy)). The selection now asks two questions and
lets only the first decide:

```text
 a set is published  ──►  which is the NEWEST release this environment's policy admits?
                                     │
                          that one ──► different from current ⇒ update
                                     │
                          and, about that one release only:
                          what do THIS environment's packages say?  ──► REPORTED
                              (advisories + the denominator on the outcome; never a decline)
```

A package that is not available for the selected release does not follow the roll: it keeps its
previous generation, or parks, and catches up through its own update policy. The
[release availability predicate](../ReleaseGates) does not go away — it still answers the package
question truthfully, and the CI build gate and the package surfaces still read it as a yes or no.

**The package answer is per environment.** It is a statement about *that environment's* plugin
list, so two environments selecting the same release may carry different advisories.

## The algorithm

`MeshWeaver.PluginCatalog.RollSelection.Select` states it once:

```text
1. no candidate was offered        ⇒ NoCandidates   (said, not left as a silence)
2. read this environment's plugin list          ← the DENOMINATOR
   · could not read it  ⇒ select the NEWEST candidate; advisory: "the plugin list could not be read …"
   · read it, EMPTY     ⇒ select the NEWEST candidate; advisory: "0 plugins required — … named none …"
3. otherwise take the NEWEST candidate and ask the SHARED predicate about it — once
   · the verdict goes through UpdatabilityVerdict.ForPlatformRoll: a refusal becomes advisories,
     led by the reason it would have held on (WaivedHold)
   · a content package with no bake for it ⇒ "would recompile at boot: …" (BootCompiles)
4. conclude, from where the selection sits relative to what runs
   · selected == current          ⇒ AlreadyCurrent   (said, not left as a silence)
   · otherwise                    ⇒ Update
```

The walk is lazy and now trivially so: it examines one candidate, so a selection costs exactly one
verdict. `Declined` on the outcome is empty.

`ReleaseAvailabilityService.SelectRollTarget` wraps that with what only the instance knows:

| Before the walk | Outcome |
|---|---|
| the deployment consumes no CI bakes (no published root, or a root that serves none) | `NotEnforced` — nothing is selected *by this selection*; the caller takes the newest admitted tag |
| the published root could not be read, and the caller supplied candidates | the newest candidate (`RollSelection.Unjudged`), advisory: *"the published bundle root could not be read …, so no package was checked"* |
| the published root could not be read, and the candidates would have come from it | `Indeterminate` — there is nothing to select from |
| the release markers could not be listed | `Indeterminate` |
| the selection did not answer within its budget, or faulted | `Indeterminate` |

`Indeterminate` therefore means only *"the candidate universe could not be seen, or the selection
did not answer"* — never a statement about a package. It is not a
hold on the poller: `SelfUpdateHostedService.FirstRollable` takes the head of its own
candidate list whenever the selection names no release.

### What a package verdict becomes

Nothing declines a candidate. Each thing the shared predicate can say about the selected release
is carried on the outcome instead:

| A package that… | The predicate's answer | What the outcome says |
|---|---|---|
| ships a compiled module the target publishes a build of | available | nothing |
| ships a compiled module the target publishes no build of, whose **landed generation links** against the target's published surface (`platform-surface.json`, [the link gate](../ModulePlatformLinkGate)) | available | nothing |
| …whose landed generation **does not link** — it references a type the target does not carry (`ModuleUnloadable`) | refuses | `Advisories[0]`: *"the platform rolls independently of packages — N package(s) are not available for this release …"* with the module and the missing type; the summary: *"not following this roll: …"* |
| …whose loadability **could not be measured** — the release published no surface, the bytes are unreadable | available, with an advisory | `Advisories`: *"could not be determined …"* |
| has no sealed content bake for the target | available — the instance compiles it at boot | `BootCompiles` / `Advisories`: *"would recompile at boot: …"*, and the summary line |
| has no sealed content bake, on an instance with `Modules:RequirePrebuilt` (the seeder refuses a boot compile and parks the type) | refuses | advisory + *"not following this roll: …"*; the package parks until its bake exists |
| has a bake whose sealed set is torn or inconsistent (MeshWeaver#3175) | refuses | advisory + *"not following this roll: …"* |
| cannot be judged because the release's framework identity is not resolvable (no release marker) | indeterminate | `Advisories[0]`: *"… package availability for this release could NOT be determined, and that decides nothing about the platform roll: …"* |

`SatisfiedPlugins` counts the packages that are available on the selected release, so it can be
**less than** `RequiredPlugins` — that difference is the number of packages that do not follow
the roll.

### What is shared, and what is new

| Piece | Where | Status |
|---|---|---|
| *Does release R ship package P?* | `ReleaseAvailability.IsUpdatable` | **shared** — the predicate [#3441/#3443](../ReleaseGateDenominator) built and falsified |
| *What is published for R?* | `PublishedBundleCatalogue.Read` | **shared** |
| *Which packages must be shipped?* | `ReleaseAvailabilityService.RequiredPackages` | **shared** — the monotone denominator |
| *Which release should this environment be on?* | `RollSelection.Select` | the selection — the newest admitted candidate |
| *What does a package refusal become on a roll?* | `UpdatabilityVerdict.ForPlatformRoll` | **shared** — the one waiver every roll path applies |
| *Which releases exist at all?* | `PublishedBundleCatalogue.PublishedReleases` | new — the `_releases/` markers |
| *The answer, readable from outside* | `GET /api/plugins/roll-target` | new |

There is deliberately **no second copy** of the package rule. `RollSelection.Select` takes the
verdict as a function, so what the outcome reports cannot drift from the predicate: the poller, the
endpoint and CD all reach the same predicate through the same service method, and the same waiver.

### The candidate universe is the release markers

`<root>/_releases/<version>` records the framework identity a release resolves — the only way
anything outside the image can learn it, because the identity is a property of the binaries. A
version with **no** marker has no resolvable identity, and every package answers `Indeterminate` for
it. When the selection lists its own candidates (`GET /api/plugins/roll-target`), the markers are
the universe, which lets an environment answer for itself without listing a container registry it
may not be able to reach — and means a release with no marker is not offered there. A caller that
supplies its own candidates (the poller, from the registry's tags) is taken at its word: a tag with
no marker is selected like any other, and the unresolvable identity is the leading advisory.

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
   from the gate's would report one package answer on the outcome and another on the Updates tab —
   two answers about one environment, which is the drift sharing it exists to prevent.
2. **Only an install record carries the id the bake names its bundle by.** A sync entry names a
   *partition*; the published root holds `<bundle>.zip`. Folding sync entries in would add names
   that can never match a bundle, and every environment would report them as lagging forever.

The known weakness of that source is that it can answer **empty** — measured on memex on 2026-08-10:
42 sync entries, zero install records. That is said on the outcome rather than passed off as completeness
(below), which is the whole reason the source is *stated* on every outcome instead of assumed.

### 🚨 An empty denominator is said, never dressed as a pass

With an empty denominator every release ships all zero plugins. Before policy
`platform-rolls-independently-of-packages` that was refused (`NoPluginsKnown`), because a selector
that tolerated it degenerated into "take the newest" while claiming completeness. Taking the
newest is now the rule, so the refusal is gone — and the **claim** is what must not come back.
**"No plugins were checked" and "all plugins present" are still never spelled the same way:**

- the list **could not be read** ⇒ the newest candidate is selected, `satisfiedPlugins` is `0`, and
  the advisory says *"the plugin list could not be read from … — package availability for this
  release is unknown, which decides nothing about the platform roll"*.
- the list was read and is **empty** ⇒ the newest candidate is selected, `0 of 0`, and the advisory
  says what to check: either this environment genuinely deploys no packages, or its plugin list is
  not being read — which is the failure the line exists to make visible.

**The per-version verdict is unchanged, and the roll paths waive it.**
`ReleaseAvailabilityService.IsUpdatable` still answers an empty denominator — and an unreadable
published root — as *not updatable* (`UpdatabilityVerdict.Unavailable`), because as an answer to
the package question that is the truth. Every roll path (the poller, the reboot's image step, the
manual button, `GET /api/plugins/is-updatable`) passes the verdict through `ForPlatformRoll`, which
turns the refusal into the leading advisory and keeps the reason verbatim as `WaivedHold`.

Every outcome **prints the denominator** and the **cost**, whatever it concluded:

```text
[RollSelect] memex: 77 plugin(s) required (from the install records (Plugins/*, nodeType:Package));
             75 of 77 satisfied by 3.0.0-ci.8100 (the newest candidate); 2 package(s) would
             recompile at boot: AgenticPrimer, Crm; the platform rolls independently of packages —
             not following this roll: SocialMedia: its landed module MeshWeaver.Social cannot load
             on 3.0.0-ci.8100 … references MeshWeaver.Mesh.CodeOutputCurrency …. Different from
             3.0.0-ci.8009 ⇒ update.
```

A package answer whose expected count nobody can read is one nobody can tell from a vacuous one.
This is the same discipline `[ReleaseGate] denominator: …` established one level up.

## Presence, or presence *and* adoptability?

The package predicate reads both, and each has discriminated on a different real incident — so
neither half may be dropped from what the outcome reports:

| Incident | What the publication looked like | Which half saw it | What the selection does with it now |
|---|---|---|---|
| the public instance, `3.0.0-rc9.ci.7676` (held 2026-09-03) | 38 of 77 installed packages had **no sealed bundle** under identity `sf61a0f5d75…` | **presence** | **named as the boot compile**, and the release is selected; under `Modules:RequirePrebuilt` the packages are named as not following the roll |
| the public instance, `3.0.0-ci.7621` ([#3175](../ReleaseGates)) | every package present; a bundle's NodeTypes recorded a module MVID the same identity's sealed module set did not carry | **adoptability** | the predicate still refuses the torn set; the release is selected and the package is named as not following the roll |
| the control instance, `3.0.0-ci.7926` (2026-09-06) | complete **and** consistent | **neither** — see below | unchanged |
| every production portal, `3.0.0-ci.8009` → all eleven candidates (2026-09-07) | complete for the platform's own content; the satellites not yet baked for the new identity; every landed module **would have loaded** | **presence** held it (behind the floors) — wrongly | the newest is selected; the unbaked satellites are the named cost |

The predicate carries all of it, and `RollSelection` reports it unchanged.

## 🚨 The boundary: what happened on `3.0.0-ci.7926`

This is the incident that prompted the directive, and it is worth stating exactly, because a reader
will otherwise assume this algorithm covers it.

The control instance rolled to `3.0.0-ci.7926` at 19:17Z on 2026-09-06. Its bake gate found
`Feedback/Feedback` regressed and refused readiness — correctly. The refused pod nonetheless kept
running and kept stamping module generations, two images served one mesh for two hours, and
`Crm/Offer` and `Crm/Opportunity` went unloadable: every deal and offer page on the client portal was
dead from 19:28Z.

**A completeness selector would have chosen 7926 anyway — and so does today's — and the evidence
is in the CD run.** Run
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

So what the selection can report stops at the **publication**. Its package answer is *"has this
release published something every installed package can adopt?"*, and it cannot answer *"will this instance's own
content build against it?"* — the instance's content is not a property of the release. The checks
that do see that are instance-side and already exist:

- the [combo gate](../ComboGateWiring), which runs this instance's modules inside the candidate image.
  A recorded Red names modules that do not survive the candidate — a statement about packages — so
  it is an **advisory** on the roll, not a reason to choose another release: the poller records the
  failing modules on `Admin/UpdatePolicy` and rolls, and a caller that hands the selection a second
  opinion (`condemned`) gets it appended to the selected release's advisories;
- the NodeType bake sweep itself, whose refusal was correct both times;
- and #3478's rule that a refused process must not join the mesh, which is what turned a correct
  refusal into a two-hour outage and is not fixed here.

Stretching the package answer to try to cover this would be guesswork. Recording the boundary is
not, and `AReleaseThatShipsEveryPluginIsSelectedEvenWhenTheInstanceCannotBuildOne` pins it as a test.

## The selection never goes backwards

Before policy `platform-rolls-independently-of-packages` the walk could conclude that the latest
*complete* release was behind what the environment runs, and reported that as `BehindCurrent`
without applying it. The walk no longer produces that answer: it takes the head of the caller's
newest-first list, and nothing behind the head is examined.

What keeps a roll from moving an environment backwards is therefore the candidate list. The
poller — the path that applies a selection — offers only tags strictly newer than the running
build. `roll-target` lists the release markers newest-first and applies nothing: its answer is for
a person or a pipeline to read, and when the running build is not among the markers it cannot
place the head relative to it. A rollback is still a separate decision: a
schema migration is not reversible and the roll path carries the image and nothing else
([The Self-Update Schema Wall](../SelfUpdateSchemaWall)). The one deliberate exception — recovering
an install stranded on a tag the registry no longer lists — belongs to `VersionSelect`, which
builds the candidate list, not to this selection
([Self-Update Target Selection](/Doc/Architecture/SelfUpdateTargetSelection)).

## Who asks

All roll paths reach the same method and the same waiver, for the same reason the availability
verdict has one implementation — a rule only one caller honours is not a rule.

| Path | How |
|---|---|
| The self-update poll | `SelfUpdateHostedService.FirstRollable` → `SelectRollTarget` over the registry's tags. The poller supplies its own candidates (policy-filtered, strictly newer than what it runs) and takes the head whenever the selection names no release. The combo verdict is consulted afterwards, on the selected tag, and recorded as an advisory. |
| An operator or a pipeline rolling by hand | `GET /api/plugins/roll-target` — instance-key gated, fails closed, answers `{environment, kind, current, selected, shouldUpdate, indeterminate, requiredPlugins, satisfiedPlugins, advisories[], bootCompiles[], declined[]}` (`declined` is empty). The running version is read from the instance, never taken from the caller, so nobody can ask for a rollback by naming one. |
| A version somebody already chose | `GET /api/plugins/is-updatable?version=…` — what `check-release-availability.sh` asks at promote time. It answers the **roll** question: a package refusal comes back as `isUpdatable: true` with the reason verbatim in `waivedHold`, the advisories, and every package's own status in `packages[]`. |

🚨 **The gap the endpoint closed is not hypothetical.** Both memex portals carried `policy: None`
at the time, so the poller — the only place a selection existed — was not running on either.
`is-updatable` can only answer about a version the caller already named. On 2026-09-06 there was
no way, anywhere, to ask *"which release should this environment be on"*.

## What it does not see

- **The instance's own content**, as above.
- **A release with no published surface.** A publication sealed before MeshWeaver#3651 carries no
  `platform-surface.json`, so no landed module can be linked against it; the outcome carries that
  as an advisory. The boot-time probe decides then — and the
  keep-the-previous-generation fallback keeps the portal serving if it refuses.
- **Anything with no release marker, when it lists its own candidates.** `roll-target` enumerates
  the markers, so a release that published no platform content bake is not offered there, and a
  bake lane that stops writing markers silently shrinks that universe rather than failing. A tag the
  poller supplies is selected without a marker, with the unresolvable identity as its advisory.
- **Packages that have never sealed under any identity.** The monotone denominator's stated
  exemption, preserved exactly: a module-only or NodeType-less package produces no bundle ever and is
  not demanded, because demanding one would report it as lagging on that environment forever.
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
