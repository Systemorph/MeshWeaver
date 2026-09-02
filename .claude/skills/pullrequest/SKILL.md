---
name: pullrequest
description: Open a PR and merge it — and the NON-NEGOTIABLE rule that the PR's CI must be GREEN before you merge, because the pull-based self-update (memex-local autoroll + the AKS portals) deploys main's image. A red or still-pending main blocks the self-update from rolling forward and can wedge the deployment. Use whenever you create/review/merge a PR, when a merge "went through" but CI later failed, or when main is red and the auto-update is stuck. Covers the exact gh/API commands, the automatic ruleset-driven Copilot review (never hand-request or withdraw it; a 402 quota error leaves the PR unreviewed), the merge-only-when-green gate, and the half-committed-WIP trap that turns main red on a clean CI checkout.
user-invocable: true
allowed-tools:
  - Bash
  - Read
  - Grep
---

# /pullrequest — open → wait for CI → merge, and NEVER merge a red or pending main

## 🚨 Finishing a change set means MERGED — merge it yourself on green

A PR left open with a link handed back is unfinished work: it rots against a moving `main` and the
human has to return to press a button whose only precondition is the one the flow already proves.
Asking is not extra safety. The safety IS the gate — green CI plus the automatic Copilot review,
both of which you wait for anyway (and, for anything a portal runs, the image check in
[/release](../release/SKILL.md)).

Stop only when CI is red for a reason you cannot fix, when the review asks for a decision that
changes what the change set IS, or when the work turns out to need a scope call the user has not
made — one line, then stop. A change set spanning repos (MeshWeaver.Education,
MeshWeaver.Plugins) is finished when every part is merged in dependency order: platform first, then
what depends on it.

### PR capability is CREDENTIAL × REPO — measure it, never remember it

The guidance here once read *"`gh` CLI has read + push only — cannot merge, resolve threads, or
request reviewers"*, which was wrong in a way that cost real throughput: believing it makes a
session stop at the finish line and hand the merge back, exactly the pause the rule above says not
to take once CI is green.

Both factors are real and neither alone predicts the answer. **The repo half** is branch protection,
`strict`, required checks, who may bypass. **The credential half** is the one nobody expects,
because it makes the SAME repo answer differently to two sessions on the same day (2026-08-26): one
measured `admin:false, maintain:false` on `MeshWeaver` with scopes `gist, read:org, repo, workflow`,
while another measured `admin:true, maintain:true` with
`admin:org, delete:packages, gist, repo, workflow, write:packages` — and merged #2328 and #2429
there. So a static table of either shape is false for somebody, and a refusal you hit is not
evidence about anyone else's session. Check both:

```bash
gh api repos/Systemorph/<repo> --jq '.permissions'   # push is normally enough to merge
gh auth status                                        # scopes, if the above surprises you
```

Merging and resolving threads both work wherever the credential allows them (verified on
`MeshWeaver`, `Memex` and `MeshWeaver.Education` on 2026-08-26). **Never reach for `--admin`** — a
refusal is information about the gate, not an obstacle to route around. If a call comes back
`FORBIDDEN`, re-authenticate with `! gh auth login`.

```bash
# Find unresolved review threads
gh api graphql -f query='query($owner:String!, $repo:String!, $pr:Int!) { repository(owner:$owner, name:$repo) { pullRequest(number:$pr) { reviewThreads(first:100) { nodes { id isResolved } } } } }' \
  -f owner=Systemorph -f repo=MeshWeaver -F pr=PR_NUMBER \
  --jq '.data.repository.pullRequest.reviewThreads.nodes[] | select(.isResolved==false) | .id'
# Resolve a thread
gh api graphql -f query='mutation($id:ID!){ resolveReviewThread(input:{threadId:$id}){ clientMutationId }}' -f id=THREAD_ID
gh pr merge PR_NUMBER --merge
```

## 🚨🚨🚨 The one rule: main must be GREEN before you merge

