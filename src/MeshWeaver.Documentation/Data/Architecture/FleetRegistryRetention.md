---
Name: FleetRegistryRetention
Category: Architecture
Description: What may be deleted from cr.meshweaver.cloud and what proves something is still needed — the declared rule, the derived protected set, and why a registry with NO LOCK needs a stricter rule than the ACR rather than the same one
Icon: ShieldLock
---

# Retention on the fleet's own registry

`cr.meshweaver.cloud` is the fleet's **own** container registry — off-the-shelf CNCF
`distribution` beside a `docker_auth` token server, configured and not extended
([ContainerRegistryInMemex](/Doc/Architecture/ContainerRegistryInMemex)). It is the default for newly provisioned
instances, it already serves a live one, and **everything in
[ArtifactRetentionInterlock](/Doc/Architecture/ArtifactRetentionInterlock) is about the other registry**:
`meshweaver.azurecr.io`. The lock, the tag lock, the index closure, the 30-day window, the
interlock between two clocks — all of it is `acr`, and `acr purge` cannot reach this host.

So the question [#4230](https://github.com/Systemorph/MeshWeaver/issues/4230) asks is the same one
[#3438](https://github.com/Systemorph/MeshWeaver/issues/3438) answers for the ACR, asked about a
second store: **what may be deleted, and what proves something is still needed.**

> 🚨 **The one-sentence answer, and it is a RULE rather than an observation: nothing deletes from
> `cr.meshweaver.cloud` today, and nothing may, until a cleanup can derive a protected set over
> what the registry actually holds.** `distribution` has no lock. On the ACR the protected set is
> written into the registry as state, ahead of the deleter, and the registry enforces it. Here
> there is no such object, so the derivation IS the entire safety margin — with nothing behind it.

## 1. What deletes today — enumerated, with the denominator

Measured 2026-09-14 (`date -u`) against core `origin/main` `192bb073b8`, read-only throughout: no
registry call of any kind, no purge, no dry run, no deletion.

🚨 **Two denominators, and they are different numbers on purpose.** The hand sweep that produced
this table read **all 409 files** under `deploy/` and `.github/`, every extension included. The
GATE (§6) sweeps the **246** of them that could actually run a command — `.yml`, `.yaml`, `.sh`,
`.py`, `.tpl`, `.bicep` — and skips 3 it names: this script, which carries every pattern as a
literal, and the ACR record, whose job is to name deleters. Neither number is the other's; a reader
who takes 246 for a shortfall against 409 is reading two sweeps as one.

Both were run with the pattern **proven to fire on a synthetic control first** — a file containing
each deleter spelling — because a grep that matches nothing is not evidence until it has been shown
able to match something.

| mechanism | present? | what it could delete | verdict |
|---|---|---|---|
| `acr purge` / the two recorded ACR tasks | **cannot reach this host** — `az acr` addresses `meshweaver.azurecr.io` | — | not a deleter here, and this is the fact that makes #4230 a separate question |
| `maintenance.uploadpurging` (registry `config.yml`) | **YES — enabled**, `age: 168h`, `interval: 24h`, `dryrun: false` | **incomplete upload sessions only** — the `_uploads/` scratch state of a push that never finished | the ONE automatic deletion in this registry, and it can never reach a manifest, a tag or a referenced blob |
| `registry garbage-collect` (blob GC) | **NO** — no `Job`, no `CronJob`, nothing under `deploy/helm/templates/registry/` (8 rendered objects, none of them a Job) and no invocation anywhere in `deploy/` or `.github/` | unreferenced blobs, permanently | never runs. Storage therefore grows without bound, which is the other half of §1's answer |
| an explicit registry `DELETE` | **NO CALLER** — zero matches over the 409-file hand sweep for an HTTP DELETE (`-X DELETE`, `-XDELETE`, `--request=DELETE`) or a crane / skopeo / regctl / oras / `az acr repository` deletion | any manifest or tag | CD only ever **pushes** here (`mirror-image-to-registry.sh` for images, `node-repo-publish-bake.yml` for bundles) |
| anything authenticating as a non-publisher account | **NO** — by the ACL, not by a grep | — | `docker_auth`'s ACL grants `delete` to **exactly one account**, the publisher. Every other authenticated account matches a `pull`-only rule, and anonymous matches no rule at all. An installation holding an instance key **cannot** delete, whatever it asks |
| an Azure blob lifecycle / management policy on the registry's storage container | **UNVERIFIED** — and that is a state of its own in the record, not a `false` | any blob, including a referenced one | 🚨 **the one remaining unknown, and it is a maintainer read.** The storage account is `registry.storage.accountName`, provisioned outside this chart; `storage.bicep` in this repo is pgBackRest's, not the registry's. See §7 |

🚨 **One row's PROVENANCE is different from the rest, and it is load-bearing.** Everything above is
read off committed files except the *meaning* of `maintenance.uploadpurging`: what that key deletes
is `distribution`'s own behaviour (the `_uploads` scratch tree of pushes that never completed), not
something this repository measured. It is the configuration of an off-the-shelf component we render
rather than extend, so upstream's contract is the right source — but it is the one line here that a
`distribution` major version could change under us without any file in this repo moving. What would
falsify it: that key reaching a tagged manifest or a referenced blob. The chart pins the image by
digest (`ghcr.io/distribution/distribution:3.1.1@sha256:…`), so the version this statement is true
of is pinned beside it; a bump is the moment to re-read it.

**So both of #4230's dangerous readings are answered, and it is the second one.** This is not
silent deletion — it is unbounded growth. The registry keeps every image and every bundle ever
pushed to it, plus the blobs of everything ever pushed, and nothing has ever collected either.

That is a cost, not an incident, and it is the safe direction to be wrong in. The hazard is the
**fix**: the day somebody adds a cleanup, it will have nothing to protect what it must not delete.

## 2. 🚨 Why this registry needs a STRICTER rule than the ACR, not an equivalent one

The instinct is to port `lock-pinned-digests.py` one registry along. It does not port, and the
reason is structural rather than an implementation gap.

| | ACR `meshweaver.azurecr.io` | fleet `cr.meshweaver.cloud` |
|---|---|---|
| the protection primitive | `changeableAttributes.deleteEnabled` on the manifest **and** on the tag | **none exists** |
| who enforces it | **the registry.** `acr purge` skips a locked manifest unless `--include-locked` is passed, and neither recorded step passes it | nothing does. The deleter's own derivation is the only thing standing between a live reference and a `DELETE` |
| what a protected set IS | *persistent state, written ahead of the deleter, by a different process, on a different clock* | *a list in the memory of the process that is about to delete* |
| a run whose derivation is INCOMPLETE | writes fewer locks. Everything locked on an earlier night **stays locked** | **deletes what it could not see, in the same act** |
| a lane outage | degrades gracefully. `lock-pinned-digests` was red for two days in September 2026 and the previously locked manifests were never at risk | there is nothing to stand. An outage is either "no cleanup ran" or "a cleanup ran on a stale derivation" |
| the ordering problem | real and open ([#3859](https://github.com/Systemorph/MeshWeaver/issues/3859)): the lock is an Actions cron at 01:00, the purge an ACR timer at 03:00, and nothing makes the second wait for the first | **absent — and that is not a simplification to celebrate.** Fusing derive-and-delete into one act removes the interlock problem by removing the interlock |

The last row is the one to read twice. *Having no two clocks* looks like the better design until you
ask what happens on the bad night. On the ACR, a derivation that is wrong on one night is survivable
because the previous night's locks are still in the registry. Here, a derivation that is wrong once
deletes once, and there is no earlier night's work to fall back on.

**Therefore the rule below is stricter than the ACR's, deliberately.** Every clause exists because
the second line of defence that makes the ACR's version survivable does not exist here.

## 3. The rule

### R0 — Nothing deletes from `cr.meshweaver.cloud`, and that is a DECLARED state

§1 is its evidence and `.github/acr-retention/instances.json` is its record: the `registries` table
entry for this host carries a `retention` block naming **every** mechanism that could delete, each
with a verdict. The declaration is held by a gate (§6) that re-derives the chart's own deleters and
reds when the chart and the record disagree — so adding a GC `CronJob`, or changing the
`maintenance:` stanza, cannot land while the record still says nothing deletes.

🚨 **An enumeration in which nothing is present is an enumeration that inspected nothing.** The
record must name at least one mechanism that IS present — today `uploadpurging` — or the gate reds.
"Zero deleters found" and "the sweep did not run" read identically otherwise, which is the confusion
#3438 is made of.

🚨 **And `present` has THREE states, because "measured, and it is not there" and "nobody could look"
are different facts.** The lifecycle row of §1 is the second kind: the storage account is
provisioned outside this chart, so no committed file can answer it. Filing that as `false` would let
the declaration read as *fully measured* over an open question — the same
not-checked-spelled-as-clean confusion one level down. So it is `"unverified"`, it must name a
`verifiedBy` (what would answer it), and it is **printed on its own line on every run whatever the
verdict**.

**What the unknown blocks is the ACT, not the gate.** `cleanupAuthorized` is the record's own
statement that deleting here would be safe, and it **may not be `true` while any mechanism is
unverified** — the gate reds naming the open row. Making the *gate* red instead would be a check
that stays red until somebody reads an Azure storage account, and a check that is always red is one
nobody reads (this lane learned that on 2026-09-07, over a trailing newline).

### R1 — A cleanup may not be added until it derives a protected set, and an INCOMPLETE derivation deletes NOTHING

Not "deletes less". Nothing. Every input the derivation needs — each expected installation's
`/api/version`, each deployment overlay, each page of the registry's own catalog listing — is either
read in full or the run refuses and deletes nothing, naming what it could not read.

On the ACR this clause is a nicety (an incomplete run just writes fewer locks, and the old ones
hold). Here it is the whole safety property.

### R2 — The protected set is DERIVED. Age only ever NARROWS the complement; it never defines it

A window is not a protected set. `--ago 7d --keep 10` is what destroyed the pins in #3438, and the
lesson was stated there as a flag: **republishing frequency does not protect a pinned reference — it
is what destroys it**, because a build-count quota counts newer builds. Raising the number moves the
cliff.

The order is fixed and may not be inverted: compute what is referenced → subtract it from what the
registry holds → *then* apply an age floor to the remainder. A cleanup that starts from a filter and
adds exceptions is the shape this rule exists to refuse.

### R3 — What the derivation could not SEE is PROTECTED, not skipped

A repository the catalog walk could not enumerate, a tag page that failed, a manifest whose
referrers could not be read: each one is added to the protected set, and the run says so. The
failure mode this closes is the one that has already been paid for twice here — an instrument
answering confidently about a set it never looked at.

### R4 — Deletion is TWO-PHASE, and the phases are days apart

`distribution` separates the two, and the separation is the only reversibility available:

1. **Delete the manifest / untag.** The blobs stay. Re-pushing the identical manifest restores the
   reference and uploads nothing, because the layers are still there and mount by digest. This phase
   is recoverable.
2. **`registry garbage-collect`.** This removes the blobs, and nothing recovers them. It may run
   only after a stated quarantine has elapsed since phase 1, and **never in the same act**.

🚨 Upstream's own caveat is load-bearing here: `garbage-collect` races a concurrent push — a blob
uploaded while the walk is in flight can be collected as unreferenced — so the registry must be
read-only for the duration. **A blob GC is a maintenance window, not a cron**, and it is the
maintainer's to schedule.

### R5 — #3842's window applies here UNCHANGED

*Retain unreferenced continuous artifacts for at least 30 days by age, without a build-count quota.*
That is a property of the artifact class, not of the store — the prebuilt-bundle store and the
assembly cache both clamp to it in code (`PrebuiltBundleRetention.cs`, `AssemblyCacheRetention.cs`),
and the ACR task record is now held to it by a gate. This registry is the fourth store and inherits
the same floor, subordinate to R2: the floor narrows the complement, it never defines it.

### R6 — TWO artifact families, TWO protected sets, and neither may be derived from the other's evidence

One registry holds two completely different things, referenced by completely different consumers:

- **portal images** — `memex-portal-ai`, `memex-migration`, …, mirrored digest-identical from ACR;
- **plugin bundles** — `plugins/<source>/<package>`, the publication index `plugins/<source>`, and
  `plugins/releases`, published by the node repos' bake lane.

A derivation that reads the deployment overlays answers the first family completely and the second
not at all. Reporting one number over both would be a protected set with a silent hole in it, which
is R1's failure wearing a success message.

## 4. The protected set — what references an artifact here

### 4.1 Images

| axis | what it protects | derivable today? |
|---|---|---|
| **1 — committed digest pins** | every `MW_IMAGE_DIGEST` / `platform-image-digest:` the fleet's workflows carry | **yes**, and already extracted — the same extractor the ACR lane runs |
| **2 — deployment overlay pins** | every image a `values*.yaml` in `Systemorph/Memex` pins | **yes**, and already extracted: `extract_foreign_pins` has read this host since #4221. Measured 2026-09-13: **4 references** — `build` ×2, `pearl` ×2. The nightly run now prints them as a protected set (§6) |
| **3 — what a live installation is RUNNING** | the closure of the image set built from the commit each installation answers with at `/api/version` | **yes, and it is the same code**. The mirror pushes the *identical* manifest under the *identical* tag and proves it by read-back, so a commit maps to the same tag here as in the ACR |
| **0 — the bootstrap exception** | `ghcr.io/distribution/distribution` and `cesanta/docker_auth` | not applicable, and worth stating so it is never "missing": **a registry cannot serve the image that boots it.** Those two are pulled from outside and are never stored here, so they are never in this registry's protected set |

So for images the protected set **is derivable today from exactly the sources the ACR lane already
reads**, with one thing added that it does not have: the denominator (§5.1).

### 4.2 Plugin bundles

| what references a bundle | derivable today? |
|---|---|
| every generation a live installation has **landed** | **no** — see §5.3 |
| every module version a `Hosting/Deployment` record pins | partly: the records exist and are read, but for *platform builds*, not for registry bundle repositories (§5.3) |
| every **released** module version (`plugins/releases`) | not as registry references |

**The bundle family's protected set cannot be derived today.** That is the honest answer, and it is
the reason R6 is a rule rather than an observation: a cleanup written against the overlays alone
would have a complete protected set for images and an empty one for bundles, and would delete every
bundle in the registry while reporting a full inventory.

## 5. What is missing to derive this from mesh data — [#4066](https://github.com/Systemorph/MeshWeaver/issues/4066)'s half, precisely

### 5.1 Nothing enumerates what the registry HOLDS — and this is the largest gap

A protected set is the thing you keep. **The deletable set is its complement over the registry's
CONTENTS**, and the contents are unmeasured: `_catalog` and `tags/list` are HTTP reads behind the
publisher credential, and no record of the answer exists as data anywhere.

A protected set with no denominator cannot authorize a deletion — it can only authorize a
*retention*, which is what R0 declares.

🚨 **`ContainerImageRecord` was the closest shape ever built, it could not answer this, and it is
now DELETED** (2026-09-17, #4066 item 1, with the rest of `src/MeshWeaver.ContainerImages`). The
reasons it could not are kept here, because "we already had a type for that" is how this gets
closed on a false premise — by resurrecting it:

1. It recorded what was pulled **through the read-through mirror**, and that mirror was never
   wired: `MapContainerImages` was called by **no host** — two tests and nothing else, org-wide.
   It was deleted rather than mapped because `cr.meshweaver.cloud` is a different service (§7).
2. Even wired, it would not have answered: the recorded decision
   ([ContainerRegistryInMemex](/Doc/Architecture/ContainerRegistryInMemex), 2026-09-08) is that the fleet registry is
   a *separate service* and installations pull from it directly, so the portal mirror was never in
   the pull path at all.

And structurally: a record keyed on *"what somebody pulled"* is not an inventory of *"what is
stored"* in either case. It is a consumption log, which is §5.2's question, not this one.

### 5.2 Nothing records a LAST-PULLED signal, and the socket for one is already wired

The registry is **already configured** to POST every event — manifest push, pull, delete, blob
upload — to `registry.notifications.url`, with the `Authorization` header value coming from Key
Vault. The chart renders the block only when a URL is set, deliberately (*"an endpoint with an empty
url is a queue that backs off forever, not notifications off"*), and the default in `values.yaml` is
`""`. **No receiver exists in this repository.**

That is the one instrument that turns *"nothing I can read references this"* into *"and nothing has
pulled it in N days"* — which is the evidence that makes an age floor a measurement instead of a
guess, and the only thing that can see a consumer no committed file and no `/api/version` knows
about.

### 5.3 Bundle consumption is inventoried for the PORTAL'S store, not for the registry

`DeploymentPinnedReferences.Resolve` already does the hard half of R1 correctly: it takes the
`Hosting/Deployment` records as the expected-consumer **denominator**, and refuses the whole pass
when one has no report, an unreadable report, or one older than 24 hours.

But what it yields is `PinnedPlatformReference(Origin, Version, Identity)` — *platform builds*,
consumed by `PrebuiltBundleRetention` for the portal's own prebuilt-bundle store. It names no
registry repository and no bundle digest. The denominator machinery is reusable; the reference shape
is not.

### 5.4 What is NOT missing

The overlay and workflow half is complete and needs nothing built: the extractors are shipped,
proven by a self-test on every pull request, and already read this host. §6 turns their output into
a report rather than leaving it as a by-product.

## 6. The declaration, the gate, and the dry run

**The declaration** is `.github/acr-retention/instances.json` → `registries.<host>.retention`. The
`registries` table was made the unit of declaration deliberately (a per-instance field can answer
*"is this one out of scope"* and can never answer *"is every registry the fleet pins in accounted
for"*), and retention is the second question asked of the same unit. Every registry in the table
carries a `retention` block, so a registry added to the fleet cannot enter without answering it.

**The gate** is `lock-pinned-digests.py --check-registry-retention .` — credential-free, no network,
run on every pull request beside the existing record gate. It asserts:

- every declared registry has a `retention` block, with a known rule and a reason;
- a `nothing-deletes` rule enumerates its `deleters`, and **at least one is `present: true`** (R0's
  denominator clause); an `"unverified"` one names its `verifiedBy` and keeps `cleanupAuthorized`
  false;
- a `derived-protected-set` rule names its axes, and every axis declares `onIncomplete: "refuse"`
  (R1, held by the gate rather than by review);
- **the chart still agrees with the record** — the registry templates render no `Job` or `CronJob`,
  the `maintenance:` stanza carries exactly the declared keys and window, **every ACL rule granting
  the `delete` action still names the declared principal**, and no executable line anywhere runs a
  deletion against a registry.

The chart arms are what make the declaration falsifiable rather than a comment. Two of them are
worth naming:

- **The ACL is re-derived, because one verdict rests on it and on nothing else.** *"An installation
  holding an instance key cannot delete"* is not a grep result — it is a property of `docker_auth`'s
  ACL, where exactly one rule carries `delete` and its match names the publisher. Change that rule's
  match to `/.+/` and the verdict is false with every other arm still green. The record declares
  `deleteGrantedTo`, the gate re-reads the rules, and **zero rules found is a failure** — an ACL
  nobody located is not an ACL that grants nothing.
- **The deleter sweep matches the COMMAND LINE's spellings, not the README's.** `curl` takes
  `-XDELETE` with no space and `--request=DELETE` with an equals sign, and both execute identically
  to the spaced forms. A sweep matching only `-X DELETE` is defeated by a keystroke — silently,
  while still printing a denominator and a clean verdict. Same lesson as `--include-"locked"` one
  gate along: read what the shell will *run*.

Each arm is driven both ways by `--self-test`.

**It runs on every pull request**, in `dotnet-test.yml`'s `workflow-shell` job — a `needs:` of
`collect-results` (`Consolidate test results`, this repository's only required status check), named
by that job's explicit fail step. `main` is ruleset-protected and merges through the merge queue,
which builds the merged branch and runs the same job, so there is no path to `main` on which the
gate does not block. It is deliberately **not** duplicated onto the nightly lock lane: that would
add no coverage and one new way to red the lane whose green is `pause.reEnableWhen`.

**The dry run** is the nightly protection run's report, which now prints, per foreign registry,
every reference the fleet's committed files make to it — repository, tag and the file that pins it,
**with the host's declared disposition beside it**. That output **is** the deliverable: it says what
a cleanup on that registry would have to keep, it deletes nothing, it needs no new credential, and it
prints its own incompleteness (the image family is covered by the committed axes; the bundle family
is named as NOT COVERED, per §4.2, rather than silently omitted).

🚨 **The disposition is part of the label, and LISTING rather than counting is the point — both
because of what the first live run found** ([run 34852827116](https://github.com/Systemorph/MeshWeaver/actions/runs/34852827116), #4323):

- It printed *"a cleanup must KEEP"* over **`ghcr.io`**, a host declared `third-party`, where this
  fleet runs no cleanup at all. One sentence cannot be right about both a store we retain and a
  store somebody else retains, so a `third-party` host now gets its own — *declared NOT ours to
  retain … listed so the declaration can be checked against what is really pinned*.
- And that listing immediately earned itself: **two of `ghcr.io`'s five references are
  `systemorph/*`** — our own images — on a host whose declaration reads *"never published by this
  fleet"*, while `release.yml` mirrors three repositories there on every official release. **A count
  alone would have hidden it.** Filed as #4323 and **answered in §8**, which also measures why
  `third-party` → `fleet-unlockable` — the correction the issue proposes — is not merely a
  loosening but a **false red** on `memex-cloud`.
- A **moving tag** is flagged where it appears — against the shared `FLOATING_TAGS` set
  (`latest`, `main`, `master`, `edge`, `stable`, `nightly`), case-insensitively, **not** the string
  `latest`: the extractor preserves whichever one an overlay wrote, and the other five move exactly
  as `latest` does. Two of those references are `ghcr.io/systemorph/…:latest`, and they are the
  CHART'S OWN DEFAULTS — so the unconfigured install pulls the platform from GHCR. #3438 carries
  *"a moving tag is named by no file and is outside this model"*; it is named by a file now.
- 🚨 **An UNDECLARED host gets no verdict at all.** It has already BLOCKED the run, so the one thing
  the report must not emit is a list of its references under *"a cleanup must KEEP"* — an
  actionable-looking answer produced by a run that refused. It prints `UNCLASSIFIED … and NO verdict
  about them`, and says to declare the host before reading the list as anything.

## 7. What is the maintainer's

1. **The blob lifecycle question (§1, the one UNVERIFIED row).** Does the registry's storage account
   or container carry an Azure management policy? A lifecycle rule deleting blobs under a
   *content-addressed* store is not "old data expiring" — it is deleting a layer that a current
   manifest still names, and the image reads as present until something pulls it. This needs one
   read on the account named by `registry.storage.accountName`.
2. **Whether unbounded growth is accepted.** §1 says nothing deletes. If that stands as the policy,
   it wants a measured storage bound and a threshold that would change the answer — the ACR's is
   measured (2,936 manifests on 2026-09-13) and this one is not.
3. **Whether to build §5.2's receiver.** The socket is wired and costs nothing until a URL is set.
   It is the difference between a cleanup that can ever be safe and one that cannot.
4. ~~**#4066 item 1** — map `MapContainerImages` in a host, or delete the assembly.~~ **Decided
   and done 2026-09-17: deleted.** The rule was to map it if it was what serves
   `cr.meshweaver.cloud`, and delete it otherwise; that host is the separate distribution +
   docker_auth service (realm `/auth`, not the mirror's `/v2/token`), so the assembly, its tests
   and the chart's `containerImages:` block went. Nothing in this page depended on the answer
   (§5.1).
5. **§8.5 — the chart's default images.** Should an unconfigured install pull the platform from
   `ghcr.io/systemorph/…:latest`, a store this fleet publishes to and does not retain, at a moving
   tag? The record now *states* that it does; whether it should is a deployment decision.

## 8. 🚨 What this fleet PUBLISHES to a registry — the third question (#4323)

`disposition` answers *"can THIS lane lock that host"*. `retention` answers *"what deletes from
it"*. Neither asks **"does this fleet publish there at all"**, and for `ghcr.io` the record answered
that unasked question wrongly, in two fields at once:

```json
"disposition": "third-party",
"reason": "Somebody else's images, never ours to protect and never published by this fleet. …"
"retention": { "rule": "not-ours",
  "reason": "Nothing this fleet produces is stored on ghcr.io, …" }
```

**Both sentences were false, and both validated green** — because every arm of the gate asks the
record what it *says*, and nothing asked the workflows what they *do*.

### 8.1 What the fleet actually does, measured

`main-cd.yml`'s promote job, run 34918214035, 2026-09-15T02:27Z — an ordinary promoting run:

```
pushing … to ghcr.io/systemorph/mw-plugin-test:3.0.0-ci.8630   (+ :b63310a, :main, :latest)
pushing … to ghcr.io/systemorph/memex-migration:3-latest       (+ :3.0-latest, :3.0.0-latest, :3.0.0-ci.8630)
pushing … to ghcr.io/systemorph/memex-portal-ai:3-latest       (+ :3.0-latest, :3.0.0-latest, :3.0.0-ci.8630)
```

**Twelve tags across three of our own repositories, on every promoting run.** And `release.yml` —
the lane #4323 named — **has never run**: the workflow reports `total_count: 0`. So the continuous
lane is the *whole* of the publication, and the mirror is older and busier than the issue's framing.

### 8.2 🚨 Why the disposition was NOT flipped: the correct-sounding fix REDS the lane

`third-party` → `fleet-unlockable` reads like the correction. It is not, and the reason is
measurable rather than a matter of taste. `memex-cloud`'s overlay pins **both**:

```yaml
portal:   { image: "meshweaver.azurecr.io/memex-portal-ai:3.0.0-ci.8411" }
registry: { image: "ghcr.io/distribution/distribution:3.1.1@sha256:bca247…" }
```

With `ghcr.io` declared `fleet-unlockable`, `unlockable_registries` becomes non-empty while
`repositories` already is, and `resolve_running_sets` fires **"half its running set would be
protected and half would not"** — over an installation whose every image *of ours* is in this ACR
and locked. `pause.reEnableWhen` is *"lock-pinned-digests is green"*, so that false red would stand
between the fleet and re-enabling cleanup: the exact failure the `registries` table was built to
end, manufactured by its own fix.

**The unit is the defect, not the value.** `ghcr.io` is the fleet's only MIXED host —
`systemorph/*` is ours, `distribution/*`, `oras-project/*` and `actions/*` are not — and a single
per-host disposition is false about one half whichever way it reads:

| value | false about | consequence |
|---|---|---|
| `third-party` | `systemorph/*` | our own mirror declared "never published by this fleet" — #4323 |
| `fleet-unlockable` | `distribution/*` | **`memex-cloud` reds**, and the cleanup re-enable is blocked behind it |

The host unit was justified by a measurement — *"exactly two hosts … `ghcr.io` (2, one of which was
a line of prose)"* — that was true of the **overlays** on 2026-09-13 and is still true of them
today. Nothing about the fleet changed; what changed is that #4315 widened the extractor, so the
report now sees the chart's own defaults and the mixture became visible.

### 8.3 The `publishes` block, and `operator-retained`

The disposition keeps the meaning every axis reads it for — **what the fleet PULLS**, which is what
an installation's running set is made of — and the publication is declared as its own fact on the
same unit:

```json
"ghcr.io": {
  "disposition": "third-party",          // what we PULL here: distribution, oras — somebody else's
  "publishes": {
    "repositories": ["systemorph/memex-portal-ai", "systemorph/memex-migration",
                     "systemorph/mw-plugin-test"],
    "producedBy": [".github/workflows/main-cd.yml", ".github/workflows/release.yml"],
    "runFrom": "NO INSTALLATION. …",
    "retention": { "rule": "operator-retained", "operator": "GitHub (GitHub Packages / ghcr.io)",
                   "cleanupAuthorized": false, "reason": "…" }
  }
}
```

**`operator-retained` is a publication into a store this fleet does not operate**, and it is
deliberately *not* a host-level rule — allowing it there would be a trapdoor out of
`nothing-deletes`, letting any registry answer the second question with *"somebody else's problem"*.
It may **not** carry `deleters`: GitHub Packages is rendered by no file of ours and its ACL is not
ours to read, so an enumeration would be a verdict about **our** artifacts resting on nothing — the
same false reassurance `not-ours` refuses one field along (§R0's *"an enumeration in which nothing
is present is one that inspected nothing"*). It may not authorize a cleanup either.

What *is* established, and is why the mirror is a publication rather than a store: **every reference
is a copy by digest of a manifest that exists, locked, in `meshweaver.azurecr.io`**, and
`cr.meshweaver.cloud` carries the same set for the installations that actually run from a registry.
A GHCR retention event loses a *mirror*. The consumer that would notice is MeshWeaver.Plugins, which
pins `ghcr.io/systemorph/mw-plugin-test:latest` as `MW_TEST_IMAGE`.

### 8.4 The gate DERIVES the publication, and the declaration is load-bearing

`--check-registry-retention` reads the push targets off the **committed lanes** — `main-cd.yml` and
`release.yml` — and holds the table to them. A record that checks itself passes on the day it stops
being true.

🚨 **Reading a lane is where this kind of derivation goes quietly wrong, so the parsing is stated
rather than assumed.** A workflow spells one push in more ways than a naive reader expects, and
every one of these appears in these two lanes:

| spelling | where | read by |
|---|---|---|
| `--tag "<host>/<repo>:<tag>"` | main-cd phases A–D | the `--tag` scan |
| `mirror-image-to-registry.sh <src> <dst>…` | every fleet-registry mirror | the call scan — **no `--tag` on the line at all** |
| destinations on the next line behind a `\` | phase B's `mw-plugin-test` mirror | the continuation join |
| a YAML **folded** `run: >`, arguments on following lines, **no backslash** | `main-cd.yml:1260`, `:1416` | the folded-scalar join |
| `"${{ env.ACR }}/…"` | main-cd's tag lines | the workflow's own `env:` map |
| a plain shell `"$ACR/…"` / `"${ACR}/…"` | `release.yml:272`, `:275`, `:279` | the same map |
| `"$NS/$repo"` inside `for repo in …; do` | release.yml's mirror loop | the loop expansion (`$NS` resolved from the owner, and the *assignment* asserted) |
| `"${margs[@]}"` | phase D's mirror | the array-append collection |

🚨 **An UNRESOLVED host is a PROBLEM, never a silent drop, and the order of the two tests is the
whole point** — a host still carrying `$` has no dot, so a Docker-Hub short-name test placed first
discards it with no error. That is a push target dropped silently by the one derivation whose job is
to make a dropped push target impossible: this issue's own defect, one register down.

| the lanes push to a host that… | the run |
|---|---|
| is `fleet-unlockable` | accounted for — the host-level declaration already says our images live there |
| carries a `publishes` block naming exactly the derived repositories | accounted for |
| is `third-party` with **no** `publishes` | **RED**, naming the repositories and the lanes — *this is #4323* |
| the table does not declare at all | **RED** |
| is named in `publishes` but pushed by nothing | **RED** — a stale entry exempts nothing and hides the next one |
| is `meshweaver.azurecr.io` | skipped **by name and printed** — it is the registry this lane locks, the subject of the script rather than a foreign host it declares |

Three more hold the *declaration* rather than the derivation:

- **`producedBy` is held to the derived producers**, not to the host's name appearing somewhere in
  the file — a comment satisfies a substring test, and a misattributed lane reads as evidence. Both
  directions: a named lane that emits nothing for this host, and an emitting lane the record does
  not name.
- **A whole-host `publishes` block whose lanes have stopped pushing is RED.** The population is the
  *union* of derived hosts and declared publishers, because a loop over derived hosts alone never
  visits a stale block — it would pass having inspected nothing.
- **`cleanupAuthorized` must be the boolean `false`**, not merely "not the singleton `True`". This
  file has already paid for the other spelling twice, on `inForce` and on `present`: every
  `is True` reader treats the string `"true"` as absent.

Two further arms stop the block being prose:

- **An overlay may not PIN a published repository.** An installation pinning
  `ghcr.io/systemorph/memex-portal-ai` is running *our* images from a store nothing of ours retains
  and this lane cannot lock — and the host's `third-party` disposition would wave it through in the
  one branch (pins here *and* there) that prints success. It fires on nobody today, which is exactly
  the claim `runFrom` makes and therefore exactly the claim that must red when it stops being true.
- **The report splits a mixed host's references.** Printing our own two chart-default references
  under *"declared NOT ours to retain"* would be the record's false sentence reproduced in the one
  artifact a reader checks it against.

Every arm is driven both ways by `--self-test` (ARM 34 / 34b), and each negative control was proven
to fire by neutering its subject — including the two readers that decide the derivation separately:
disabling the mirror-call reader loses the host, and disabling the continuation join loses
`mw-plugin-test` alone. An arm asserting only the host's presence would have passed with
continuations unread, which is why it asserts the repository set.

### 8.5 What #4323 leaves open

**The chart's defaults still point at GHCR, at a moving tag.** `deploy/helm/values.yaml` defaults
`portal.image` to `ghcr.io/systemorph/memex-portal-ai:latest` and `migration.image` to
`ghcr.io/systemorph/memex-migration:latest` (the ACA bicep and the AKS README say the same), so an
**unconfigured install pulls the platform from a store this fleet does not retain, at a tag that
moves**. That is recorded in `publishes.runFrom` rather than fixed here: it is a deployment-defaults
decision, not a retention one, and changing it moves what an unconfigured install runs.

### 8.6 🚨 `out-of-estate` — our images in a registry outside this fleet's reach (#3438)

`Systemorph/PartnerRe.Memex` joined the fleet on 2026-09-14 with a **live** control instance
(`partnerre.meshweaver.cloud`) whose overlay pinned the portal and migration images in
`memexaksacrqoqqdqnhlaksg.azurecr.io` — an ACR in the **`PartnerRe Memex` subscription**, which this
lane's OIDC credential does not reach at all. Every rule in the vocabulary was a *false sentence*
about it:

| rule | why it is false here |
|---|---|
| `not-ours` | the images **are** ours, mirrored from the platform's own build — and a `third-party` disposition also **reds** the lane, because an installation whose every pin is third-party is one whose running set no registry holding our images accounts for |
| `nothing-deletes` | its enumeration is held to a committed **chart** this gate re-derives the deleters from, plus the ACL carrying the `delete` action. An ACR renders neither; nothing in this repository could re-derive a word of it |
| `derived-protected-set` | it asserts a cleanup exists there that deletes only the complement of a derived set. Nobody here is in a position to say that |

🚨 **THE HOST IN THAT PARAGRAPH IS THE ONE IT WAS DECLARED ABOUT, AND IT HAS MOVED.** That
estate was torn down on 2026-09-16 and rebuilt on 2026-09-17 in PartnerRe's *own* subscription
(`5896de84`) and Entra tenant (`e51e062f`), minting `memexaksacr43rzd6faaix36.azurecr.io`; the
overlay followed the same morning and the declaration did not, which held the lock lane red from
2026-09-17 to 2026-09-21. (The three reds before that are a different cause and worth separating: the
ramp-up portal stopped answering `/api/version` as it was torn down, which is the axis-3 refusal
working. Ten runs, two causes — reading them as one is what made the lane look like a single
unexplained red.) Nothing in the reasoning below changed — the rule is still the only true sentence about the
host — but the *staleness* is what the table had no arm for, and now does:
[ArtifactRetentionInterlock → the declaration is checked BOTH ways](/Doc/Architecture/ArtifactRetentionInterlock).

Leaving it undeclared reds the lane; a false declaration is worse than a red. So the vocabulary grew
a word for the fact: **the registry is in an estate outside this fleet's reach, what deletes from it
is decided there, this record measures nothing about it, and no cleanup is authorized here.** It
requires `estate` (whose decision it is, and where that decision lives) and
`cleanupAuthorized: false`, and it may **not** carry `deleters` — the same refusal
`operator-retained` carries, for the same reason.

🚨 **It is not §8.3's trapdoor reopened, and the guard is DERIVED rather than declared.** A host this
fleet's own publishing lanes push to may never take this rule — and that is exactly
`cr.meshweaver.cloud` and `meshweaver.azurecr.io`, the two hosts where the trapdoor would have
mattered. The refusal reads the same derived push-target set §8.4 does, and a run whose derivation
cannot be trusted (it does not even derive the registry this lane locks) **refuses** the rule rather
than allowing it: the derivation is the whole of this rule's safety, and one that cannot refuse must
not permit.

Every run **prints** the host on its own line saying this record measures nothing about it —
*"not ours to answer"* and *"clean"* are different sentences, which is the same reasoning as the
`unverified` lines in §6.

For the record and **not** as a claim this gate re-derives: read-only from a maintainer credential on
2026-09-15, that registry carried **zero** `az acr task`s and its untagged-manifest retention policy
read `status: disabled` — so nothing deletes there today either. It is written into the record as
evidence for whoever asks, never as a measurement the lane repeats, because it cannot: the nightly
run holds no credential for that subscription.

## Related

- [ArtifactRetentionInterlock](/Doc/Architecture/ArtifactRetentionInterlock) — the same question for
  `meshweaver.azurecr.io`, where a lock exists.
- [ContainerRegistryInMemex](/Doc/Architecture/ContainerRegistryInMemex) — what this registry IS, and the 2026-09-08
  decision to run it as a separate service.
- [PluginBundlesInTheRegistry](/Doc/Architecture/PluginBundlesInTheRegistry) — the bundle family and its
  authorization.
- [PrebuiltBundleRetention](/Doc/Architecture/PrebuiltBundleRetention) — the portal-side store, and the completeness
  refusal §5.3 reuses.
- [ReleasedArtifactRetention](/Doc/Architecture/ReleasedArtifactRetention) — #3842's decided window.
