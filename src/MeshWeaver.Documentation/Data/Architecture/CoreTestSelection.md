---
Name: Core Test Selection
Category: Architecture
Description: >-
  How a core pull request decides which .NET test projects it owes — the ProjectReference closure
  for src/ and test/, and for .github/** a split derived from the tree: a file the .NET lane
  consumes runs the full suite, a file a test project reads runs that project, any other workflow
  or script owes no .NET test because the CI's-own-shell jobs cover it. What is derived, what
  fails safe, and what it does not see.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18"/><path d="M7 12h10"/><path d="M10 18h4"/></svg>
---

# Core Test Selection

Core runs **tests only for changed functionality**. On a pull request the `precheck` job of
`.github/workflows/dotnet-test.yml` runs `.github/scripts/affected-tests.py` against the diff and
prints one verdict — `mode` (`all`, `incremental` or `none`), the owed test `projects`, and a
one-line `reason`. Every shard obeys that verdict through `shard-assign.sh`, and `Consolidate test
results` accepts a run with no `.trx` only when the mode was `none`. A merge group, a push to a
delivery line, a schedule and a dispatch always run the full suite.

## The rules

| A changed path | What it owes |
|---|---|
| `src/<P>/…`, `test/<P>/…` | every test project whose transitive `ProjectReference` closure reaches `P` (or that is `P`) |
| `docs/**`, `.claude/**`, any `*.md`, `CODEOWNERS`, `.github/ISSUE_TEMPLATE/**`, `.github/*.txt` | nothing |
| `.github/workflows/dotnet-test.yml` | **all** |
| a `.github/**` file the **.NET lane consumes** | **all** |
| a `.github/**` file a **test project reads** | that test project |
| any other `.github/**` file | no .NET test — the shell jobs cover it |
| anything else (`Directory.*.props/targets`, `global.json`, `nuget.config`, `*.slnx`, `test/*.allow`, `tools/**`, `deploy/**`, …) | **all** |

Several files combine: one forcing file makes the run `all`; otherwise the owed projects are the
union. So a script nobody in the lane runs, changed beside `src/MeshWeaver.Data/X.cs`, owes exactly
the `MeshWeaver.Data` closure.

## `.github/**`: derived, never listed

Before this split every `.github/**` file forced the full suite. Measured on PR #8228, which changed
only `node-repo-module-pack.yml` and one `test-*.py` that the shell jobs execute: all eight shards
ran, about ten minutes, for files no .NET test reads.

The selector now derives, from the tree under test, **who consumes each `.github/**` file**:

**The .NET lane** is every job of `dotnet-test.yml` except two groups, both read from the workflow
itself: the CI's-own-shell jobs (every job whose id starts `workflow-shell`, so a split of those
jobs needs no change here) and the stage-0 static controls, which the stage gate names in its
`stage0-results` input. Every other job — `precheck`, `build`, `test`, `host-*`, `doc-gate`,
`platform-compat`, `licence-gate`, `collect-results`, … — is lane, and **a job added later is lane
by default**, so a new consumer can only make the selection more conservative.

From those jobs' text (comments removed) the walk collects what they consume, transitively:

- **YAML that runs as the lane** — the jobs, every reusable workflow they **call** (`uses:`), every
  composite action — is walked. Every `.github/…` path it names is consumed, and so is every
  non-workflow file it names by basename (`$ACTION_PATH/../../scripts/ci-fresh-merge.py`). A
  workflow it names without calling it is data (consumed, not walked); a workflow named by basename
  alone is an API name or a message (`actions/workflows/clients.yml/runs`).
- **A script** is walked for the scripts it loads — a sibling named in its code (`import x`, a path
  literal, an argv), with docstrings, triple-quoted fixtures and comment lines removed — and for
  the data files it names. Never for workflows: a `uses:` inside a script is a fixture.
- **A data file** (json, allow, list, …) is consumed and not walked.
- The selector itself is consumed (changing it runs everything) and is not walked — its self-test
  fixtures name files on purpose.

**Test projects that read `.github/`.** Several .NET tests are guards over the workflows and
scripts. The selector scans each `test/<P>/` (code, comments removed) for a `.github/…` path or a
`Path.Combine(…, ".github", "a", "b")`; that project is owed when one of those files changes,
transitively through a script it runs. A bare `".github"` it cannot resolve to a sub-path means it
reads the **whole** tree — `MeshWeaver.Documentation.Test` does (`NoLiteralNulInSourceGuard`,
`GuidanceBridgeRatchetGuard`), so in practice every `.github/**` change outside the lane owes that
one project and nothing else.

`affected-tests.py --explain` prints the derived lane jobs, the consumed files with what named each
one, and the test-project readers.

## Unknown fails safe

Every way the derivation can lose its footing forces the full suite for `.github/**` — the old rule:

- `dotnet-test.yml` is absent, has no `jobs:`, or has no `build` or `test` job;
- the walk reaches no file at all;
- a lane **command** names a whole directory (`--dir .github/scripts`), which reads files the walk
  cannot enumerate (a glob such as `.github/scripts/*` is taken as every file it matches; a
  directory mentioned in an `echo`/`::error` message, `name:` or `description:` is prose);
- the shell jobs are not guaranteed to run: the `pull_request` trigger carries a `paths`,
  `paths-ignore` or `branches-ignore` filter, a `workflow-shell*` job carries a job-level `if:` or
  `needs:`, or no such job exists. A `.github/**` file is let through only because those jobs run
  on **every** pull request, so this is checked on every run, not assumed.

A rename is listed under both paths (`git diff --no-renames`), so a file moved out of the lane
still forces.

## What it does not see

- Runtime code under `src/` is not scanned: it does not read this repository's `.github/` tree.
- A lane script that reads another workflow file as data is not followed into it.
- A reference that exists only in a comment is not a consumer — by design.

The cost of an over-read is runner minutes; the cost of an under-read is a skipped gate. Every
approximation above errs toward the first.

## Guards

`affected-tests.py --self-test` runs in `precheck` before the verdict. It builds synthetic trees and
proves: a script a lane job references forces, and so do its transitive imports, a composite action
and the script it reaches, a called reusable workflow and its script, the selector and
`dotnet-test.yml`; a shell-only script, a stage-0 control's script, an unreferenced script, another
workflow and `dependabot.yml` owe nothing; an unknown non-`.github` path forces; an unreferenced
script beside a `src/` file selects that closure only; a file a test reads owes that test (through a
script it runs, not through a workflow it reads); every fail-safe variant above forces; and, as the
**negative control**, the pre-derivation rule forces `all` on the very files the derivation lets
through. On the real tree it then asserts a canary: `shard-assign.sh`, `affected-tests.py`, the
`fresh-merge` action and `ci-fresh-merge.py` are lane-consumed, and `check-workflow-shell.py` is not.

## Replayed over history

Replayed over the 40 core pull requests merged before this change (the derivation of the current
tree applied to each one's file list): 19 forced the full suite under the old rule; **9 of them now
run only the affected test projects** (#8228 itself: `MeshWeaver.Documentation.Test` alone, 1 of
22), and 10 still force — they changed `dotnet-test.yml`, a lane script (`host-build-outputs.py`,
`check-review-answered.py`) or `tools/**`. None of the nine touched a file the lane consumes.

See also [Reading CI Signals](../ReadingCiSignals) for what a `none` run does and does not prove, and
[Fresh Merge Under Test](../FreshMergeUnderTest) for the tree the selection is computed on.
