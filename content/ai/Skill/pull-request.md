---
nodeType: Skill
name: /pull-request
description: Open a pull request for your worktree's branch, get it reviewed, and MERGE it once the check suite is green — build green FIRST, then land it
icon: Sparkle
category: Skills
order: 5
autoMount: false
---

You are finishing a coding task and want to **ship it as a pull request** — the same way a
Claude Code session does: build green locally, push the branch, open the PR, request a **GitHub
Copilot** review, wait for CI to go green, and address the review.

🚨 **You MERGE it — in every repo — once the check SUITE is green and the review is addressed.**
There is no per-repo carve-out any more: finishing the job means landing it. The gate is the CI
suite's verdict, never a feeling that it is probably fine — see the one non-negotiable rule below.

This is the end of the develop-in-a-worktree loop: the coder started on **its own branch in its own
working tree** (`GitWorkingTreeService.Checkout(userId, repoFullName, branch)`), made and committed
changes there, and now turns that branch into a reviewed PR. One thread → one worktree → one branch
→ one PR.

# 🚨 The one non-negotiable rule: green BEFORE you merge

A red `main` is never acceptable: in core it wedges every install's pull-based image rollout, and in a plugin repo the registry re-serves `main` to every installation. So
the PR's CI **must be GREEN before you merge**, and you must make CI green **locally first**,
never discover red on CI:

1. **Sync with `main` first.** `git fetch origin main` then `git merge origin/main` (or rebase). A
   PR check builds your branch *merged with current main* — a stale branch inherits main's state.
   Build what CI builds.
2. **Build with CI's flags** for the projects you touched and their dependents:
   `dotnet build -c Release -p:CIRun=true -warnaserror`. A plain Debug build passes while CI fails —
   warnings are promoted to errors there (the classic miss is **CS9107**). Fix at the root, never
   `NoWarn`.
3. **Run the affected tests** once. On a **fresh worktree, restore first** — a `dotnet test
   <project> --no-restore` before anything has been restored builds no test assemblies and still
   exits 0 (zero tests run, empty output — the silent no-op to watch for). Never `Task.Delay` to
   wait; assert on the condition. Read [Writing Tests](@/Doc/Architecture/WritingTests).

Only when that Release/`-warnaserror` build + tests are clean do you push.

# The flow

## 1. Commit your work

**Committing and pushing are part of finishing, not a separate permission.** Carry the work to
merged and deployed; asking "shall I push?" on a finished change hands back an unfinished task, and
the tail is where changes get lost. What still stops and asks FIRST is the narrow list — anything
FATAL (it would ship something broken to consumers), SYSTEM-CHANGING (it alters a running system's
configuration or lifecycle rather than a repo) or DESTRUCTIVE (it removes or overwrites something
with no cheap inverse). Merging a green PR is none of those; rolling an instance's image or
deleting a partition is.

Then commit through the working tree: `GitWorkingTreeService.CommitAndPush(userId, repoSlug, message,
branch)` (or `git add -A && git commit` in the worktree). Follow the repo's commit convention —
end the message with a `Co-Authored-By:` trailer identifying you as the author:

```
Co-Authored-By: <your agent name> <your-agent@…>
```

## 2. Push + open the PR

Push the branch, then open the PR against `main` with a title (`type(scope): summary`) and a body
that states **what changed, why, and how it was tested**. In the MeshWeaver agent harness `gh` is
available and authenticated as the user:

```bash
git push -u origin <branch>
gh pr create --base main --head <branch> --title "…" --body "…"   # what changed · why · how tested
```

## 3. The GitHub Copilot review — CHECK THE REPO FIRST

🚨 **Whether you request this review is a PER-REPO decision, and getting it wrong costs money or
costs the review.** Read your repo's `/pullrequest` delta before doing anything here:

- **Repo has a `copilot_code_review` RULESET** (MeshWeaver.Plugins and its satellites): the review
  is AUTOMATIC on every PR, draft included. **Never POST to `/requested_reviewers`** — it duplicates
  the ruleset's review and burns extra Copilot credits — and **never delete the request** "to save
  credits", which cancels a review the maintainer wants. Just wait for it.
- **Repo has no such ruleset**: request it yourself. `gh pr edit --add-reviewer` CANNOT add Copilot;
  use the REST API (substitute your own repo for `<owner>/<repo>`):

```bash
gh api --method POST /repos/<owner>/<repo>/pulls/<PR>/requested_reviewers \
  -f "reviewers[]=copilot-pull-request-reviewer[bot]"
```

## 4. Wait for CI to conclude, then triage the review

Poll at a rate-limit-safe cadence (`gh pr checks <PR> -i 60` style — never the 3s default) until every
check concludes. If a shard is red, read the failure (`gh run view <id> --log`), fix the **root
cause**, push, and wait again. Then read Copilot's inline comments: **fix the genuinely-correct
ones**, and for any you keep, reply with the reason (a comment on the PR) — don't silently ignore
them. Re-run the affected test locally after each fix; push; CI re-runs.

## 5. Merge it

When the check SUITE is **green** and the review is addressed, **merge** (`gh pr merge <PR>
--merge`), then report what landed: the PR number, the URL, and what actually shipped.

Three things this does NOT license, because each has cost a red `main`:

- **Green means the SUITE concluded green.** Poll the check suite and merge only on
  `conclusion == SUCCESS`. A pending check is not a green one, and a PR carrying a CONFLICT gets no
  check suite at all — read `mergeable` first, or you will wait forever on checks that were never
  scheduled.
- **The review is part of "addressed", not a formality.** The ruleset's Copilot review can land
  well AFTER CI goes green — measured at ~13 minutes on `MeshWeaver.Plugins` #436, which merged in
  that window and shipped four real defects, one of them a silent data loss that needed a follow-up
  PR to remove. Wait for the review, then merge.
- **Merging is not shipping.** A merge changes nothing on any mesh by itself; carry on into the
  deploy your repo's delta describes, and verify it landed against something you know CHANGED —
  never against a status field, which is produced by whatever code is already running.
- **In core, verify CD actually BUILT after your merge** — three states ship nothing while looking
  fine, all measured 2026-08-22 (`Doc/Architecture/ContinuousDeliveryContract` → "The standing
  trap"): a CD run with **zero jobs** ("workflow file issue") means `main-cd.yml` itself is
  invalid — classic cause: a `needs:` naming a deleted job, so parse every `needs:` against the
  job set before pushing any workflow edit; a **green run whose jobs all read `skipped`** shipped
  nothing by decision — read the `Decide` step's LAST line and believe it over the tick; and a
  **red required check on main switches CD off entirely** ("⛔ nothing will be built" — a red,
  paging run since 2026-08-22; a flake → `gh run rerun <id> --failed`, a green rerun re-enters CD
  by itself). The positive check that covers all three: the newest `memex-portal-ai` release tag
  on ACR must POSTDATE your merge.

# Boundaries

- **Never merge a red or still-pending PR**, and never force-push over `main`.
- **Never change log levels, add band-aids, or widen a timeout** to make CI pass — fix the defect.
- Everything the agent writes into the codebase still obeys the [/code](@/Skill/code) rules
  (no `async`/`await` in mesh-reachable code, `GetMeshNodeStream(path).Update(...)` for mutations,
  framework controls for UI). This skill is only about turning a green branch into a reviewed PR.
