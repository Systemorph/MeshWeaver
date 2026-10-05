---
Name: Runner Pools and Dispatch Queues
Category: Architecture
Description: >-
  The fleet's CI runs on exactly two runner pools, plain and Docker, that share all capacity.
  Priority never comes from a pool: every run takes a place in the control instance's CI queue in a
  tier (express > trunk > gate > pr), and the queue dispatches runs in that order with a measured
  share kept free for express. It covers why reserved lanes were retired (with the measurement),
  where the queue lives and why, how it fails open so CI never depends on the control instance, how
  it is re-ordered through MCP, and what the runner gates refuse.
---

# Runner Pools and Dispatch Queues

**Policy [`ci-two-pools-priority-queues`](../PolicyNotProse).** This page is the platform's
statement of the rule. The mechanism lives in MeshWeaver.Plugins (`Hosting/CiDispatchQueue.md`,
`get Hosting/CiDispatchQueue` on the control instance), and the runner configuration lives in
Systemorph/Memex (`deployments/aks/ci-runners/README.md`).

## The rule

1. **Two pools, split by technical need only.** Plain (`vars.MW_RUNNER` → `aks-silos`) and Docker
   (`vars.MW_RUNNER_DOCKER` → `aks-silos-dind`). They share ALL capacity. No pool, label or
   PriorityClass is reserved for a kind of work.
2. **Priority comes from the queue.** Every CI run takes a place in the control instance's `ci`
   queue (`Hosting/Queues/operator/ci`) in one tier:

   | Tier | For |
   |---|---|
   | **express** | blocking work: a run on main while main is red; a settle / stamp / publish change on main (or its bot PR); an explicit unblock or hotfix |
   | **trunk** | every other main / schedule / dispatch run |
   | **gate** | core's dependent suites (MeshWeaver.Plugins `core-candidate.yml`) |
   | **pr** | pull requests |

   The queue **dispatches**: a run does not start its heavy legs until the queue starts it, in tier
   order. A measured share of the pool (20 % by default, relative, never a count) is kept free so
   express never waits behind a lower tier.
3. **The order is mesh state.** It lives on the queue node and its job nodes. A platform admin lists
   and moves it through MCP (`queue_list`, `queue_reorder`), and the next dispatch follows.
4. **CI never depends on the control instance to run.** An enqueue that fails, a trunk run not
   admitted within 15 minutes, and a held pull request the dispatcher never re-runs all **start
   anyway, loudly**. The last of these is handled by a GitHub-side fallback lane that watches the
   dispatcher's activity, never the control instance.

## Why reserved lanes were retired

Until this policy the fleet had five scale sets. Three of them, trunk (8), Docker trunk (12) and gate
(12), bought precedence by **reserving** runners under their own label and PriorityClass. GitHub
hands a label's queued jobs out first come, first served, and has no job priority. A reservation is
always either too big, idling while other work queues, or too small, so its own work queues. Measured
over the 40 most recent MeshWeaver.Plugins `Plugin Catalog CI` runs (2026-10-04 20:21Z → 10-05
06:57Z), jobs on the reserved trunk sets waited p90 **6.6 / 3.8 min**. Pull-request jobs on the shared
sets waited p90 **0.6 / 1.0 min**. Ordering one shared pool fails in neither direction.

## Where the queue lives

GitHub cannot express the order. A concurrency group holds one pending run and replaces it with the
newest, and nothing in GitHub can be re-ordered by an admin. The queue is therefore an ordinary
`Hosting/Queue` execution queue on the control instance ([The activity execution model], MeshWeaver.Plugins
`Hosting/ActivityExecutionModel.md`). Its own hub is the only writer of the order, and the tiers are
priority bands of that ONE queue. One hub ordering one capacity has no cross-queue arbitration that
could deadlock. Its dependency on the control instance is cut by rule 4.

## How a run waits, and how it is dispatched

- **A pull-request run is held.** Its admission step enqueues the run and ends the first attempt at
  once, red, with an error line reading `HELD FOR DISPATCH (queue ci): run <id> of PR #<n> is queued on
  the control instance's ci queue; …` — the readers (the PR babysitter, the dispatch fallback) match
  its prefix `HELD FOR DISPATCH (` (`HOLD_MARKER` in MeshWeaver.Plugins `scripts/ci-queue-admission.py`).
  No runner is held, and the pull request cannot look green.
  The queue dispatches it by re-running that attempt, and attempt 2 proceeds.
