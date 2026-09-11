# A caller grants what its lane demands

🚨 **A job-level `permissions:` block inside a `workflow_call` workflow is not a grant. It is a
requirement imposed on every caller, in every repository — including repositories the lane's own repo
cannot see.**

A called workflow's job can never hold a permission its caller did not grant. So adding
`permissions:` to a shared lane silently makes it a precondition fleet-wide, and a caller that does
not meet it does not get a permission error.

## What it looks like when it goes wrong

**The run graph is rejected before a single job is scheduled.** `startup_failure`, **zero jobs**, and
therefore **no check-run published at all**. Under classic branch protection an *absent* required
context blocks forever rather than passing (see [Reading CI Signals](../ReadingCiSignals)), so the
repository cannot merge anything — including the fix for the thing that broke it.

There is no error text naming the permission. The symptom is an absence.

## The incident this is written from (2026-09-10)

MeshWeaver#3933 added `permissions: {contents: read, id-token: write}` to three jobs of
`node-repo-module-pack.yml`, so core's CD could authenticate to ACR over OIDC without a long-lived
credential. It updated core's own caller in the same commit, and **no satellite**.

| | |
|---|---|
| 18:54:37Z | run resolves core `@main` = `756d6d99e` (pre-#3933) → jobs ran |
| **18:59:37Z** | run resolves `d540b0dca` → **0 jobs** |
| 18:59Z → 23:5xZ | MeshWeaver.Plugins dark: main, every PR, every trigger |
| — | MeshWeaver.SocialMedia had the identical gap, unfired only because its lane had not run |

`id-token` is used only on the opt-in `acr-login: oidc` path; the default is `basic`. So every
satellite was made to require a permission for a code path none of them take.

Fixed by MeshWeaver#3968, which removed the job-level blocks and restored caller inheritance.

## The rule, and the guard

For every job in a repository that `uses:` a shared lane, the caller's effective permissions must be
a **superset** of every job-level `permissions:` that lane declares.

`.github/scripts/check-workflow-permission-pairing.py` enforces it. Core runs it on itself;
**every satellite runs it through `node-repo-validate.yml`** against the lane definitions fetched at
its own `platform-ref` — which is the only place the pair is visible at all, because the caller lives
in one repository and the lane in another.

### Two things the guard deliberately gets right

**It does not ban job-level permissions in a lane.** Measured on core 2026-09-11: eleven of twelve
`workflow_call` workflows declare them, and `node-repo-gate` and `node-repo-publish-bake` both demand
`id-token: write` with **all twelve** of their fleet callers correctly paired. Demanding a permission
is legitimate; demanding it without pairing the callers is the defect.

**An absent `permissions:` block is not "grants nothing".** The job inherits the repository default,
whose floor is `contents`/`packages`/`metadata: read`. The first version of this guard treated
absence as zero and produced, against a real satellite, **one true finding and two false ones** — and
a guard that fires on working lanes gets muted, which is how the next real one goes unnoticed. What
the default can *never* provide is `id-token`, which always needs an explicit grant — and that is
exactly the scope this incident turned on.

### Verified in three directions, on real repositories

| | |
|---|---|
| satellite at its pre-fix commit + the demanding lane | **1 violation** — the incident, and nothing else |
| the same satellite after its fix | **0** — the positive control |
| core against itself | **0** — correct; core's own caller *was* paired |

The denominator (`N caller/lane permission pair(s) resolved`) prints on every run, green or red, so a
check pointed at the wrong root cannot tick like a clean measurement. Zero pairs says so explicitly.

## Related

- [Reading CI Signals](../ReadingCiSignals) — why an absent required context is not a skipped one.
- [Cross-Repo Pair Gate](../CrossRepoPairGate) — the same family: a change whose other half lives in
  a repository this one cannot see. A new **required input** on a reusable workflow is the other
  known instance of this exact shape.
