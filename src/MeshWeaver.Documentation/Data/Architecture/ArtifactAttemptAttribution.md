---
Name: Which Attempt an Artefact Belongs To
Category: Architecture
Description: A workflow run holds every attempt's artefacts at once, the artefacts API carries no attempt field, and download-artifact resolves a duplicated name by artefact ID — which is not monotonic across attempts. That is how the required check consolidated attempt 1's failing results over attempt 2's green ones and could not be made green by any re-run. The rule that follows - evidence carries its attempt in the name, transport overwrites - and how to measure a run for it.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 12a9 9 0 1 1-3-6.7L21 8"/><path d="M21 3v5h-5"/></svg>
---

# Which Attempt an Artefact Belongs To

**A re-run does not replace the previous attempt's artefacts — it adds to them.** Everything below
follows from that one fact, and from a second: nothing GitHub returns about an artefact says which
attempt produced it.

## What it cost (measured, 2026-09-14, run 34823051999)

`Consolidate test results` is this repository's ONE required check. On PR #4292, head `75ae0835`:

| attempt | `Run tests (shard 1)` | `Consolidate test results` |
|---|---|---|
| 1 | ❌ one failing test (an unattributed failure, #4300) | ❌ correct |
| 2 — `rerun-failed-jobs` | ✅ 1454 / 1454 | ❌ **re-declared the same failing test** |
| 3 — `rerun-failed-jobs` of the consolidate alone | — | ❌ the same, three minutes later |

The run held two artefacts under one name:

```
id 10338854604  size 297521  created 08:38:28Z   ← attempt 1, carries the failure
id 10338734845  size 298484  created 08:49:55Z   ← attempt 2, green
```

`actions/download-artifact` de-duplicates a name collision by keeping the **highest artefact ID**,
and artefact IDs are **not monotonic across attempts**: attempt 2's ID is the lower of the two. So
the consolidate downloaded attempt 1's trx, re-declared the failure the re-run had just cleared, and
did it again on the next re-run. **That run could not be made green by any re-run — only by a new
head.**

A required check whose recovery path is a no-op turns every unattributed failure and every
infrastructure hiccup into a forced new push. It compounds with anything that races on head
freshness: on the same day, half of the day's CD runs were evicted by a newer head
([#4322](https://github.com/Systemorph/MeshWeaver/issues/4322)), and a forced new push is what feeds
that race.

## Why the name, and not the API

`GET /repos/{owner}/{repo}/actions/runs/{id}/artifacts` returns, per artefact: `id`, `name`,
`size_in_bytes`, `created_at`, `updated_at`, `expires_at`, `expired`, `digest` and a `workflow_run`
block of `{id, repository_id, head_repository_id, head_branch, head_sha}`.

**There is no attempt field** — measured on the two artefacts above. A consumer therefore cannot
attribute an artefact to an attempt from anything the platform tells it. The producer has to say so,
and the NAME is the only channel it has.

`created_at` orders those two correctly and is still the wrong instrument, for the same reason the
ID is: it answers *which upload is newer*, not *which attempt is this shard's outcome*. A
`rerun-failed-jobs` re-runs only the failed jobs, so five shards keep their attempt-1 artefacts and
one has an attempt-2 artefact — a legitimate MIXTURE of attempts, in which "take the newest upload"
and "take the current attempt" are both wrong. Only *the highest attempt per shard* is right.

## The rule

**Evidence carries its attempt in the name. Transport overwrites.**

| | shard test results | shard build output |
|---|---|---|
| what it is | EVIDENCE — the trx a verdict is read from | TRANSPORT — binaries, superseded the moment they are rebuilt |
| older attempt | must SURVIVE and stay attributable (forensics) | worthless, and dangerous if read (you would test code this attempt did not build) |
| name | `testResults-shard<N>-attempt<run_attempt>` | `build-output-<N>` |
| collision | cannot happen — the name is unique per attempt | cannot happen — `overwrite: true` deletes the name it replaces |
| consumer | `resolve-shard-attempts.py` keeps the highest attempt per shard | plain `name:` — only one can exist |

Both halves remove the ambiguity rather than resolving it after the fact. Deleting the stale artefact
in a cleanup step is the same fix from the wrong end: it leaves a window in which the consolidate can
still read it, and it makes the read depend on a race.

This is the same remedy the fleet already applies to the other artefact-name collision: in
`node-repo-module-pack.yml` a repository may invoke the reusable lane twice in one run, two artefacts
named `workspace-build` coexisted, `download-artifact` resolved the name to one of them, and a
required gate flipped green/red on `main` with no source change (Plugins#1077). **When two producers
can land one name in one run, the discriminator goes INTO the name, derived from the producing
context rather than trusted from a caller.**

## The rate, with its denominator

Swept 2026-09-14 over every `dotnet-test.yml` run available in the API window:

| | |
|---|---|
| runs swept (2026-09-12T07:33Z → 2026-09-14T16:32Z) | **700** |
| with `run_attempt > 1` | **15** (2.1 %) |
| of those, carrying a duplicated `testResults-shard*` name | **9** |
| of those 9, where the ID-pick resolves to the OLDER upload | **1** |
| carrying a duplicated `build-output-*` name | **0** |

So the consolidate read a superseded artefact in **1 of 9** duplicated runs — the ID ordering decides
it, and it is a coin toss. The build-output half is **latent, not hypothetical**: zero occurrences
only because `rerun-failed-jobs` re-ran shards and never `build`; a whole-workflow re-run re-runs
`build` too, and a shard would then extract whichever tarball drew the higher ID.

## Measuring a run for it

```bash
gh api "repos/Systemorph/MeshWeaver/actions/runs/<run_id>/artifacts?per_page=100" \
  --jq '.artifacts[] | "\(.id) \(.name) \(.size_in_bytes) \(.created_at)"' | sort -k2
```

Two rows with the same name is the collision. Compare `max by id` against `max by created_at`: when
they differ, a by-name download resolved to the superseded upload. `run_attempt` itself comes from
the run: `gh api repos/{o}/{r}/actions/runs/<run_id> --jq '.run_attempt'`.

After the fix there is nothing to compare: every name is distinct, and the consolidate PRINTS what it
resolved —

```
shard 0: attempt 1 (its only upload)
shard 1: attempt 2 — superseding attempt(s) 1
resolved 6 shard directories to the newest attempt (run attempt 2); 1 superseded upload(s) dropped
```

— which is the positive, specific success signal the run previously had no way to give.

## What the resolver refuses, and what it deliberately does not

`.github/scripts/resolve-shard-attempts.py` runs in `collect-results`, straight after the download,
with its own self-test one step ahead of it (an unproven resolver is no resolver). It REDS on:

- a directory whose name is not attempt-scoped — evidence it cannot attribute;
- an attempt NEWER than the run's own — a state that cannot exist;
- a shard index outside the matrix — the coverage gate counts against the matrix, so this would be
  evidence nothing downstream is checking.

It says nothing about a shard that reported **nothing**: that is
`Every shard must have reported`'s verdict, which names which sixth of the suite is missing. One red
per cause, and the script never becomes a second, weaker copy of a gate that already exists.

## Related

[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — what a check's colour is worth ·
[The Merge Queue](/Doc/Architecture/MergeQueue) — where a queue red lives, and why the steward reads
a shard's trx rather than the run's colour ·
[Artifact Retention Interlock](/Doc/Architecture/ArtifactRetentionInterlock) — how long these
artefacts exist at all
