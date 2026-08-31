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

## Reading a RED shard: the exit marker classifies it, the log text does not

A red shard says *why* in exactly one place — the **exit marker** printed by "Fail on non-zero
project exit". Read that line; do not count words in the log.

```
[CI] MeshWeaver.Hosting.Monolith.Test exit=1 TESTFAIL (1 failing test(s) recorded in trx —
     xunit v3 exits with the failure count; the host completed normally) elapsed=402s

[CI] MeshWeaver.Hosting.Monolith.Test (part 1/2) exit=1 MASKED (trx records 2 failing test(s),
     which does not explain exit=1 — host crashed after streaming results) elapsed=462s
```

| classification | what the marker CLAIMS | what to do |
|---|---|---|
| `TESTFAIL` | ordinary failing tests; the host completed normally | read the named tests — a flake cluster or a real regression |
| `MASKED` | the exit code is not explained by the trx, so the host is presumed to have died after streaming results | **verify before believing it** — see below |
| `TIMEOUT` / `SIGNAL` | the host died mid-run | the host, not the test — [Debugging Native Crashes](/Doc/Architecture/DebuggingNativeCrashes) |

**These attribute differently, so the classification has to be right.** A test failure is a problem
in that test's own path; a crash is a process-level failure that can take unrelated tests down with
it. Treating a failing test as a crash invents a trunk emergency; treating a crash as a flake hides
one.

🚨 **`MASKED` is DERIVED, not observed — and it has been wrong.** The marker infers "the host died"
from `rc != <failures recorded in trx>`, which assumes xUnit v3 exits with the failure *count*. It
does not always: on 2026-08-30 a `MeshWeaver.Hosting.Monolith.Test` shard recorded 2 failures and
exited **1**, and the harness called that `MASKED (host crashed after streaming results)` — but the
assembly had printed its own summary and completed:

```
=== TEST EXECUTION SUMMARY ===
   MeshWeaver.Hosting.Monolith.Test  Total: 331, Errors: 0, Failed: 2, Skipped: 0, Not Run: 0, Time: 461.193s
```

**That line is the decisive evidence, and it outranks the marker.** An assembly that prints its
execution summary ran to completion, whatever the classification says. A genuine crash looks like
the *absence* of it — no summary for that assembly, a signal exit, or a non-zero exit with nothing
recorded at all. (The classifier rule itself is being corrected in #2738; until that lands, and for
reading any older run, check the summary yourself.)

🚨 **`HOST_CRASHED` appearing in the log is NOT evidence of a crash.** The "Summarize test failures"
step *echoes its own script*, including the sentence explaining the mechanism —
*"…a crashed/killed host is NOT covered by this silence: since #2495 it is written INTO the trx as a
`<project>.HOST_CRASHED` failure by `.github/scripts/record-host-crash.py`…"*. A `grep -c HOST_CRASHED`
over the log therefore returns hits on runs where **no host crashed at all**, and the count scales
with the number of steps that echoed the sentence, not with crashes. Measured 2026-08-30: two main
reds were reported as "2× then 4× HOST_CRASHED" when the markers said one `TESTFAIL` (a
`SilentReadNackTest` flake) and one `MASKED` that the execution summary then showed was **also just
failing tests**. Neither run crashed. It is the same family as the monitor traps above — a line a
*script* printed is not a measurement.

**Then attribute by REACHABILITY before by adjacency.** The merge that happens to sit under a red is
the first suspect and usually the wrong one. Open the failing test project's `.csproj` and ask
whether the suspect diff is even on its reference graph: on 2026-08-30 an Orleans silo-stop change
(#2726) was suspected for a `MeshWeaver.Hosting.Monolith.Test` crash, and
`MeshWeaver.Hosting.Monolith.Test.csproj` references no Orleans project at all — the monolith host
runs no Orleans. One `grep` closed it. The same check exonerated a CI/tooling diff (#2721) whose own
issue body had already said so.

### 🚨 When reachability CANNOT exonerate you: ask whether your path EMITTED anything

Reachability only ever answers *no*. When the failing test's project **does** reference everything
your diff touched — and a plausible mechanism exists — the graph says nothing, and this is exactly
the moment the temptation to re-run "to see" is strongest.

The decisive question is cheaper than the reasoning: **did the code you changed produce any output
in the failing test?** Every interesting path in this codebase logs a tag. Grep the failing test's
own lines for it:

```bash
gh run view <run-id> --repo Systemorph/MeshWeaver --job <job-id> --log > job.log
grep "<FailingTestName>" job.log | grep -cE "LATE_NACK|OwnerDisposing"   # a tag YOUR change emits
```

Measured 2026-08-31 on #2868, which changed four assemblies that `MeshWeaver.Graph.Test` depends on,
with a real mechanism to worry about (the change moved a callback onto a different thread):

```
the changed path's tags in the failing test's own output:   0
the same tags elsewhere in that shard:                     30
```

**A change cannot hang a test through a branch the test never takes.** Zero-against-thirty is
positive evidence that the branch was not entered, not an absence of evidence — the thirty prove the
grep works. Re-running then confirmed it, but the attribution was already sound before the re-run,
which is the point: a re-run you cannot predict the result of is a coin toss, and a re-run you can is
a confirmation.

**Corroboration worth checking in the same breath:** two *unrelated* PRs going red in the same window
on different tests and different shards is the signature of an ambient population, not of either
diff. That happened here — the sibling PR's diff was CI YAML and a plugin catalog, and it failed on a
chart-render flake.

### 🚨 A host-cap kill DESTROYS the hung test's transcript; a `methodTimeout` kill KEEPS it

The two ways a hang ends a test are not equally useful, and the difference decides where to look:

| how it dies | transcript of the test that hung | usable? |
|---|---|---|
| host cap (`exit=124`, `HOST_CRASHED`) | **destroyed** — no trx entry, no captured stdout | no |
| xUnit `methodTimeout` (30 s, `test/xunit.runner.json`) | **written in full** | yes |

So the ambient hang family **erases its own defining artefact**: an investigation that waits for a CI
occurrence and reads the artifacts is reading everything *except* the thing that hung. Measured
2026-08-31 — a crashed shard held 21 `Dropping StreamEndedEvent` and 3 `ADVANCE_WITHOUT_HANDOFF`,
while the hung test's name appeared **nowhere**, so nothing could be established about whether the
two co-occurred.

**Hunt the method-timeout instances instead.** They carry the window in full, and they are routinely
discarded as ordinary flakes. One found the same night showed the pair 12 ms apart on the same path,
inside the hang:

```
02:46:41.286  Dropping StreamEndedEvent for stream _FIj…
02:46:41.298  [UpdateQueue] ADVANCE_WITHOUT_HANDOFF path=logonuser
02:46:41.300  Dropping StreamEndedEvent for stream P3X…
              ── 25 s of complete silence ──
02:47:06.218  TEST FAILED: The operation has timed out
```

A burst of work followed by *total* silence to the deadline is
[/debug](/Doc/Architecture/DebuggingMessageFlow)'s signature for a **dropped reactive emission**, not
a lock — idle cores and silence are never a hot loop.

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

[Module Versioning](/Doc/Architecture/ModuleVersioning) · [Modules](/Doc/Architecture/Modules) ·
[Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges)
