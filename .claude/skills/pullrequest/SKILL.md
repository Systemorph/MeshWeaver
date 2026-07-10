---
name: pullrequest
description: Open a PR, get a GitHub Copilot code review, and merge it — and the NON-NEGOTIABLE rule that the PR's CI must be GREEN before you merge, because the pull-based self-update (memex-local autoroll + the AKS portals) deploys main's image. A red or still-pending main blocks the self-update from rolling forward and can wedge the deployment. Use whenever you create/review/merge a PR, when a merge "went through" but CI later failed, or when main is red and the auto-update is stuck. Covers the exact gh/API commands (Copilot reviewer can ONLY be requested via the REST API, not `gh pr edit --add-reviewer`), the merge-only-when-green gate, and the half-committed-WIP trap that turns main red on a clean CI checkout.
user-invocable: true
allowed-tools:
  - Bash
  - Read
  - Grep
---

# /pullrequest — open → Copilot-review → merge, and NEVER merge a red or pending main

## 🚨🚨🚨 The one rule: main must be GREEN before you merge

**The pull-based self-update deploys `main`.** The `memex-local autoroll` watches the moving
`*-local:latest` image, and the AKS portals (atioz / memex / memex-cloud) self-roll to the latest
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

## The procedure

```bash
# 0. PRE-FLIGHT — a clean-checkout CI catches what a local build hides. Check for the trap below.
git status --porcelain | grep '^??'        # untracked files a committed file might reference
dotnet build src/<TheProjectYouTouched> -c Release -warnaserror --no-restore   # match the CI flags

# 0.5 RELEASE NOTE — every USER-FACING PR ships a "What's New" entry as a doc node (one node per
#     entry → no cross-PR merge conflicts). It's shipped in the docs partition and surfaced by the
#     What's New settings tab (Doc/WhatsNew, newest first). SKIP only for pure-internal changes
#     (refactor/test/CI/deps with NO user-visible effect) — say so in the PR body when you skip.
DATE=$(date -u +%Y-%m-%d)                   # no clock in scripts elsewhere, but this is a shell step
cat > src/MeshWeaver.Documentation/Data/WhatsNew/${DATE}-<slug>.md <<'NOTE'
---
Name: <short human title of the change>
Category: What's New
Description: <one-line summary shown in the What's New list>
Icon: Sparkle
---

# <title>

<2–5 plain-language sentences on what changed and why it matters to a user — not the how.>
NOTE
git add src/MeshWeaver.Documentation/Data/WhatsNew/${DATE}-<slug>.md

# 1. CREATE the PR (branch must be pushed first; push only when the user asked — AGENTS.md).
git push -u origin "$(git branch --show-current)"
gh pr create --base main --head "$(git branch --show-current)" --title "…" --body "…"

# 2. REQUEST the Copilot review — ONLY via the REST API. `gh pr edit --add-reviewer copilot`
#    FAILS ("Could not resolve user 'copilot'"). The reviewer is a bot:
gh api -X POST repos/Systemorph/MeshWeaver/pulls/<PR>/requested_reviewers \
  -f "reviewers[]=copilot-pull-request-reviewer[bot]"
#    Confirm it took: the response reviewers list contains "login":"Copilot".

# 3. WAIT for CI via GraphQL — this is the gate. NOT `gh run watch` (it polls REST every ~3s and
#    drains the shared 5000/hr user-token budget → 403s that look like CI-red). GraphQL has its OWN
#    budget (~1 point/query). Poll the "MeshWeaver Build and Test" check SUITE until COMPLETED — the
#    suite finishes only when every shard job does, so there's no late-shard race — then read its
#    conclusion. Merge ONLY on SUCCESS.
#
#    🔔 RUN THIS BLOCK IN THE BACKGROUND (harness: run_in_background: true) so you get ONE completion
#    NOTIFICATION and keep working meanwhile — the background task IS your "CI finished" event. The
#    loop exits 0 iff green, so the notification's exit code tells you whether to merge. (This is the
#    push-style event a local session can have; GitHub can't webhook a CLI directly — see below.)
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
#    - Copilot review → read its comments, address actionable ones, resolve threads, push, GOTO 3.
#        gh pr view <PR> --json reviews,comments

# 5. MERGE — only now, only if step 3 was green.
gh pr merge <PR> --merge

# 6. UPDATE local main to the merge you just landed — in place, WITHOUT switching branches or
#    touching your working tree. `git checkout main && git pull` is WRONG here: work in this repo
#    happens on long-lived feature branches with a dirty tree (uncommitted WIP), so a checkout
#    fails or thrashes it. This fast-forwards the local `main` REF while you stay on your branch:
git fetch origin main:main        # local main -> origin/main (ff-only); current branch untouched
```

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

> Do **not** rely on `gh pr merge --auto` either: GitHub auto-merge only waits for *required* status
> checks. This repo currently merges with checks pending (no branch protection), so `--auto` merges
> immediately — defeating the rule. Gate it yourself with the step-3 GraphQL check-suite poll.

## What's New entry (step 0.5) — one doc node per user-facing PR

The platform's **What's New** feed is not a hand-maintained changelog: it's the set of per-entry
markdown nodes under `src/MeshWeaver.Documentation/Data/WhatsNew/` (shipped in the `Doc` partition,
so every self-updating deployment shows the same feed). The **What's New** settings tab lists them
newest-first; each entry is a normal doc node you can open.

- **One file per PR** (`<YYYY-MM-DD>-<slug>.md`) — the date prefix drives newest-first ordering, and
  a distinct filename per PR means two concurrent PRs never conflict on the feed (the reason we do
  NOT prepend to a single rolling file).
- **Front-matter**: `Name` (title shown in the list), `Category: What's New`, `Description`
  (one-liner), `Icon` (a Fluent icon name, e.g. `Sparkle`). Body is plain-language user-facing prose.
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
