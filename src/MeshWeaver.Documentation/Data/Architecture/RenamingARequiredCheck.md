---
Name: Renaming a Required Status Check
Category: Architecture
Description: A required context's name lives in the workflow and in branch protection, and no single write moves both — so every rename has a moment where they disagree. The three orderings, which one deadlocks under classic protection, and the shim job that renames with no unguarded window.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 7V5a1 1 0 0 1 1-1h14a1 1 0 0 1 1 1v2"/><path d="M9 20h6"/><path d="M12 4v16"/><path d="m17 13 3 3-3 3" opacity="0.5"/></svg>
---

# Renaming a Required Status Check

**Branch protection matches a required check by its literal NAME.** That name lives in two places —
the workflow that publishes it, and the protection that requires it — and **no single commit and no
single API call moves both.** Every rename therefore has an interval in which the two disagree. The
whole problem is choosing an interval whose failure mode you can live with, and the default choice
is usually the one that cannot be undone from inside the repo.

## The rename is usually a side effect, not a decision

Nobody sets out to rename a required check. It happens because **calling a reusable workflow
prefixes every context that workflow publishes with the calling job's id.**

A repo that inlines a lane as a job named `Validate node repos` publishes exactly that string. The
moment it adopts the shared `workflow_call` lane as a job `validate:`, the identical work publishes
as `validate / Validate node repos`. The diff that causes this contains no new name — it deletes a
block of steps and adds a `uses:` — so nothing in review looks like a rename.

This is why the repo rules require the context move to happen *in the same change* as the lane
adoption — see [CI Content Bake](/Doc/Architecture/CiContentBake) for the shared lanes themselves.

## Why it cannot be atomic, and what each ordering costs

The asymmetry that decides everything is in
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals): a required context that **reported**
`SKIPPED` is *satisfied* under both protection mechanisms, while one that was **never reported at
all** is *satisfied* under a **ruleset** and **blocks forever** under **classic** protection.

| ordering | classic protection | ruleset |
|---|---|---|
| **Rename the workflow first**, protection untouched | the old name is never published again ⇒ **every PR blocks forever**, with a full green wall and no red check to point at | the gate silently stops being required — a hole nobody can see |
| **Drop the old name, merge, then add the new one** | works, but between the drop and the add the lane is **required by nobody** | same window |
| **Add the new name first, then rename, then drop the old** | **deadlock** — see below | **no window at all; the best route** |
| **Shim job publishing the legacy name** | **no window**; the only such route on classic | works, but the both-names route is simpler |

🚨 **"Add the new name first" is not a slower route on classic protection — it is a deadlock.**
Requiring a name nothing publishes yet means requiring an *absent* context, which under classic
blocks every PR in the repo *including the workflow rename itself* — the only change that could
unblock it. With `enforce_admins: true` there is no override, and the repo is wedged until someone
edits protection again. Under a ruleset the same step costs nothing, because absent counts as
satisfied; there it is the cleanest route and needs no shim.

**Measure the mechanism before choosing.** `branches/main/protection` answering `404` means look at
`rulesets`, never that the branch is unprotected:

```bash
gh api repos/<owner>/<repo>/branches/main/protection --jq '.required_status_checks'
gh api repos/<owner>/<repo>/rulesets --jq '.[]|"\(.id) \(.name) \(.enforcement)"'
```

## The measured case

**MeshWeaver.Plugins#1453**, 2026-09-08. The PR adopts the shared `node-repo-validate` lane. All
**106** of its jobs concluded `success`, `validate / Validate node repos` among them. The required
context `Validate node repos` appeared **nowhere** on the head sha — no check-run, no commit status —
and the PR read `mergeable_state: blocked`:

```
required contexts (classic, MeshWeaver.Plugins, enforce_admins: true):
  Validate node repos                            <- NO check-run, NO commit status  ← the block
  Compile every NodeType (vs core)               success
  Build + test the portal hosts                  success
  Module bundles / All selected bundles built    success
  Module bundles (floor) / All selected bundles built   success
```

Confounds ruled out before concluding: `required_pull_request_reviews` was `null`, `strict` was
`false`, and the repo's only ruleset carried `deletion`, `non_fast_forward` and
`copilot_code_review` — none of which blocks a merge.

Ten other pull requests were open against that branch at the time, which is what took the
drop-then-re-add ordering off the table: its window is not theoretical when ten authors are landing
through it.

## The shim: move the NAME to a job, not the protection to the name

Add a job whose `name:` **is** the required context and whose only work is to assert the real lane's
verdict. The old context keeps being published, continuously, so there is no window and nothing to
co-ordinate with other authors.

