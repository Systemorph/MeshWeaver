---
NodeType: Markdown
Name: "Developing from within MeshWeaver"
Abstract: "Drive a code change end-to-end from the MeshWeaver instance — the same loop a Claude Code session runs. A coding thread gets its own worktree, the agent makes changes there, the /pull-request skill opens a reviewed pull request, and you approve the merge. Covers the worktree-per-task model (GitWorkingTreeService), the /code and /pull-request skills, the non-negotiable green-before-merge gate, and how the GitHub issues/PRs/webhooks integration fits in."
Authors:
  - "Roland Buergi"
Tags:
  - "Architecture"
  - "GitHub"
  - "AI"
  - "DevWorkflow"
  - "Skills"
---

# Developing from within MeshWeaver

You can steer a coding change **end-to-end from the MeshWeaver instance** — the same loop a Claude
Code session runs — and finish at a **pull request you approve**. A coding thread gets its own
worktree, the agent makes and commits changes there, a skill opens a reviewed PR, and the merge is
**your** call.

```
thread ──▶ worktree ──▶ (coder edits + commits) ──▶ pull request + Copilot review ──▶ you merge
        Checkout            /code skill                    /pull-request skill        (human gate)
```

## The loop

### 1. A worktree per task

A coding thread works on **its own branch in its own on-disk git working tree**, created via
`GitWorkingTreeService.Checkout(userId, repoFullName, branch)` — an isolated checkout on the
`/workspace` PVC, shared by the AI harness and the in-portal editor. **One thread → one worktree →
one branch → one PR.** Working in isolation is what lets several coding threads run at once without
stepping on each other (the same reason human developers use branches).

### 2. The coder makes changes

The agent edits in that worktree following the **`/code` skill** — the platform's engineering rule
book: no `async`/`await`/`Task<T>` in mesh-reachable code, every mutation through
`GetMeshNodeStream(path).Update(...)`, framework controls (never hand-rolled HTML) for UI, and
reactive tests. It commits with `GitWorkingTreeService.CommitAndPush(userId, repoSlug, message,
branch)`. See [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) and
[Writing Tests](/Doc/Architecture/WritingTests) for the rules the coder must honour.

### 3. Ship it — the `/pull-request` skill

When you say "open a PR", the agent runs the **`/pull-request` skill**, which codifies the exact
ship sequence:

1. **Build green with CI's flags FIRST** — sync with `main`, `dotnet build -c Release -p:CIRun=true
   -warnaserror` for the touched projects, and run the affected tests. Never discover red on CI.
2. **Push** the branch and **open the PR** against `main` (title `type(scope): summary`; body =
   what changed · why · how tested).
3. **Request the GitHub Copilot review** — via the REST API (`requested_reviewers` with the Copilot
   bot; `gh pr edit --add-reviewer` cannot add it).
4. **Wait for CI to conclude**, fix any red at the root, and **triage Copilot's comments** (fix the
   correct ones, reply with a reason for any kept).

### 4. You merge

The agent **stops at green-and-reviewed and hands the merge to you**. Merging is the **human
approval gate**, never the agent's — you approve it on the PR page. This is deliberate: see below.

## Why "green before merge" is absolute

Deployment is **pull-based**: every installation (memex, atioz, memex-cloud, and any external
install) runs an in-portal self-updater that polls the registry and rolls `main`'s image per its
own update policy. A **red `main` wedges every install's rollout**. So a PR's CI must be green
before it is merged, and the agent makes it green **locally first** — a Debug build passes while CI
(Release, warnings-as-errors) fails, so the coder builds what CI builds. No band-aids, no widened
timeouts, no log-level fiddling to make it pass — the defect is fixed at the root.

## The GitHub side

Issues, pull requests, branches, and **live webhooks** are the data this loop acts on. A Space
connects to a repository; issues sync into `{space}/_Issue/{number}` nodes; pull requests are read
live (list, checks + reviews roll-up, comment, merge) and never replicated; a HMAC-verified webhook
keeps issue nodes fresh. The reactive Octokit client that powers all of it runs every call through
the controlled I/O pool. Full reference: [Syncing a Space with GitHub](/Doc/Architecture/GitHubSync).

## What's here today, and the direction

**Live today:** per-agent worktrees (`GitWorkingTreeService`), the `/code` and `/pull-request`
skills, and the full GitHub issues/PRs/webhooks backend (reactive Octokit, merge/close/comment,
checks + reviews).

**The direction** (not yet built): binding a thread to its worktree so picking a thread shows its
work-in-progress, and a single **overview page** — worktrees ↔ branches ↔ open PRs, GitHub-Desktop
style — with the one-click merge approval. Note that a cloud portal can surface the *GitHub* side
(open PRs, branches, checks) but not a developer's **local, uncommitted** worktree state — that
half is inherently a local tool.

## See also

- [Syncing a Space with GitHub](/Doc/Architecture/GitHubSync) — issues, PRs, webhooks, the sync directions.
- [Local Dev Workflow](/Doc/Architecture/LocalDevWorkflow) — applying code changes to a running portal.
- [Activity Control Plane](/Doc/Architecture/ActivityControlPlane) — how long-running operations (like a sync or a merge) run as tracked activities.
- [Asynchronous Calls](/Doc/Architecture/AsynchronousCalls) · [Writing Tests](/Doc/Architecture/WritingTests) — the rules the coder honours.
