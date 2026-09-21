---
Name: Telling a Stalled Pipeline From a Dead One
Category: Architecture
Description: The incident store carries three clocks and only two of them are about the pipeline — lastModified is triage bookkeeping and moves on a node nothing has detected for days. Which clock answers which question, the exact queries that reach each one (and the three that fail), and the discriminator that separates a pipeline running hours behind from one that stopped.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/><path d="M3 5.5 5.5 3"/><path d="M21 5.5 18.5 3"/></svg>
---

# Telling a Stalled Pipeline From a Dead One

A log-ingestion pipeline fails by producing **nothing**, and an absence is the one symptom that
reds no gate. Worse, the two absences a reader cares about are not the same defect and do not have
the same remedy:

- **Behind** — detection is still happening, delivery trails it by hours. Reports are on disk and
  will arrive. Nothing is lost; the cursor is not advancing fast enough.
- **Dead** — nothing is happening at all. Every minute that passes is a window that will never be
  read, and no amount of waiting recovers it.

From a listing of incident nodes these render identically, which is why the question has been
settled wrongly more than once. This page is the method that separates them, and the instrument
facts you need to run it.

> The governing rule from [Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly)
> applies in full here: **establish that the instrument could have seen the event before you report
> that it did not.** Everything below is a read; nothing on this page mutates anything.

## The incident store has three clocks, and one of them is a decoy

| field | what it means | changed by |
|---|---|---|
| `content.firstSeen` / `content.lastSeen` | **DETECTION** — when the watcher saw the log line | a fold |
| `createdDate` | **DELIVERY** — when the report reached the mesh and the node was written | the first delivery of that fingerprint |
| `lastModified` | **neither** | a fold *or* any triage write |

🚨 **`lastModified` is not a pipeline clock and must never be used as one.** Triage writes drafts,
issue numbers, comment stamps and supersession pointers onto the same node, so it advances on a
fingerprint nothing has detected for days. Sorting a listing by it produces a page of recent
timestamps that says nothing whatever about ingestion.

Worked example, measured on the control instance 2026-09-21:

```
Admin/_LogIncident/c0b1424c7beb28e0
  lastModified 2026-09-21T09:04:23Z   ← reads as "folded a few hours ago"
  lastSeen     2026-09-19T05:38:46Z   ← last actually detected, two days earlier
  version 1277, status Filed, supersededBy 1ff4c4f89b08b75e
```

A `sort:lastModified-desc` listing put this node at the top. Read as ingestion, it says the
pipeline is healthy. It is a superseded fingerprint being rewritten by triage bookkeeping.

## The discriminator

Take **the newest delivery** and **the newest detection**, and — this is the half that is usually
skipped — **the lag between them at the moment the record stops.**

- **Behind**: detection timestamps continue past the last delivery, and the gap between the two
  widens. Delivery is starved; detection is fine.
- **Dead**: both clocks stop at the same moment, and the lag *right up to that moment is normal*.
  A pipeline that was keeping up and then produced nothing did not fall behind — it stopped.

The second reading is the one a lag-shaped hypothesis will talk you out of, so measure the lag on
the **last few nodes before the stop**, not on the average.

Worked example, same instance, same session:

```
newest created  Admin/_LogIncident/3172fa7f8d939ff7
                detected (lastSeen)  2026-09-20T22:23:22Z
                delivered (created)  2026-09-20T22:30:27Z    lag ~7 min

next newest     Admin/_LogIncident/b8716c17b178ac35
                detected             2026-09-20T22:22:16Z
                delivered            2026-09-20T22:29:42Z    lag ~7 min

detection after 2026-09-20T22:2x  — none
delivery  after 2026-09-20T22:30  — none
read at 2026-09-21T13:3xZ         — ~15 h of silence on BOTH clocks
```

A seven-minute lag on the last two nodes and then nothing on either clock is **dead**, not behind.
Had this been the behind shape, detection would have carried on into 09-21 with delivery trailing.

## The queries that reach each clock — and the three that do not

`content.*` values live in JSON and compare as text; node columns are real Postgres types. They do
not accept the same operators, and the failures are loud rather than silent — which is the good
case, but only if you know which form to reach for.

**Works** — detection, by date prefix:

```
namespace:Admin scope:descendants nodeType:LogIncident content.lastSeen:2026-09-21*
```

