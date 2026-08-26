---
nodeType: Skill
name: /pull-request
description: Open a pull request for your worktree's branch, trigger a GitHub Copilot review, and land it — build green FIRST, then merge
icon: Sparkle
category: Skills
order: 5
autoMount: false
---

You are finishing a coding task and want to **ship it as a pull request** — the same way a
Claude Code session does: build green locally, push the branch, open the PR, request a **GitHub
Copilot** review, wait for CI to go green, address the review — then **merge it**. You carry the
change all the way to landed; a green, reviewed PR left open is an unfinished task handed back, and
the tail is where changes get lost.

This is the end of the develop-in-a-worktree loop: the coder started on **its own branch in its own
working tree** (`GitWorkingTreeService.Checkout(userId, repoFullName, branch)`), made and committed
changes there, and now turns that branch into a reviewed PR. One thread → one worktree → one branch
→ one PR.

# 🚨 The one non-negotiable rule: green BEFORE you merge

The pull-based self-update deploys `main`'s image — a red `main` wedges every install's rollout. So
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

## 1. Commit your work (only when the user asked you to ship)

Never commit or push on your own initiative — wait for the explicit "ship it" / "open a PR". Then
commit through the working tree: `GitWorkingTreeService.CommitAndPush(userId, repoSlug, message,
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

## 3. Request the GitHub Copilot review — REST API only

`gh pr edit --add-reviewer` CANNOT add Copilot. Request it through the REST API:

```bash
gh api --method POST /repos/Systemorph/MeshWeaver/pulls/<PR>/requested_reviewers \
  -f "reviewers[]=copilot-pull-request-reviewer[bot]"
```

## 4. Wait for CI to conclude, then triage the review

Poll at a rate-limit-safe cadence (`gh pr checks <PR> -i 60` style — never the 3s default) until every
check concludes. If a shard is red, read the failure (`gh run view <id> --log`), fix the **root
cause**, push, and wait again. Then read Copilot's inline comments: **fix the genuinely-correct
ones**, and for any you keep, reply with the reason (a comment on the PR) — don't silently ignore
them. Re-run the affected test locally after each fix; push; CI re-runs.

## 5. Merge it

When the check **suite** is green and the review is addressed, **merge** — `gh pr merge <PR> --merge`
(or `--auto`, which arms it to land the moment the required contexts report). Then report what
landed: the PR number, the URL, and what actually shipped.

- **Green means the SUITE concluded green**, not that no check has failed yet. Poll the suite; a PR
  with no check suite at all (`mergeable` unreadable) will otherwise be waited on forever.
- **A branch behind `main` may need a trunk merge first** where the repo protects `main` with
  `strict: true` — auto-merge waits indefinitely on a `BEHIND` PR because nothing updates it for you.
- **Merging is not shipping.** A merge changes nothing on any mesh by itself; carry on into the
  deploy, which is a separate, system-changing step and IS the one to check in about.

**Stop and ask FIRST only when the next step is fatal, system-changing or destructive** — shipping
something broken to consumers, changing a running system's configuration or lifecycle, or removing
something with no cheap inverse. A flaky job unrelated to your diff, a review finding you disagree
with, and a merge that needs a trunk merge or a re-run are **not** reasons to stop.

# Boundaries

- **Never merge a red or still-pending PR**, and never force-push over `main`
  (`--force-with-lease` on your own PR branch is routine).
- **Never change log levels, add band-aids, or widen a timeout** to make CI pass — fix the defect.
- Everything the agent writes into the codebase still obeys the [/code](@/Skill/code) rules
  (no `async`/`await` in mesh-reachable code, `GetMeshNodeStream(path).Update(...)` for mutations,
  framework controls for UI). This skill is only about turning a green branch into a reviewed PR.