```yaml
  validate:                      # the shared lane; publishes `validate / Validate node repos`
    uses: Systemorph/MeshWeaver/.github/workflows/node-repo-validate.yml@<pinned sha>
    with:
      platform-ref: <pinned sha>

  validate-name-shim:
    name: Validate node repos    # ← the LEGACY context, published verbatim
    needs: [validate]
    if: ${{ !cancelled() }}      # a failed `needs:` must make this RUN and go RED, never skip
    runs-on: ubuntu-latest
    timeout-minutes: 5
    steps:
      - name: The shared validate lane must have SUCCEEDED
        env:
          VALIDATE_RESULT: ${{ needs.validate.result }}
        run: |
          set -euo pipefail
          echo "validate (shared lane) => $VALIDATE_RESULT"
          if [ "$VALIDATE_RESULT" != "success" ]; then
            echo "::error::the shared validate lane did not succeed (result: $VALIDATE_RESULT)"
            exit 1
          fi
```

### The two failure modes are opposites, and the design must exclude both

- **The shim reports `skipped`** ⇒ protection **accepts** it and the gate is silently unrequired.
  You would have traded a permanent block for a green tick over a gate that never ran — strictly
  worse than the bug you started with.
- **The shim is never reported at all** ⇒ under classic protection every PR blocks forever, which
  is the original bug reproduced exactly.

So the shim must be unable to skip **and** unable to go missing:

🚨 **The job-level `if: ${{ !cancelled() }}` is the whole design, not a detail.** Without it, a
failed `validate` makes GitHub **skip** the dependent job rather than run it. For the same reason
the assertion lives **inside** the job: a step-level `if:` never evaluates at all when the job
itself was skipped, so moving the condition one level down silently reintroduces the accepted
`skipped`.

🚨 **No path filter, no job-level condition that can evaluate false, and no `continue-on-error:`** —
and the workflow must fire on every event a merge can come from. This is the
[no-skip-trapdoor rule](/Doc/Architecture/ControlsThatCannotFail); a gate that cannot fail is not a
gate, and a required context that is simply never published is the deadlock again.

🚨 **Register the shim in the repo's skip detector** (`gates-executed` / `Every gate executed`). The
shim is a gate; a meta-job that does not enumerate it cannot notice it going missing.

### The control: this shape does go red

The shape is not new — it is what a repo already uses wherever a required name must survive a
refactor. MeshWeaver.Plugins' `portal-hosts-gate` carries `Build + test the portal hosts` over a
sharded matrix for exactly this reason.

**Measured**, Plugins run `34182621883`: `Portal hosts (shard 3)` concluded `failure`, and
`Build + test the portal hosts` — same `needs:` + `if: ${{ !cancelled() }}` + assert-inside shape —
concluded **`failure`**, not `skipped`. Do not ship a shim without a control like this one; a shim
that cannot go red is a [control that cannot fail](/Doc/Architecture/ControlsThatCannotFail).

## Retiring the shim — write a CONDITION, never "delete later"

The shim is transitional, and the retirement order is the **reverse** of the adoption order:

1. every open PR has the **new** context on its head — i.e. each has merged, or taken the main that
   calls the shared lane; **then**
2. protection is edited to require the new context instead of the bare name; **then**
3. the shim job is deleted.

**Doing 3 before 2 reopens the identical permanent block.** Write those steps *on the job*, as a
condition a reader can evaluate — including the command that evaluates step 1:

```bash
gh api repos/<owner>/<repo>/pulls --jq '.[]|"\(.number) \(.head.sha)"'
# then read the check-run names back off each head
gh api --paginate "repos/<owner>/<repo>/commits/<sha>/check-runs?per_page=100" \
  --jq '.check_runs[].name'
```

Never write "delete this line later" into the job instead. An instruction to remove something later
was ignored once in this fleet and reddened every C#-touching PR for about forty minutes (#3422) —
which is why [transitional allow entries](/Doc/Architecture/TransitionalAllowEntries) expire by
mechanism rather than by reminder. A shim cannot expire by mechanism (its whole purpose is to keep
publishing), so the condition on the job is the only thing standing in for one.

## Verify by reading the context back, not by reading the YAML

The point of the exercise is a specific string appearing on a specific commit. Assert that, not the
job definition:

```bash
gh api --paginate "repos/<owner>/<repo>/commits/<head-sha>/check-runs?per_page=100" \
  --jq '.check_runs[]|select(.name=="<the required context>")|"\(.status)/\(.conclusion)"'
```

An empty answer means the context is absent — which is the failure, not the absence of one. And
paginate: `check-runs` caps at 100 per page, and a repo with a hundred-job matrix will silently
truncate the row you are looking for.

## Related

[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — what a check's colour means, and the
SKIPPED-versus-never-reported asymmetry this page turns on
· [Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail) — the no-skip-trapdoor rule
· [Transitional Allow Entries](/Doc/Architecture/TransitionalAllowEntries) — expiry by mechanism
· [CI Content Bake](/Doc/Architecture/CiContentBake) — the shared `workflow_call` lanes whose
adoption causes this rename in the first place
