---
Name: The Merge Queue
Category: Architecture
Description: Why main needs a queue, what the first outing measured (the churn window; hand re-queues), the settings that remove the churn, and the steward that re-queues on evidence so nobody runs after the queue.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="8" y1="6" x2="21" y2="6"/><line x1="8" y1="12" x2="21" y2="12"/><line x1="8" y1="18" x2="21" y2="18"/><line x1="3" y1="6" x2="3.01" y2="6"/><line x1="3" y1="12" x2="3.01" y2="12"/><line x1="3" y1="18" x2="3.01" y2="18"/></svg>
---

# The Merge Queue

**A merge queue builds the combination that is about to land, before it lands.** With
`strict: false` branch protection every pull request is tested against the `main` it branched from,
so a burst of merges lands a tree no run ever compiled. That happened twice in one week —
`CS0246: MeshOperations` on 2026-08-26, and on 2026-08-30 a new guard (#2782) plus an
independently-landed gate primitive (#2792) that were green alone and red together, holding `main`
red for five consecutive runs and catching a documentation-only PR in the blast. The queue is the
structural fix (#2412): each entry is built on top of the entries ahead of it, and the tested commit
is the commit that lands.

This page is the operating manual: what the first outing taught, the settings that answer it, and
the **steward** — the lane that acts on every ejection so a person does not have to.

## What the first outing measured (2026-08-30 → 09-01)

The queue was enabled on 2026-08-30 by #2799 (`merge_group` trigger on Build and Test) plus a
`merge_queue` rule on the `main pr protection` ruleset — `ALLGREEN`, up to 3 entries built and merged,
`SQUASH`, 60-minute check timeout — and removed on 2026-09-01. Two failure modes, both measured:

1. **The churn window.** With three entries built speculatively, *every* membership mutation —
   an enqueue, a dequeue, a push to a queued branch — rebuilt the speculative stack and restarted
   every in-flight build. Several sessions each acting reasonably kept the stack in permanent
   rebuild: for over an hour nothing landed, and **nothing failed**. Zero reds, zero merges — the
   run list showed only cancellations, which nothing alerts on (the same shape as
   [Reading CI Signals](../ReadingCiSignals) § "Delivery can stop for hours with every dashboard green").
2. **Hand re-queues.** An entry a flake ejected simply left the queue. Nothing re-queued it; the
   maintainer did, by hand, after noticing. Of the 83 red Build-and-Test runs between 08-30 and
   09-02, 51 were `merge_group` runs — most of them ejections that then needed a person.

Two things did work and are kept: queue merges are ordinary pushes, so `main`'s push lanes ran on
every one of them (40/40 on 08-31 — the `GITHUB_TOKEN` non-triggering trap does not apply to the
queue); and Build and Test already fires on `merge_group`, with `Consolidate test results` produced
unconditionally on that event (`collect-results` is `if: always()` with no event filter, and the one
gate that needs a pull-request body — `cross-repo-pair` — is exempted on the *event*, so its fail
step cannot fire on a queue run).

## The settings, and why each one

| Parameter | Value | Why |
|---|---|---|
| `max_entries_to_build` | **1** | Removes the speculative stack, and with it the churn window: with one entry building there is nothing for a membership mutation to rebuild except that entry. The cost is serial throughput — one group build at a time — which is affordable now that a PR run takes 6–19 minutes, and often ~2: a single entry whose PR run tested against the same `main` tip has an **identical tree**, and Build and Test reuses that green (`refs/ci-green/<tree>`) instead of re-running it. |
| `max_entries_to_merge` | 3 | The upper bound on how many consecutive green entries land as one push. Bounds the blast radius of a red `main` to three PRs. |
| `min_entries_to_merge` | 1 | A green entry is never held hostage to a second one arriving. |
| `min_entries_to_merge_wait_minutes` | 3 | Only delays a group *smaller than the minimum*, so with a minimum of 1 it cannot bind today; it is set short so that raising the minimum later never introduces a long wait by accident. |
| `grouping_strategy` | `ALLGREEN` | Every entry in a merge group must be green on its own build; nothing lands on the strength of a later entry's green. (With one entry built at a time, `HEADGREEN` would be equivalent — `ALLGREEN` states the intent.) |
| `merge_method` | `MERGE` | The repo's convention (`gh pr merge --merge`), keeps each PR's commits and `Co-Authored-By` trailers, and the commit the queue built is the commit `main` fast-forwards to — sha-identical, so [verifying an image by commit](../ContinuousDeliveryContract) needs no detour through the tree. |
| `check_response_timeout_minutes` | 45 | Matches the fleet's hard per-job cap (`check-workflow-timeouts.py`): a queue build that has not reported in 45 minutes is stuck by the same doctrine, and the steward re-queues it once. 60 was the 08-30 value; the slowest honest PR run is 19 minutes. |

Two properties of `dotnet-test.yml` matter for the queue and were checked rather than assumed:

- **Concurrency cannot cancel a queue build.** The group is
  `build-test-${{ github.ref }}`, and a queue entry's ref is `gh-readonly-queue/main/pr-<N>-<base sha>`
  — unique per entry per base. Two runs share a group only when GitHub rebuilds the *same* entry on
  the *same* base, and then cancelling the older one is correct.
- **`Consolidate test results` is present on every queue run.** See above; a queue whose required
  context can be absent ejects entries with nothing to point at (`CI_TIMEOUT`), which is what the
  missing `merge_group` trigger looked like before #2799.

### Enabling it

The rule is added to ruleset `2128472` (`main pr protection`) with the REST rulesets API. `PUT`
replaces the whole ruleset, so the existing rules are read back and the queue rule appended:

```bash
gh api repos/Systemorph/MeshWeaver/rulesets/2128472 \
  --jq '{name, target, enforcement, conditions, bypass_actors,
         rules: (.rules + [{type: "merge_queue", parameters: {
           merge_method: "MERGE", grouping_strategy: "ALLGREEN",
           max_entries_to_build: 1, max_entries_to_merge: 3,
           min_entries_to_merge: 1, min_entries_to_merge_wait_minutes: 3,
           check_response_timeout_minutes: 45}}])}' > /tmp/ruleset-2128472.json
gh api -X PUT repos/Systemorph/MeshWeaver/rulesets/2128472 --input /tmp/ruleset-2128472.json
```

Read it back — `mergeQueue(branch:"main")` is `null` while disabled:

```bash
python3 .github/scripts/merge-queue-steward.py status --repo Systemorph/MeshWeaver
```

`status` prints the live configuration, warns on every parameter that drifts from the table above,
and lists the entries currently queued.

## The steward — the hand that re-queues, on evidence only

`.github/workflows/merge-queue-steward.yml` fires on `pull_request` with `action: dequeued`; the
event carries a `reason`. The workflow is thin; `.github/scripts/merge-queue-steward.py` decides,
mints nothing itself, and proves its own decision table with `--self-test` before every real
decision. It uses **two tokens, split by direction**: every *read* (the failed run's jobs and
artifacts, the PR head's check-runs, commits, comments) goes through the job's own `GITHUB_TOKEN`
with `actions: read` + `checks: read`; every *write* (comment, label, enqueue) goes through the App
installation token minted the way `auto-arm.yml` does — the org grants that App exactly
`contents: write` + `pull_requests: write`, so it cannot read Actions, and a re-queue performed with
`GITHUB_TOKEN` would merge as the bot and start no run on `main` (#2916). The `queue-rejected`
label is a repository fixture (creating a label needs `issues: write`, which the App does not hold;
applying an existing one needs only `pull_requests: write`).

| Reason | What the steward finds | Action | Cap per head sha |
|---|---|---|---|
| `CI_TIMEOUT` | — | re-queue | 2 |
| `CI_FAILURE` | a job other than a test shard failed (build, a gate) | **reject** — never a flake | — |
| `CI_FAILURE` | every failed assertion matches an active catalogue entry | re-queue | 2 |
| `CI_FAILURE` | a shard failed on an infrastructure step (download, upload, setup) and left no test evidence | re-queue | 2 |
| `CI_FAILURE` | an uncatalogued assertion, the group held more than one PR, and this PR's own run was green | re-queue **alone** — the culprit's solo group fails and stays out | 1 |
| `CI_FAILURE` | anything else — an uncatalogued assertion, a dead host with no recorded failure, no artifact to read | **reject**: comment the assertion and the run, label `queue-rejected` | — |
| `MANUAL`, `QUEUE_CLEARED`, `ROLL_BACK`, `BRANCH_PROTECTIONS`, `GIT_TREE_INVALID`, `INVALID_MERGE_COMMIT`, `MERGE_CONFLICT`, `UNKNOWN_REMOVAL_REASON` | — | comment once, no action | — |
| `MERGE`, `ALREADY_MERGED` | — | nothing | — |

How it reads the failure: the newest `merge_group` run of *MeshWeaver Build and Test* whose head
branch is `gh-readonly-queue/main/pr-<N>-…`; its failed jobs; for each failed shard the
`testResults-shard<N>` artifact — the same evidence `Consolidate test results` downloads — parsed
for `<UnitTestResult outcome="Failed">` with message and stack, and for non-`TESTFAIL` exit markers
in `test-results.log`. The group's PR list is the first-parent chain of the queue commit back to a
commit on `main`; "own run green" is `Consolidate test results` on the PR's head commit.

Every attempt is recorded in a hidden marker on the PR — `<!-- steward: requeued=N head=<sha> kind=<kind> -->`
— and caps are counted from those markers **per head sha**: a new push is a new question. A
rejection spends nothing.

🚨 **The steward never re-runs a workflow.** A re-run of the same tree hides the bug the failing
run found (and the run's failing transcript is the [control arm](../ReadingCiSignals) for the flake).
A re-*queue* builds a new tree against the `main` that has moved — a different measurement.

### The flake catalogue

`.github/known-flakes.json` is the only thing that turns a red assertion into a re-queue, so each
entry is a **temporary, evidence-bearing** allowance:

```json
{
  "id": "graph-late-nack-timeout",
  "assertionPattern": "TimeoutException : The operation has timed out\\.[\\s\\S]*LateNackReenqueueTest\\.cs:line 131",
  "testName": "MeshWeaver.Graph.Test.LateNackReenqueueTest.LateOwnerDisposingNack_AfterOptimisticEmit_ReenqueuesAndLands",
  "issue": "https://github.com/Systemorph/MeshWeaver/issues/NNNN",
  "evidence": ["https://github.com/Systemorph/MeshWeaver/actions/runs/33630685580"],
  "addedOn": "2026-09-02",
  "expires": "2026-10-02",
  "addedBy": "rbuergi"
}
```

- `assertionPattern` is a regex over the failure **message and stack trace — never the test name**.
  A catalogued flake must be evidence-bearing: the same test failing on a *different* assertion is a
  different defect and must reject. The loader refuses a pattern that matches the empty string.
- `issue` tracks the root cause; `evidence` lists the failing run(s), ideally beside a passing run
  of the same tree (the control arm).
- `expires` is at most 30 days after `addedOn`. An expired entry is **inert** — the steward treats
  it as uncatalogued — *and* it reds `MergeQueueStewardGuard` until it is deleted or renewed with
  fresh evidence. A catalogue is a ledger of tolerated defects; it may not rot silently.

The catalogue was **seeded empty** on 2026-09-02, and deliberately. The 700 Build-and-Test runs
since the queue was first enabled hold 83 reds; the repeated signatures were
`MeshWeaver.Hosting.Monolith.Test.HOST_CRASHED` (×5, `exit=124` at the 8-minute cap) and
`CompileFinishAndDisposeTest` (×4) — both in a project that has since moved to MeshWeaver.Plugins —
and `TestTimeoutLiteralRatchetGuard` (×6 on 09-01), which was the queue *working*: two PRs each moved
a ratchet count, green alone and red together. Every remaining candidate was a single occurrence, or
its subject changed after the failing runs (`ScopeTeardownRenderTest`, #2877 on 08-31). A catalogue
seeded on weaker evidence than that would be an automatic re-runner with a JSON file in front of it.

### Guards

- `MergeQueueStewardGuard` (`test/MeshWeaver.Documentation.Test`): every catalogue entry carries an
  issue URL, run-URL evidence, a compiling assertion regex, and an unexpired date within 30 days of
  `addedOn` — with a negative control proving the validator refuses each defect; runs the Python
  self-test; and pins the workflow's shape (`types: [dequeued]`, every job capped at 10 minutes, no
  workflow re-run anywhere).
- `dotnet-test.yml`'s *CI's own shell* job runs the steward's `--self-test` on every pull request,
  so a classifier regression cannot merge on a branch that never dequeued anything.

## Working with the queue

- **`gh pr merge <n> --auto` enqueues.** With a queue enabled, "auto-merge" means *enqueue when the
  PR's own required checks are green*. `auto-arm.yml` does this for every non-draft PR, so a green PR
  lands without anyone pressing anything. Marking a PR **draft** is the opt-out.
- **A push to a queued branch ejects it.** GitHub removes the entry (reason `MANUAL`-shaped from the
  steward's point of view: it comments once and takes no action); auto-arm re-arms on the
  `synchronize` event, so the new head re-enters the queue once its own run is green. Do not push to
  a queued branch expecting the queue to pick up the new commit in place.
- **Dequeue via GraphQL, never by re-ordering.**
  `gh api graphql -f query='mutation($id:ID!){dequeuePullRequest(input:{id:$id}){clientMutationId}}' -f id=<PR node id>`.
  There is no "jump the queue" here: `enqueuePullRequest`'s `jump` is not used by the steward and
  should not be used by hand — the queue's order is its correctness argument.
- **Never re-queue by hand after an ejection.** The steward does it, on evidence, and records the
  attempt. If it rejected — the PR carries `queue-rejected` and a comment naming the assertion and
  the run — the PR needs a fix or, with evidence and an issue, a catalogue entry. Remove the label
  when you re-arm.
- **Reading the queue:** `gh pr view <n> --json mergeQueueEntry`, or the `status` command above.

## Related

[Reading CI Signals](../ReadingCiSignals) · [The Continuous Delivery Contract](../ContinuousDeliveryContract)
· [The Cross-Repo Pair Gate](../CrossRepoPairGate) · [Writing Tests](../WritingTests)