**The pull-based self-update deploys `main`.** The `memex-local autoroll` watches the moving
`*-local:latest` image, and the AKS portals (memex / memex-cloud) self-roll to the latest
green CI image. So `main`'s CI is not a formality — it is the source of the image that ships.

- **Red main** (build/test failure) → no valid image is produced → the self-update **cannot roll
  forward** (it's stuck on the last good version) and the auto-update *is failing*. If a broken
  image does get published, the portals CrashLoop / 502 → the wedge.
- **Therefore: NEVER merge with CI pending or red.** Merging before CI finishes is the exact
  mistake that turned main red after #136 (merged with "Build solution" still pending; it then
  failed on a clean-checkout `-warnaserror` build, and the self-update was blocked until the
  hotfix landed). The merge succeeding is not the goal — a green `main` after the merge is.

> If you only remember one thing: **poll the CI via GraphQL until the check SUITE is `COMPLETED`, and
> merge only on `conclusion == SUCCESS`** (step 3). Do NOT use `gh run watch` — it polls REST and
> drains the shared token budget into 403s that masquerade as CI-red.

**"Consolidate test results" is the required check** (ruleset `main pr protection`) — GitHub now blocks
the merge until it reports green, so the gate above is mechanical as well as a rule. Require nothing
else from that workflow: `Build solution (once)` and the shards are legitimately **skipped** when the
run reuses an already-green tree, and a skipped *required* check blocks the merge forever.

**A main run with skipped build/test jobs is not a run that didn't happen.** A PR is tested as
`refs/pull/N/merge` — your branch already merged with main — so when main hasn't moved, the commit
that lands has the identical TREE and `dotnet-test.yml` reuses that green instead of re-testing the
same bytes (marker refs `refs/ci-green/<tree>/<epoch>`, 24 h TTL; see the workflow header). The run
still concludes `success`, main-cd still ships the image — ~22 min earlier. If main moved, the tree
differs and the full suite runs; there is no way to skip an untested tree.

> 🚨 **Read from the other end, that same mechanism is a trap: `conclusion: SUCCESS` on the check
> suite can coexist with `Run tests = skipped`.** The tree was tested *earlier*, on the PR that
> produced it — which is sound for "did this tree pass", but says **nothing** about a test the run
> never executed. So a green suite is NOT evidence that a NEW test passed, and "this test is green on
> main" is NOT evidence it is green at all if main's latest run reused a tree.
>
> This is not hypothetical: a PR merged on such a green introduced a racy test that had genuinely run
> only once, and it then reddened an unrelated PR. Separately, "main is green so this failure is
> mine" was nearly concluded from a main run that had skipped every shard and therefore never ran the
> project in question.
>
> **Before treating any run as evidence a test passed, check the shard jobs' own conclusions:**
>
> ```bash
> gh run view <run-id> --repo Systemorph/MeshWeaver --json jobs \
>   --jq '[.jobs[] | select(.name | startswith("Run tests")) | .conclusion]'
> ```
>
> All `skipped` ⇒ that run executed no tests. Go back to the run that actually did — or, for a PR
> adding tests, require a run whose shards executed before believing the new test is green.

## The procedure

