# The fleet's ONE CI process

Maintainer, 2026-09-13: *"see that process is same as in plugins everywhere ⇒ harmonize."* This page
is the process every node repo runs — MeshWeaver.Plugins, Education, Reinsurance, SocialMedia, Crm,
Manufacturing — and where each piece lives. The rule behind it: **the mechanism lives in this repo
as a shared lane or a shared script; a satellite carries a thin caller and its own content.** A
satellite that re-implements a piece drifts, and the next incident is measured twice.

## The pieces, and where they live

| Piece | Mechanism (this repo) | The caller carries |
|---|---|---|
| Validate node JSON + sources, workflow guards | `node-repo-validate.yml` | the call, `enforce-cancel-rule: true` |
| Compile-check against the platform surface | `node-repo-compile-check.yml` | the call |
| The Tests-area gate (import + compile + render + run) | `node-repo-gate.yml` | the call, `permissions: actions: read` |
| Module pack / publish / tag / bake / canary / ref-bump | `node-repo-*.yml` | the calls |
| **Generated-lock conflicts** — a PR that conflicts only on `*/manifest.lock` lands anyway | `node-repo-resolve-locks.yml` + `.github/scripts/resolve-generated-conflicts.py` | a 12-line workflow on `push: main`, hourly, dispatch; the App secrets |
| **Verdict adoption** — a merge from main (the resolver, `update-branch`, a hand merge) or a lock-only commit never costs the run | `.github/scripts/adopt-verdict.py`, decided by `node-repo-gate.yml` (`adopted-verdict: auto`) | nothing (Plugins decides itself in its change-set classifier and passes the URL) |
| **Cancellation** — nothing on main cancels; a person's PR push supersedes; the bot's push never | `.github/scripts/check-main-runs-not-cancelled.py`, run by the validate lane | `cancel-in-progress: ${{ github.event_name == 'pull_request' && github.event.sender.type != 'Bot' }}` |
| Auto-merge armed on every non-draft PR to main; draft is the only hold | `auto-arm.yml` | the call |
| Build queue front door (admission through the build instance) | Plugins `admission` job today; a `node-repo-admission.yml` lane next | — |

## Why adoption is sound

