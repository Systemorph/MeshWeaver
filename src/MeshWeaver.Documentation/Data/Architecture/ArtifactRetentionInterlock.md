---
Name: ArtifactRetentionInterlock
Category: Architecture
Description: Cleanup may only delete what a COMPLETE and FRESH inventory of consumers shows to be unreferenced — the one mechanism behind the four retention issues, the denominator it must state, and the two things it locks
Icon: ShieldLock
---

# The Artifact Retention Interlock

**One sentence, and it is the whole of it: a cleanup whose protection set is derived from a stale or
incomplete source deletes something that is still in use.** Every retention incident in this fleet is
an instance of that sentence, and the four issues filed against it — [#3438](https://github.com/Systemorph/MeshWeaver/issues/3438)
(the parent), #3858 (a complete and fresh inventory), #3859 (interlock cleanup with protection), and
#3860 (the full artifact set) — are four views of one mechanism rather than four pieces of work.

> 🚨 **STATUS, 2026-09-12: the purge is PAUSED and there is no live clock.** Roland ordered it
> stopped while protection was incomplete, and `purge-old-images` was disabled (`az acr task list`
> → `status: Disabled`; Memex#219). **Quote no deletion date while that holds.** The pause is the
> MITIGATION and this mechanism is the FIX, so **this is the precondition for safely turning the
> task back on, not a race against a deadline.** A disabled task still reports an `Enabled` TIMER
> TRIGGER at `0 3 * * *` — read the task's `status`, not the trigger's. The condition for
> re-enabling is recorded in `.github/acr-retention/tasks.json` under `pause.reEnableWhen`, and the
> protection report prints it in place of a window.

It has cost, so far: a public brand site 503 for ~11 h (Memex#122), three satellite repositories'
CI dead simultaneously (2026-09-05T15:29Z, #3438), a migration Job in `ImagePullBackOff` 639 times
unalerted (Memex#219), and every MeshWeaver.Plugins run blocked at preflight (2026-09-07).

## The shape of the failure, every time

| The protection set was derived from | …and the thing in use was |
|---|---|
| a human remembering to add a repository to the right one of two purge tasks | a repository in neither |
| the tags a repository republishes often | a **digest** something pinned (#3438) |
| committed workflow pins | a **tag** a deployment overlay pinned (Memex#122, #141) |
| committed deployment overlays | the image an instance was **rolled onto** ahead of its pin (Memex#219, 2026-09-12) |
| a report an instance filed | a report that **never arrived**, or arrived weeks ago |
| the **manifest** the pin resolves to | the **tag** that is the actual reference (measured 2026-09-12) |

Note what is NOT on that list: a window that was too short. **Raising `--ago` or `--keep` moves the
cliff; it does not remove it**, and a pin left stable for a quarter walks off the new one just the
same. So does a bigger keep count, a retry, and a `continue-on-error` on the step that fetches the
protection set. Those knobs are what this mechanism exists to make unnecessary.

## The contract

> **Cleanup is eligible only downstream of a protection decision that is COMPLETE and FRESH, and an
> incomplete inventory is a REFUSAL rather than a smaller number.**

Three consequences, and each is enforced rather than described:

1. **The denominator is stated in the output.** How many installations were expected, how many
   answered, how many digests and tag references were protected, over what window. A run that
   protected nothing and a run that had nothing to protect must not print the same thing.
2. **Absence of evidence is never zero consumers.** An installation that did not answer contributes
   exactly the same reference list as an installation that consumes nothing, and one of those two
   readings authorises deleting what it is running.
3. **Retirement is a declaration, never an inference from silence.** A portal that is down and a
   portal that was decommissioned are the same silence.

## Where the protected set comes from — four axes, and what each one alone cannot see

`.github/scripts/lock-pinned-digests.py` derives the registry half nightly at 01:00 UTC, two hours
before the 03:00 `purge-old-images` task.

| Axis | Source | What it alone misses |
|---|---|---|
| 1 | digest pins in every repository's `.github/workflows` | tags; anything resolved at run time |
| 2 | image **tag** pins in the deployment overlays (`values*.yaml`) | an instance running ahead of its committed pin |
| 3 | **each installation's own `/api/version`** — what it is actually RUNNING | an image no instance has pulled yet |
| — | official release tags (`v?X.Y.Z`) in the publisher's repositories | — |

### Axis 3, and why the committed pin is only a proxy

An overlay says what an instance *should* run. `/api/version` says what it *does*. On
2026-09-12 those disagreed on a production instance: memex-cloud was rolled to `3.0.0-ci.8399` at
06:15Z while its committed pin still read `8372`, so that night's lock protected the manifest it was
not running — against a purge task that filters `memex-portal-ai:.*` at `--ago 7d --keep 10`.

The route is the instance's own unauthenticated version endpoint
(`MapVersionEndpoint`, `.AllowAnonymous()`), whose whole contract is `{ "version": …, "commit": … }`.
The expected set and each host come from the same overlays axis 2 already reads:

```yaml
config:
  memex_portal:
    Hosting__Deployment: "memex-cloud"     # the id, equal to its Hosting/Deployment record's
ingress:
  host: "memex.meshweaver.cloud"           # where to ask it
```

🚨 **The bare short-sha tag cannot identify a build, so the axis protects a CLOSURE.** Measured
2026-09-12: core `4c99ec26` produced **two** image sets ninety minutes apart — `3.0.0-ci.8399` /
`4c99ec2-p38ebf08` and `3.0.0-ci.8401` / `4c99ec2` — because the plugins half moved underneath it,
and the bare `4c99ec2` tag followed the newer one. `/api/version` answers the core commit and
nothing finer, so *which* of the two an instance runs cannot be decided from outside it. The axis
therefore protects every manifest tagged for that commit (`<short>`, `<short>-p<plugins>`,
`staging-<short>-<run>`) in each repository the instance's overlay pins, and says so. Locking a
superset is safe — a lock destroys nothing — and guessing a member of it is not.

An installation that answers a commit **no manifest carries** is red: either the set it is running
has already been purged, which is the incident recurring, or the tag scheme moved and the axis
stopped matching. Neither is a pass.

### The roster: `.github/acr-retention/instances.json`

The overlays are the denominator; that file only ever *explains an absence*. An installation missing
from it is **live**, so forgetting an entry makes a run stricter and never looser. A non-live entry
needs a `state` (`not-installed` / `retired`) and a `reason`, and an entry naming an installation no
overlay declares is red — a stale exemption hides the next one.

## The two locks — the bytes and the reference are different objects

🚨 **A TAG carries its own `deleteEnabled`, and it is the one the purge reads when it deletes a
tag.** Measured on `meshweaver.azurecr.io`, 2026-09-12:

| | `deleteEnabled` |
|---|---|
| tag `memex-portal-ai:3.0.0-ci.8372` — what both production overlays pin | **true** |
| the manifest it resolves to (`sha256:0217fd11…`) | false (locked) |
| all 1,396 tags of `memex-portal-ai` | **0 locked** |

`Azure/acr-cli` decides tag deletion on the tag:

```go
if includeLocked || (*(*tag.ChangeableAttributes).DeleteEnabled && *(*tag.ChangeableAttributes).WriteEnabled) {
    tagsEligibleForDeletion = append(tagsEligibleForDeletion, tag)
}
```

So a manifest lock saves the **bytes** and loses the **reference**: the purge deletes the tag, the
locked manifest survives untagged, and `…/memex-portal-ai:3.0.0-ci.8372` answers `manifest unknown`
to the next pull — the same wedge, from a fully protected manifest. Every tag reference the fleet
depends on is therefore locked too, `deleteEnabled` only: `writeEnabled: false` would additionally
refuse the retag `release.yml` promotes with, and buys no purge protection, because acr-cli already
requires **both** to be true before it will delete.

🚨 **For a RUNNING manifest that means every one of its tags, not only the ones the axis matched on.**
The pod spec names `memex-portal-ai:3.0.0-ci.N`; `/api/version` answers the core commit; the axis
matched the short-sha tags. While an instance is AHEAD of its committed pin that `3.0.0-ci.N` tag is
in **no** committed file, so axis 2 never sees it — and from outside the cluster there is no way to
tell which of a manifest's names the spec used. Protecting the manifest's whole set of names is the
decidable move; protecting one of them is a guess that fails on the next restart.

### 🚨 And a locked index does not protect its platform manifests — it REMOVES the protection they had

The third lock, and the least obvious. From `Azure/acr-cli`'s `GetUntaggedManifests`, in statement
order:

```go
if _, ok := ignoreList.Load(*manifest.Digest); ok { continue }
if !includeLocked && manifest.ChangeableAttributes != nil {
    if …DeleteEnabled != nil && !(*…DeleteEnabled) { continue }          // ← a LOCKED index exits HERE
    …
}
…
if isProtectedByTags || isProtectedByAge {
    if *manifest.MediaType != v1.MediaTypeImageIndex && … { continue }
    group.SubmitErr(func() error { … addDependentManifestsToIgnoreList(…) })   // ← the walk
    continue
}
```

The lock `continue` comes **before** the walk that adds an index's children to the ignore list. So a
locked index is never walked, its children are never ignore-listed, and each untagged child is then
judged on its own: no tags, older than `--ago` ⇒ collected. The locked index is left pointing at
manifests that no longer exist, and the pull fails exactly as if the image had been deleted.

**The corollary is what makes this more than a gap.** An index that is TAGGED and UNLOCKED reaches
`isProtectedByTags`, IS walked, and its children ARE ignore-listed. Locking it short-circuits that.
**Protection applied to the index alone makes its children strictly less safe than leaving it
unprotected** — a fix that causes the failure it prevents, on a delay.

Measured 2026-09-12: `memex-portal-ai@sha256:0217fd11…`, the set `memex` is RUNNING, is locked, and
its two children (`sha256:3296b0ba…` linux/amd64, `sha256:322de2ff…` linux/arm64) both read
`deleteEnabled: true`. They survive today only because they still carry
`staging-74d4c85-…-linux-x64` / `-linux-arm64` tags — which the **same** purge step deletes once
they pass `--ago 7d`.

So every protected manifest is expanded to its closure: an index pulls in its platform manifests,
transitively, and a closure that could not be enumerated is a blocker rather than an empty one.

## How exposed this actually is — sized 2026-09-12, read-only

Worth having in one place, because the intuition about `--keep` is wrong in a way that makes the
exposure look both larger today and smaller later than it is.

**`--keep` is applied AFTER `--ago`, over the eligible set only.** From `getTagsToDelete`:

```go
if lastUpdateTime.Before(timeToCompare) {
    if includeLocked || (*(*tag.ChangeableAttributes).DeleteEnabled && *(*tag.ChangeableAttributes).WriteEnabled) {
        tagsEligibleForDeletion = append(tagsEligibleForDeletion, tag)
    }
}
…
for _, tag := range tagsEligibleForDeletion {
    if skippedTagsCount < keep { skippedTagsCount++ } else { tagsToDelete = append(tagsToDelete, tag) }
}
```

So `--keep 10` does **not** mean "the ten newest tags in the repository". It means "of the tags
already older than `--ago`, spare the ten newest". A tag younger than seven days is never eligible,
however many newer builds exist — and a tag older than seven days is spared only until ten more
tags cross the same line behind it. The timestamp compared is the **tag's** `lastUpdateTime`, not
the manifest's creation and not a pull time.

| Measured on `memex-portal-ai`, 2026-09-12T09:09Z | |
|---|---|
| tags | **1,402** |
| …with both `deleteEnabled` and `writeEnabled` true (acr-cli needs both) | **1,402 — none locked** |
| publication rate, mean over the 7 complete days before | **193.1 tags/day** |
| …so the 10-tag keep window is consumed in | **1.2 h** |
| oldest tag in the repository | **exactly 7 days** |
| eligible at a run, were one to happen | 49 |
| deleted at such a run | 39, none version-shaped |

The last two rows are the ones that matter: **the repository is already in steady state at a hard
seven-day horizon.** `--ago 7d` is the entire policy; `--keep` is noise at this publication rate.
Every figure here describes what the task does *when enabled* — it is disabled as this is written,
which is why the horizon is a property of the policy rather than a countdown.

### The worked case, and why it is one run rather than two

`3.0.0-ci.8372` is what `memex` runs and what both overlays pinned. Its six tags cross the seven-day
line within **2m46s** of each other:

```
18:06:02  staging-74d4c85-…-linux-x64     → child sha256:3296b0ba…  (linux/amd64)
18:07:16  staging-74d4c85-…-linux-arm64   → child sha256:322de2ff…  (linux/arm64)
18:07:17  staging-74d4c85-34627334628     → the index sha256:0217fd11… (LOCKED)
18:08:10  74d4c85 / 74d4c85-p24c2d02      → the index
18:08:48  3.0.0-ci.8372                   → the index
```

so a **single** run strips the children's tags in `purgeTags`, then re-lists manifests, finds
the children untagged and past the cutoff — and skips the locked parent index before the walk that
would have ignore-listed them. What survives is a locked index pointing at two manifests that no
longer exist.

**The consequence is recoverable for the minutes between those two phases and not afterwards.** While
the index manifest is intact, `docker buildx imagetools create --tag <repo>:<tag> <repo>@<digest>`
restores the reference. Once the platform manifests are gone, only a rebuild does.

And what breaks meanwhile is narrower than "nothing can start": the portal container carries
`imagePullPolicy: "IfNotPresent"`, so a pod restarting on a node that still holds the layers starts
without pulling. That reprieve is node-local and not durable — kubelet garbage-collects unused images
under disk pressure. What fails for certain is a pod scheduled onto a node without them (scale-up,
node upgrade or replacement, eviction, a new nodepool) and the migration Job, whose tag helm derives
from the portal's (Memex#219's measured case: 639 `ImagePullBackOff` in 146 minutes, unalerted).

🚨 **And the instance whose index is not locked at all is the more exposed one.** Measured the same
morning, `memex-cloud` runs `3.0.0-ci.8403` / `sha256:81fe4f29…` with `deleteEnabled: true` on the
index itself.

## The instrument is measured, not inferred from the fleet

The old axis-1 rule read: *"six repositories pinned a digest on 2026-09-06, so zero means the
extractor stopped matching"*. On 2026-09-12 the scheduled run
([34664099031](https://github.com/Systemorph/MeshWeaver/actions/runs/34664099031), 01:12Z) died on
exactly that line — two hours before the purge — and the premise was simply no longer true: #3842
moved every satellite to **resolving** the platform set at run time (`platform-ref` /
`resolve-platform.py`), and measured with this repository's own extractor the fleet declares zero
digest pins and six repositories that name a platform image without pinning one. Pinning did stop.

**An assertion about the FLEET can expire like that; an assertion about the INSTRUMENT cannot.** So
`extractor_control()` runs `extract()` over a fixture carrying two known digest pins on every run and
reds when they stop being found — which is strictly stronger, because it fires even when the fleet
happens to declare pins anyway. The denominator that survives is the one that is still impossible:
a fleet naming **no** platform image anywhere.

## The same contract in the portal — the prebuilt-bundle store

The registry is not the only cleanup that derives a protected set from an inventory.
`PrebuiltBundleRetention` prunes the prebuilt-bundle store, and its reference source is
`DeploymentPinnedReferences` on a control instance: the `Hosting/Deployment` records' pins and every
registered instance's `Hosting/ModuleInventory` report.

[DeploymentInventory](/Doc/Architecture/DeploymentInventory) landed the per-report half — a report
that read its adoption stamps incompletely says so, and aborts the pass. What was missing is the
**fleet** half, and that doc named it: *"these fields describe an individual report, not proof that
every fleet member reported or that an old report is current"*. `DeploymentPinnedReferences.Resolve`
now takes the Deployment records as the denominator:

- every non-retired record is an **expected consumer**;
- one with no report, an unreadable report, or a report whose `sampledAt` is older than
  `StaleAfter` (24 h — twenty-four consecutive missed ticks of the one-hour reporter) refuses the
  whole pass, naming it;
- a record declaring `retired` / `retiredAt` leaves the expected set, and that exclusion is logged;
- reports from instances with **no** record are still read: an unexpected consumer is a consumer;
- the denominator is logged on every pass.

🚨 **Zero expected consumers is a true answer on a host that is nobody's fleet.** The source is
registered on every portal, not only the control instance, so an ordinary installation holds no
Deployment records and has nothing to account for — its own live identity, its adoption stamps and
the 30-day floor are what protect it, and refusing there would wedge retention fleet-wide.

Two zeroes are refused instead, and they are different readings of the same empty answer. A host that
**receives reports and holds no records** is a control instance by construction. And a host that
**names itself and posts its report nowhere** (`Hosting:Deployment` set, `Hosting:ReportTo` unset) is
one too — by its own configuration, which is authoritative where the record query is only eventually
consistent. Without that second signal, a control instance whose Deployment *and* inventory indexes
have both not caught up answers exactly as an ordinary portal does, and an empty protected set then
authorises collecting every remote consumer's artifacts.

A `sampledAt` in the **future** is an unknown age, not a fresh one: a negative age passes the budget
trivially, so one skewed clock would make every report that producer files permanently fresh —
protecting a single identity for ever while the installation moves on.

### 🚨 What this refuses on day one, and the one-off it is asking for

Measured 2026-09-12 on the control instance: three `Hosting/Deployment` records — `memex`,
`memex-cloud` and `pearl` — all `Active`, none declaring retirement. `pearl` **has never been
installed**, so it files no report, so it is an expected consumer that cannot be accounted for and
the prebuilt-bundle pass refuses.

That refusal is the mechanism working, not a defect: "is `pearl` a consumer?" is exactly the question
retention must answer before it deletes anything, and until now it was answered by silence. It costs
nothing operationally today — the chart disarms bundle deletion by default
(`PreWarm:PrebuiltBundleRetention:Delete=false`, see
[PrebuiltBundleRetention](/Doc/Architecture/PrebuiltBundleRetention)) — so what it does is surface
the question rather than wedge a live sweep.

**The one-off it asks for is one field**, and it is a record write in the private deployments repo
rather than anything this repository can do: give `pearl`'s record `retired: true` or a `retiredAt`
stamp with the reason, or install it. The registry lane already carries the same declaration in
`.github/acr-retention/instances.json`, with the reason and the issue; the two are deliberately
separate files because they are two different stores, and neither infers the other's answer.

## What is still the maintainer's, and is not code

1. **`purge-old-images` carries `memex-portal-ai` on its 7-day step** — the image both production
   portals run — while the 30-day step covers only `memex-portal`, a repository no overlay pins.
   Whether that is the intended policy is a decision, not a defect.
2. **The two clocks are still two clocks.** The lock is a GitHub Actions cron at 01:00; the purge is
   an ACR timer task at 03:00; nothing makes the second wait for the first. A pin that lands in that
   window meets the purge unprotected, and if the lock job does not run at all the purge still does.
   The structural fix is to make cleanup a step *downstream* of the protection decision — one lane,
   the destructive step `needs:` the complete verdict, the ACR timer task disabled. That is a
   registry change and a destructive-schedule change, and it belongs to whoever owns the registry.
3. **The `latest`-tag question.** A moving tag is named by no file and is outside this model. Four
   of five filtered repositories have no `:latest`; the survivor survives by being quiet.
4. **The `Container Registry Repository Writer` grant** is what lets the lane write at all.
5. **`pearl`'s Deployment record needs its retirement declared, or `pearl` needs installing** — see
   above. One field, in the deployments repo.

Until (2) lands, what this mechanism buys is that the exposure is **visible instead of silent**: a
run states its denominator, and an incomplete one refuses rather than quietly protecting less.

## Related

- [DeploymentInventory](/Doc/Architecture/DeploymentInventory) — what each instance reports about itself, and the per-report completeness signal
- [PinnedImageRetention](/Doc/Architecture/PinnedImageRetention) — the lock job's history, the two purge tasks and the incidents
- [ReleasedArtifactRetention](/Doc/Architecture/ReleasedArtifactRetention) — the policy contract (#3842): 30 days by age, no build-count quota
- [PrebuiltBundleRetention](/Doc/Architecture/PrebuiltBundleRetention) — the store this protects in the portal
- [PinSetConsistency](/Doc/Architecture/PinSetConsistency) — the extractor the digest axis shares
- [ModuleBuildArchitecture](/Doc/Architecture/ModuleBuildArchitecture) — why a pin is meant to be stable for weeks