```bash
# 0. PRE-FLIGHT — a clean-checkout CI catches what a local build hides. Check for the trap below.
git status --porcelain | grep '^??'        # untracked files a committed file might reference
dotnet build src/<TheProjectYouTouched> -c Release -warnaserror --no-restore   # match the CI flags

# 0.5 RELEASE NOTE — every USER-FACING PR ships a "What's New" entry as a doc node (one node per
#     entry → no cross-PR merge conflicts). It's shipped in the docs partition and surfaced by the
#     What's New settings tab (Doc/WhatsNew, grouped by ship day, newest first). SKIP only for
#     pure-internal changes (refactor/test/CI/deps with NO user-visible effect) — say so in the PR
#     body when you skip.
#
#     🚨 A BUG FIX A USER CAN NOTICE IS USER-FACING. Fixes are the entries that go missing: over
#     2026-07-29…08-09, 86 of 127 `fix/` PRs shipped none, against 22 of 69 `feat/` PRs. That is
#     what makes the feed read as a feature-only changelog when most of the work is repair. A fix
#     costs one line — Category: Fix bundles it into the day's "N fixes" summary rather than giving
#     it a paragraph — so the bar is "would a user notice?", never "is it big enough?".
#
#     Category is the LABEL the tab groups by, not a constant:
#       feat/ perf/ chore/ docs/ → Category: Feature   (rendered in full, with its Description)
#       fix/                     → Category: Fix       (bundled into the day's one-line fix summary)
#     Order is -YYYYMMDD (a NEGATIVE date), which sorts the doc tree and the Doc/WhatsNew folder
#     page newest-first — without it they fall back to alphabetical by title.
#
#     🚨 IN A SATELLITE REPO (Plugins, Education, Reinsurance, SocialMedia, Memex) the path below
#     does not exist — it is core-only. Write the entry into YOUR repo instead, at
#     `WhatsNew/${DATE}-<slug>.md`, and add ONE line to the front matter:
#
#         nodeType: WhatsNew
#
#     That is the whole difference. The feed lists entries by node TYPE as well as by the core
#     path (#2539), so an entry declaring it reaches the one Doc/WhatsNew feed from any repo, with
#     no cross-repo PR. Everything else — Name / Category / Description / Icon / Order, and the
#     rule that a user-noticeable FIX gets an entry — is identical.
#
#     Before #2539 there was no route at all, so satellite fixes were simply skipped, and the feed
#     drifted toward being a platform-only changelog while reading as a complete one.
DATE=$(date -u +%Y-%m-%d)                   # no clock in scripts elsewhere, but this is a shell step
NOTE_FILE=src/MeshWeaver.Documentation/Data/WhatsNew/${DATE}-<slug>.md   # core; satellites: WhatsNew/${DATE}-<slug>.md
# Frontmatter is printf'd (Order needs the date substituted); the PROSE goes in a QUOTED heredoc so
# a note containing `$`, `${…}` or backticks is written literally instead of being expanded by bash.
printf -- '---\nName: %s\nCategory: %s\nDescription: %s\nIcon: Sparkle\nOrder: -%s\n---\n\n' \
  '<short human title of the change>' '<Feature|Fix>' \
  '<one-line summary shown in the What'"'"'s New list>' "${DATE//-/}" > "$NOTE_FILE"
cat >> "$NOTE_FILE" <<'NOTE'
# <title>

<2–5 plain-language sentences on what changed and why it matters to a user — not the how.>
NOTE
git add "$NOTE_FILE"

# 1. CREATE the PR (branch must be pushed first; push only when the user asked — AGENTS.md).
git push -u origin "$(git branch --show-current)"
gh pr create --base main --head "$(git branch --show-current)" --title "…" --body "…"

# 2. The Copilot review is AUTOMATIC — do not request it, and never withdraw it.
#    The `main pr protection` branch RULESET carries a `copilot_code_review` rule, so every PR
#    against the default branch is reviewed without you lifting a finger (plugins has the same via
#    its "Copilot review for default branch" ruleset). Reviews ARE wanted on every PR — maintainer,
#    2026-07-26. So:
#      • Never POST to `/requested_reviewers` — a hand-requested review DUPLICATES the ruleset's and
#        burns extra Copilot credits.
#      • Never DELETE the request "to save credits" — that cancels a review the maintainer wants.
#      • Just wait for it: it lands as the "Running Copilot Code Review" run / a Copilot review on
#        the PR. Address it in step 4.
#    If that run FAILED, find out why before merging unreviewed:
#      gh run view <run-id> --log-failed | grep -iE "quota|errorType|statusCode"
#    `statusCode: 402, errorType: 'quota'` ("You have exceeded your monthly quota") = the org's
#    monthly Copilot allowance is spent — seen 2026-07-26, when it silently left PRs unreviewed.
#    That is a billing matter to RAISE WITH THE MAINTAINER, not something to work around; say
#    explicitly that the PR merged unreviewed if it does.

# 3. WAIT for CI via GraphQL — this is the gate. NOT `gh run watch` (it polls REST every ~3s and
#    drains the shared 5000/hr user-token budget → 403s that look like CI-red). GraphQL has its OWN
#    budget (~1 point/query). Poll the "MeshWeaver Build and Test" check SUITE until COMPLETED — the
#    suite finishes only when every shard job does, so there's no late-shard race — then read its
#    conclusion. Merge ONLY on SUCCESS.
#
#    🔔 PREFERRED SHAPE: a PERSISTENT harness `Monitor` armed at PR-open, subscribed to EVERY
#    transition you'd act on — suite COMPLETED/SUCCESS, suite COMPLETED/<anything else> (red /
#    cancelled / timed-out), a NEW unresolved review thread (the automatic Copilot review gates the
#    merge and lands minutes after open), mergeStateStatus=DIRTY (a dirty PR runs ZERO CI), and
#    MERGED/CLOSED (the monitor's exit). 🚨 Never a success-only watch: silence looks identical to
#    "still running" while the thing you needed to react to already happened — and one-shot
#    background loops die at their timeout cap and leave dead air until someone re-arms them
#    (maintainer, 2026-08-17). See AGENTS.md → "SUBSCRIBE to a PR". One monitor can cover several
#    open PRs and re-armed pushes (it keys on the LATEST commit each poll).
#
#    The single-shot fallback below (harness Bash, run_in_background: true) is acceptable when no
#    Monitor is available: it exits 0 iff green, so the notification's exit code is the merge
#    signal — but it fires ONCE, covers no review threads, and dies at the bash timeout cap.
PR=<PR>
Q='query($o:String!,$r:String!,$p:Int!){repository(owner:$o,name:$r){pullRequest(number:$p){commits(last:1){nodes{commit{checkSuites(first:20){nodes{status conclusion workflowRun{workflow{name}}}}}}}}}}'
suite(){ gh api graphql -f query="$Q" -f o=Systemorph -f r=MeshWeaver -F p=$PR \
  --jq "[.data.repository.pullRequest.commits.nodes[0].commit.checkSuites.nodes[]|select(.workflowRun.workflow.name==\"MeshWeaver Build and Test\")]|last|.$1 // empty"; }
# `last` collapses to the most-recent suite — a re-run adds another suite for the same commit; without
# this, $(suite …) is multi-line and the compare below never matches COMPLETED even when CI is green.
until [ "$(suite status)" = "COMPLETED" ]; do sleep 45; done   # cheap: ~1 GraphQL point per poll
c=$(suite conclusion); echo "PR $PR CI: $c"; [ "$c" = "SUCCESS" ]   # exit 0 iff green → the merge signal

# 4. ADDRESS findings BEFORE merge:
#    - CI red  → pull the failing job log (REST, but ONE call — not a poll — so it's fine), fix, push, GOTO 3.
#        gh run view <run-id> --log-failed | grep -iE 'error|##\[error\]'
#    - Copilot review (arrives automatically — see step 2) → read its comments, address the
#      actionable ones, resolve threads, push, GOTO 3. Same for any human review.
#        gh pr view <PR> --json reviews,comments

# 5. MERGE — only now, only if step 3 was green.
gh pr merge <PR> --merge

# 6. UPDATE local main to the merge you just landed — in place, WITHOUT switching branches or
#    touching your working tree. `git checkout main && git pull` is WRONG here: work in this repo
#    happens on long-lived feature branches with a dirty tree (uncommitted WIP), so a checkout
#    fails or thrashes it. This fast-forwards the local `main` REF while you stay on your branch:
git fetch origin main:main        # local main -> origin/main (ff-only); current branch untouched
```

