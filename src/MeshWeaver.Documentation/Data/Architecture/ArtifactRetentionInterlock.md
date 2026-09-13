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

## 🚨 A second registry: when an installation's images are somewhere this lane cannot lock

**Measured 2026-09-13: the protection lane was RED, and its reason was a false sentence.** The
01:18Z scheduled run ended:

```
##[error]AXIS 3 — installation `build` runs core c84c6c0 and its overlay
(Systemorph/Memex deployments/aks/build/values.build.public.yaml) pins no image at all,
so there is no repository in which to protect what it runs.
```

That overlay pins **two** images. Run through the shipped extractors, the real file answers
`ACR pins: 0`, `foreign pins: 2 → cr.meshweaver.cloud` — `memex-portal-ai:3.0.0-ci.8411` and its
migration twin, in **the fleet's own registry**. `REGISTRY_HOST_RE` matches `*.azurecr.io` and
nothing else, which is correct for a lane that locks one ACR, but it made "pins its images
elsewhere" spell identically to "pins nothing" — and *that* reading sends the reader to fix an
extractor that is working perfectly.

🚨 **And the red was not free.** `pause.reEnableWhen` is *"lock-pinned-digests is green"*, so a live
installation moving registry was standing between the fleet and re-enabling cleanup — while the
installation that IS exposed to the ACR purge (`memex`, six pins in this ACR) got no locks either,
because the run refuses as a whole. One instance's registry question was holding the other's
protection hostage.

`build` is the fleet's build server and it is the first LIVE installation on `cr.meshweaver.cloud`;
`pearl` pins there too and is declared not-installed, so it was never asked.

**What the mechanism now does.** Foreign references are *extracted and named*. They are never
locked — nothing here can write to another registry — but the facts stay distinct:

| the overlay | the run |
|---|---|
| pins in this ACR | protected, as before |
| names an **undeclared** registry, anywhere | **RED**, naming the host and what to write |
| pins **only** in a declared `fleet-unlockable` one | out of scope — counted on its own line, and that line says protection there is **UNVERIFIED** |
| pins **both** here and in a `fleet-unlockable` one | **RED.** Half its running set would be protected and half not, and the run would report success |
| pins **only** in declared `third-party` ones | **RED** — nothing it runs is then accounted for by any registry that holds our images |
| pins **nothing**, in any registry | RED, and the message now says "in ANY registry" so it means what it says |

### 🚨 The unit of declaration is the REGISTRY, not the installation

`instances.json` gains a top-level `registries` table, and a per-instance `registry` key is
**refused**. The reason is completeness: a per-instance field answers *"is this one out of scope"*
and can never answer *"is every registry the fleet pins in accounted for"* — an installation pinning
its declared registry **and** a second undeclared one would pass it with half its running set
unnamed. Two dispositions:

- **`fleet-unlockable`** — serves *our* images and this lane cannot lock them (`cr.meshweaver.cloud`).
- **`third-party`** — the images are somebody else's and were never ours to protect (`ghcr.io`).

Each needs a `reason`; an unknown disposition, a missing reason and an **undeclared host** are each
RED. An undeclared host reds **wherever it appears**, including on an installation that also pins
here — the branch a *"no in-scope repositories"* guard never reaches.

### The table is small because it was measured, not guessed

Across the fleet's **twelve** deployment overlays on 2026-09-13, exactly **two** foreign hosts:

