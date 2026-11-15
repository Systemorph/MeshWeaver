---
Name: CI's Own Shell Scope
Category: Architecture
Description: >-
  How core's CI decides whether a pull request owes the "CI's own shell" self-test jobs, why they
  run beside the build instead of in front of it, and how a skipped self-test job is kept from ever
  reading as a pass: a recorded scope decision, an inert-paths allow-list that fails safe, the
  whole-tree guards that are never path-selected, and the required check that accepts a skip only
  beside that decision.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 17l6-6-6-6"/><path d="M12 19h8"/></svg>
---

# CI's Own Shell Scope

The `CI's own shell` jobs of `dotnet-test.yml` prove core's own CI code: every workflow, composite
action and script under `.github/`, the scripts under `scripts/`, the runner under `deploy/core-tests`
and the MSBuild files their harnesses evaluate. On the `main3` line two jobs carry those
self-tests — `workflow-shell` and `workflow-shell-executions` (the `main` line splits the second into
three) — and `workflow-shell-tree` carries the guards that
read the product itself.

## What changed, and why

The November 2026 CI profile (`Doc/Architecture/CiProfile2026-11` on the `main` line, recommendation R1) measured two costs on `main`:

- **Latency.** The self-test jobs were `needs:` of `stage-gate`, so no build started until the
  slowest of them finished: `stage-gate` started at minute **5.7** of an **18.5**-minute pull-request
  run (85 successful runs).
- **Runner minutes.** They ran on every pull request whatever it changed: 2,835 minutes over 3.25
  days, about **870 a day**.

Now the self-test jobs are no longer needs of `stage-gate`; they run **beside** the build. They run
only when the change set touches what they test, and they still gate the required check.

## The scope decision

`workflow-shell-scope` ("CI's own shell: are its inputs changed?") runs on every event with no
`needs:` and no `if:`. It first runs `workflow-shell-scope.py --self-test`, then decides:

| Situation | Decision |
|---|---|
| not a pull request (push, merge group, schedule, dispatch) | run |
| the change set cannot be read, or is empty | run (unknown fails safe) |
| any changed path is **not inert** | run, naming the paths |
| every changed path is inert | skip |

A path is **inert** only when all of these hold:

1. it sits under `src/`, `test/`, `samples/`, `docs/` or `.claude/`. Every other path is input or
   unknown: `.github/**`, `scripts/**`, `deploy/**`, `tools/**`, root files such as
   `Directory.Build.props` or `global.json`, and any new top-level folder;
2. it is not MSBuild or solution input (`*.csproj`, `*.props`, `*.targets`, `*.sln`, `*.slnx`) and
   not script- or workflow-shaped (`*.py`, `*.sh`, `*.bash`, `*.ps1`, `*.yml`, `*.yaml`). The
   self-tests evaluate or scan those files wherever they sit;
3. no self-test names it. A file listed in `NAMED_INPUTS` is input, and so is any existing file that
   the CI's own code (`.github/scripts`, `.github/actions`, `scripts`, `deploy/core-tests`) spells
   literally.

The rule is an allow-list of what is inert, so a path nobody thought about **runs** the self-tests.
The change set is the merge commit GitHub checks out (`refs/pull/N/merge`) diffed against its first
parent, the base it merged onto. That is the pull request's own change set, and a move on `main`
since the branch point does not count.

Suffixes match case-insensitively, the fail-safe direction: a `.Yml` file is input. The literal
derivation reads `.claude/` like the other inert tops.

The decision is printed, written to the run summary and, on a skip, raised as a notice. The skip is
a decision recorded in the run, never an absence.

File names are pull-request controlled, so the classifier prints `reason` as a single line that is
safe everywhere it lands:

- `%`, CR and LF are escaped the way GitHub's command parser requires (`%25`, `%0D`, `%0A`), and `:`
  becomes `%3A`. No name can add a `$GITHUB_OUTPUT` key or start a second `::` command.
- Each name sits in a code span with its backticks replaced, so the run summary shows it as text,
  never as Markdown.

The self-test asserts this on hostile names.

## How a skip is kept from reading as a pass

GitHub paints a skipped job with the same tick as a passed one, which is why a gate must never test
its own inputs (AGENTS.md). Here the skip is judged in `Consolidate test results`, the required
check:

- **The scope job's own verdict is a gate.** If `workflow-shell-scope` did not succeed, or printed
  neither `run=true` nor `run=false`, the required check is red by name.
- **A skip is accepted only beside a successful scope job that said `run=false`.** For each
  self-test job: `success` passes, and `skipped` passes only with that decision. Anything else is red,
  including `skipped` beside `run=true` or a failed scope job.
- **`workflow-shell-tree` is never exempt.** It must read `success`.

The classifier proves itself before it decides. Its self-test runs synthetic paths, a throwaway git
repository shaped like a pull request's merge commit, and the real tree. Its **negative control** is
that a change under `.github/scripts` runs the self-tests, checked both as a path and end to end
through git. On the real tree it also checks that every file under `.github/`, `scripts/` and
`deploy/` classifies as input, and that every `NAMED_INPUTS` entry still exists, because a stale
entry protects nothing.

## The whole-tree guards are never path-selected

Some guards read product files, so no change set is inert to them:

- the Key Vault secret-command scan (`check-manual-keyvault.py`) reads every tracked text file;
- the build-stage census (`core-build-stages.py --check`) reads test sources and docs;
- the node-tree path-collision and retired-field scans read the node trees.

They are cheap, so they run in `workflow-shell-tree` on every pull request, beside the build. They
keep the same posture as the self-test jobs: no `needs:`, no `if:`, the shared tool assertion and
the one prerequisite line that `check-guard-step-masking.py` asserts. Because they are not
path-selected, the scope rule never has to classify what they read.

## Coupling with the test selection

On the `main3` line `affected-tests.py` lets no `.github/**` file skip the .NET suite, so it reads
no shell job and the scope decision changes no test selection. (On `main`, Core Test Selection lets a
shell-only `.github/**` file owe no test project, and checks that the shell jobs carry exactly the
scope decision.)

## What it gives up, and what it does not see

- **Fail-fast.** A red self-test no longer holds the build; the run turns red at
  `Consolidate test results` once the build and shards finish. Such reds are rare, and the build
  minutes they cost are the price of not delaying every other pull request.
- **Reads that are not literal.** How the classifier knows what a self-test reads:
  - Today's reads were measured: every step of the `main` line's four jobs was run under a Python audit hook,
    recording each file opened or directory listed.
  - A future self-test that walks a product directory gets no protection from the literal
    derivation. Put such a guard in `workflow-shell-tree`, or list its files in `NAMED_INPUTS`.
  - Reads made by non-Python steps (shell scripts and `dotnet msbuild`) were not captured by the
    audit hook.

## Measuring it

The saving shows as the **`stage-gate` start offset** in a pull-request run: the minutes from the
run's start to the start of `stage-gate / Stage gate: may the suites start`. That offset was 5.7
before this change. It should now be set by the cheap body and policy gates, which finish at
minutes 0.2 to 0.6. The runner-minute saving is the `CI's own shell*` job minutes on pull requests
whose scope job said `run=false`.

Before merge, the rule was replayed over the last 200 first-parent merges of `main` (not `main3`), each merge
diffed against its first parent: **119 of 200 (60 %)** would have skipped the self-tests. Of the
rest, the first path that owed them was most often under `.github/scripts` (44), then `src/` (9:
MSBuild files and named inputs) and `.github/delivery-lines.json` (5). That replay covers merged
pull requests, not every pushed head.
