---
Name: Self-Update Target Selection
Category: Architecture
Description: How a running install picks the release it rolls to — the sealed-publication lineage (the CD run number) rather than the version string, and the three-valued answer to "does the tag I run still exist". The 2026-09-07 memex-cloud roll onto a withdrawn line, why every gate said yes, and why the install could not leave.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7"/><path d="M21 3v6h-6"/><path d="M12 8v4l3 2"/></svg>
---

# Self-Update Target Selection

> 🚨 **Rule change, 2026-09-07 (maintainer) — see [Module Adoption Policy](@/Doc/Architecture/ModuleAdoptionPolicy).** The floor table below stops mattering: `ModulePlatformFloor.DeclineReason` becomes advisory at every runtime decision point, and "declared floors are not going away, so do not design as if they were" is superseded — they stay as pack-time authoring lint only. The mechanism described below is what runs until [#3648](https://github.com/Systemorph/MeshWeaver/issues/3648) lands; this page is rewritten by that change.

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

## 4. The same wrong assumption has a SECOND call site

`ModulePlatformFloor.DeclineReason` decides whether a module bundle may land: it declines when
`NuGetVersionComparer.Instance.Compare(runningVersion, minMeshVersion) < 0`. That is pure SemVer, and
SemVer §11.4 compares pre-release identifiers as text — so `"ci"` < `"rc"`, and a floor of
`3.0.0-rc8` is unsatisfiable by **every** `3.0.0-ci.N`. Measured 2026-09-07 against the real comparer
(`Compare(running, floor) < 0` ⇒ DECLINED):

| running \ floor | `3.0.0-rc4` | `3.0.0-rc8` | `3.0.0-rc9` | `3.0.0` |
|---|---|---|---|---|
| `3.0.0-ci.1` | DECLINED | DECLINED | DECLINED | DECLINED |
| `3.0.0-ci.7989` | DECLINED | DECLINED | DECLINED | DECLINED |
| `3.0.0-ci.999999999` | DECLINED | DECLINED | DECLINED | DECLINED |
| `3.0.0` | ok | ok | ok | ok |
| `3.1.0-ci.1` | ok | ok | ok | ok |

Note the last column: a **clean `3.0.0` floor is refused too**, because a pre-release ranks below its
own release. So it is not only the ~20 packages carrying retired `rc4`..`rc9` floors — a package
declaring the current line's clean version is equally un-landable while the fleet runs `3.0.0-ci.N`.
On both AKS portals at 08:20Z the observed line was:

```
HOLDING 3.0.0-ci.7989 — AI: the module requires platform 3.0.0-rc8 or newer
                         but this deployment runs 3.0.0-ci.7989
```

### 🚨 …but "newer than" and "satisfies the floor of" are NOT the same predicate

The tempting conclusion is "make the floor use this page's comparison". **That is measured to be
wrong, and the counter-example is one line up in this very design.** For an updater, `3.0.0` MUST
outrank `3.0.0-ci.7977` — otherwise a Stable install running a continuous build can never take the
clean release it is waiting for, which is the regression §2 exists to avoid. For a floor, the same
two strings must give the OPPOSITE answer: a `3.0.0` floor must be *satisfied* by `3.0.0-ci.7977`.

Same two strings, opposite required answers. So the shareable part is the **key**
(`PlatformReleaseOrder.BuildOrdinal` — the lineage), not the **predicate**. Forcing one predicate to
serve both would fix the floor by breaking the update path.

The overlap that *does* exist is the **same-line** case, and there the two must agree exactly: two
builds of one line compare by build number, numerically. Once the declared floors name a
`3.0.0-ci.<n>` (MeshWeaver.Plugins#1447) that is the whole of the shared surface, and
`VersionSelectTest.SameLineBuildsCompareNumerically_TheOneCaseTheFloorAlsoAsks` pins this side of it.

🚨 **Declared floors are not going away, so do not design as if they were.** The measured link probe
(core #3552) reads `ModulePlatformFloor.DeclineReason(minMeshVersion) ?? LinkHoldReason()` — the probe
short-circuits *behind* the declared floor and never runs unless the declared floor already passes.
The declared floor still gates first.

That is why `PlatformReleaseOrder` lives in `MeshWeaver.Plugin.Packaging`, beside
`NuGetVersionComparer` and in the lowest assembly both `MeshWeaver.PluginCatalog` (which owns
`ModulePlatformFloor`) and `Memex.Portal.Shared` (which owns `VersionSelect`) already reference
directly — the key is in reach of whoever writes the floor's own predicate, and neither call site
carries a private idea of lineage. `PlatformReleaseOrderTest` pins the boundary so the floor decision
is taken knowingly rather than by reusing the update one.

## 5. What this does NOT fix

- **The withdrawn tags themselves.** Ordering makes them lose; it does not remove them. Measured
  2026-09-07 (`az acr repository show-tags -n meshweaver --repository memex-portal-ai`): **1268 tags —
  798 `staging-*`, 48 `3.0.0-ci.*`, ZERO `3.1.0-*`, ZERO `rc*`.** The slip tags and the whole retired
  rc line are already untagged from all three repositories (manifests kept, reachable via
  `staging-*`), so the 2026-09-07 trap has no tag left to fire on. A future slip needs the same
  maintainer action — the selector's job is to make the slip harmless while its tags are still there.
- **The policy record losing its own policy** under its bookkeeping writes (issue #3542, proposal 3).
- **"Installed" is what the pod RUNS.** This reads `ShippedReleaseSeed.InstalledPlatformVersion` —
  the injected `MESHWEAVER_PLATFORM_VERSION`, never the record's `LatestAvailableTag`, which after a
  manual roll-back kept naming a version no pod ran.

## Where it lives

- `src/MeshWeaver.Plugin.Packaging/PlatformReleaseOrder.cs` — the lineage key (`BuildOrdinal`) and the
  update predicate (`Compare` / `IsNewer`), in the lowest assembly both call sites reach.
- `memex/Memex.Portal.Shared/SelfUpdate/VersionSelect.cs` — the tag-shape filters, the policy, the
  total order a heterogeneous listing needs, `CheckInstalledTag` and `SelectCandidates`. Pure; no hub,
  no registry, no Rx.
- `memex/Memex.Portal.Shared/SelfUpdate/SelfUpdateHostedService.cs` — `RunOnce` / `NothingToRoll`.
- `test/MeshWeaver.Graph.Test/PlatformReleaseOrderTest.cs` — the lineage key, the incident, and the
  measured floor boundary.
- `test/Memex.Portal.Shared.Test/VersionSelectTest.cs` — the ordering and the three-valued check.
- `test/Memex.Portal.Shared.Test/SelfUpdateStrandRecoveryTest.cs` — the poller, against a real mesh.

## Related

- [Release & Self-Update Strategy](/Doc/Architecture/ReleaseStrategy) — the channels, the policy, and
  what each one selects.
- [The Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract) — what a sealed,
  promoted set guarantees.
- [Release Process & Versioning](/Doc/Architecture/ReleaseProcess) — where the version number comes
  from and when the line moves.
