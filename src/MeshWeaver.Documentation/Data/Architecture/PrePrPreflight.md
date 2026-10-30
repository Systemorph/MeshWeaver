---
Name: Pre-PR Preflight
Category: Architecture
Description: One command before opening or pushing a pull request — scripts/preflight-pr.py runs every body-declaration gate, the source-guard root scan and (in MeshWeaver.Plugins) the platform-mount checks locally, reusing the CI gate scripts; offline mode reports what it could not ask as SKIPPED; and how an edited pull-request body re-judges the body gates without re-running the build.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 11l3 3L22 4"/><path d="M21 12v7a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11"/></svg>
---

# Pre-PR Preflight

**Run `scripts/preflight-pr.py` before `gh pr create` and before every push.** It reports every gate
CI would turn red for a reason you can already know locally: the declarations your pull-request body
owes, and the source guards a deletion leaves pointing at nothing. Each such red that reaches CI costs
a full round (a push or a body edit, a re-run, a wait) for a verdict that was available before the
pull request existed.

```bash
# core worktree — draft the body into a file first
python3 scripts/preflight-pr.py --body-file pr-body.md            # online: uses $GH_TOKEN or `gh auth token`
python3 scripts/preflight-pr.py --body-file pr-body.md --offline  # no network: static halves only
python3 scripts/preflight-pr.py                                   # no body yet: guards only, body gates SKIPPED

# a satellite checkout (MeshWeaver.Plugins, a node repo)
python3 <core checkout>/scripts/preflight-pr.py --repo-root . --body-file pr-body.md
```

The verdict line and the exit code say the same thing:

| Exit | Verdict | Meaning |
|---|---|---|
| 0 | GREEN | every check that applies passed |
| 1 | RED | at least one check would fail in CI, and the line under it says why |
| 2 | INCOMPLETE | nothing red, but at least one check was SKIPPED and is NOT verified |

A SKIPPED check is never a pass. It always prints its reason: `--offline`, no token, no `--body-file`,
an input only CI has.

## What it checks, and how

The script never re-implements a gate. It invokes the gate script that CI's job invokes, with the
same arguments, from the repository root, against the **merge base** with `--base` (default
`origin/main`). That is the same comparison every diff gate makes in CI. It checks the working tree as
it stands. Uncommitted changes are included, and the header says so, because CI sees only what you push.

| Check | Gate it invokes | Network |
|---|---|---|
| Pairs-with | `check-type-forwards.py --surface-json`, then `check-cross-repo-pair.py` | resolves a declared counterpart's state |
| Satellite-pins | `check-package-pin-removal.py` | none |
| Implementers | `check-interface-addition.py` on the same surface report | none |
| Mirror-sync | `check-i18n-mirror-sync.py` | none |
| Closing keywords and `Verified-closing:` | `check-closing-keywords.py --repo <this repo>` | reads each closed issue's labels |
| Db-migration | the repository's own `scripts/check-db-migration-declaration.py`, where it exists (MeshWeaver.Plugins) | none |
| Source-guard scanned roots | the preflight's own scan (below) | none |
| Platform mount | MeshWeaver.Plugins' `scripts/platform-mount.py --self-test`, and `--audit-core` when `--platform-core` names the checkout to audit | none |

In core the gate scripts are the checkout's own copies, because core's CI runs them at the head under
test. In a satellite the core gates do not apply, except the closing-keyword gate in the protected-core
shape `node-repo-validate.yml` runs (`--protect-public-repo Systemorph/MeshWeaver --only-protected-repos`).
That gate comes from `--core-root`, or from the directory the preflight lives in. Failing both, it is
fetched at `--core-ref` (default `main`) over anonymous git, the transport of
`.github/scripts/mw-core-file.sh`. A satellite lane can therefore run it the way it runs every central
script: fetch `scripts/preflight-pr.py` with `mw-core-file`, then
`python3 preflight-pr.py --repo-root . --core-ref <the lane's scripts ref> --body-file …`.

### Offline

`--offline` runs only the static half. For the two gates that need GitHub, the preflight calls the
gate's own evaluation function (`evaluate` in `check-cross-repo-pair.py` and in
`check-closing-keywords.py`) with a resolver that asks nothing:

- a negated or possessive closing keyword, a refused or unbound `Verified-closing:`, a missing or
  unparseable `Pairs-with:` and a counterpart outside the fleet are still **RED**, because none of
  them needs the network;
- a counterpart whose state was not read, and an issue whose labels were not read, are **SKIPPED**,
  each named. A `sev:B`/`sev:H` label on that issue would be red in CI.

Without a token the online mode does the same, and says it had no token.

### Source guards whose roots a deletion empties

