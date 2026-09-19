---
Name: The Bundle Transfer Budget
Category: Architecture
Description: "A module adopt gets 120 s per attempt and the whole archive used to be downloaded inside that attempt, so the budget measured size divided by throughput rather than whether the registry was answering — and nothing recorded either number, which is why eighteen identical failures could not be explained."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12"/><path d="m8 11 4 4 4-4"/><path d="M3 17a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2"/></svg>
---

# The Bundle Transfer Budget

A consuming instance adopts a package's compiled module by downloading a **bundle** from its
registry. Two budgets bound that download, they were authored independently, and until #4528 they
disagreed with each other and with the thing they were supposed to bound.

## What was measured

On memex.systemorph.com, 2026-09-17, reading Loki through the control instance's `Logs` action.

| | |
|---|---|
| module adopts that failed | **18** — 14 in one pass on 2026-09-15 (19:58:24Z → 20:54:45Z), 4 more on 2026-09-16 (14:20:44Z → 14:33:11Z) |
| spacing between failures | **exactly 180 s** for 10 of them — the reconciler's `PerPackageAdoptBudget` |
| cause, every time | `The operation has timed out.` |
| packages | AI, Anthropic, AppleIntelligence, Chat, Essentials, Import, Mcp, Northwind, Notifications, OgCard, OpenAI, OpenStreetMap, Publish, Radzen — the pass walking the catalog in order |
| attempt timeouts on the transfer pipeline | **11**, all inside the 65-minute 2026-09-15 episode, 10 of them on ONE pod |
| `Bundle fetch for …` lines beside them | **0** |
| byte counts or durations recorded anywhere | **none** |

🚨 **The last two rows are the finding.** Eleven attempts hit the 120 s transfer budget and the
bundle client never reported one of them, because the reconciler's outer 3-minute wait cancelled the
operation first and logged its own timeout. So every occurrence of this defect left exactly one
sentence — *"The operation has timed out"* — and no evidence of **why**. "Is 120 s too short?" is
really *"how many bytes, at what throughput?"*, and that question could not be asked at all.

**It is not a burst, and not a roll wave.** The episode is a sequential reconcile pass in which
every package fails at the same bound; it recurred the next afternoon on different pods. The
incident that folded these events retains only ten samples, which is why they first appeared to be
six failures in four minutes across four pods.

## The shape defect

`PluginBundleClient.DownloadOverHttp` sent with `HttpClient`'s buffering default, so the entire
archive was downloaded **inside** `SendAsync` — inside the Polly attempt. A per-attempt budget then
measures **size ÷ throughput**, not *"is the registry answering?"*, which is the only question a
per-attempt budget can meaningfully answer. A large bundle and a dead registry produced the same
timeout.

Its sibling in the same assembly never had this problem: `OciRegistryClient` reads with
`ResponseHeadersRead` and streams the blob. The HTTP route now does the same — headers bound the
attempt, and the body streams outside it.

