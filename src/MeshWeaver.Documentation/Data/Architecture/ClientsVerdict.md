---
Name: The Clients Workflow Verdict
Category: Architecture
Description: "How core's `Clients workflow verdict` job reports the path-filtered `Clients` workflow into `Consolidate test results` without waiting three minutes on every run: it applies clients.yml's own trigger to the change set, believes a zero early only when that proves no Clients run is coming, and keeps the full wait whenever the proof cannot be made."
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# The Clients workflow verdict

`clients.yml` (the `Clients` workflow, today the Python SDK under `clients/python`) is a separate
workflow with a path filter: it runs only when a change set touches `clients/**`, the workflow file
itself, or the protos the SDK generates from. Its verdict still has to be able to block a merge, so
the `Clients workflow verdict` job of `dotnet-test.yml` reads the newest Clients run for the commit
under test and `Consolidate test results`, the required check, fails unless that job succeeded.

Related pages: [CI Profile, November 2026](../CiProfile2026-11) (row R10) ·
[Reading CI Signals](../ReadingCiSignals) · [Staged Pull Request Pipeline](../StagedPullRequestPipeline).

## What the verdict is

| Clients runs for the commit | Verdict | Job |
|---|---|---|
| newest run completed `success` or `skipped` | that conclusion | green |
| newest run completed with any other conclusion | that conclusion | **red** |
| newest run not completed within the job's 25-minute budget | none | **red** (Clients is wedged) |
| no run, after the zero-runs grace period | `not-applicable` | green |
| the runs could not be listed even once | none | **red** ("could not fetch" is not "no runs") |

## Why "zero runs" needs a grace period

Both workflows fire on the same event, so at the moment the job first looks, zero runs can mean two
things: the path filter matched nothing, or GitHub has not created the Clients run yet. Reading the
second as the first would let a change under `clients/` merge without its verdict. So a zero is
believed only after a grace period of consecutive zeros.

That grace period used to be a flat 180 seconds counted from the job's start. Measured over three
recent pull-request runs (jobs `132976854871`, `132977212166`, `132977419766`), the job polled nine
times, found no run, and reported `not-applicable` after 3 minutes 6 to 8 seconds. The last Clients run
in the repository is from 2026-10-27, so in the profiled window every run of this job spent its three
minutes waiting for a run that could never exist: about 250 runner-minutes a day.

## Proving no run is coming

The job now decides, before it reads the runs, whether this change set can start a Clients run at all.
`.github/scripts/clients-applicability.py` reads `clients.yml` at the commit under test (a sparse
checkout of `.github`, so the trigger is never copied into the gate) and applies its trigger to the
change set:

* a pull request's files come from `pulls/{n}/files`, and the listing must hold exactly the PR's
  `changed_files`;
* a push's files come from `compare/{before}...{sha}`, and a listing that reaches the API's
  300-file cap proves nothing;
* a renamed file is matched under both its old and its new name;
* only plain `paths:` globs and literal `branches:` are evaluated. A negation, `paths-ignore`, a
  branch pattern or any other event (`merge_group`, `workflow_dispatch`) gives `unknown`;
* a file or a pushed branch that matches the filter only when case is ignored gives `unknown`,
  never `false`. Whichever way GitHub compares case, a `false` stays proven.

The answer is `applicable=true`, `false` or `unknown`, with a reason, printed in the log and in the
job summary.

| Answer | Zero-runs grace |
|---|---|
| `true` | 180 s from the job's start, as before. A run is expected; if GitHub skipped it anyway (its own path-filter limits), the old behaviour applies. |
| `unknown` (any listing failure, any trigger shape not evaluated) | 180 s from the job's start, as before. |
| `false` | 30 s from **this workflow run's creation**. If that time cannot be read, 180 s from the job's start. |

The shortened grace counts from the run's creation, not from the job's start, because GitHub creates
every workflow run of one event together. Over the last 12 Clients runs, each was created within one
second of the `MeshWeaver Build and Test` run for the same commit. So at 30 seconds, a Clients run that
GitHub started despite the proof is already listed, and the job gates on it like any other run. One
such disagreement exists in the history: GitHub started a Clients run on a merge-from-main head of
#7307 whose diff, as listed today, touches none of the filtered paths. A disagreement is not a pass.

## What did not change

* `Consolidate test results` still fails unless the job **succeeded**. A skipped or cancelled
  verdict job is red, exactly as before.
* No step carries `continue-on-error`, and no `if:` asks whether an input exists. Every failure of
  the proof (a listing error, an unparseable workflow, an unknown event, a truncated listing) answers
  `unknown`, and `unknown` keeps the old wait. A failure can make the job slower, never greener.
* A failed read of the runs is still retried and is still never treated as zero.
* The self-test runs as the job's first step on every run, with negative controls (a change under
  `clients/`, a proto change, a change to the workflow file, a rename out of `clients/`) that must
  answer `true`.

## Why not read the verdict inside Consolidate

The profile proposed reading the Clients check once, at `Consolidate test results` time. That
removes the job but moves the creation race into the required check: when the suites are skipped
(a docs-only change set, or a reused green-tree marker), Consolidate can start seconds after the
event, and it would need the same grace period or a poll. Proving that no run is coming removes the
wait in both places. The job keeps its own setup cost (about half a minute: the sparse checkout, the
listing and a 30-second floor), against 3.1 minutes before.
