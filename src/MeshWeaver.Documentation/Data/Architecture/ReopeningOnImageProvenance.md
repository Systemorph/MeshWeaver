---
Name: Reopening on Image Provenance
Category: Architecture
Description: The third reopen predicate — an occurrence counts only if it came from an image whose commit CONTAINS the fix. Why that beats a staleness window (a window closes live defects; this cannot), the five-link evidence chain with each link measured, the one link that is missing today and the two places it can be added, the fail-safe for an unresolvable image, and why the rule must not ship before the map.
Icon: ArrowSync
---

# Reopening on Image Provenance

A recurrence reopen asserts *"this fault is happening again"*. Two predicates have been proposed for
it and neither is sufficient:

| predicate | owner | what it catches | what it misses |
|---|---|---|---|
| the fault was seen **after the close** | [MeshWeaver.Plugins#2014](https://github.com/Systemorph/MeshWeaver.Plugins/pull/2014) | a late comment reverting a human's close — 53 of the 2026-09-17 wave | an occurrence from a replica that never had the fix |
| the fault was seen **recently** | proposed on #2014 | the 41 of 51 that had been quiet 8–37 days | — and it closes live defects; see below |
| the occurrence came from an image **containing the fix** | this page | both of the above, for the right reason | occurrences older than the image map |

The first is correct and pending. This page is the third, and it subsumes the second.

## Why not a staleness window

A window ("reopen only if the newest occurrence is inside `CommentInterval`") is an approximation,
and its failure mode points the wrong way: **a fault that fires once a month is stale under every
window and is genuinely live.** Suppressing its reopen hides a real regression — strictly worse than
the noise the window exists to remove. It also needs a clock, and `Reopen` deliberately has none:
every timing in the filer and in `LogIncidentFilingIdempotencyTest` comes from the incident's own
`LastSeen`, so a window would be the first thing there that needs *now* — a seam, plus a change to
every existing reopen test.

The image test has neither problem. It asks the question the reopen is actually making a claim
about — *did this fault occur in code that has the fix?* — and answers it from facts already in the
record. A once-a-month fault on a current image reopens correctly. An hourly fault on a replica that
predates the fix does not, however recent it is.

## The evidence chain, link by link

Measured 2026-09-17:

| link | where it lives | state |
|---|---|---|
| occurrence → **pod** | `LogSample(Timestamp, Pod, Line)`, on every sample of every incident | **present** |
| pod → **generation** (the ReplicaSet) | string arithmetic: the pod name minus its last segment | free |
| generation → **image** | `Ops/Status/{instance}.replicas[].generation` + `.image`, written by the fleet watch | **present, and only just** |
| image → **commit** | nowhere | 🚨 **MISSING** |
| commit → **contains the fix** | GitHub `GET /repos/{o}/{r}/compare/{fix}...{image}` → `status` of `identical` or `ahead` | one REST call |

The middle link is a pure function, and an immutable one: **a ReplicaSet's pod template, hence its
image, never changes for the life of that ReplicaSet.** So the map needs one observation per
generation — roughly one per roll — and never needs refreshing.

### `Ops/Status` carries pod → image now, and did not two days ago

This corrects a belief worth correcting. Measured on the control instance:

| version | sampled | `notScraped` | `replicas[]` |
|---|---|---|---|
| 400 | 2026-09-13 02:05Z | — | empty, *"Prometheus returned no series"* |
| 1100 | 2026-09-15 12:57Z | `true` | empty |
| 1457 | 2026-09-17 18:18Z | `false` | **2 replicas, each with `pod`, `image`, `generation`, `startedAt`** |

So kube-state-metrics began scraping the `memex` namespace between 2026-09-15 12:57Z and
2026-09-17 18:18Z, and the per-pod image has been available since. Any note saying `replicas[]` is
empty fleet-wide is now stale.

🚨 **It cannot be backfilled.** `Ops/Status/{instance}` is ONE node overwritten each sample. Its
history survives as node versions, but only the populated ones carry images — about two days of them
— and a pod that ran before that is unresolvable **forever**. The 2026-09-17 wave's occurrences are
therefore permanently out of this predicate's reach, and that is fine: the rule exists to stop the
NEXT wave, not to re-adjudicate the last one.

🚨 **And a sampled node is the wrong shape for a durable map.** The map must be ACCUMULATED, not
observed: one small immutable record per generation (`Ops/Images/{generation}` → `{ image, commit,
firstObserved }`), written once and never updated. Leaving it inside a node that is overwritten
every minute makes the map's depth equal to whatever version retention happens to be.

## The missing link: where an image learns its commit

Two places can supply it, and the first is nearly free:

1. **The fleet watch already probes each pod's `/health`** — that is where `healthDetail` and the
   per-pod warnings in `Ops/Status` come from. Probing `/api/version` in the same pass yields
   `{"commit":"…"}` from the running assembly, so one sample would carry `(pod, generation, image,
   commit)` together. 🚨 The public `/health` body does **not** carry it: measured on
   memex.systemorph.com, that payload names no pod, no image and no commit — it is a prose census.
   `/api/version` is the endpoint that answers, and it answers for whichever replica takes the
   request, which is why it has to be probed **per pod** rather than through the ingress.
2. **The watcher may already hold a label it throws away.** `LokiClient` parses Loki's stream
   label-set into a `Dictionary<string, string>` and reads exactly two keys — `namespace` and `pod`.
   Every other label the shipper attaches is already in that dictionary and is dropped. If the
   relabel config attaches an image or container-image label, the chain's missing link is one
   `Label(stream, "…")` call. **Unverified:** the shipper's relabel config lives in the cluster and
   was not read for this page, so this is a possibility to check, not a plan to rely on.

Option 1 is the one a config change elsewhere cannot take away, and it reuses a probe that already
runs.

## The rule

At the point `Reopen` has established that the fault was seen after the close, ask one more thing of
the newest occurrence:

```
generation := ReplicaSetOf(occurrence.Pod)     // the pod name minus its last segment
image      := ImageMap[generation]             // Ops/Images/{generation}
commit     := image.Commit
contains   := Compare(fixCommit, commit).Status is Identical or Ahead
```

`fixCommit` is the commit that closed the issue — the merge sha GitHub reports for the closing event
or for the closing pull request.

**Reopen only when `contains` is true.** Everything else leaves the issue closed **and says which
link broke**, in the comment and in the log, exactly as #2014 does for its own unprovable cases:

| what was missing | the sentence the comment prints |
|---|---|
| the sample names no pod | *"this occurrence names no pod, so the image it ran on cannot be established."* |
| the generation is not in the map | *"the image this occurrence ran on is not recorded, so it cannot be shown to contain the fix."* |
| the image has no commit | *"the image {tag} has no recorded commit, so it cannot be compared to the fix."* |
| compare says `behind` or `diverged` | *"{tag} was built from a commit that does not contain the fix, so this is pre-fix code, not a regression."* |

🚨 **Never reopen on an unresolvable image.** That is the same asymmetry #2014 already argues:
reverting a close needs positive evidence, a missed reopen stays visible as a "still occurring" note
on a closed ticket, and a wrong reopen is indistinguishable from a real regression. The one addition
here is that the *reason* must be printed — an unresolvable image is a gap in the fleet's own
records, and a silent one is a gap nobody ever fixes.

## Ordering, and the observe-only phase

🚨 **The rule must not be armed before the map is populated, or every reopen stops.** With an empty
map every occurrence falls into "the generation is not in the map", every closed issue stays closed,
and the result is safe but blind — indistinguishable from the bot having died.

So it ships in two steps, with a measurable switch between them:

1. **The map lands first**, and is populated for the current generation of every instance the
   watcher files for.
2. **The predicate is computed and PRINTED before it is obeyed.** In observe-only mode the comment
   carries the verdict it *would* have reached while the reopen behaves as it does today. When the
   printed verdicts show the map resolving the generations that actually appear in incidents, the
   rule is armed.

Observe-only is not decoration: it is the only way to tell "the map is complete enough" from "the
map is empty and nothing is being caught", and those two are identical when read from the reopen
counts alone.

## Ownership

`LogIncidentFiler.Reopen` is being rewritten in
[Plugins#2014](https://github.com/Systemorph/MeshWeaver.Plugins/pull/2014), whose author asked that
the remaining predicate land *in that rewrite rather than in a competing edit*. This predicate
belongs there. The evidence half — the accumulated generation → image → commit map — touches neither
`Reopen` nor `LogIncidentFiler`, so it can land independently and first, which is also the order the
previous section requires.

## See also

- [Reading a Recurrence Reopen](../ReadingARecurrenceReopen) — the 2026-09-17 wave measured, and the
  15 of 51 whose only post-close evidence is within 17 hours of the close: the population this
  predicate exists for
- [Operating from the portal, not the cluster](../OperatingFromThePortal) — the fleet watch,
  `Ops/Status`, and what `/health` does and does not carry