🚨 **That alone would have traded a timeout for a HANG, and the body carries its own bound because
of it.** Taking the body outside the attempt leaves it bounded by nothing on the callers that have no
operation deadline: `RegistryUpdateReconciler` wraps its adopt in `PerPackageAdoptBudget`, but
`CatalogLayoutAreas.InstallPackage` (the manual click) and `InstanceAutoRegistrationService` (the
default install) do **not**, so a registry that sends headers and then stops would hang them
indefinitely — and a hang is worse than a failure (Plugins#959). So the read is bounded **here**, by
a STALL budget: every chunk that arrives resets the deadline, so a transfer still making progress is
never cut off however large it is — the whole point — while one that goes silent for
`TransferStallBudget` fails, names itself as a stall rather than someone else's cancellation, and
reports the byte count it reached. **It bounds silence, never total size.**

🚨 **Every transfer in the fleet takes this route.** The OCI path runs only when a catalog entry
carries an `artifact`, and the platform default `IPublicationArtifacts` records none, so the
digest-verified artifact path — the one that *did* log its byte count — is dead code on every
deployment. That is why no size was recoverable from production logs.

## What the transfer now records

A completed transfer states the bytes, the elapsed time and the rate. A transfer that does **not**
complete states how much had arrived before it was cut, and then rethrows untouched — a fault is
never swallowed to produce a log line. Those two lines separate the two diagnoses that want opposite
fixes:

- **zero bytes after two minutes** accuses the registry, not the archive;
- **most of a large archive** accuses the size, and the fix is then the transfer itself
  (a smaller or resumable bundle), never a bigger number.

A rate is reported as `0` when the interval is too small to divide by: a fabricated throughput in
the one line that exists to be trusted is worse than no number.

## What this does NOT claim

**It does not prove a bundle fits 120 s, and it does not cure the eighteen failures.** No size for a
production bundle was obtainable while writing this, precisely because nothing recorded one. What
changed is that the attempt budget now bounds responsiveness rather than size, and the next
occurrence will say which of the two it was. Settling #4528 needs that evidence.

## Measured after the roll: the fix is live on every replica and the budget is still exceeded

The paragraph above expected the next occurrence to be informative. It was informative in the
direction nobody wrote down: **the shape fix did not stop the attempt timeout.**

Measured 2026-09-19 19:0xZ, read-only, from `Ops/Status/{memex,memex-cloud}` on the control instance
and the incident node the log watcher folds these events onto.

| deployment | image | commit | replicas | pods up since | carries the shape fix? |
|---|---|---|---|---|---|
| `memex` | `3.0.0-ci.8968` | `96f88406` | 2/2, `converged: true` | 2026-09-19T08:10Z | **yes** |
| `memex-cloud` | `3.0.0-ci.8969` | `c25f86ae` | 3/3, `converged: true` | 2026-09-19T08:35Z | **yes** |

All ten retained samples on the incident read
`Source: 'plugin-registry-bundles-standard//Standard-AttemptTimeout'`, spanning 19:00:52Z → 19:06:53Z
across five pods, and its shape counter advanced **375 → 397 in sixteen minutes**. So the 120 s
*attempt* budget on this pipeline is exceeded roughly one and a half times a minute, on five replicas
of two deployments, all of which have been running the streaming transfer for ten hours.

**That relocates the cost, and the relocation is what the fix bought.** With
`HttpCompletionOption.ResponseHeadersRead` the body leaves the Polly attempt and `CopyStallBounded`
bounds the copy separately, so on these images an attempt timeout cannot be spent *streaming bytes*.
It is spent before the response headers arrive — which points at the bundle **index** endpoint and
its uncached per-request work rather than at the blob transfers this page was written about. The
earlier attribution (*"it was the blob transfers, not the index"*) held for the pre-fix images and
does not survive the roll.

🚨 **A per-pod period is the reading that rules out "one large bundle".** The samples sit at a 180 s
spacing with millisecond jitter — `…-2kcwk` at 19:00:52.047 · 19:03:52.052 · 19:06:52.052, five
milliseconds of drift over six minutes. A fixed-period population is a repeating scheduled adopt
whose attempt exceeds the budget *every time it runs*, not an unlucky request; the period is
`PerPackageAdoptBudget`, i.e. the outer bound of the inversion below cutting each pass. So the
inversion is no longer only a shape problem — it is the reason 397 occurrences in one day still
cannot say which of the two call shapes timed out.

## Two findings this does not fix

🚨 **The two budgets are inverted.** `RegistryUpdateReconciler.PerPackageAdoptBudget` is **3
minutes**; the transfer pipeline's own `TotalRequestTimeout` is **5 minutes**
(`ServiceDefaults`, `plugin-registry-bundles`). The outer wait therefore expires before the inner
policy can finish retrying, so the retry is structurally unable to complete and the HTTP layer's
cause is always discarded in favour of a bare `TimeoutException`. The finite outer bound is
deliberate and correct — *a hang is worse than a failure* — but two independently authored budgets
that contradict each other is a shape problem, not a tuning one, and the fix is to derive one from
the other rather than to raise either.

🚨 **The incident fingerprint masks `Source:`**, so every Polly `OnTimeout` on every pipeline folds
onto one incident node — the samples on it have also included `Orleans.Placement/(null)/Timeout`.
That formula lives in the log watcher in MeshWeaver.Plugins and is unchanged.

**What the masking costs is an attribution, and the attribution has to be repaired by hand.** The
listing pipeline (#4222) and this transfer pipeline shared one counter, so neither could be closed on
*"occurrences stopped advancing"*. When the listing fix reached the registry on 2026-09-19 the listing
samples stopped and the transfer samples did not — so the counter's `issueNumber` was pointing at a
defect that was fixed, and the recurrence bot would have reopened the fixed issue on the next tick,
inside the hour. The step that makes such a close hold is to **repoint the incident node at the issue
its current samples name** (`content.issueNumber` / `issueUrl`, an ordinary patch; the same operation
had already been done once on this fingerprint, 1134 → 4222). The superseded predecessor node is left
pointing at the closed issue on purpose: it is that issue's historical record, and if *it* ever
advances again the reopen would be correct.

🚨 **So the discriminator is `samples[]`, never the count.** Read the `Source:` value on a reopen of
any issue attributed to a masked fingerprint before believing the reopen is about that issue. Two
issues have now been reopened against fixed defects by this mechanism (#1134, then #4222 repeatedly),
which is the argument for deriving the fingerprint from `Source:` rather than masking it.

## Where this sits

[The Registry Listing Cache](../RegistryListingCache) — the sibling endpoint, and the same lesson about
measuring the cost before changing the number. [Plugin Bundles in the
Registry](../PluginBundlesInTheRegistry) — what a bundle is and how it is published. [Operating from
the Portal](../OperatingFromThePortal) — the `Logs` action every measurement above came from.