- **A trunk or gate run waits in place** on one light runner for an admission check run that names
  it. A trunk run that was held and re-run would end every main run red on each push.
- **Dispatch is idempotent.** The dispatcher reads the run first, and a later attempt that already
  exists is adopted rather than dispatched again. GitHub refuses to re-run a run that is in progress.

## Agent work: the same queue model, no limit

The same queue model, tiers and MCP re-ordering apply to every control-plane **agent thread**:

- review rounds;
- fixer and validator hand-offs;
- bug-fix threads and their re-drive rounds;
- triage rounds.

Each one is a job on `Hosting/Queues/operator/agents`, which the queue's hub dispatches in tier order.
Two rules differ from the CI queue:

- **No limit.** An agent queue carries no concurrency bound and no pre-set share: everything waiting
  is dispatched at once. The only thing that holds work back is a **live refusal** from a model
  provider — a 429, a 402 or a quota answer read off a finished round. That refusal backs off the one
  (model, upstream) pair it came from. The backoff doubles per refusal from 30 s up to 15 min, and a
  clean finish resets it. A slow, stalled or capped round holds nothing back. The same rule holds one
  layer down, where each dispatched ROUND is admitted (the AI engine's shared admission, MeshWeaver.Plugins
  `AI/AgentAdmission`): the relative per-lane share it applied once a model pool was measured exhausted
  (floors, borrowing, a cost guard, a budget reserve) is removed, and so are the review ledger's share
  and the fleet coordinator's — a 429 backs off that one pool, a daily limit, credit or rejected key
  closes the provider with a probe, and nothing else is a throttle. The executors (the
  control instance's pods) scale with KEDA on the queues' waiting jobs: the chart's
  `keda.queueDepth`, a `metrics-api` trigger on the portal's `GET /api/queues/depth`, set from the
  record's `autoscaling.scaleOnQueueDepth`.
- **Reviews stay on GLM-5.3 at effort max, spread over its EU upstreams.** Every review uses the same
  model whatever the diff: a measured evaluation scored a smaller model far below it. GLM-5.3 is pinned
  once per EU upstream (Inceptron, Mistral), so a 429 on one upstream shifts the load to the other
  instead of throttling every review. The reviewer sends a 128k output cap, because a review at effort
  max needs about 46k output tokens. Every candidate stays EU-only and ZDR (policy `code-review-eu-only`).

Fail-safe as for CI: if the queue never stamps a job, the thread is started directly, with a
warning. The mechanism, the bounds this removed and the tests are in MeshWeaver.Plugins
`Hosting/CiDispatchQueue.md` §7a–7c.

## What the gates refuse

- Core: `.github/scripts/check-reusable-workflow-runners.py`. A reusable lane's `runs-on` may read
  only `vars.MW_RUNNER` / `vars.MW_RUNNER_DOCKER` (or an input defaulting to them). The retired
  variables (`MW_RUNNER_TRUNK`, `MW_RUNNER_TRUNK_DOCKER`, `MW_RUNNER_CORE_GATE`) are refused by name,
  with self-test cases.
- MeshWeaver.Plugins: `scripts/check-runner-labels.py` refuses the retired labels and variables
  anywhere in workflow code. `scripts/check-build-queue-admission.py` pins the admission job: it
  never skips, every heavy leg needs it, and it asks the queue through the self-tested
  `scripts/ci-queue-admission.py`.
- Memex: `scripts/check-workflow-runners.py` rule 4 refuses a retired lane in any `runs-on:` /
  `runner:`.

## Migration

The order leaves no unguarded moment:

1. Memex raises the two pools to absorb the retiring sets. The namespace quotas are unchanged, and
   the old sets stay installed until nothing names them.
2. MeshWeaver.Plugins stops naming the lanes and ships the queue. The queue stays bypassed
   (`MW_BUILD_QUEUE=off`, loud) until the control instance runs it.
3. Setting `MW_BUILD_QUEUE=dispatch` turns it on. This policy is `in force` from that moment, and
   rollback is `off`.
4. Delete the trunk variables. A follow-up Memex change then removes the retired sets, applied
   through the governed Hosting path.

The [Staged Pull Request Pipeline](../StagedPullRequestPipeline) decides WHEN a pull request may
start its heavy legs. This policy decides in what ORDER the started work gets runners.

[The activity execution model]: https://github.com/Systemorph/MeshWeaver.Plugins/blob/main/Hosting/ActivityExecutionModel.md
