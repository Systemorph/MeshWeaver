---
Name: Fresh Merge Under Test
Category: Architecture
Description: >-
  Every suite job of a pull request tests the head merged onto the base branch's CURRENT tip,
  computed in the job and reported in the run — never GitHub's refs/pull/N/merge as it stood at the
  push. Why the merge ref goes stale, how the merge is computed (no push, no new head, no loop), one
  main per run, a conflict is red, where it runs in core and in the shared lanes, and what it does
  not cover.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="6" cy="6" r="2"/><circle cx="6" cy="18" r="2"/><circle cx="18" cy="12" r="2"/><path d="M6 8v8"/><path d="M8 6c6 0 8 2 8 4"/></svg>
---

# Fresh Merge Under Test

Policy [`suites-test-fresh-merge`](../PolicyNotProse): **every suite job of a pull request tests the
head merged onto the base branch's CURRENT tip**, resolved when the suites start and printed in the
run. Never GitHub's merge ref from the push, and never a stale merge on a re-run.

## Why the merge ref is not enough

A `pull_request` run checks out `refs/pull/<n>/merge` **at the commit the event carried**. GitHub
builds that merge when the head is pushed and refreshes it lazily, so the run tests the head merged
onto main *as main stood at the push*. A re-run — a build-queue dispatch, a transient retry, the
steward, a stage advance — keeps the event's commit and tests the **same old merge** again.

Measured on core (REST, 2026-10-05): of 18 open pull requests, **13** had a `merge_commit_sha` whose
first parent was not main's tip, and reading every pull request again did not refresh a single one.
So a green earned that way can be green against a main that has since moved. That is how *"a gate
fix in main does not reach open PRs"* happened, and how two pull requests that were each green landed
a combination nobody had compiled.

## How the merge is computed

`.github/actions/fresh-merge` (composite, backed by `.github/scripts/ci-fresh-merge.py`) runs right
after the checkout of the repository under test and before anything reads the tree:

```text
main   = the base tip handed in, or `git ls-remote origin refs/heads/<base>` now
base   = git merge-base main head          (a shallow checkout deepens over git; REST is the last resort)
tree   = git merge-tree --write-tree --merge-base=<base> <main> <head>
commit = git commit-tree <tree> -p <main> -p <head>     (fixed identity, the head's commit date)
git checkout --detach <commit>
```

- **Nothing is pushed.** No branch update, no new head, no new review, no new run — so it cannot
  loop, cannot fight auto-arm or the queue, and needs no write token. A fork's pull request gets it too.
- **One main per run.** The merge commit is a pure function of (main tip, head), so every job handed
  the same `main-sha` arrives at the **byte-identical commit**. The first job resolves the tip and
  passes its `main-sha` output on, so a build and the tests that read its artifacts test one tree.
- **A conflict is red.** A head that conflicts with the current tip fails with the conflicting
  paths. It cannot merge anyway, and testing the stale merge would paint it green against a main it
  no longer merges onto.
- **It is reported.** Every applying job prints, and adds to its summary:
  *Tested: head `abc…` merged onto `main@def…` → `123…` (merged) (GitHub's merge ref was built on main@456…)*.
- **A selector sees the right diff.** A scope that diffs `origin/<base>...HEAD` now gets exactly the
  pull request's effect on the CURRENT main, because the merge-base of the tip and the fresh merge is
  the tip itself.
- **Off pull requests it does nothing**, and says so: a push, the schedule, a dispatch, the merge
  queue already test their own commit. A lane given an explicit `content-ref` (the platform's CD
  pointing at another repository's main) tests that ref as given.

The self-test (`ci-fresh-merge.py --self-test`, run by core's `workflow-shell` job) builds throwaway
repositories and proves each verdict both ways: the parents are `[main tip, head]`; main's NEWER
change is in the tree; a shallow and a full checkout produce the identical commit; a checkout already
on the fresh merge is left alone; a conflict is refused, naming the path; and, as the negative
control, the merge onto the OLD main is a different tree. Swapping the parents in the script reds
two of the cases.

## Where it runs

| Repository / lane | Resolves the tip | Applies the merge |
|---|---|---|
| core `dotnet-test.yml` | `build` (its `main-sha` output) — as the suites START; a run held for its review is released by `rerun-failed-jobs`, which re-runs `build` but not a green `precheck` | `precheck` (for the green-tree probe, on its own resolution), `build`, every `test` shard, `doc-gate`, `platform-compat` (all on the build's main), and `collect-results` (the `refs/ci-green` marker keys the BUILD's tree) |
| `node-repo-gate.yml` | `plan` (resolve only — the shard plan is unchanged) | every gate shard |
| `node-repo-module-pack.yml` | `select` | `select` (the build keys hash the tested tree), `build-workspace`, every `pack` and `tests` leg |
| `node-repo-compile-check.yml` | the job itself | the job itself |
| MeshWeaver.Plugins `ci.yml` | `admission`, as the run is ADMITTED (a held run's dispatch re-runs it) — for the portal hosts and the two shared lanes; the lighter legs that do not wait for admission resolve at their own start | `portal-hosts-build` + `portal-hosts-test`, `modules-floor` / `test-repos` (through the lanes' `merge-main-sha`), `compile-check-unit`, `compile-check-lanes`, `tests-ratchet`, `test-drift`, `rn-app`, `e2e-static`, `memex-template` |

A caller may pass `merge-main-sha` to the three lanes; empty means the lane resolves the tip itself
in its first job. Satellites need no change: they call the lanes at `@main`.

**When is "now"?** When the suites START. Under policy `review-then-suites` a run is held at the
stage gate until its review is answered and then released by `rerun-failed-jobs`; the job that
resolves the tip is one that re-runs then (core's `build`, Plugins' `admission`), so the released
suites test the main of the moment they start, not the main of the push. A re-run of failed shards
alone keeps the tree its build compiled — on purpose, since the shards read that build's binaries.

## What it does not do (stated)

- **Main moving AFTER the suites finished.** A pull request tested green against `main@X` is not
  re-tested when main moves to `Y`; with `strict: false` and no merge queue on core it can still merge
  a combination nobody compiled. The run says which main it tested, and main's own run builds what
  landed. Closing this fully is a merge queue or `strict: true`, both a maintainer's call.
- **Adopted verdicts.** Plugins' content-neutral-push adoption (`content-neutral-push-keeps-run`) still
  carries an earlier head's verdict, which was tested against the main of ITS run.
- **The dependent-suites candidate** core hands MeshWeaver.Plugins is still `github.sha` (the merge
  ref): a locally computed merge is not fetchable by another repository.
- **The cheap stage-0 controls** still read the event's checkout; they run within minutes of the
  push and compare against the merge-base, not the tip.
- **A DIRTY pull request still gets no run at all** — GitHub creates no `pull_request` run for it.
  That is why AGENTS.md keeps `merge-latest-before-push`: merging main before a push is how an author
  resolves the conflict this page turns red.

## Related

- [Staged Pull Request Pipeline](../StagedPullRequestPipeline) — review first, then these suites, then the arm (policy `review-then-suites`)
- [Review Findings Answered](../ReviewFindingsAnswered) — the required check that still gates the merge
