---
name: worktree
description: 'Work in an isolated git worktree and get CI green LOCALLY before pushing. Use at the START of any change to this repo — creating a branch, editing, building, or pushing — and whenever you are about to run a build/test command whose success you intend to believe. Covers the absolute primary-checkout rule (many agent sessions share this repo; mutating the primary clobbers their WIP), the never-git-stash rule, per-repo branch-protection strictness (mergeable is the bar, not merged), the CI flag set that promotes warnings to errors, and the seven commands that have produced a FALSE PASS on this macOS host — no timeout binary, --no-build on an unbuilt project, multi-project dotnet build, piping a build into tail.'
user-invocable: true
allowed-tools:
  - Read
  - Bash
  - Grep
  - Edit
---

# /worktree — isolate your work, then prove it green before you push

Two rules that never bend, and everything else here serves them:

1. **EVERY agent session works in its OWN `git worktree` on a fresh branch.** The primary checkout
   is a read-only reference.
2. **A verification step that cannot fail is not a verification step.** Demand a positive, specific
   success signal — `0 Error(s)`, a fresh `.trx`, an elapsed time that makes sense — never "the
   command returned".

## 🚨🚨🚨 ABSOLUTE: never work on the primary checkout

**The primary checkout (`/Users/roland/code/MeshWeaver`) is a READ-ONLY reference, not a workspace.
It must stay parked on `main` and untouched — never edit, build, commit, `checkout`, `switch`,
`reset`, or `stash` there, and never leave it on a feature branch.** It is the shared base that
every session's worktree is cut from; the moment you mutate it (working tree, index, or HEAD) you
can clobber every concurrent session's uncommitted WIP — a `reset --hard` there once wiped live
work across every session.

If you find yourself about to touch a file under the primary directly, STOP and create a worktree
first. Many Claude/agent sessions run against this repo at once; the worktree is what keeps them
isolated.

```bash
# base on origin/main for a fresh change, or on the feature branch you're extending
git worktree add -b feat/my-change /Users/roland/code/MW-my-change origin/main
cd /Users/roland/code/MW-my-change      # isolated tree + index — your edits can't touch other sessions
# …edit, commit, build with CI's flags, push, open the PR — all from here…
git worktree remove /Users/roland/code/MW-my-change   # once merged/abandoned
```

- `git worktree list` shows every active worktree (and which branch each holds — a branch can be
  checked out in only one).
- **Keep the primary parked on `main` and clean.** If you find it on a feature branch or with a
  dirty tree, capture any work you care about (`git diff > patch`) and restore it — `git switch
  main` — before continuing in a worktree. It is the cut-point for every other session; a
  dirty/feature-branch primary breaks the "cut a fresh worktree off `origin/main`" flow.
- **Never `git stash`** — the stash stack is repo-global and collides across worktrees; use
  `git diff > patch` + `git apply` instead.
- Parallel PR-building sub-agents must pass `isolation: "worktree"` as a tool PARAM (a prompt-only
  "work in a worktree" does nothing).
- **Stay at the root of your worktree** for every command — never the primary, never a hard-coded
  path. Avoid chained commands (`&&`, `||`), `for` loops, and `cd`; they all require user
  confirmation.

## 🚨 Before you push: make CI green LOCALLY first

CI builds **Release with warnings-as-errors**:
`dotnet build --no-restore -c Release -p:CIRun=true -warnaserror`. A plain local `dotnet build`
(Debug, no `-warnaserror`) passes while CI fails — warnings are promoted to errors there. Pushing a
red branch wastes a CI cycle and, per the green-merge gate, blocks the pull-based self-update if it
reaches main.

### 1. The bar is MERGEABLE, not merged

A branch that is merely *behind* main merges fine — do NOT re-sync and re-run CI just to catch up.
The `main pr protection` ruleset has **`strict_required_status_checks_policy: false`** and exactly
ONE required check, `Consolidate test results`. Verify rather than trust this line:

```bash
gh api repos/Systemorph/MeshWeaver/rulesets/2128472 \
  --jq '.rules[] | select(.type=="required_status_checks") | .parameters | {strict: .strict_required_status_checks_policy, checks: [.required_status_checks[].context]}'
```

Merging main into every branch before every push costs a full CI cycle per PR and, with several PRs
in flight, most of the throughput — the first merge makes every other branch stale again. Merge to
main, then let main's own build recompile. What you DO owe: no conflicts, and a green required
check.

🚨 **`strict` is PER-REPO — check the repo you are actually in.** This differs across the node repos,
and getting it wrong wastes a cycle in one direction or blocks you in the other. As of 2026-08-12:

| repo | `strict` | behind-main merges? |
|---|---|---|
| MeshWeaver | false (ruleset) | yes |
| MeshWeaver.Education (was education) | no branch protection | yes |
| MeshWeaver.Reinsurance | false | yes |
| MeshWeaver.SocialMedia | false | yes |
| MeshWeaver.Plugins | false (measured 2026-09-02; was true until 2026-08-29) | yes |

One command answers it for any repo, and beats trusting this table:

```bash
gh api repos/Systemorph/<repo>/branches/main/protection --jq '.required_status_checks.strict'   # 404 = unprotected
```