## 🚨 "Is the build finished?" — filter by WORKFLOW, never wait for all check suites

**Never wait for every check suite on the commit to reach `COMPLETED` — that never happens.** GitHub
creates a check suite for *every* installed App holding the Checks permission, and an App that posts
no check runs leaves its suite at `queued` forever. This repo has exactly that: the **Azure Boards**
App (installed 2026-08-03, `latest_check_runs_count: 0`, zero `AB#` references in the history) puts
a permanently-`queued` suite on every commit. It never blocks a merge — `mergeStateStatus` stays
`CLEAN` — but a naive "all suites complete" poll hangs until its timeout and then looks like CI
never finished. Poll the `MeshWeaver Build and Test` suite specifically (step 3 above does).

Two further gotchas:

- **`Consolidate test results` is the required check** — and the ONLY one to require. `Build solution
  (once)` and the shards are legitimately *skipped* when a run reuses an already-green tree, and a
  skipped required check blocks the merge forever.
- **Also check the clock before declaring a job stuck.** GitHub timestamps are UTC; a local-time
  comparison makes a healthy 7-minute build look like a 33-minute hang. `date -u` first.

## 🚨 SUBSCRIBE to a PR — one persistent Monitor over EVERY event you'd act on

**Waiting on a PR is event-driven work: arm ONE persistent `Monitor` (the harness tool) when you
open the PR, and let it wake you.** Hand-re-armed one-shot polls die at their timeout cap and leave
dead air between "CI finished" and "you noticed" — and a watch that only fires on the happy path is
the same defect as a gate that skips on missing input: **silence looks identical to "still running"
while the thing you needed to react to already happened** (maintainer, 2026-08-17: "we keep losing
time because of such problems"). The monitor's poll loop (GraphQL, ~45 s) must emit a line on EACH
of these transitions, not just green:

- **suite `COMPLETED/SUCCESS`** — the merge signal;
- **suite `COMPLETED/<anything else>`** (FAILURE / CANCELLED / TIMED_OUT / STALE) — go read the
  failing job log NOW, not at the next manual check;
- **a NEW unresolved review thread** (the ruleset's Copilot review lands minutes after open —
  unwatched, it silently gates the merge);
- **`mergeStateStatus = DIRTY`** — a dirty PR runs ZERO CI, so with a success-only watch it waits
  forever;
- **`MERGED` / `CLOSED`** — the monitor's own exit condition.

One monitor can cover several open PRs (loop over them; emit per-PR lines; exit when all are
terminal). The same rule applies to any long-running external wait — a deploy, a bake, a
reconciler: enumerate the terminal states first, subscribe to all of them, and treat "my filter only
matches success" as a bug to fix before arming.

## 🚨 Merging is a SHARED action — coordinate before you land

A merge to `main` supersedes whatever run is QUEUED behind the one in flight, **including a run
another session is waiting on**. Several sessions merge into this repo at once, so "wait for the
run" only works if *everyone* waits; two sessions each merging politely still cancel each other's
pending run. (Why main's *in-progress* runs are never cancelled, and what that does and does not
buy, is in [/ci](../ci/SKILL.md).)

- **Before merging, check whether main has a run in flight that someone is gating a deploy on.** If
  so, **hold and say so.** On 2026-08-26 a routine merge killed the run another session was watching
  to end a CD freeze — no damage beyond a lost cycle, but the fix is coordination, not care.
- **The wait is owed to a merge that must ship ON ITS OWN** (a CD fix, a hotfix someone is
  verifying): merge it, then wait for **that merge commit's** Build-and-Test to COMPLETE — and check
  the completed run's **head SHA is your merge commit**, because "a run completed" and "the run for
  my commit completed" diverge during exactly the burst you are working around.
- 🚨 **A hold reaches your hands, NOT your subagents' — push it to them explicitly.** An agent
  briefed to "root-cause and open a PR" follows the merge-on-green default, which is correct on any
  other day. The same 2026-08-26 hold was then broken **twice more, by two subagents**, each merging
  a perfectly good fix at exactly the wrong moment. When you take a hold: message every running
  agent, tell them to push and PARK, and disarm any auto-merge already armed. The general form — **a
  constraint is only as complete as the set of hands it reaches** — applies to anything you
  delegate, not just merges.

### Why GraphQL, not `gh run watch` — the rate limit and the late-shard race, solved together

`gh run watch` and `gh pr checks --watch` poll the **REST** API every few seconds. Two distinct
failures come from that, and the GraphQL poll in step 3 kills both:

- **Rate limit → false CI-red.** The `gho_…` CLI login is a *user* OAuth token: **5000 REST req/hour
  shared across every session and tool under your account**. One 20-min run (build + 6 shards) is
  hundreds of polls, and several concurrent worktree sessions drain the pool together → `403 API rate
  limit exceeded`, whose exit=1 *looks* like CI-red but is not. Never merge or abort off a 403 —
  check the reset with the **exempt** `gh api /rate_limit` and wait. GraphQL draws on a **separate**
  5000-point budget at ~1 point/query, so a whole run costs a few dozen points; it does not compete
  with your interactive `gh`.
- **Late-shard race → merged before tests ran.** `gh pr checks --watch` returns "all passed" as soon
  as the *currently visible* checks complete, but the test shards register **after** `Build solution`
  — so it exits green while shards are still pending and you merge before tests ran (this turned main
  red after #138). Gating on the **check SUITE** status (step 3) has no such window: the suite is
  `COMPLETED` only when every job/shard in the run has finished.

**After the merge (step 5–6):** poll `main`'s post-merge run the SAME way — re-target the step-3
`suite()` helper at the ref (`repository.ref("refs/heads/main").target … checkSuites`) — because a
test that only runs post-merge can still turn main red. Then `git fetch origin main:main` (step 6).

**Durable fix for heavy parallel CI-watching:** authenticate agent/automation `gh` as the repo's
**GitHub App installation** (its own rate-limit budget — org apps up to ~15k/hr) instead of the
human's shared 5000; see the GitHub App machine-identity notes.

**Push instead of poll — why step 3 runs in the background, and the future upgrade.** GitHub can't
webhook a local CLI session (no inbound endpoint), so the closest thing to "subscribe to a CI-done
event" is to run the step-3 loop as a **background task**: it exits when the suite completes and the
harness delivers a single completion notification — a push from the session's POV, off the REST
budget. A *true* server-side push is possible but not built: the portal already receives GitHub
webhooks at `POST /webhooks/github` (`GitHubWebhookProcessor`, currently `issues`/`issue_comment`
only); adding a `workflow_run` branch + a raw-WS `/events/ci` endpoint would let a session subscribe
with the harness `Monitor` `ws:` source for zero-poll delivery. Deferred — the background loop is
enough today.

## 🚦 The merge queue — `--auto` enqueues, the steward re-queues, you never re-order

Core `main` merges through GitHub's **merge queue** (ruleset `main pr protection`, rule
`merge_queue`; the full manual is
[MergeQueue.md](../../../src/MeshWeaver.Documentation/Data/Architecture/MergeQueue.md)). Each
entry is built on top of the entries ahead of it, so the combination that lands is the combination
that was tested — the fix for two independently-green PRs being red together (#2412). What that
changes about this procedure:

- **`gh pr merge <n> --auto` means "enqueue when this PR's own required checks are green".**
  `auto-arm.yml` runs it on every non-draft PR, so a green PR lands without a hand on it. Convert to
  **draft** to opt out. `gh pr merge <n> --merge` on a green PR is the same thing done by hand: it
  enters the queue, it does not merge on the spot.
- **A push to a queued branch ejects it.** The queue does not pick up the new commit in place; the
  arm lane re-arms on `synchronize` and the new head re-enters once its own run is green.
- **Dequeue via GraphQL** (`dequeuePullRequest(input:{id:<PR node id>})`), never by re-ordering or
  by `jump`. The queue's order is its correctness argument.
- **Never re-queue an ejected PR by hand, and never re-run the failed queue build.** The
  `merge-queue-steward.yml` lane acts on every `dequeued` event: a catalogued flake, an
  infrastructure death, a `CI_TIMEOUT`, or a multi-PR group whose own run was green is re-queued
  (capped per head sha, recorded in a marker comment); anything else is left out with the failing
  assertion, the run URL and the `queue-rejected` label. Read its comment. A red on a queue build
  lives on the `gh-readonly-queue/main/pr-<N>-…` run, not on the PR's commit — `gh pr checks` shows
  nothing. To make a flake re-queueable, add an evidence-bearing entry (assertion-MESSAGE regex,
  issue, run URLs, ≤30-day expiry) to `.github/known-flakes.json`; never a test-name pattern.
- **The step-3 poll still applies to the PR's own run**, and after the queue lands it, to `main`'s.
  The queue is not a reason to stop watching; it is the reason the merge is no longer yours to press.

## What's New entry (step 0.5) — one doc node per user-facing PR

The platform's **What's New** feed is not a hand-maintained changelog: it's the set of per-entry
markdown nodes under `src/MeshWeaver.Documentation/Data/WhatsNew/` (shipped in the `Doc` partition,
so every self-updating deployment shows the same feed). The **What's New** settings tab lists them
newest-first; each entry is a normal doc node you can open.

- **One file per PR** (`<YYYY-MM-DD>-<slug>.md`) — the date prefix drives newest-first ordering, and
  a distinct filename per PR means two concurrent PRs never conflict on the feed (the reason we do
  NOT prepend to a single rolling file).
- **Front-matter**: `Name` (title shown in the list), `Category` — **`Feature` or `Fix`, nothing else**
  (`feat/ perf/ chore/ docs/` → `Feature`, rendered in full; `fix/` → `Fix`, bundled into the day's
  one-line summary) — `Description` (one-liner), `Icon` (a Fluent icon name, e.g. `Sparkle`), and
  `Order: -YYYYMMDD` (a **negative** ship date, matching the filename's date) so the doc tree sorts
  newest-first instead of alphabetically by title. Body is plain-language user-facing prose.
  `WhatsNewEntryIntegrityTest` enforces all five — a wrong `Category` or a missing `Order` turns
  **main** red, not just your PR, because the entry only reaches CI once it has merged.
- **When to skip**: pure-internal PRs (refactors, tests, CI, dependency bumps) with no user-visible
  change don't need an entry — note the skip in the PR body so a reviewer knows it was deliberate.

## The half-committed-WIP trap (how main went red on a clean CI)

A file that is **referenced by a committed file but never `git add`ed** builds fine locally (the
untracked source sits in your working tree) and fails on CI's **clean checkout** with
`-warnaserror`:

```
ThreadMessageBubbleView.razor(57): error CS0103: The name 'ToolCallVisibility' does not exist …
```

`ThreadMessageBubbleView.razor` (committed) used `ToolCallVisibility.Partition(...)`, but
`src/MeshWeaver.Layout/ToolCallVisibility.cs` was untracked. The fix is to **track the missing
source file**, not to revert the reference. Catch it in pre-flight:

```bash
# any committed file referencing a symbol whose source is still untracked?
git status --porcelain | grep '^??'                       # list untracked
rg -l '\bToolCallVisibility\b' src | xargs git ls-files    # is each referenced source tracked?
```

CI builds with `dotnet build --no-restore -c Release -p:CIRun=true -warnaserror` — reproduce that
exact line locally before pushing if anything feels half-committed.

## Why this matters here specifically

This repo's portals **auto-update from main**. A green main is the contract that keeps the
self-update healthy; a red main silently stalls every portal's roll-forward. That is why the
merge gate is a hard rule, not a nicety — see the self-update rollout notes and
[DeploymentAKS.md](../../../src/MeshWeaver.Documentation/Data/Architecture/DeploymentAKS.md)
(auto-baked feed + self-roll) and the `autoroll` subcommand in `deploy/homebrew/bin/memex-local`.
