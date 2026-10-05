---
Name: Staged Pull Request Pipeline
Category: Architecture
Description: >-
  Every pull request head moves through four stages in order — cheap static controls, the automatic
  review landed and answered, the expensive suites on a FRESH merge with the current main, and arming
  only once the review is answered AND every required check is green (policy review-then-suites). What
  each stage runs, what moves a head on (events, not polling), the three loud releases that keep a
  reviewer outage from freezing the fleet, why a hold is red and never skipped, a day of running the
  suites in parallel and why it was reverted, and the measured saving.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="4" height="16" rx="1"/><rect x="10" y="4" width="4" height="16" rx="1"/><rect x="17" y="4" width="4" height="16" rx="1"/></svg>
---

# Staged Pull Request Pipeline

> **🔀 Review first, then the suites, then the arm** — policy
> [`review-then-suites`](../PolicyNotProse), the fleet-wide order. The stage gate holds a head until
> its review landed and every finding is answered; the suites then test the head merged onto the
> CURRENT main ([Fresh Merge Under Test](../FreshMergeUnderTest)); and auto-merge is armed only once
> the review is answered AND every required check is green. A short-lived parallel default
> (`suites-parallel-with-review`) was reverted the same day — see
> [A day in parallel](#a-day-in-parallel-and-why-it-was-reverted).

**Decision (maintainer, 2026-10-04, verbatim):** *"should we maybe say that code review must pass and
also other controls such as no client etc. must pass before we start test. and we arm only at end of
test"*.

So every pull request HEAD now moves through four stages, in order, and a stage starts only when the
one before it is green **for that head**:

| Stage | What runs | Cost | What moves the head on |
|---|---|---|---|
| **0 — static controls** | shape/validate, confidential terms ("no client names"), AGENTS.md shared-rule blocks, generated files and locks, repo policy gates, CI inputs (core: shared rules, closing keywords, package pins, interface additions, i18n mirror, CI shell, cross-repo pair) | minutes, one light runner each | they finish; a red one **fails fast** — nothing heavy starts |
| **1 — automatic review** | the head's `internal-review` (App `systemorph-com`, the steward on the control instance) has COMPLETED and every thread a reviewer opened has a reply from a person | the reviewer's time, no runner | `check_run: completed` of the review, a person's reply — see [the event half](#what-moves-a-head-on--events-not-polling) |
| **2 — expensive suites** | core: build + test shards, doc gate, platform-compat, the dependent-suites request · Plugins: module bundles, compile-check, gate shards, portal hosts (every leg behind `admission`) | the run's runner-minutes, almost all of them | the suites finish |
| **3 — arming** | the control plane's babysitter arms auto-merge (Plugins `PrArming`, #2828) | none | the arm gate (`check-review-answered.py` → `arm_readiness`): the review answered (conditions 1–3) AND every required status check of the base `success` on the head (condition 4, `required_checks_green`) |

The order is the point. Stage 1 is the stage most likely to send the author back to the keyboard, so
it runs before the stage that costs the most. A finding answered with a fix push makes a NEW head,
and a new head restarts at stage 0 — the suites never ran on the head the review changed.

## A day in parallel, and why it was reverted

On 2026-10-05 the suites briefly ran IN PARALLEL with the review (`suites-parallel-with-review`: the
stage gate held on stage 0 only), because with ~50 reviews in flight the runners sat idle (09:00Z:
core 31 jobs running / 0 waiting, Plugins 30 / 0). The maintainer corrected it the same day: the
fleet-wide order is **review first → suites against a fresh merge with the current main → arm only
when the review is answered and the suites are green** (policy `review-then-suites`, which replaces
`suites-parallel-with-review` in the register). What survived the reversal:

- the **fresh merge** on every suite job ([Fresh Merge Under Test](../FreshMergeUnderTest)) — a held
  run released hours later tests today's main, not the merge GitHub built at the push;
- the gate job's name, `Stage gate: may the suites start` (formerly `Stage 1: review landed and
  answered`; the event half recognises both);
- the opt-out, `review-before-suites: false`, for a caller that wants a test reading early — it never
  changes the merge or the arm, which still need the answered review.

**Arming after green.** Condition 4 of the arm gate (`required_checks_green`) refuses while any
required status check of the base branch is missing, pending or red on the current head — read from
BOTH rulesets and classic protection, the review's own contexts excluded (they are conditions 2–3),
and an empty required set refused rather than read as "all green". MeshWeaver.Plugins' `PrArming`
ports the same condition.

## Measured — why the review-first ordering saves runners (and when it does not)

Sample (REST only, 2026-10-04; bot and lock-settle pull requests excluded): the newest **40 merged
pull requests per repository** — core 2026-10-02 09:44Z → 10-04 07:05Z (130 CI heads), Plugins
10-03 17:06Z → 10-04 03:30Z (163 CI heads); SocialMedia and Reinsurance 15 each (09-19 → 10-03).
*Review latency* = the head's first CI run created → its first `internal-review` completed (success or
failure). *Runner-minutes* = the sum of every job's duration, all attempts.

| | Core (`dotnet-test.yml`) | Plugins (`ci.yml`) |
|---|---|---|
| Review latency, median / p75 / p90 | **16.6 / 52.3 / 118.9 min** (n=49) | **31.9 / 91.7 / 116.1 min** (n=89) |
| PR CI wall time, median / p75 (all completed runs) | 18.5 / 20.9 min (n=134) | 59 / 92 min (n=176; successful runs 93 / 108) |
| Runner-minutes per run, median / p75 | 60 / 62 | 90 / 172 (successful runs 122 / 178) |
| Test run finished after the review, median / p75 | +1.7 / +7.9 min — they finish together | +14 / +47 min — the review usually lands first |
| Reviewed heads with ≥ 1 finding | 46 / 49 (94 %) | 79 / 89 (89 %) |
| Reviewed heads whose review was blocking (`failure`) | 9 / 49 (18 %) | 14 / 89 (16 %) |
| **Reviewed heads followed by a new push after the review landed** | **33 / 49 (67 %)** — 31 after findings, all 9 blocking ones | **57 / 89 (64 %)** — 49 after findings, all 14 blocking ones |
| **Runner-minutes spent testing heads that a findings review then replaced** | **2,037 of 8,321 (24 %)** | **4,547 of 18,705 (24 %)** |
| …of which after a BLOCKING review | 560 (7 %) | 940 (5 %) |
| Heads superseded by any later push (any reason) | 90 / 130 (69 %), 66 % of runner-minutes | 123 / 163 (75 %), 68 % of runner-minutes |
| "Reviewer unavailable" degradations in the sample | 9 (2026-10-03 14:11Z → 20:16Z) | 18 (10-03 13:19Z → 20:16Z, ~7 h) + 1 "agent not found" |

Satellites are small and recently reviewed (the internal review reached them ~09-28): SocialMedia
7 reviewed heads, 0 re-pushes; Reinsurance 9, 4 re-pushes (34 runner-minutes). Their CI is 10–16
runner-minutes per run, so the saving there is small and adoption is not urgent.

**What it saves.** Up to **~24 % of pull-request runner-minutes in both repositories** — the tests
run on heads that a review then sent back for a fix push (2,037 min in core and 4,547 min in Plugins
over the sample; the sampled pull requests were opened from 09-27, so the sample does not convert
honestly into a per-day rate). That is the upper bound: it counts every push that FOLLOWED a findings review, and some of those
pushes were merges of main or work the author would have pushed anyway; the blocking-review subset
(7 % / 5 %) is the floor. Stage 0's fail-fast saving comes on top and was not measured separately.

**What it costs.** Time to a green wall grows by the review latency where the two used to overlap:
in core the tests and the review finished together, so a head now waits a median **~17 min** longer
(p90 ~2 h when the reviewer is queued); in Plugins the review usually landed first, so the added wait
is the median **~32 min** review latency minus the overlap that already existed. Time to MERGE moves
less than that, because merging already waited for the review AND every answer (`Automatic review
answered`, the arm gate). The answer time is now on the critical path too — by the maintainer's
decision: a review must PASS, answers included, before the tests start.

**What the numbers also say.**
- *"Has findings" barely separates heads* — 9 in 10 reviews leave inline threads even when titled "No
  blocking findings". So stage 1 waits for **every thread answered**, the same predicate as the merge
  and arm gates, not merely for a `success` conclusion.
- *72 of 130 core heads and 57 of 163 Plugins heads had CI but no review at all* — mostly superseded
  before a review started (they are cancelled under the supersession rules anyway), but on
  2026-10-03 22:00–23:59Z 15 of 17 core heads went unreviewed: a silent outage, which is what the
  bounded fallback is for.

## Stage 1's predicate — one implementation, three questions

Stage 1 asks **the same question the arm gate and the merge gate ask**, at an earlier moment.
`check-review-answered.py` holds all three, and `stage_readiness` reuses the arm gate's
`internal_review_runs`, `listing_incomplete` and `reviewer_threads` verbatim, so the three can never
disagree about what "reviewed" or "answered" means:

| | Merge gate (`Automatic review answered`) | **Stage gate** (`--stage-gate`) | Arm gate (`--arm-gate`, control) |
|---|---|---|---|
| Asks | may this merge? | may stage 2 start for THIS head? | may auto-merge be armed now? |
| Review must be on | any head of the PR | **the current head** — or carried over a clean base merge | the current head — or carried over a clean base merge |
| Reviewer unavailable (degradation) | releases | **releases, loudly** | refuses — a person merges |
| No review after the fallback | red | **releases, loudly** | refuses |
| `tests-before-review` label | — | **releases, loudly** | ignored |
| Unanswered thread | red | **holds — never falls back** | refuses |
| Draft | — | holds | refuses |

An unanswered thread never falls back, because that wait belongs to a person, not to the
infrastructure. Every RELEASE is loud: a `::warning::` titled *"Stage 2 released without a completed
review"*, a line in the job summary, and — because none of them arms — a head released that way still
needs its real review before stage 3.

## What moves a head on — events, not polling

A hold is the stage gate **failing**. Something must ask again when the answer changes, and the
doctrine already has the shape: `review-answered-on-degradation.yml` (core) listens for a check run
and **re-runs** the read-eligible `pull_request` run instead of judging from a default-branch run
(whose check runs would land on main's commit, where branch protection never reads them). The staged
pipeline generalises it:

- **`node-repo-stage-advance.yml`** (core, reusable) runs `check-review-answered.py --stage-advance`,
  which POSTs `rerun-failed-jobs` on the head's newest held run ONLY when the pull request is open,
  the run is for its **current** head, that run's stage gate concluded `failure`, and
  `stage_readiness` is green now (`advance_action`, self-tested both ways). A gate still evaluating,
  a gate already passed, a superseded (cancelled) gate, an older head — each is left alone and said.
- **Each adopting repository** carries a thin `stage-advance.yml` with the triggers, because
  `check_run` and `schedule` only fire for a workflow file on the repository's own default branch:

| Trigger | What it means |
|---|---|
| `check_run: completed` (job filter: name `internal-review`, App id 4918443) | the review landed — or degraded. The happy path. |
| `pull_request_review_comment: created`, `pull_request_review: submitted` | a person answered a finding |
| `schedule` every 15 minutes | the bounded **fallback** timer — a review OUTAGE raises no event at all |

`rerun-failed-jobs` re-runs the gate, the front door it held (`admission` in Plugins,
`collect-results` in core) and every job that skipped behind them, on the SAME head; the green stage-0
jobs are not repeated. Runs created by GitHub Actions raise no `check_run` workflow events, so there
is no loop.

**Why not the alternatives.** `workflow_run` cannot see a check run an App posts. A
`repository_dispatch` from the control plane would make stage 2 depend on the control plane being up
— exactly the component whose outage the fallback must survive — and would need a second credential
path. The re-run uses only the repository's own `GITHUB_TOKEN` (`actions: write`) and the remedy the
doctrine has already sanctioned.

## The three loud releases — an infrastructure blocker never freezes the fleet

| Blocker | What happens | Arming |
|---|---|---|
| **Reviewer unavailable** — the steward's neutral `internal-review` "Reviewer unavailable …" (posted only for an infrastructure cause, after a re-kick) | its `check_run: completed` re-runs the held run; stage 2 starts at once, warning | still refused; a person merges and a post-merge review is owed (unchanged contract) |
| **Review outage** — nothing posted at all (2026-10-04: the FlattenMarkdown fault; on the ten newest open Plugins pull requests that morning no head carried any `internal-review` run) | after `fallback-minutes` (default **120** — the measured p90 review latency, below) from the head's run creation with no completed review, the next sweep re-runs it; stage 2 starts, warning *"REVIEW UNAVAILABLE … that is the incident to chase"* | still refused until the real review lands and is answered |
| **`tests-before-review`** label (a draft that wants test feedback, an author who prefers it) | stage 2 starts immediately, warning | unaffected |

The fallback is measured from the run's `created_at`, which a re-run keeps, so re-evaluation never
restarts the clock. The sweep fires every 15 minutes, so the effective release lands between 120 and
~135 minutes (plus GitHub's own schedule jitter) — bounded, and every minute of it loud in the gate's
log.

**Why 120 and not less.** The fallback exists for an OUTAGE, not for a slow review: the measured p75
review latency is 52 min (core) and 92 min (Plugins), so a 60-minute fallback would have released a
quarter of all Plugins heads into stage 2 before their review — exactly the spend the staging removes.
120 sits at the measured p90 (119 / 116 min). It costs little during an outage, because arming waits
for the review anyway: the fallback buys an early test READING, never an earlier merge. A caller may
pass `fallback-minutes` to both lanes (they must agree), and `tests-before-review` is the per-PR override.

**Generated-only pull requests owe no review.** main's own jobs propose generated files as pull
requests — `settle-locks` (every `manifest.lock`) and `stamp-floors` (`mesh-floor.lock` plus each
root's `minMeshVersion`). There is nothing to review, and the settle job rewrites the head on every
main merge, so a per-head fallback clock restarted forever: during the 2026-10-04 review outage the
held settle PR (Plugins #2860) stopped module publishing. Such a pull request skips stage 1 on
PROVENANCE (`generated_only`): authored by the App that writes them (`meshweaver-cloud[bot]`, by id),
every commit that App's (the commit listing must cover the pull request's own commit count), every
changed file a lock or a root `index.json` whose changed lines are each NOTHING BUT a
`"minMeshVersion": "…"` key/value. A person's pull request that touches a lock is staged like any
other, a generated-only DRAFT is held as a draft, and for any pull request the App authored the
fallback clock keys on the PULL REQUEST's creation, not the head.

What this skip is NOT: a merge gate. The required `Automatic review answered` lane never consults
`generated_only`, so the skip only starts stage 2's suites earlier. Its provenance reads GitHub's
resolved commit author, which follows the author email — so it is as strong as the App's branch is
closed to other pushers (the App's commits are unsigned today, so a signature cannot be required
until the producing jobs commit through the API), and a lock's CONTENT is held exact by
`Validate node repos`, not by this check.

**Stage-0 blockers keep their own rules.** A control whose INPUT is absent (the confidential-terms
denylist secret on a fork or a Dependabot run) already *skips with a notice* by its own design and so
does not hold stage 2. A control that goes RED on infrastructure (an unreadable API, a lost runner) is
a red like any other: `retry-known-transients.yml` re-runs the recognised shapes, and the head waits
in stage 0 — because "the static controls could not run" must never read as "the static controls
passed".

## A push during stage 2

Unchanged supersession rules, applied per head:

- **Core**: `cancel-in-progress` for pull-request refs — the old run stops, the new head starts at stage 0.
- **Plugins**: `pr-supersede.yml` cancels the older run only when the AUTHORED diff changed. A
  content-neutral push (a merge of main, a regenerated lock) keeps the run and the new head **adopts**
  its verdict (`change-set` mode `adopted`); the stage step is skipped for an adopted change set,
  because the verdict it carries already passed stage 1 on its parent head.
- A review finding answered **by a fix push** is a new authored head: no stale verdict is carried. The
  gate reads `internal-review` runs on the current head sha — or carries one over a CLEAN MERGE of the
  base branch, and over nothing else (next section) — and the advance re-runs only a run whose
  `head_sha` is the PR's current head.

## A clean merge of the base branch carries the review

Policy [`review-carries-over-clean-base-merge`](../PolicyNotProse). Measured 2026-10-05 22:38Z: 25
open non-draft pull requests across core, Plugins and Memex, and 7 merges in 2.5 hours — six of the
25 DIRTY at the same moment. The loop that kept them there: main moves → a pull request goes DIRTY →
main is merged into the branch → the new head has no `internal-review` → this gate holds it at stage 1
→ a full review round queues behind admission → main moves again. A merge of the base branch that
applied cleanly changes nothing the reviewer read, so it must not cost a review round.

**The rule.** The review of an earlier head carries over to the current head when ALL of these hold,
each computed from the repository through REST — never from a commit message or a branch name
(`check-review-answered.py` `find_reviewed_ancestor` + `carry_over`, read by `read_carry`):

1. **Walking back from the head through MERGE commits only** reaches a commit whose newest
   `internal-review` run from the reviewer's App is a real review — not the *Reviewer unavailable*
   degradation, not the steward's *Review not completed* exit. Every merge walked has exactly ONE
   parent on the pull request's side (the other is in the base branch), so a merge of another
   feature branch does not carry.
2. **Every commit on the pull request now that was not on it at the reviewed head is a merge.** One
   non-merge commit is new content and owes a fresh review.
3. **The pull request's OWN diff is byte-identical**: compare(base...head) and compare(base...reviewed
   head) — each merge-base..head, as GitHub computes it — have the same patch-id. The patch-id hashes
   every file's status, names and patch with each hunk header reduced to `@@` (line numbers move when
   main changes elsewhere in the file; the change does not); context lines are kept, as the git
   patch-id keeps them. A file sent without a patch (binary, too large) contributes its blob id.

Anything unreadable does NOT carry: a short commit listing, the compare API's 300-file cap, a file
with neither patch nor blob id, a failed read. The head is then reviewed fresh, exactly as before.

**What carries.** The reviewed head's run stands in for the current head's in the STAGE gate (mode
`carried` — stage 2 starts at once) and the ARM gate; the pull request's threads are pull-request-wide
already, so a thread answered on the earlier head stays answered. The head's own real review, when it
has one, always wins; a carry replaces only an absent, running or degraded own run. The merge gate
(`Automatic review answered`) never asked per head and is unchanged.

**What is logged.** The stage gate's job summary and the arm gate's line name the reviewed head, its
check run and conclusion, the merges since it, and the patch-id with both merge-base ranges:

> ✅ Stage 1 green for #N by CARRY-OVER — no new review round: review CARRIED from head d4815d432e
> (`internal-review` check run 111882997610: success "No blocking findings") to 4aff97fd03: the 1
> commit(s) since it are all merges of the base branch (4aff97fd03), and the pull request's own diff is
> byte-identical — patch-id … over N file(s), &lt;merge-base&gt;..d4815d432e = &lt;merge-base&gt;..4aff97fd03

A refused carry is logged too (`no carry-over from reviewed head …: the pull request's own diff
changed — patch-id X (…) vs Y (…)`), so a head that waits says why it waits.

**No round is spent.** The PR steward (MeshWeaver.Plugins `Hosting/Deployment/Source/ReviewCarryOver`)
applies the same rule before it claims a review slot: a carried head gets an `internal-review` run
posted with the reviewed head's conclusion, titled `Review carried from <sha>`, and no reviewer
thread — the required `internal-review` context of a plugin repository is satisfied by the evidence,
not by a model.

**Measured on the history** (replaying every reviewed-head → merge-of-main pair on the last 60 core
pull requests, base = the merged main commit): 7 of 11 carry. The 4 that do not are each a change to
the pull request's diff — a conflict resolution, or main editing a line inside a hunk's context (the
policy register's last row, which every policy-adding pull request appends after: the added row is
unchanged, its neighbour is not; strict by design). The same replay showed why the walk refuses
*Review not completed*: three further clean merges sat on that exit, which is not a review.

**Negative control.** `--no-carry` (stage gate and arm gate) disables the carry; the self-test runs
the pure-merge fixture both ways — carried → stage 2, disabled → *waiting* — and a mutation of each
condition (patch content ignored, non-merge commits allowed, hunk headers kept, either gate ignoring
the carry) turns the self-test red.

## Required contexts stay satisfiable — a hold is RED, never skipped

A skipped required context counts as satisfied, so the hold is a **failure** at every level:

- **Core**: `stage-gate` fails; `build` requires `needs.stage-gate.result == 'success'` explicitly.
  The gate itself runs with `if: !cancelled()` and receives the stage-0 results as an input: several
  stage-0 controls SKIP by design (push, merge queue, forks), and a job whose need is skipped is itself
  skipped — on the implicit guard the gate, and through it the build, would have skipped on every main
  push (#6070 review). So `failure`/`cancelled` holds (mode `stage0-red`), `skipped` passes;
  the test shards, doc gate and platform-compat also use `!cancelled()` with an explicit successful
  `build` result. Without that status function, their implicit `success()` still sees the skipped
  PR-only stage-0 ancestors on a main push and silently skips them even after a green build (main
  run 37197032428). The result gate then correctly refuses to claim test evidence. A failed
  `precheck` does not suppress a successful build's suites (an indeterminate probe still needs
  evidence), but `Consolidate test results` explicitly rejects a non-successful precheck verdict;
  test evidence alone cannot turn that run green.
  `Consolidate test results` (required) fails at its first step, *"Stage 2 held — stage 1 (…)"* or
  *"— stage 0 red"*, before the generic no-evidence reds. The green-tree reuse path is not held — it
  spends nothing.
- **Plugins**: `admission`'s FIRST step holds a pull request until `stage-gate`, `validate`,
  `repo-gates` and `preflight` are all green; every heavy leg already requires
  `needs.admission.result == 'success'`, and `Every gate executed` counts `admission`, so the required
  aggregators read red with the hold as the root line. `scripts/check-build-queue-admission.py` pins
  the needs, the step and the lane, and its self-test proves each can be caught missing.
- **No required context was renamed.** The gate is a need of the front door, not a new required check.

**Every hold's error MESSAGE starts with `STAGE 2 HELD (stage 1, <mode>)` or `STAGE 2 HELD (stage 0
red)`** — the stage gate, Plugins' `admission` and core's `Consolidate test results` all print it — so
a hold is recognisable from a check's annotations alone. The control plane's PR babysitter keys on it
(`PrBabysitter.IsStageHold`): a stage-1 hold with no open thread is the class `waiting-for-review`
(never sent to the author, never interrupted, never handed to the fixer, never re-run from there —
the event half owns it), with open threads it is `review-unanswered`, and whatever is red
INDEPENDENTLY of the hold (the stage-0 control itself, a leg that does not wait for the front door)
is classified exactly as before. Without the marker, every hold would read as a real defect on the
front door — and the fixer would cancel the very run the review is about to release.

**Not staged at all** (the gate answers `not-staged` in green): a **fork's** pull request (the internal
reviewer does not review forks, so a hold would only wait out the fallback under a warning blaming an
outage — the merge gate still applies to it), and core's **green-tree reuse** path (it spends nothing).

`push`, `schedule`, `workflow_dispatch` and `merge_group` are never staged: the gate answers
`not-staged` in green at once. Trunk never waits for a review, and core's merge queue re-tests a PR
whose review was already required to merge.

## Stage 3 — arming waits for stage 2

Arming moves to the control plane (Plugins #2828, `PrArming`), and core #6063 makes `auto-arm.yml`
disarm-only. The arm predicate gains **"every required check is green on the head"** in front of
`arm_readiness`, so the order is enforced at both ends: nothing heavy starts before the review, and
nothing arms before the tests. A head released by any of the three loud releases is never armed by
that release.

## Adoption, per repository

| Repository | Stage gate | Event half | State |
|---|---|---|---|
| Systemorph/MeshWeaver | `dotnet-test.yml` → `stage-gate` (local lane, `scripts-ref: github.sha`) | `stage-advance.yml` | review first (default) |
| Systemorph/MeshWeaver.Plugins | `ci.yml` → `stage-gate` → `admission` | `stage-advance.yml` | review first (default) |
| the satellites | not yet — they run as before | — | adopt with the same two edits: a `stage-gate` job (`node-repo-stage-gate.yml@main`) that their heavy jobs need with `result == 'success'`, and the thin `stage-advance.yml`; plus their rows in `.github/lane-caller-grants.yml` |

A repository that has the gate but not the listener would hold forever, which is why the two are
adopted together and why the Plugins pin (`check-build-queue-admission.py`) names the lane.

## Trunk outranks pull requests — through the CI queue, not a lane

Staging stops a pull request from STARTING expensive work too early. It does not decide who gets a
runner when the pools are full. That is the CI queue's job, under policy
[`ci-two-pools-priority-queues`](../PolicyNotProse) ([Runner Pools and Dispatch Queues](../RunnerPoolsAndDispatchQueues)).
There are two shared pools, and runs are dispatched in tier order: express, trunk, gate, pr.

> 🗄️ **Superseded: the trunk lanes.** On 2026-10-04, with both ARC sets at their cap and a label's
> queue first-come-first-served, a main run's worst queued job waited like a pull request's (median
> 1.9 vs 1.4 min, p90 4.7 vs 4.4 min over 19 main and 133 PR runs). The first answer was two reserved
> lanes, `aks-silos-trunk` (8) and `aks-silos-dind-trunk` (12), with a higher PriorityClass. Measured
> overnight after they went live, trunk jobs on them waited p90 6.6 / 3.8 min against 0.6 / 1.0 min for
> pull requests on the shared sets: the reservation was too small for its own work. The lanes are
> retired, and every runner gate of the fleet refuses their names.

## What it does not do (residue, stated)

- **A carry is strict on context.** A merge in which main edited a line next to one of the pull
  request's hunks changes the patch-id, so that head is reviewed fresh although its own +/- lines are
  unchanged.

- **An answer on a branch that predates the listener.** `pull_request_review_comment` runs the
  workflow file from the pull request's own merge ref, so a branch cut before `stage-advance.yml`
  existed raises no run on a reply. The 15-minute sweep catches it.
- **The sweep looks back six hours** (every page of failed runs in that window) and at most 25 pull
  requests per pass, so a held run older than that needs a push or a manual re-run of its failed jobs.
- **A cancelled run is never revived**, and a run whose wait for completion outlasted a push, a close
  or a deleted answer is re-judged on a fresh read before the re-run is posted.
- **Drafts are not reviewed, so they wait** — mark the pull request ready, or apply
  `tests-before-review`.
- **Not every repository is staged yet** (see the adoption table); an unadopted repository behaves
  exactly as before.

## Reading a held pull request

1. The red front door (`Consolidate test results` / `Admitted by the build queue`) names the stage.
2. Open **`Stage gate: may the suites start`** (formerly `Stage 1: review landed and answered`) — it says *waiting* (with minutes of the fallback
   used), *unanswered* (with the first thread), *draft*, or *unreadable*.
3. Do nothing for *waiting*; reply to each thread for *unanswered*. The suites start by themselves.
4. If the review is down and you cannot wait for the fallback: `tests-before-review`. It buys the
   test reading early; it does not buy the merge.