**Merge main only when it actually buys something**, which — outside a `strict` repo — is exactly
two cases:

- **The PR is `DIRTY`** (real conflicts). Resolve it — and afterwards run the revert-check:
  `git diff origin/main...HEAD --stat` (THREE dots) must show only your intended files. A
  branch-favoured hunk silently undoing someone else's merged work is a real failure mode here, not
  a hypothetical.
- **CI fails on something your diff does not touch.** Merge main *before* investigating: a stale
  branch re-samples flakes main has ALREADY fixed, and each one looks like a defect in your change.
  PR #794 burned five CI runs and most of a day that way — three different red tests, two already
  fixed on main, none caused by the branch.

🚨 But do not let that second case become a blanket excuse. Before attributing a red to main, check
whether your diff can actually reach the failing test. **"It's only markdown" is NOT such a check.**
Markdown in this repo is shipped, loadable content with guards over it —
`src/MeshWeaver.Documentation/Data/**` ships as `<EmbeddedResource>` and is scanned by
`WhatsNewEntryIntegrityTest` and `MigrationWorkloadModelGuard`, and `.claude/skills/**` is scanned
by the latter too. Front matter is YAML: an unquoted `: ` or ` #` inside a scalar stops the node
loading altogether (this took down Skill nodes on 2026-08-12).

### 2. Build with CI's flags

`dotnet build -c Release -warnaserror` for at least the projects you touched and their dependents.
Green here ⇒ green there for compile/warning errors. The classic miss: **CS9107** — a
primary-constructor parameter captured *and* passed to a base ctor (warning in Debug, ERROR under
`-warnaserror`). Fix it at the root: use the base's exposed member (e.g. `protected Output`)
instead of capturing the param; do NOT just `NoWarn` it.

### 3. Only push when that Release/`-warnaserror` build is clean

Then verify the PR check went green (`gh pr checks`) before declaring done. Full PR/merge gate:
[/pullrequest](../pullrequest/SKILL.md).

## 🚨 A verification step that cannot fail is not a verification step

Every command below has produced a **false pass** on this repo — it looked like it succeeded and it
did nothing.

| Trap | What you see | What actually happened | Instead |
|---|---|---|---|
| **`timeout` / `gtimeout`** | `command not found`, lost in a long log | Neither binary exists on this macOS host — the wrapped command **never ran** | Background it and enforce your own deadline (below) |
| **`dotnet test --no-restore` on a project never built in this worktree** | **Zero output, exit 0, no `.trx`** | Nothing was built, so no test ran. A fresh worktree has no `bin/` — this is its default state, and `--no-build` behaves the same | `dotnet build <project>.csproj` first, then require a fresh `.trx` |
| **`dotnet build a.csproj b.csproj`** (several project args) | `MSB1008: Only one project can be specified`, exit 1 | Nothing built. Reads like a transient and gets retried instead of fixed | One project per invocation |
| **Piping a build into `tail`/`head`** | A tidy tail with no error lines | `Build FAILED` scrolled off, and `$?` is **the pager's** status, not the build's | Don't pipe it; capture the build's own exit code and grep the `0 Error(s)` summary |
| **A build that finishes suspiciously fast** | `Build succeeded` in ~2 s | Up-to-date no-op — your edit may not be in it at all | Re-run with `--no-incremental` to prove a real compile happened |
| **`--no-build` after editing a doc / non-`.cs` asset** | Tests pass, so the edit is fine | `src/MeshWeaver.Documentation/Data/**` ships as `<EmbeddedResource>`; a stale DLL still holds the **old** file, so the test never saw your change. Caught while writing this table — the run predated the edit by 54 s | Rebuild after editing embedded content, and check the DLL's mtime is newer than the file's |
| **Reading a background task's output file right after launching it** | Plausible contents, so "the wait completed" | You read a stale, empty, or partial file. One session's "29 minutes of sleeps" had actually elapsed **2 minutes** | Compare wall-clock elapsed against the expected duration, not just the contents |

**Capping a run without `timeout`** — `timeout` exists on CI's Linux runners, NOT on this macOS
host. Locally: start the run in the background, hold the deadline yourself, and finish on the
positive signal.

```bash
date -u                                                       # 1. record the start — UTC, so it compares with CI
dotnet build test/MeshWeaver.Data.Test/MeshWeaver.Data.Test.csproj   # 2. never skip: --no-restore alone runs nothing
dotnet test test/MeshWeaver.Data.Test --no-build --logger trx        # 3. run_in_background: true
date -u                                                       # 4. poll; over budget ⇒ WEDGED, not slow
ls -la test/MeshWeaver.Data.Test/TestResults/*.trx            # 5. the pass signal: a .trx newer than step 1
```

Over budget means **stuck** — find what is not completing, never raise the bound (AGENTS.md → "No
band-aids").

## Checklist

- [ ] I am in a worktree, not `/Users/roland/code/MeshWeaver`.
- [ ] No `git stash` — patches instead.
- [ ] `dotnet build -c Release -warnaserror` on every touched project + dependents, one project per
      invocation, unpiped, and it printed `0 Error(s)`.
- [ ] Test runs were built first and produced a `.trx` newer than the run's start time.
- [ ] The PR is not `DIRTY`; I have not merged main "just to catch up".