**Works** — delivery, by ordering (there is no delivery *filter*; take the top node and read its
`createdDate`):

```
namespace:Admin scope:descendants nodeType:LogIncident sort:createdDate-desc
```

**Fails**, with the exact server errors, so you do not spend a turn wondering:

| query form | error |
|---|---|
| `content.lastSeen:>2026-09-20T12:00:00Z` | `42883: operator does not exist: numeric > timestamp with time zone` |
| `createdDate:2026-09-21*` | `42883: operator does not exist: timestamp with time zone ~~* text` |
| `createdDate:>2026-09-21T00:00:00Z` | `42846: cannot cast type timestamp with time zone to numeric` |

## A date-prefix zero needs a control on the other side

`content.lastSeen:2026-09-21*` returning `count: 0` is worth nothing on its own — a wildcard that
matches no rows and a wildcard that cannot match are the same answer. Run the identical shape
against a date you already know has hits:

```
content.lastSeen:2026-09-19*  → count 50, truncated   (instrument works)
content.lastSeen:2026-09-20*  → count 50, truncated   (instrument works)
content.lastSeen:2026-09-21*  → count 0,  not truncated
```

Three readings, one query shape, two of them non-zero: now the zero means something. And read every
count against the envelope's `coverage.partitions` — these all answered from `["admin"]`, which is
where `_LogIncident` lives, so the denominator is the right one.

## A watcher cannot report its own death

Every self-finding the watcher publishes — the silent-window alarm, the truncation finding, the
header-only finding, a falling-behind finding — is **emitted by the watcher**. A watcher that is
lagging emits them. A watcher that has stopped emits none, including the one whose whole job is to
say it stopped.

So the absence of a pipeline self-finding is **evidence for death and against lag**, which is the
opposite of how an empty findings list usually reads. It is never evidence that the pipeline is
healthy.

The corollary is structural: **the only instrument that can detect a dead watcher lives outside the
watcher.** If the component has no deployment record, nothing rolls it and nothing samples it, then
its liveness is unobservable from the portal and the two clocks above are the only signal there is.
Check for the record before concluding anything about the process:

```
search 'namespace:Deployments scope:descendants'
```

## The same error class in CI

The defect underneath all of the above is reading an instrument that answers a **different
question** than the one asked. CI has the identical trap, in the shape that matters most:

A workflow whose credential step is conditional publishes a green run when the condition is false.
The run conclusion then answers *"did this run finish without error"*, not *"did this lane ever do
its job"* — and those diverge completely when the job is skipped.

So when a lane's value depends on one step, **sweep the step conclusions across the whole
population**, and cite the population:

```bash
gh api "repos/{owner}/{repo}/actions/workflows/{id}/runs?per_page=1" --jq '.total_count'
gh api "repos/{owner}/{repo}/actions/runs/{run_id}/jobs" \
  --jq '[.jobs[].steps[] | select(.name|test("<the step>")) | .conclusion]'
```

Measured on one such lane: 101 runs total, 101 of 101 read `skipped` on the step the lane exists to
perform, while 17 of those runs were green. The run-level pass rate said 17%; the step-level answer
was **zero out of the entire history**. Quoting `total_count` alongside the sweep is what makes the
second number a fact rather than a sample.

## Checklist

1. Never sort an ingestion question by `lastModified`.
2. Read **detection** (`content.lastSeen`) and **delivery** (`createdDate`) separately.
3. Measure the lag on the **last nodes before the stop**; normal lag then silence means dead.
4. Give every zero a same-shape control on a date with hits, and cite `coverage.partitions`.
5. Treat a missing pipeline self-finding as evidence of death, never of health.
6. Before concluding about a process, check whether any record of it exists to observe.

## See also

- [Which Kind of Silence](../WhichKindOfSilence) — the four causes a quiet log has, with four owners; this page decides whether the *instrument* is one of them
- [Measuring a Live Portal Read-Only](../MeasuringALivePortalReadOnly) — which read-only instrument answers which question, and why an absence needs a coverage fact
- [A Census That Counts Must Name](../ACensusThatCountsMustName) — a count with no identity reads as clean
- [Search Coverage and Refusal](../SearchCoverageAndRefusal) — why a sweep's zero needs its denominator stated
