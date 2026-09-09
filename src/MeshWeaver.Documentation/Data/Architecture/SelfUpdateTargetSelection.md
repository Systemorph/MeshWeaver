---
Name: Self-Update Target Selection
Category: Architecture
Description: How a running install picks the release it rolls to — the sealed-publication lineage (the CD run number) rather than the version string, and the three-valued answer to "does the tag I run still exist". The 2026-09-07 memex-cloud roll onto a withdrawn line, why every gate said yes, and why the install could not leave.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/><path d="M12 8v4l3 2"/></svg>
---

# Self-Update Target Selection

> ✅ **Settled, 2026-09-08.** [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648) landed:
> `ModulePlatformFloor.DeclineReason` is **advisory** at every runtime decision point (see
> [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy), rule R2 — whether a module loads
> is MEASURED by the link probe, never declared by a version string), and it remains a pack-time
> authoring lint. §4 below is rewritten accordingly. The floors themselves moved off the retired rc
> line the day before (MeshWeaver.Plugins#1447 — 42 packages, plus a gate that refuses a new one).

**A version string is a LABEL a human maintains. The CD run number is the ORDER a machine
produced. The self-updater must rank candidates by the second, because the first can be wrong —
and when it is wrong, SemVer makes the mistake permanent.**

Two defects, one file, one shape: an install's self-update picked the wrong target and then could
not recover. Both were live on `memex` and `memex-cloud` on 2026-09-07 and both needed an operator
`kubectl set image` to undo.

## 1. The incident

`Directory.Build.props` briefly read `3.1.0` on 2026-09-05, so ten continuous sets published as
`3.1.0-ci.7832 … 7841` before the bump was reverted. Two days later, with self-update enabled, both
AKS portals rolled themselves onto `3.1.0-ci.7841` — a build three days behind the live line, missing
the payment contract, the Store git-feed fix and more. Then they printed this on every check:

```
[SelfUpdate] check: no newer release: 500 tag(s) listed, none newer than the installed 3.1.0-ci.7841.
```

`3.0.0-ci.7955 / 7962 / 7964 / 7977` were all in the registry, all newer, all sealed. None of them
could ever win, because `3.1.0 > 3.0.0`.

**Nothing in the release process can catch this.** Build 7841 passed promote, bake, seal and
register: every gate it has said yes. None of them asks whether this core is newer than what the
fleet already runs — that question belongs to the selector, and the selector was asking SemVer.

Removing the `3.1.0` tags did not help either, and that is the instructive part. SemVer §11.4
compares pre-release identifiers as **text**, so `ci` < `rc9`, and the selector simply moved to the
next mislabelled tag:

```
[SelfUpdate] check: 3.0.0-rc9.ci.7824 is available but this install rolled 00:11:21 ago …
```

`3.0.0-rc9.ci.7824` is a 2026-09-04 build. It outranks `3.0.0-ci.7977` for the same reason. Ordering
by any property of the *label* recreates the trap under the next label.

## 2. The key is the sealed-publication lineage

Every continuous image is tagged `<line>[-.]ci.<run>`, and `<run>` is `GITHUB_RUN_NUMBER` — the
number `Directory.Build.props` already calls **monotonic and load-bearing**, and the same value
`MissedBuildFact` already uses to order publications. It is the only property of a tag that is
produced by the machine that published it.

So `VersionSelect` reads it (`BuildOrdinal`) and ranks on it:

| Tag | Line (label) | Run (lineage) |
|---|---|---|
| `3.1.0-ci.7841` | 3.1.0 | 7841 |
| `3.0.0-rc9.ci.7824` | 3.0.0 | 7824 |
| `3.0.0-ci.7977` | 3.0.0 | **7977** ← newest |

Both separators are accepted (`-ci.` on a clean line, `.ci.` on a labelled one) because the retired
rc images are still in ACR, and the `edge` channel is read the same way — `edge-images.yml` rewrites
the `ci` label to `edge` and keeps the number.

🚨 **Only `-ci.` can be MINTED, and that is not a reason to stop READING `.ci.`.** The scheme has
exactly two shapes — `X.Y.Z-ci.<n>` and clean `X.Y.Z`, with no labelled line, ever
([Release Process & Versioning §1](/Doc/Architecture/ReleaseProcess)) — but this file is a reader of
whatever the registry holds, and a retired tag parsed as *carrying no run number* is promoted into
the promotion-ranked half of the order below: the §1 freeze, rebuilt by a tidy-up.
`PlatformReleaseOrderTest` pins `3.0.0-rc9.ci.7824` → `7824` for exactly that reason.

### The one case where the label is still the key

An **official release** is a clean `3.0.0` with no run number of its own. It is not a publication, it
is a *promotion*: `release.yml` retags one already-sealed continuous set. Its lineage lives on the
sibling tag it was cut from and cannot be read off the tag, so the two questions the selector answers
use the key differently — deliberately:

- **Ordering** (`PickTargets`) needs a **total** order over a heterogeneous set. Lineage-bearing tags
  order among themselves by run number and rank ahead of promotion tags, which order among themselves
  by SemVer. Under `Stable` no continuous tag is eligible at all, so that path is pure SemVer exactly
  as before; under `Continuous` a promotion tag names the same bytes as a continuous tag already in
  the list, so ranking it behind them costs nothing.
- **"Is this newer than what I run"** (`IsNewer`) is a **pairwise** question with no transitivity to
  preserve, so it answers on the strongest key BOTH sides share: run number when both are continuous
  builds, SemVer when either side is a promotion.

That split is what keeps a `Stable` install running a continuous build able to reach the clean
release it is waiting for. Mixing the two keys *pairwise inside the ordering* would not be transitive
— a slip tag beats a release beats a sealed tag beats the slip tag — and an intransitive comparer
hands a sort an arbitrary answer.

### 🚦 Continuous follows the ci line

**Maintainer decision, 2026-09-07.** Once `v3.0.0` is tagged, a Continuous install keeps taking the
newest sealed `3.0.0-ci.<n>`. It must **not** jump to the clean `3.0.0` merely because SemVer ranks a
release above its pre-releases — the clean release belongs to the Stable policy.

That is not a preference, it is the #3542 freeze again wearing the release's clothes: a Continuous
install that took `3.0.0` would then sit on it while every later sealed `3.0.0-ci.<n>` accumulated
*below* it in SemVer order, and nothing would ever look newer again.

So `SelectCandidates` adds one policy rule: **an install whose running build carries a run number is
ON the line, and under `Continuous` only tags that also carry one are candidates.** An install that is
*not* on the line (it runs a promotion) is not held there — the version string decides for it, so it
rejoins at the next line's first `ci` build.

🚨 **This deliberately does not change `IsNewer`, and the two must not be merged.** *"Is the release
newer than this ci build?"* is **yes** — Stable needs that answer to reach the release at all. *"Should
a Continuous install take it?"* is **no**. One predicate cannot carry both, so the POLICY lives in
`SelectCandidates`, where the policy is in scope, and the ORDER stays a pure fact about publications.
`VersionSelectTest.ContinuousFollowsTheCiLine_AndDoesNotJumpToTheRelease` pins all four cases,
including the one that matters most: right after the tag, when nothing on the line is newer yet and
the release is sitting there looking newer.

The RECOVERY path (§3) deliberately drops the line rule: an install that cannot start a pod at all
takes the best image that EXISTS, and the verdict says `RECOVERY` so the departure is visible.

### 🚦 …and only the builds its PATTERN admits (2026-09-08)

**Maintainer decision, 2026-09-08.** The default channel is the clean release: *"by default we will
not upgrade as long as no version without `-ci…` is labelled."* A continuous build is eligible only
when the policy record's `pattern` — a glob over the tag, `3.0.1-ci*` — admits it, so
`SelectCandidates` resolves the CHANNEL first (`VersionSelect.ResolveChannel`): `Continuous` with a
pattern lists that pattern's tags, `Continuous` without one **is `Stable`** and the poller logs the
advisory once. The order under a pattern is unchanged — still `BuildOrdinal`, still the promotion
band last — and the three `-latest` pointers CD moves are dropped by the structural filter before any
policy is applied. `PickTargets` keeps its LISTING semantics for the availability service, which
enumerates publications under `Continuous` regardless of what an install would apply. Rule, record
shapes and the fleet's current `3.0.0-ci*`:
[Release Process & Versioning](/Doc/Architecture/ReleaseProcess) → "Which build an install takes".

## 3. "I am current" is not "my tag no longer exists"

The second defect is the same code being unable to tell two opposite states apart.

The check's only question was *is anything newer than what I run?* — and once installed on the
highest-sorting tag, **nothing is ever newer**. That answer is also correct-sounding when the
installed tag has been **withdrawn**: the `3.1.0` tags were untagged from ACR on 2026-09-07, so the
Deployment named an image no new pod could start from, and the portal still printed the sentence a
perfectly up-to-date install prints. From outside the process the two were byte-identical.

The listing needed to tell them apart was already fetched — the verdict quotes its size. So the check
now asks it a second question, and gets **three** answers, never two:

| `InstalledTagResolution` | Meaning | What the check does |
|---|---|---|
| `Resolved` | the installed version is in the listing | "no newer release" — genuinely up to date |
| `Withdrawn` | the listing is populated and does NOT contain it | roll to the best **available** release, backwards if need be |
| `Indeterminate` | the running version does not parse, or the listing carried no platform tags | say so; change nothing |

The third row is the point. **A check that answers a boolean about something it had to READ must not
collapse a failed read into a real negative.** An unreachable registry, the wrong repository, or a
paged response that came back empty all produce the same shape as "your tag is gone" and mean the
opposite; recovering on one bad response would roll the whole fleet backwards.

A withdrawn tag with something newer above it is still an ordinary update — the recovery branch is
reached only once the newer set is empty.

### What an operator sees

- `Admin/UpdatePolicy` carries `UnresolvedInstalledTag`, written on **every** check so a healed strand
  clears itself, and the Updates settings tab renders it above everything else (`ui.updateInstalledTagWithdrawn`).
- A recovery roll's verdict carries `RECOVERY — …` so a backwards roll is never unexplained.
- A strand with nothing eligible to recover to reports `SelfUpdateOutcome.InstalledTagWithdrawn` at
  Warning, naming the operator's move. Nothing in the process can fix that one.

## 4. Fixing the selector moved the trap — FOUR readers rank platform builds

The selector was fixed on 2026-09-07. On 2026-09-08 the same wrong assumption reappeared one layer
down, in code written that day: the **release markers**. `publish-bake-bundles.sh` writes
`_releases/<platform-version>` on every run — one file per publication, its NAME the exact version
string that publication was labelled with, its CONTENT the framework identity — and three new
readers ranked those names by SemVer.

| Reader | What it ranks | What a mislabelled or retired label does to it |
|---|---|---|
| `VersionSelect.PickTargets` | registry tags | rolled both AKS portals onto a withdrawn `3.1.0` build (§1) |
| `SealedPublicationIndex.ReleasesOf` | markers → identity → its newest version | places an identity on a stale platform LINE |
| `ShippedPrebuiltBundles.FallbackPublishedBundlesOf` | which sealed publication a tolerant adoption takes | adopts a three-day-old bake over the newest sealed one |
| `PrebuiltBundleStore.Plan` (retention) | which publications survive the sweep | **protects the stale publication and deletes the newest** |

The markers carry exactly the strings the tags did, so the same two members lose to nothing:
`3.1.0-ci.7841` (the withdrawn slip) and `3.0.0-rc9.ci.7824` (the retired line, where SemVer §11.4
compares the identifiers `ci` and `rc9` as **text**) both outrank the later, sealed `3.0.0-ci.8130`
for ever. The last row is the one that costs bytes rather than a listing: retention decides what to
**delete**, so a label that sorts above a later run does not merely mis-rank — it keeps the stale
publication and collects the one the running platform actually adopts.

### The order is defined once — `PlatformReleaseOrder.Newest`

Three keys, lexicographically, each a total order on its own:

1. **the band** — a version carrying a `BuildOrdinal` outranks one that does not (a build the machine
   published outranks a string nobody can place);
2. **the run number**, within the lineage band — the line in front of it is ignored, which is
   precisely what makes a mislabelled line lose;
3. **SemVer**, as the tie-break inside the lineage band and as the whole of the order in the
   promotion band, where the version string is the only key the members share.

🚨 **It is a comparer and `Compare` is deliberately not.** Pairwise, the three members form a CYCLE:
the slip `3.1.0-ci.7841` beats the promotion `3.0.0` (SemVer), the promotion beats the sealed
`3.0.0-ci.8130` (SemVer, a release outranks its pre-releases), and the sealed tag beats the slip
(run number). A sort handed a cycle answers arbitrarily — which is how a "fixed" ordering keeps
picking the wrong build. Banding removes it, and `PlatformReleaseOrderTest` runs the cycle through
every input permutation so a stable-sort accident cannot hide a regression.

### 🚨 …and why the FLOOR is still not this predicate

The tempting conclusion is "make the declared `minMeshVersion` floor use this comparison too".
**That is measured to be wrong, and the counter-example is one section up.** For an updater, `3.0.0`
MUST outrank `3.0.0-ci.7977` — otherwise a Stable install running a continuous build can never take
the clean release it is waiting for (§2). For a floor, the same two strings must give the OPPOSITE
answer: a `3.0.0` floor must be *satisfied* by `3.0.0-ci.7977`. Same two strings, opposite required
answers, so the shareable part is the **key** (`BuildOrdinal`), never the **predicate**.

The floor's own answer was settled elsewhere, and by two changes on consecutive days rather than by
a comparator:

- **The data moved.** MeshWeaver.Plugins#1447 (2026-09-07) took 42 packages off the retired rc line —
  they now declare `3.0.0-ci.7845`, the first build of the ci line, which every existing platform
  satisfies — and added `check-module-floors.py`, which refuses a new floor naming an rc. Both sides
  of every floor comparison are now on one convention, where SemVer is right by construction.
- **The decision moved.** [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648) (2026-09-08)
  made the declared floor **advisory** at every runtime decision point: whether a module loads is
  measured by the type-level link probe, never declared by a version string
  ([Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy), rule R2). It survives as a
  pack-time authoring lint and as a sentence on the status surfaces.

So `PlatformReleaseOrder` lives in `MeshWeaver.Plugin.Packaging`, beside `NuGetVersionComparer` and in
the lowest assembly `MeshWeaver.Hosting`, `MeshWeaver.PluginCatalog` and `Memex.Portal.Shared` all
reference directly. `PlatformReleaseOrderTest.ThePreReleaseLabelIsTextToSemVer_WhichIsWhyTheFloorNeedsItsOwnPredicate`
pins the boundary so the floor decision is taken knowingly rather than by reusing the update one.

## 5. A trigger whose verdict is a foregone conclusion is not a check

**The rate of a check is a property the install must own.** Everything above is about *what* a check
selects; this is about *when* one happens, and the two failed in the same direction on
[#3790](https://github.com/Systemorph/MeshWeaver/issues/3790).

The self-updater is event-driven, which is right: `BuildCompletion` ticks once per publication
**anywhere in the fleet**, and nothing beats an event for latency. But the rate that carries is the
fleet's build cadence, not anything about this install — and under `UpdatePolicyKind.None` a check
can act on none of it. Two places in the code already say so:

- `RunOnce` returns `SelfUpdateVerdict.UpdatesDisabled()` **before it lists a single tag**; and
- `SelfUpdateVerdict.MayRestartAfter` answers `false` for that outcome, so the module-restart half
  ([#3650](https://github.com/Systemorph/MeshWeaver/issues/3650)) is not taken either.

So the whole effect of such a check is a log line plus a bookkeeping stamp repeating a sentence the
node already carries. Measured on memex, 2026-09-09 01:30–06:36Z, policy `None`, two replicas:

| | |
|---|---|
| `[SelfUpdate] check (BuildCompletion): updates are disabled …` | **158** in 5 h |
| `[MergeGuard] refused stale/reordered cross-hub write to 'lastCheckedAt'` | 14 |
| `[UpdateRemote] OWNER_NACK_REENQUEUE … code=Conflict` | 10 |
| `[UpdateQueue] FAILED path=Admin/UpdatePolicy … elapsedMs=10015` | 2 |
| `Admin/UpdatePolicy` version | **62,671** |

Two replicas writing one leaf on the same event is a conflict by construction; at event rate it is a
sustained one, in the update queue that also carries user writes.

`SelfUpdateHostedService.IsDecisionPoint(trigger, policy)` is the rule, and it is a filter on the
**trigger**, never on the check:

| trigger | paced by | a decision point under `None`? |
|---|---|---|
| `BuildCompletion` | the fleet | **no** |
| `ModuleSetProposed` | the fleet | **no** |
| `Startup` | this pod | yes — the record must carry the disabled verdict and when this pod established it |
| `PolicyChange` | an admin | yes — enabling updates must not wait for a publication |
| `SafetyNet` | this service | yes — `LastCheckedAt` keeps moving on its own period, so a dead checker still reads as stale |

Under any policy that can act, every trigger is a decision point.

🚨 **This is the opposite of the `Where` that #2553 removed, and they are one line apart.** That one
dropped *every* check under `None`, so an install an administrator had deliberately pinned and an
install whose updater was broken both left the record empty — indistinguishable, and memex sat three
builds behind for seven hours in that state. What #3790 stops is only the **repetition**, at a rate
nobody here chooses, of an answer already on the node. Because the regression that would undo #2553
is one enum member away, the rule is pinned as a truth table over every trigger × every policy
(`SelfUpdateChecksOnlyAtDecisionPointsTest.TheDecisionPointRule`), and the integration half is a
controlled experiment: the same event, pushed through the same seam, is *not* a check under `None`
and *is* one under `Continuous`.

## 6. What this does NOT fix

- **The withdrawn tags themselves.** Ordering makes them lose; it does not remove them. Measured
  2026-09-08 (`az acr repository show-tags -n meshweaver --repository memex-portal-ai`): **1403 tags,
  of which 97 survive the structural filters — every one of them `3.0.0-ci.<n>`, ZERO `3.1.0-*`,
  ZERO `rc*`, so ZERO outrank the newest sealed set (`3.0.0-ci.8130`) under either comparator.** That
  zero is the product of a hand deletion (issue #3542, proposal 4), not of the ordering: the slip
  tags and the whole retired rc line were untagged by an operator on 2026-09-07. A future slip needs
  the same action — the ordering's job is to make the slip HARMLESS while its tags are still there.
  🚨 The `_releases/` markers are the same set of labels and are **not** covered by that deletion;
  retention removes a pre-release marker only when the identity it names is collected.
- **The policy record losing its own policy** under its bookkeeping writes (issue #3542, proposal 3).
  Settled by #3619: the framework's own typed write (`Update<UpdatePolicyContent>`) refuses a record
  it cannot read instead of persisting defaults over it, and all four bookkeeping writes are pinned
  with positive controls in `UnreadablePolicyRecordIsNotClobberedTest`. 🚨 The `[MergeGuard] refused
  stale/reordered cross-hub write` lines that accompanied it were a **rate**, not a write shape, and
  §5 removes the rate. Two replicas may still write the install-scoped stamp — that is deliberate,
  it describes "this install checked" rather than "this pod checked" — and a refusal of the older of
  two is the merge guard being RIGHT, not a defect to remove.
- **"Installed" is what the pod RUNS.** This reads `ShippedReleaseSeed.InstalledPlatformVersion` —
  the injected `MESHWEAVER_PLATFORM_VERSION`, never the record's `LatestAvailableTag`, which after a
  manual roll-back kept naming a version no pod ran.

## 7. A hold nothing can recompute is HISTORY, not the current verdict

§5's consequence, and the one that bit a reader within a day of it landing.

`HeldTag` / `HeldReason` / `HeldAt` are written **only** by `RecordHold`, which runs only when a
candidate is actually evaluated. `LastCheckedAt` is written by `RecordCheck` on **every** check. Once
a check can decline to evaluate — which is exactly what §5 made it do — those two clocks separate,
and nothing in the record says so.

Measured on `memex`, `get @Admin/UpdatePolicy`, 2026-09-09 10:42Z:

```
heldAt          : 2026-09-07T22:27:17Z
heldTag         : 3.0.0-ci.8057
lastCheckedAt   : 2026-09-09T10:41:24Z          <- 36 h later
lastCheckVerdict: "updates are disabled on this install (Admin/UpdatePolicy = None);
                   the registry was not listed."
```

**A frozen verdict beside a fresh timestamp reads as a current one.** Issue #3706 was filed on that
record: it quoted three module floors as *"holding every self-update on memex forever"*. Those lines
were computed at 22:27Z on 09-07; [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648) made
floors advisory at 00:22Z and [#3651](https://github.com/Systemorph/MeshWeaver/issues/3651) reduced
the hold to measured unloadability at 01:10Z the next morning. **The evidence predated its own fix by
two hours, and the record could not say so.** Of the eleven lines in that `heldReason`, ten are
advisories under today's rules; the one real blocker is the two-build inconsistency of #3732.

### The distinction, and where it is drawn

`IsHeld(tag)` answers *"is there a hold record for this tag"*. Both readers used it to answer *"is
this why the install is not moving right now"*. Those are now different questions, so they are
different methods:

| | asks | true when |
|---|---|---|
| `IsHeld(tag)` | is there a record | `HeldTag == tag` |
| `IsHoldOperative(tag)` | is it a live verdict | …and a poller is running that would clear it |

- **The Updates tab** renders an operative hold as before, and a frozen one as history — *"this is a
  record, not a current verdict … nothing has re-checked `{tag}` since «date»"*. The `(held «date»)`
  suffix is dropped with the live framing, because that phrasing reads as an ongoing state.
- **`PlatformUpdateStatus.Derive`** stops answering `UpdateHeld` off a note nothing can refresh. A
  **Red combo verification keeps holding** regardless: that is a recorded fact about the build, not a
  note about the poller — the distinction the surrounding comment already drew, extended to the case
  it did not anticipate.

🚨 **The record is deliberately NOT cleared when updates are switched off.** The last real evaluation
is the only diagnostic an operator has *before* turning updates back on; destroying it to avoid
showing something stale trades a misleading answer for no answer. Only the framing changes — the
reason is still quoted in full.

## 8. A record naming a package NOBODY publishes — named, never held

§7 explains why #3706's *evidence* was stale. This is the part of #3706 that was real, and it sat on
the other side of the same reading: memex's install records still named `Plugins/Agent`,
`Plugins/Skill` and `Plugins/PlatformUI` after those packages had been withdrawn — Agent and Skill
**deliberately** (`MeshWeaver.Plugins@7afbd745`, *"remove agents and skill package and serve from the
main ai package"*; the AI engine's `BuiltInAgentProvider` / `BuiltInSkillProvider` are the live
master). Nothing publishes them, and nothing ever will again.

The gate's answer was **silence**, and silence was wrong in a way that is easy to mistake for right.

### Not holding was already correct

`ReleaseAvailability.IsUpdatable` did not block on them, and must not: a roll cannot conjure a build
nobody publishes, so waiting for one waits for ever. That is the shape #3706 is named after, and the
two fixes that produced it — floors advisory ([#3648]), a missing bake a cost rather than a hold
([#3651]) — are the right ones. **Saying nothing was the defect.**

The consequence is what makes it worth a section: an operator with a stale install record had *no
signal at all* from the gate, so the only place the package names appeared was a frozen `heldReason`
— §7's trap. **The absence of a live diagnosis is what sent a reader to a dead one.** A gate that
correctly declines to hold still owes an account of what it saw.

### What it says now

A required package is reported as an orphan when three things are true at once:

1. its install record names a **module**,
2. the module set sealed for the target's framework identity **does not carry** it, and
3. **no generation of it is landed** on this instance.

Together those mean the instance has never had it and the target does not offer it — an assertion
about the record, not about the release, so the remedy named is the record:

> `Agent: the install record names module MeshWeaver.Agent, which the module set sealed for framework
> identity s8055 does not carry, and no generation of it is landed on this instance — so no publisher
> produces it and no roll can obtain it. Reported, never a hold: holding would wait for ever. If the
> package is genuinely retired, remove its install record; if it moved, the record must name its new
> home.`

### The three neighbours it must not swallow

Each is a different absence with a different remedy, and each has a test:

| state | told apart by | remedy |
|---|---|---|
| **Orphan** — nobody publishes it | nothing landed, not in the sealed set | fix the **record** |
| **Landed but unpublished for the target** | something *is* landed | the **link probe** measures the bytes; may legitimately hold |
| **Content with no bake** | no module to look for | reported as *"would recompile at boot"*; fix the **bake** |
| **Set unreadable / unobserved** | `SealedModuleSet` null or carrying a `Refusal` | say **nothing** — see below |

🚨 **The fourth row is the one that keeps the other three honest.** A module set that could not be
read means the module was never *looked for*, and "we did not look" must never be worded as "it does
not exist" — the conflation [#1754] forbids one severity up. An advisory that fired on an unmeasured
set would be a confident sentence about evidence nobody gathered, and it would fire hardest exactly
when the observation infrastructure is broken. Unmeasured stays silent here and is reported by the
paths that own it.

### The general rule

> A gate that is right not to hold still owes you what it saw. "Did not block" and "found nothing
> worth mentioning" must not render identically, or the only remaining account of the problem is
> whatever stale field happens to mention it.

[#1754]: https://github.com/Systemorph/MeshWeaver/issues/1754
[#3648]: https://github.com/Systemorph/MeshWeaver/issues/3648
[#3651]: https://github.com/Systemorph/MeshWeaver/issues/3651

## Where it lives

- `src/MeshWeaver.Plugin.Packaging/PlatformReleaseOrder.cs` — the lineage key (`BuildOrdinal`), the
  pairwise update predicate (`Compare` / `IsNewer`) and the total order every ranking caller shares
  (`Newest`), in the lowest assembly all of them reach.
- `memex/Memex.Portal.Shared/SelfUpdate/VersionSelect.cs` — the tag-shape filters, the policy,
  `CheckInstalledTag` and `SelectCandidates`. Pure; no hub, no registry, no Rx.
- `memex/Memex.Portal.Shared/SelfUpdate/SelfUpdateHostedService.cs` — `RunOnce` / `NothingToRoll`,
  and `IsDecisionPoint` (§5).
- `src/MeshWeaver.Hosting/SealedPublicationIndex.cs` — identity → its newest published version.
- `src/MeshWeaver.Hosting/ShippedPrebuiltBundles.cs` — which sealed publication a tolerant adoption
  takes (`Modules:VersionStrictness`, [Module Versioning](/Doc/Architecture/ModuleVersioning)).
- `src/MeshWeaver.Hosting/PrebuiltBundleRetention.cs` — the sweep plan, i.e. what is deleted.
- `test/MeshWeaver.Graph.Test/PlatformReleaseOrderTest.cs` — the lineage key, the incident, the total
  order and its transitivity, and the measured floor boundary.
- `test/MeshWeaver.Hosting.Test/SealedPublicationLineageTest.cs` — the markers: the index and the
  sweep, each in both directions.
- `test/Memex.Portal.Shared.Test/VersionSelectTest.cs` — the ordering and the three-valued check.
- `test/Memex.Portal.Shared.Test/SelfUpdateStrandRecoveryTest.cs` — the poller, against a real mesh.
- `test/Memex.Portal.Shared.Test/SelfUpdateChecksOnlyAtDecisionPointsTest.cs` — §5's truth table,
  and the same event under two policies.
- `test/Memex.Portal.Shared.Test/FrozenHoldIsHistoryTest.cs` — §7: both framings on the tab, both
  answers on the About surface, and the Red-verdict positive control that must keep holding.
- `src/MeshWeaver.PluginCatalog/ReleaseAvailability.cs` — `ModuleLane`, §8's three conditions and the
  unmeasured-set guard.
- `test/Memex.Portal.Shared.Test/OrphanedPackageRecordTest.cs` — §8: the orphan named without
  holding, the floor advisory that must keep riding beside it, each neighbouring absence kept
  distinct, and the unreadable-set control that stops the advisory becoming unconditional.

## Related

- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — the channels, the policy, and
  what each one selects.
- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what a sealed,
  promoted set guarantees.
- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — where the version number comes
  from and when the line moves.
