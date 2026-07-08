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

> If you only remember one thing: **`gh pr checks <PR> --watch --fail-fast` BEFORE `gh pr merge`.**

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

# 3. WAIT for CI to finish — this is the gate. --fail-fast exits non-zero the moment any check fails.
gh pr checks <PR> --watch --fail-fast        # blocks until all checks complete; exit 0 = all green

# 4. ADDRESS findings BEFORE merge:
#    - CI red  → pull the failing job log, diagnose, fix, push, GOTO 3.
#        gh run view <run-id> --log-failed | grep -iE 'error|##\[error\]'
#    - Copilot review → read its comments, address actionable ones, resolve threads, push, GOTO 3.
#        gh pr view <PR> --json reviews,comments

# 5. MERGE — only now, only if step 3 was green.
gh pr merge <PR> --merge
```

### ⚠️ `gh pr checks --watch` exits too early — watch the RUN, not the checks

`gh pr checks <PR> --watch --fail-fast` returns "all passed" **as soon as the checks visible at
that moment complete** — but the test shards on this repo register *after* `Build solution`
finishes, so the watch exits green while `Run tests (shard 2/3)` are still `pending`, and you merge
before tests ran (this defeated the gate on #138 — only the build had passed). **Watch the whole
workflow RUN instead**, which waits for every job/shard:

```bash
# robust merge-iff-FULLY-green: watch the run, then merge
RID=$(gh run list --branch "$(git branch --show-current)" --limit 1 --json databaseId -q '.[0].databaseId')
if gh run watch "$RID" --exit-status; then
  gh pr merge <PR> --merge        # all jobs incl. test shards green → land it
else
  echo "CI RED — NOT merging"; gh run view "$RID" --log-failed | grep -iE '##\[error\]|error CS|Failed!' | head
fi
```

After a merge, **also watch the run on `main`** (the merge commit re-runs the full suite) — a test
that only runs post-merge can still turn main red.

> Do **not** rely on `gh pr merge --auto` either: GitHub auto-merge only waits for *required* status
> checks. This repo currently merges with checks pending (no branch protection), so `--auto` merges
> immediately — defeating the rule. Gate it yourself with `gh run watch`.

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