`main`'s protection is `strict: false` in every repo: a green PR merges without being up to date.
A merge of `main` into the branch, or a regeneration of the generated locks, changes nothing the
PR **authored** — so the verdict of the previous head is the verdict of this one. `adopt-verdict.py`
checks exactly that: the leading first-parent chain is merges-from-base or lock-only commits, the
authored diff (everything but `manifest.lock`, blob ids and hunk positions stripped) is
byte-identical to a candidate head's, and that head's newest run of the caller's workflow completed
green. Any doubt gates as usual and prints why. Measured cause: three 30-minute Plugins runs cancelled
by lock-only pushes on 2026-09-13 (#1788 carried eight such merges).

## A shared script does not know where it is — point it at the repository

The resolver moved into this repo's `.github/scripts/` and the lane **fetches it into
`$RUNNER_TEMP`** at the caller's `scripts-ref`, then runs it against the caller's checkout. Its root
had been `Path(__file__).parent.parent`, which was true only of the shape it came from — a copy at
`<repo>/scripts/`, run from its own checkout. Delivered out of tree that expression resolves to
`/home/runner/work`, and the lane's first `git fetch` died with `fatal: not a git repository`.

The lane was red on **5 of 5 runs in each of Education, Reinsurance, SocialMedia, Manufacturing and
Crm** from the hour it was adopted, having resolved nothing; Plugins stayed green because its own
copy still runs in-tree, so the fleet's green/red split looked like an adoption gap rather than a
defect. The lane's `--self-test` step passed throughout: every case tested a helper, and the root was
computed in `main`.

**The rule for any script this repo lends to a satellite lane: it acts on the repository it is
POINTED at.** `--root` (default: the working directory) is resolved to a git top level and refused
by name if there is none, and the lane passes `--root "$GITHUB_WORKSPACE"` explicitly. The self-test
now drives the **entry point**, with `run` swapped out — the only shape that covers the wiring rather
than the helper it calls.

## Convergence, 2026-09-13 — done

| Repo | resolver | cancel rule | adoption | admission |
|---|---|---|---|---|
| MeshWeaver.Plugins | own workflow (source of the lane) | ✓ (#1791) | own classifier (#1791) | ✓ |
| Education | ✓ lane caller (#326) | ✓ expression (#326) | ✓ (`actions: read`, #326) | — |
| Reinsurance | ✓ (#204) | ✓ (#204) | ✓ (#204) | — |
| SocialMedia | ✓ (#185) | ✓ (#185) | ✓ (#185) | — |
| Crm | ✓ (#97) | ✓ (#97) | ✓ (#97) | — |
| Manufacturing | ✓ (#86) | ✓ (#86) | ✓ (#86) | — |

The resolver column means the caller is wired and the lane RESOLVES: wiring landed 16:10Z–17:20Z, and the
lane itself only began doing anything after #4231 (the section above) — a row that said ✓ before that
would have been describing a caller, not a resolution.

All five satellite PRs merged between 16:10Z and 17:20Z on 2026-09-13, after core #4201 recorded
their new callers in `.github/lane-caller-grants.yml` as `pending:`; this page's PR turns those rows
into plain rows and drops the superseded gate rows (the roster is a measurement, not a memory). What
is NOT converged: the admission front door is still Plugins' own `admission` job; the satellites
have no build-queue admission until `node-repo-admission.yml` exists.

## The freeze is a POSITIONAL instrument — four rules, each paid for once

**The pack receipt and the tests matrix must agree.** The module-pack lane's `test-modules` input
narrows which selected modules run their suite. The `select` job cut the tests-lane matrix by that
list, but the pack job's plan step still answered `need_test` from the entry's own `test` flag, so on
a lane-only diff 41 receipts said `tests: lane` while the lane's matrix was empty — and `verify`
rightly refused "the suite ran in NEITHER lane". Core #4203: the plan step narrows by the same list
in the same order (the ledger's REUSE answer first), and the receipt says `tests: none` for a module
the caller did not name.

**1. A freeze is not a pin, and lifting it can move pull requests BACKWARDS.** Plugins'
`MW_PLATFORM_REF` selects one sealed set for every lane. With it unset, a `push` or `pull_request`
run resolves *the newest sealed set this repository's `main` has passed* — measured 2026-09-13
18:01Z: "3.0.0-ci.8484 … 25 newer run(s) passed over" while 8506 was sealed, because Plugins' main
runs were queued for hours behind the dind pool. A PR that needs a symbol from the newest set
(#1801, `TestTimeouts.WriteConvergence` from core #4206) therefore reds on CS0117 with the freeze
lifted and compiles with it set to `3.0.0-ci.8506`. Rule: while main's own runs are backed up, keep
an explicit set in the freeze and move it forward by hand; lift only once main is green on the
newest set. The twin-parity guard (`TeardownTwinParityTest`) adds the ordering: the set carrying the
core change seals → the freeze moves → the Plugins port lands under it; every other branch that runs
in between reds once on the guard and re-runs after merging main.

**2. The value must name the NEWEST SEALED set, because that is what the runners hold.** The CI
runners mount `/opt/platform` read-only, and a CronJob refreshes it every ten minutes (Memex
`deployments/aks/ci-runners/ci-platform-refresh.py`). It installs *the newest sealed set* — and,
since Memex#329, also **the set each subscribed repository's `main` last PASSED**, installing that
one back when it is missing rather than merely declining to purge it. What it KEEPS is those two
plus a bounded window of the newest sets by install time. 🚨 **Never write the window's size into a
document or a diagnostic**: it is the cluster's to set, it moved `3` → `16` on 2026-09-17, and the
gate's own refusal spent that day telling three sessions the volume keeps the 3 newest while it kept
16. Pin anything else
and every heavy leg misses the mount, falls back to pulling the image, and that pull fails:
`could not pull meshweaver.azurecr.io/memex-portal-ai@sha256:… for the registry fallback`, with
`the platform mount at /opt/platform holds no COMPLETE set for the run's tester digest` beside it.
That is what held Plugins `main` red on the evening of 2026-09-13 while the freeze named `8506`.

**3. Until Plugins#1809 a freeze had to be a git REF.** The three image lanes handed
`MW_PLATFORM_REF` straight to `actions/checkout`, so a set name died on
`+refs/heads/3.0.0-ci.8506*:… exit 1` and every image lane was down from the moment the variable
was set. The core sha of the set satisfied both readers; since #1809 the lanes translate a set name
through `resolve-platform.py` themselves.

**4. Neither kind of re-run is a remedy for a purged set, and the difference matters.**
`rerun-failed-jobs` REUSES the run's original platform resolution: the job that resolved it
SUCCEEDED, so it is not re-run and its output is replayed verbatim — measured on Plugins#1816, which
failed the same three catalog assertions twice across a seal. A re-run of the WHOLE workflow does
run `Resolve the released platform` again — **and returns the SAME set for as long as `main` has not
PASSED on a newer one, which while `main` is red is forever.** Measured 2026-09-17 on
Plugins#2040: three attempts (original, `rerun-failed-jobs`, full `rerun`), one resolution
(`3.0.0-ci.8820`) each time. So when the refusal is *"below the OLDEST set kept"*, the fix is the
volume carrying the set again — rule 2 above — and a re-run only helps AFTER that. Reaching for a
re-run first is the standing trap here: it is the action the tooling used to recommend, it is free,
and it cannot work.

**The deadlock they produce, and how to break it.** `main` red → no green main run → every pull
request resolves an old set → the pull request that would FIX main is itself red on that old set.
Measured 2026-09-13 23:44Z: Plugins#1822 (the one-line fix restoring publication) died on
`KeyVaultSecretRef.CopyFrom` — a symbol `main` had required since #1541 merged at 22:24Z and which
the resolved set predated. **Break it by pinning the newest sealed set, never by waiting for main.**

🚨 **The RETENTION form of the same deadlock, and why rule 2 is the thing that ends it** (2026-09-17,
Memex#329). When `main` is red its main-passed set stops moving, so every pull request keeps
resolving that one set. If that set also ages out of the window, every PR in the repository goes red
in `resolve-gate-platform` on a diff that cannot reach it — *including* the PR that would green
`main` — and no re-run of any kind re-resolves anywhere else, because there is nowhere else to
resolve to. Measured that evening: MeshWeaver.Plugins was red on `main` and on all seven branches
with recent runs, every shard refused `3.0.0-ci.8820` while the volume held three sets whose oldest
was `#8836`. Rule 2 is exactly the invariant that makes this unreachable: *the set a subscribed
repository's PRs resolve is on the volume, installed back if absent, for as long as that repository's
main has not moved*. A red main therefore pins its own set in place instead of losing it — the
longer main stays red, the more firmly the set its PRs need is held. 🚨 **And committing that rule
is not running it**: the rule sat in git from 09-14 and was applied to the cluster on 09-17, and for
those three days the volume kept the 3 newest exactly as before. The lane that applies it is Memex
`.github/workflows/ci-runners-apply.yml`; editing `ci-platform.yaml` without dispatching it changes
nothing at all.

So: setting a freeze, moving it and clearing it all need two things in view at once — the colour of
`main` and the set the mount carries. A cleared freeze is not a neutral state; it is a decision to
follow whatever `main` last passed.

## Draft is the hold — until the PR is IN the merge queue

`auto-arm.yml` arms auto-merge on every non-draft PR whose base is `main`, so **draft is the opt-out**
and the way any session holds a PR after a late finding. It stops being a hold the moment the entry
is admitted to the merge queue, and that is not obvious from either UI.

Measured twice on 2026-09-13/14, the second time with the conversion landing mid-flight
(core #4269):

```
05:18:19  auto_merge_enabled          meshweaver-cloud[bot]
05:29:16  added_to_merge_queue        meshweaver-cloud[bot]
05:30:17  convert_to_draft            (a session holding it on a defect it had just found)
05:33:18  merged                      + removed_from_merge_queue   github-merge-queue[bot]
```

The PR reads `draft: true` and `auto_merge: false` **after** the merge: the conversion did disable
auto-merge and did not touch the admitted queue entry, which GitHub then completed. Nothing in the
fleet's lanes decided this and no lane can prevent it.

**So the hold depends on where the PR is:**

| state | how to hold it |
|---|---|
| open, not queued | convert to draft — that is the whole mechanism |
| **in the merge queue** | **dequeue FIRST** (GraphQL `dequeuePullRequest` — REST cannot express it), then draft |

Check before assuming, because the window is a minute wide:

```bash
gh api graphql -f query='{repository(owner:"Systemorph",name:"MeshWeaver"){
  mergeQueue(branch:"main"){entries(first:20){nodes{pullRequest{number} state}}}}}'
```
