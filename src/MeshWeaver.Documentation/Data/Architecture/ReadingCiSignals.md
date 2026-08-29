---
Name: Reading CI Signals
Category: Architecture
Description: What a check's colour actually means — why SKIPPED and ABSENT count as satisfied, why a red on a non-required check does not block, and the i18n mirror that reds every downstream PR until it lands.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>
---

# Reading CI Signals

**A check's colour is not its authority.** Every rule here was learned by getting it wrong in
production or in a merge, several of them on the same day. They are cheap to apply and expensive to
rediscover.

## 🚨 The one sentence

**The absence of a red is not evidence of green.**

`SKIPPED`, `CANCELLED`, `NEUTRAL`, an empty conclusion, and *a required context that never
appeared at all* are all "not FAILURE" — and **GitHub counts a skipped or absent required context as
SATISFIED**. A PR can therefore merge through a required gate that never ran, with a full wall of
ticks.

Measured: **Plugins #862** merged with

```
Validate node repos:                     SUCCESS
Compile every NodeType (vs core):        SKIPPED     <- required
Compile + render node repos (from ACR):  SKIPPED     <- required
Module bundle (MeshWeaver.AI):           FAILURE
```

Branch protection was satisfied and auto-merge fired. The compile gate carries **no `if:`** — it is
deliberately unconditional — and it skipped anyway. So *"it skipped, therefore skipping was safe"*
is not an inference you may make.

**The rule:** a required context counts only when its conclusion is literally `SUCCESS`.

```bash
gh pr view <N> --repo <repo> --json statusCheckRollup \
  --jq '[.statusCheckRollup[]? | select(.name | IN("<required>","<contexts>","<here>"))
         | "\(.name)=\(.conclusion)"]'
```
Every required name must be **present** and read `=SUCCESS`. Count them — a missing row is a fail,
not an absence.

## Required ≠ meaningful, in both directions

Two independent facts, and confusing them costs time in both directions:

| | |
|---|---|
| **Required, and red** | blocks the merge |
| **Required, and skipped/absent** | **does not block** — GitHub treats it as satisfied |
| **Not required, and red** | does **not** block — but it is still evidence, and may be a real defect |
| **Not required, and it is the job your diff changes** | do not arm auto-merge: the PR can land *before* that job finishes, putting a broken gate on `main` where it renders as a green tick |

**Measure protection per repo; never trust a table, including this one.** And check *both* mechanisms:
classic branch protection **and rulesets** — `GET /repos/{owner}/{repo}/branches/main/protection`
answers `404 Branch not protected` for a repo governed by a ruleset, which reads as "no protection
at all" and is wrong.

```bash
gh api repos/<owner>/<repo>/branches/main/protection --jq '.required_status_checks.contexts'
gh api repos/<owner>/<repo>/rulesets --jq '.[]|"\(.id) \(.name) \(.enforcement)"'
gh api repos/<owner>/<repo>/rulesets/<id> \
  --jq '.rules[]|select(.type=="required_status_checks").parameters.required_status_checks[]?.context'
```

A **dynamic matrix cannot be a required context** — the shard names change. Require a single
**collector** job that `needs:` every shard and fails if any did not succeed (core does this with
`Consolidate test results`). Requiring shard names by hand orphans a required context the moment the
shard count changes, and it then waits forever.

## The same trap in the tools you write to watch CI

Two bugs that make a monitor lie, both hit in one session:

- **`jq`'s `//` does not fall through on `""`.** Only `null` and `false` trigger it, and an empty
  string is truthy — so `.conclusion // .status` yields `""` for a queued check, and "not yet run"
  becomes indistinguishable from "no failure".
- **An empty or partial rollup is vacuously green.** "No failures and nothing incomplete" is *true*
  of a PR with zero checks. Decide readiness by asserting the **required set is present and
  SUCCESS**, never by the absence of failures.

## 🌍 The i18n mirror — deal with it routinely, not as an incident

Core owns `src/MeshWeaver.Messaging.Hub/Localization/strings.{en,de}.json`. MeshWeaver.Plugins
mirrors them at `clients/react/src/i18n/strings.{en,de}.json`, and its `RN app + web clients` job
asserts the mirror matches core `main`.

**So the moment a core catalog change merges, EVERY open Plugins PR goes red on that job,
regardless of its diff, until the mirror lands.** Measured 2026-08-29: eleven PRs red at once, on
diffs that could not reach the RN app — a lockfile override, a Store C# change. The guard is
correct; the gap between the two merges is the problem.

**The routine — do this every time, not as a fix afterwards:**

1. Adding a key to core's catalog? **Open the Plugins mirror PR in the same session**, and land it
   immediately after the core PR merges. Core must go first: the guard compares against core, so a
   mirror that leads *is* the drift it exists to catch.
2. 🚨 **Never patch the mirror on the individual red branches.** That creates competing edits to the
   same two files and a conflict for the real mirroring PR. **One landing clears them all**; the
   others need only a re-run, no code change.
3. Recognise it instantly: **a diff that provably cannot reach the RN app is failing the RN job.**
   Do not debug the PR — compare the catalogs:

```bash
git -C <core>    grep -c '<newKey>' origin/main -- 'src/MeshWeaver.Messaging.Hub/Localization/strings.en.json'
git -C <plugins> grep -c '<newKey>' origin/main -- clients/react/src/i18n/strings.en.json
```
Core `1` / Plugins `0` is this, every time.

🚨 **The guard asserts the mirror is IDENTICAL to the whole server catalog — not that particular
keys exist.** So a mirror PR that copies "the keys that broke it" is still red, one key short, and
looks like the fix failing:

```
FAIL  catalog drift guard > strings.en.json is identical to the server catalog
AssertionError: expected [ 'about.buildCommit', …(1043) ] to deeply equal [ …(1044) ]
```

Mirror by **diffing the key sets**, never by copying the keys you happened to notice — a second,
unrelated key added to core in the meantime is exactly what you will miss. And insert at the
**text level**: re-serialising the JSON rewrites unrelated `\uXXXX` escapes across the whole file
and buries the real change.

Note the RN job is **not** a required context in Plugins, so this reds PRs without blocking them —
which is its own hazard: eleven PRs red on a known-benign check is exactly the noise a *real*
failure hides in.

## Related

[Module Versioning](ModuleVersioning) · [Modules](Modules) ·
[Deploying Plugin Changes](DeployingPluginChanges)