Many guards under `test/` scan a fixed list of directories. `NoLiteralNulInSourceGuard` fails red on
a root that matches nothing. Most others go through `SourceScan.SourceFiles`, which drops an absent
root silently, so a guard whose root moved goes on passing having scanned nothing. That is worse than red.

The preflight reads those lists from the test sources themselves: every `…Roots` field, property or
method initializer (`ScannedRoots`, `ProductionRoots`, `HomeRoots`, …), and every inline root list
passed to `SourceScan.SourceFiles(root, [...])`. Comments are blanked first. For every path the diff
deletes or renames (`git diff --name-status -M` against the merge base), it reports each root that path
touches and that is now **missing** or **empty**, with the guard's `file:line`. The fix belongs in the
same change: point the root at the new home of a moved directory, or drop the root of a retired one.

## An edited body re-judges the body gates — without re-running the build

The body gates in `MeshWeaver Build and Test` are every job that `needs: [pr-body]`. They judge the
body as the run's `pr-body` job read it, and that workflow does not fire on `edited`. Adding `edited`
to it would be wrong twice over. Its concurrency group cancels the in-flight run of the same pull
request, so every edit would throw a running build away. And a run for an edit would still have to
publish the required `Consolidate test results`, where a skipped required context counts as satisfied.

`.github/workflows/pr-body-edited.yml` fires on `pull_request: edited` when the **body** changed, on a
same-repository head. It acts on the **existing** run, and only when that run's verdict is no longer
true. `.github/scripts/pr-body-regate.py` holds the decision table, and its self-test proves every arm:

| The run… | Current body (same gates, via this preflight) | Action |
|---|---|---|
| read the body after the edit, or has not read it yet | not judged | nothing |
| body gates green | green | **nothing.** The verdict holds for the current body, and the build is not re-run |
| body gates red | green | re-run the `pr-body` job by id once the run has finished. Every body gate needs it and re-runs with it, reading the current body, because a new attempt never reuses the old read (`read-current-pr-body.py --reuse-from-env`), and the build they held runs. It never ran, so it is owed, not repeated |
| body gates red | red | nothing. The required check is red and stays red |
| body gates green (or still judging) | red | re-run the `pr-body` job, after cancelling an in-flight run. Its dependents follow: the gates go red, stage 0 holds the build, and the required check turns red within minutes |
| any | undecided (could not be judged) | the `pr-body` job is re-run either way: truthful, and cheap whenever a gate is red, because a red gate holds the build |

🚨 **The lane never posts `rerun-failed-jobs` on the whole run.** Every re-run is one job, `pr-body`,
by id; GitHub re-runs the jobs that need it along with it. Before posting, the lane asserts that the
run belongs to `.github/workflows/dotnet-test.yml` by its workflow path, not its name. It also computes
that re-run set from the workflow file's own `needs:` graph and refuses, red and named, if any job in it
matches *review answered*. `Automatic review answered` is an API-written check-run per head, and a
re-run that included a legacy job of that name would overwrite the API verdict. `dotnet-test.yml`
carries no such job today; the assertion keeps it that way, and the self-test measures the real
workflow's set.

So an edit that fixes a body declaration turns `Consolidate test results` green with no manual
re-run. An edit that breaks one turns it red. An edit that changes nothing the gates read re-runs
nothing. While a held run is still finishing, the lane waits within a bounded poll, a script and
never a model loop, and goes red past its budget, saying what to re-run by hand.

A fork's head is exempt on the event: GitHub gives its run no secrets and a read-only token. Re-run
its failed jobs by hand.

## Proof

- `scripts/test-preflight-pr.py` uses fakes only: throwaway git repositories, draft body files, a
  fake `gh` on `PATH`, and a dead proxy set against any HTTP call that slips through. Each red case
  goes red for exactly its own gate. Each green case is green. Offline and token-less runs exit 2,
  never 0. A neutered root detector turns the guard-root red green, so the red cases cannot pass
  vacuously. The closing-keyword gate under test is the real script, and the pair gate's offline
  adapter runs against the real `check-cross-repo-pair.py`.
- `pr-body-regate.py --self-test` covers every row of the table above, a wait that outlasts its
  budget (red), and a neutered table (it must not act).
- Both run in `CI's own shell` in `dotnet-test.yml`, and again in the lane before it decides anything.

Related: [Closing Keywords and Issue State](../ClosingKeywordsAndIssueState) ·
[The Cross-Repo Pair Gate](../CrossRepoPairGate) · [Reading CI Signals](../ReadingCiSignals) ·
[Planning a Database Migration](../PlanningADatabaseMigration) · [CI REST Budget](../CiRestBudget).
