---
Name: Pre-PR Preflight
Category: Architecture
Description: One command before opening or pushing a pull request — scripts/preflight-pr.py runs every body-declaration gate, the source-guard root scan and (in MeshWeaver.Plugins) the platform-mount checks locally, reusing the CI gate scripts; offline mode reports what it could not ask as SKIPPED; and the design notes for re-judging an edited pull-request body automatically, which is still a follow-up.
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

In a fork, `origin` names the fork, not the repository the pull request targets. Without `--repo
Systemorph/<name>` the preflight then refuses with INCOMPLETE rather than guessing which gates apply.

## What it checks, and how

The script never re-implements a gate. It invokes the gate script that CI's job invokes, with the
same arguments, from the repository root, against the **merge base** with `--base` (default
`origin/main`). That is the same comparison every diff gate makes in CI. It reads the working tree as
it stands, untracked files included. But CI judges the COMMIT, and the gates do not all read the same
tree locally: the surface detector reads the working tree, while the pair gate's project listing
compares the merge base with `HEAD`. So any uncommitted change makes the verdict INCOMPLETE (exit 2,
the `Committed state` line names the changes) and never GREEN. Commit, then run it again.

| Check | Gate it invokes | Network |
|---|---|---|
| Pairs-with | `check-type-forwards.py --surface-json`, then `check-cross-repo-pair.py` | resolves a declared counterpart's state |
| Satellite-pins | `check-package-pin-removal.py` | none |
| Implementers | `check-interface-addition.py` on the same surface report | none |
| Mirror-sync | `check-i18n-mirror-sync.py` | none |
| Parser-delta | `check-parser-delta.py` (the `record-signatures` job's control) | none |
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
passed to `SourceScan.SourceFiles(root, [...])` whose root is the repository (`RepoPaths.Root`, a local
assigned from it, or a helper's `string root` parameter in a file that passes it). A scanner self-test
over a planted temp tree (`SourceScan.SourceFiles(dir.FullName, [...])`) names the fixture, not the
repository, and is skipped. A list entry naming a `const string` in the same file is resolved, and a
root list handed to a local helper that calls `SourceScan.SourceFiles` counts as SourceScan-backed.
Anything the scan cannot resolve statically (an entry built by an expression, a list handed to a method
the file does not define) is never dropped: when the diff deletes or renames anything, it makes the
verdict INCOMPLETE and is named. Comments are blanked first. For every path the diff
deletes or renames (`git diff --name-status -M` against the merge base), it reports each root that path
touches and that is now **missing** or **empty**, with the guard's `file:line`. A root read through
`SourceScan.SourceFiles` is empty once no `.cs`/`.razor`/`.csx` outside `bin`/`obj`/… is left under
it, whatever else is there, because that is all SourceScan reads. Any other root is empty when no file
is left. The fix belongs in the
same change: point the root at the new home of a moved directory, or drop the root of a retired one.

## Not yet: re-judging an edited body automatically

**A PR-body edit still needs a manual re-run of the failed body-gate jobs.** The `MeshWeaver Build and
Test` workflow does not fire on `edited`, and its body gates (every job that `needs: [pr-body]`, plus
the parser-delta control in `record-signatures`) judge the body their run read. Fix the body, run the
preflight on it, then re-run the failed jobs. The automatic re-run is a follow-up.

### Design notes for the follow-up

An event-driven lane plus a consolidation-time re-check was built and taken out again, after five
review rounds each found a new interleaving. A follow-up design has to answer every one of these
before it is built:

- **Cancel handoff.** Cancelling a running build, then re-running `pr-body`, can be interrupted
  between the two steps by a newer edit's lane, leaving the run cancelled with no re-run. Cancelling
  the whole run also cancels independent jobs (`workflow-shell`, `shared-rules`), and re-running
  `pr-body` does not bring them back.
- **Merge window.** If the build is not cancelled, it can publish `Consolidate test results = success`
  over the old body, and an armed pull request can merge before any lane reacts. Whatever re-judges
  the body must sit where the required verdict is made, or block merging until it has re-judged.
- **Title-only edits.** `edited` also fires for a title or base change. A workflow-level concurrency
  group lets those edits cancel or displace a body handler; filter them before any group is entered.
- **Attempt snapshots.** A partial re-run's attempt lists only the jobs it re-ran (`filter=latest`
  loses an earlier reader). `pr-body`'s outputs keep the attempt they were read in, so the gates of a
  later attempt may have judged a different body than `pr-body` reports. Compare per attempt.
- **Equal timestamps.** Job start times and `pull_request.updated_at` have one-second precision. A
  reader that started in the same second as the edit may have read the old body.
- **Per-gate verdicts.** One edit can clear a red gate and break a green one while the run stays red
  overall, so verdicts must be compared gate by gate, never as one aggregate. Gate jobs a run has not
  listed yet are pending, never green.
- **Recovery after a consolidation red.** If consolidation rejects an intervening bad body while every
  gate job stays green, a later corrective edit sees green → green and must still refresh the stale
  consolidation failure, without retrying unrelated failures.
- **Empty body.** A successfully read empty body encodes as an empty string. Tell it apart from a
  missing handover by the reader's head and attempt metadata.
- **Shallow checkout.** On the green-tree reuse path `collect-results` has a depth-1 checkout of the
  merge commit. Fetching only the base and the head leaves that commit as a shallow boundary, and
  `git merge-base` fails. Fetch the checked-out merge sha, or check out full history.
- **Dependabot's read-only token.** An edit by Dependabot runs with a read-only token whatever
  `permissions:` says, so a re-run POST is refused. Report it red with the run to re-run by hand.
  Never hand a write credential to a job that runs the pull request's code.
- **Run picked by head commit alone.** Two pull requests can share a head sha. Choose the run by its
  pull-request association.
- **Workflow revision.** The `needs:` graph that scopes a re-run must be the one the run executed (its
  head merged with the base as it stood then), not the edited event's checkout. A review-answered job
  in the re-run set must refuse the re-run: `Automatic review answered` is an API-written check-run
  that a re-run would overwrite. Never use `rerun-failed-jobs` on the whole run.
- **Gates outside the `pr-body` subtree.** The parser-delta control lives in `record-signatures`,
  which does not need `pr-body`, so re-running `pr-body` does not refresh it.
- **Re-running a tested tree.** A corrective edit re-runs the build and the suites even when the tree
  was already tested. That costs runner time, never correctness. Reusing tree evidence belongs with
  the build's own `precheck`.

## Proof

- `scripts/test-preflight-pr.py` uses fakes only: throwaway git repositories, draft body files, a
  fake `gh` on `PATH`, and a dead proxy set against any HTTP call that slips through. Each red case
  goes red for exactly its own gate. Each green case is green. Offline and token-less runs exit 2,
  never 0. A neutered root detector turns the guard-root red green, so the red cases cannot pass
  vacuously. The closing-keyword gate under test is the real script, and the pair gate's offline
  adapter runs against the real `check-cross-repo-pair.py`.
- It runs in `CI's own shell` in `dotnet-test.yml`.

Related: [Closing Keywords and Issue State](../ClosingKeywordsAndIssueState) ·
[The Cross-Repo Pair Gate](../CrossRepoPairGate) · [Reading CI Signals](../ReadingCiSignals) ·
[Planning a Database Migration](../PlanningADatabaseMigration) · [CI REST Budget](../CiRestBudget).