| host | references | disposition |
|---|---|---|
| `cr.meshweaver.cloud` | 4 (`build` ×2, `pearl` ×2) | `fleet-unlockable` |
| `ghcr.io` | 1 (`ghcr.io/distribution/distribution` — the registry service's own image) | `third-party` |

🚨 **There was a second `ghcr.io` "reference", and it was a line of PROSE** in the `ci-runners`
overlay describing what the runner image is built from. This issue already paid for that lesson
once: of the 31 `sha256:` tokens in its 2026-09-08 hand count, **11 were comments**, several of them
narrating this very incident inside the files it broke. With an undeclared registry now a blocker, a
match inside a comment would red the lane over a sentence — so whole-line comments are stripped
before extraction, on both the ACR and the foreign path.

### Two more shapes the extractor had to learn

- **A `*.azurecr.io` that is not THIS registry is foreign.** `--registry meshweaver` locks
  `meshweaver.azurecr.io` and nothing else; treating every ACR as in-scope would extract another
  registry's repository and then look it up in — and lock it against — this one. That is this bug
  reintroduced one registry along.
- **Helm's split `repository:` + sibling `tag:`** form, which the ACR path has always handled. A
  foreign-only overlay written that way extracted as *nothing* and fell straight back into
  `pins no image at all` — this bug in its second shape.

🚨 **What retains `cr.meshweaver.cloud` is NOT established by this, and the report says so in those
words.** The out-of-scope line reads *"NOT locked here, and whether anything retains that registry is
UNVERIFIED"* rather than the earlier *"protected by that registry's own retention"* — a summary that
claims protection nobody has checked would make a green run read as covered and support re-enabling
cleanup on a false premise, which is this mechanism's own failure mode committed by its own report.
The declaration states that those images are out of *this lane's* reach; it does not claim anything
protects them. That question
is open on #3438 and it grows with every installation provisioned on the fleet's own registry —
which, per the new-deployment path, is now the default.

## The window is DECIDED, and the record is now held to it

The policy is #3842's, quoted verbatim in #3438's body and in #3859's acceptance list:

> *Retain unreferenced continuous artifacts for at least 30 days by age, without a build-count
> quota.*

**Three stores implement it; two of them were clamped to it in code and the third was not asserted
at all.** `PrebuiltBundleRetention` clamps `MinimumAge` up to 30 days in its planner (#3843) and
`AssemblyCacheRetention` does the same (#3846) — and both carry `KeepNewestPerSource` as a property
that explicitly no longer drives deletion. #3843's own body records the gap in as many words: *"The
ACR task record and live cloud cleanup are unchanged."* So `.github/acr-retention/purge-old-images.yaml`
kept `--ago 7d --keep 10` over five continuously-republished repositories — `memex-portal-ai`
included, the image **both production portals run** — and nothing compared it to the rule that
governed the other two.

🚨 **`--keep` is the half an age window cannot replace.** `--keep N` counts NEWER BUILDS, so the
more often a repository is republished the FASTER its older manifests become eligible. A 30-day
`--ago` beside a `--keep 10` still collects a manifest ten builds old on the day it is written.
That is #3438's own root cause — *republishing frequency is what destroys a pin* — restated as a
flag, so a quota is removed rather than raised.

🚨 **The flag that counts is the one on the command that DELETES, and a quota has two spellings.**
A `cmd:` is a shell line: `echo --ago 30d; acr purge --filter 'x:.*' --untagged` carries the window
on the line and not on the purge. So each `acr purge` invocation is isolated at the first shell
separator and checked on its own — **every** invocation on the line — and the quota check reads
`--keep N`, `--keep=N` and a bare `--keep` alike. A bare `--ago` with no duration is *unchecked*,
which is a failure, not a pass.

🚨 **And a declaration whose switch is not a boolean is fail-open, silently.** Every reader of
`pause` and `recordAheadOfRegistry` asks `inForce is True` — correct for a JSON boolean, and
catastrophic for `"true"`, which is not `True`. One typed quote mark would disarm the apply
interlock, the `record` overwrite guard and the coherence gate at once, while reading to a human as
if it were armed. The shape is asserted on every pull request, and both shell halves **fail closed**
on the same condition rather than trusting that to have run.

`lock-pinned-digests.py --check-retention-record` now asserts both halves over **every** recorded
purge step, enabled or not — the record is what `acr-retention-tasks.sh apply` pushes, so a disabled
task carrying a 7-day window is a 7-day window one command away from running. It needs no
credential and runs on every pull request (`dotnet-test.yml`, the workflow-shell lane). An `--ago`
it cannot parse is RED, not a default: a window nobody could read is one nobody checked.

**This is a RECORD change, not a registry change.** The live task is disabled and still carries the
old window; `tasks.json` declares that deliberate gap under `recordAheadOfRegistry`, and three
things follow from the declaration rather than from anyone remembering it:

- `acr-retention-tasks.sh verify` prints the declared drift instead of reporting it as an incident
  — a drift report that is always red is one nobody reads — **and reds when a declared task shows
  NO drift**, because after `apply` the declaration excuses nothing and would excuse the next real
  drift. 🚨 **The exemption covers the WINDOW moving, never the task becoming something else:** the
  declaration is keyed by task name, so without a second check a live definition that had gained
  `--include-locked`, swapped its filters or stopped being an `acr purge` would print as *declared*
  and leave `verify` green — and the record-side checks read the recorded file, so they would never
  see it. The LIVE steps of a declared task are held to all three.
- `acr-retention-tasks.sh record` — the one command that overwrites the record FROM live — refuses
  without an explicit `OVERWRITE`, because re-recording would silently restore `--ago 7d --keep 10`
  into a file whose comments explain at length why it must not say that.
- The gate above reds on the restored window on the very next pull request.

## The re-enable interlock — the one edge this repository owns

#3859's first acceptance criterion is *failed / unavailable / incomplete protection collection
cannot be followed by deletion*. `acr-retention-tasks.sh apply` is the only thing in this repository
that can turn a destructive schedule back on: it pushes `status` out of `tasks.json` with
`az acr task update --status`. It used to do that with reference to nothing — so changing one word
in `tasks.json` (`Disabled` → `Enabled`) and running `apply` restored the 03:00 purge with **no
protection decision consulted at all**, while the `pause` block two screens below still said the
protection was incomplete.

`apply` now **refuses** while `pause.inForce` is true and any task is recorded `Enabled`, naming
`pause.reEnableWhen`, and it refuses *before* the confirmation prompt so the prompt cannot be
mistaken for the gate. It refuses rather than warns, and it asks for no override flag: a prompt
answered "yes" is not a decision anyone can audit, whereas deleting the `pause` block in a reviewed
diff that says what satisfied `reEnableWhen` is. The record's own coherence is gated too — a record
asserting both an in-force pause and an `Enabled` task is RED, and so is every task disabled with no
declaration explaining it, because a stopped retention and a stale record otherwise read identically.

🚨 **This does not close #3859, and the distinction is the whole of what is left.** It interlocks
the ACT of re-enabling. It does not interlock the nightly deletion: the lock is still an Actions
cron at 01:00 and the purge still an ACR timer task at 03:00, so a given night's deletion is still
not downstream of that night's protection verdict.

## What is still the maintainer's, and is not code

0. **What retains `cr.meshweaver.cloud`.** The fleet's own registry now serves at least one live
   installation and is the default for new ones, and `acr purge` cannot reach it. This lane
   declares those images out of its scope; nothing yet says what keeps them, or deletes them.
   That is the same question this page answers for the ACR, asked again about a second store.
1. **The decided window has not been APPLIED to the registry.** The record states it; the live
   (disabled) task still carries `--ago 7d --keep 10`. Applying it is the same act as lifting the
   pause, and it belongs to whoever owns the registry. Its cost is storage: dropping `--keep 10`
   and moving 7d → 30d retains strictly more, over five repositories that republish many times a
   day.
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
- [ImageCleanup](/Doc/Architecture/ImageCleanup) — the operator page: how to ask whether the purge is running, the disabled-task/enabled-trigger trap, and why `--keep N` is not "keep the N newest"
- [PinnedImageRetention](/Doc/Architecture/PinnedImageRetention) — the lock job's history, the two purge tasks and the incidents
- [ReleasedArtifactRetention](/Doc/Architecture/ReleasedArtifactRetention) — the policy contract (#3842): 30 days by age, no build-count quota
- [PrebuiltBundleRetention](/Doc/Architecture/PrebuiltBundleRetention) — the store this protects in the portal
- [PinSetConsistency](/Doc/Architecture/PinSetConsistency) — the extractor the digest axis shares
- [ModuleBuildArchitecture](/Doc/Architecture/ModuleBuildArchitecture) — why a pin is meant to be stable for weeks
