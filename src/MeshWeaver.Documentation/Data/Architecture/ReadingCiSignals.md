---
Name: Reading CI Signals
Category: Architecture
Description: What a check's colour actually means — why a SKIPPED required context counts as satisfied while a never-reported one blocks forever, why a red on a non-required check does not block, the i18n mirror that reds every downstream PR until it lands, and the shape most of these share: a narrow instrument answering correctly while the reader generalises it into a claim it never measured.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 12h-4l-3 9L9 3l-3 9H2"/></svg>
---

# Reading CI Signals

**A check's colour is not its authority.** Every rule here was learned by getting it wrong in
production or in a merge, several of them on the same day. They are cheap to apply and expensive to
rediscover.

## 🚨 The one sentence

**The absence of a red is not evidence of green.**

`SKIPPED`, `CANCELLED`, `NEUTRAL` and an empty conclusion are all "not FAILURE" — and **GitHub
counts a required context that REPORTED one of them as SATISFIED**. A PR can therefore merge through
a required gate that never ran, with a full wall of ticks.

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

### 🚨 SKIPPED and ABSENT are NOT the same reading — and they fail in OPPOSITE directions

The sentence above used to say "skipped **or absent**". That is wrong, and the two halves are not
even the same kind of mistake:

| the required context… | classic protection | ruleset |
|---|---|---|
| **reported** `SKIPPED` / `NEUTRAL` / `CANCELLED` | **satisfied** — merges | **satisfied** — merges |
| was **never reported at all** (no check-run, no commit status) | **BLOCKS, forever** | satisfied — merges |

A skipped context is a check-run that *exists* and carries a non-failure conclusion; GitHub has an
answer and accepts it. A context that was never published has no row at all, and classic protection
renders it *"Expected — Waiting for status to be reported"* and refuses the merge until the end of
time. Nothing retries it, because nothing is going to publish it.

**Measured 2026-09-08, `MeshWeaver.Plugins#1453`.** That PR adopts the shared `node-repo-validate`
lane, which renames the job's context from `Validate node repos` to `validate / Validate node
repos` and splits the repo-specific half out as `Repo policy gates`. Its run
[`34191771207`](https://github.com/Systemorph/MeshWeaver.Plugins/actions/runs/34191771207) is
`success` across **all 106 jobs**, `validate / Validate node repos` included, and the PR still reads:

```
mergeable_state: blocked
required contexts (classic, MeshWeaver.Plugins):
  Validate node repos                            <- NO check-run, NO commit status  ← the block
  Compile every NodeType (vs core)               success
  Build + test the portal hosts                  success
  Module bundles / All selected bundles built    success
  Module bundles (floor) / …                     success
```

Confound ruled out before concluding: `required_pull_request_reviews` is `null`, `strict` is
`false`, and the repo's one ruleset (`Copilot review for default branch`) carries only `deletion`,
`non_fast_forward` and `copilot_code_review` — none of which blocks a merge. A required status check
is the only thing left that can.

**Why it matters in both directions.** Read as *"absent counts as satisfied"*, a repo that has
quietly stopped publishing a gate looks safe — that is the failure the rest of this page is about,
and it is real for a **ruleset** repo. Read the same way in a **classic-protection** repo, a PR that
is structurally stuck looks merely slow, and someone waits days for a run that can never appear. The
cure for the second is a branch-protection edit, never patience.

🚨 **Adopting a reusable lane is exactly this event**, which is why AGENTS.md says the context rename
happens *in the same change*. Sequence it deliberately: dropping the old context and adding the new
ones in one step blocks **every** open PR in that repo that has not yet merged the adopting change,
because none of them publishes the new names. Dropping the old name first, merging the adopting PR,
then adding the new names is the order that blocks nobody — and the gap between step two and step
three is a window in which the gate is advisory, so it is a step to finish, not to leave.

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
| **Required, and it REPORTED skipped/neutral/cancelled** | **does not block** — GitHub treats it as satisfied |
| **Required, and never reported at all** | **blocks forever** under classic protection; does **not** block under a ruleset — see *SKIPPED and ABSENT are NOT the same reading* above |
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

## 🚨 A narrow instrument does not answer a wide question — and it is RIGHT while you misread it

Most rules on this page are instances of one shape, and it is worth naming because the instances
keep arriving in new costumes. **In every case below the instrument was working correctly.** There
was no bug to find, no error to notice, and no amount of care would have helped: the *reading* was
wrong, not the measurement. That is exactly why this class needs a control rather than diligence.

Five measured in one session (2026-09-12, while triaging #890 and #2543), by **two different
readers**. This is not one person's blind spot:

| the instrument | what it actually answers | what it was read as | what falsified it |
|---|---|---|---|
| `pendingWork=0` on **one** `[STALE-CALLBACK]` record | the pool's depth at that instant, for that correlation | *"the pool is idle"* — a live starved-pool hypothesis declared dead | max `pendingWork` in the **same log** is 606; across the 15 jobs, **2,501** |
| a `GATE FAILED — tests:` filter | how many failures matched **that stage** | *"8 occurrences"* | all 15 carry the same 120 s bound and the same `CreateOrUpdateNodeRequest@portal/nodeops` stall trail — **15**; the filter set the count, not the phenomenon |
| `gh api … 2>/dev/null` across eight repos | whatever survived a **discarded** stderr | *"0 issues, 0 PRs — every repo clean"* | every call was being refused `403`; a total refusal read as a clean sweep |
| `gh api rate_limit` → `core: 5000/5000` | the **PRIMARY** quota | *"not rate limited"* | the refusals were the **SECONDARY** limit, which that endpoint does not report |
| `gh api /apps/<slug>` permissions | what the App **declared** | *"the App can write issues and workflows"* | the **installation** carries `contents`, `metadata`, `pull_requests` only — a token minted from it can do neither |

### The test to apply before you publish a claim

**Name the question the instrument actually answers, then say why that is the same as the question
you asked.** Where the two differ, either widen the instrument or narrow the claim. Both are cheap.
Publishing the gap is not — each row above was repaired only after someone else re-measured.

### The tell, per family

- **A spot value standing in for a distribution.** State the **maximum and the count**, never the
  sample you happened to read. One record describes a moment; a hypothesis about a process needs the
  shape of the whole series.
- **A filtered count standing in for an occurrence count.** State the **filter** beside the number.
  *"8 matched `tests:`"* and *"8 occurred"* are different sentences, and only the first is a
  measurement.
- **A suppressed error standing in for a measurement.** `2>/dev/null` turns a refusal into a zero.
  **Check the exit code**, or do not suppress. A zero that cannot distinguish *"none"* from *"could
  not ask"* is not a result.
- **A healthy meter standing in for the thing that actually refused.** Read the **refusal**, not the
  meter. `5000/5000 remaining` while every call is refused is the documented signature of the
  secondary limit — the meter is honest and answering a different question.
- **A declaration standing in for an effective capability.** An App's own page lists what it *asked
  for*; the **installation** lists what it was *granted*. Measured 2026-09-12: `meshweaver-cloud`
  declares `contents, emails, issues, metadata, pull_requests, workflows`, and its installation on
  this org carries `contents, metadata, pull_requests` — so `issues`, `workflows` and `emails` are
  declared and absent. Read `orgs/<org>/installations`, not `/apps/<slug>`. This one is not a CI
  instrument at all, which is the point: the shape is about how an answer is read, not about CI.

### Why a control catches this and care does not

A broken instrument announces itself: an exception, a mismatch, a number that cannot be right. A
narrow one does none of that — it returns a true value, promptly, in the expected shape. The only
thing that separates *"what I measured"* from *"what I claimed"* is an artefact that would have come
out differently had the claim been false, so the discipline is the same one this repository applies
to gates: **an assertion that cannot fail is not an assertion.** Before a number becomes a verdict,
say aloud what reading would have refuted it, and go and look for that reading.

Two neighbouring pages carry the same lesson from other directions:
[Adoption and the Sweep Count Different Things](/Doc/Architecture/AdoptionAndTheSweepCountDifferentThings)
(two instruments, neither wrong, neither a census) and
[The Release Gate's Denominator](/Doc/Architecture/ReleaseGateDenominator) (a rate is meaningless
until you state what it is over).

## The same trap in the tools you write to watch CI

Two bugs that make a monitor lie, both hit in one session:

- **`jq`'s `//` does not fall through on `""`.** Only `null` and `false` trigger it, and an empty
  string is truthy — so `.conclusion // .status` yields `""` for a queued check, and "not yet run"
  becomes indistinguishable from "no failure".
- **An empty or partial rollup is vacuously green.** "No failures and nothing incomplete" is *true*
  of a PR with zero checks. Decide readiness by asserting the **required set is present and
  SUCCESS**, never by the absence of failures.

## A green gate can be answering with evidence it did not produce

The traps above are about a check that never *ran*. This one is worse: the check runs, does its
accounting, and reports SUCCESS **from another job's evidence**.

**Artifacts are RUN-wide, not call-wide.** A reusable workflow that a repo invokes twice in one run
— `MeshWeaver.Plugins` calls `node-repo-module-pack.yml` as both `modules-floor` and `modules-rest`,
on every run — puts both calls' artifacts in ONE namespace. Two uploads of the same name are
*accepted*: Plugins run `33487032213` carried two artifacts literally named `workspace-build`
(25.8 MB and 950 kB), and `download-artifact` resolved the name to one of them. The floor's pack job
read the other call's workspace and died on *"the global build wrote no closure manifest for
MeshWeaver.Markdown.Collaboration"* — a REQUIRED gate flipping green/red on `main` with **no source
change**, alternating by which call won the name (Plugins#1077).

**A discriminator with a shared default is not a discriminator.** The `lane-id` input existed for
exactly this and did not close it, because its default was the same literal for both calls and
neither caller set it. The fix is to *derive* the key rather than trust the caller: `select` now
computes a lane key from the call's own `modules:` matrix — unique per call by construction, with no
input a caller can forget — and every artifact the call drops is named and stamped with it.

**Three rules generalise out of it:**

1. **Name every artifact for the CALL, not the workflow.** If two invocations can coexist in one
   run, the name must carry something that differs between them, derived — not an input someone
   remembers to pass.
2. **Scope the download to match, and check the stamp anyway.** A `pattern: foo-*` glob is one edit
   from being widened back; the producer stamping its lane INTO the file means the consumer can
   still refuse foreign evidence when it is.
3. **An answer a gate composes must fail closed.** `bundles-built` is what a required gate depends
   on, so zero evidence, foreign evidence and evidence that does not record what it attests are all
   *false* — never a silent true. A marker saying "an artifact was uploaded" leaves
   present-but-uncomposable reading as green, so the marker records the bundle it attests **and**
   the closure the build resolved, and the verifier requires both.

🚨 **Preserve the reason the evidence is dropped EARLY.** The built marker lands before the module's
tests deliberately, so a red suite cannot read as "bundle missing" to a gate that only *composes*
bundles (#2710, Plugins#937). Strengthening what the marker claims must not drag test results into
it — "the bundle is complete and usable" and "the module's tests passed" are different questions,
answered by different contexts.

**And the acceptance criterion for a fix in this class is REPEATED green.** A defect that alternates
run to run produces single greens by itself; one green run is what it looks like, not evidence it is
gone.

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

### 🚨 A flake's PASSING re-run is the CONTROL ARM — do not throw it away

When a flake is re-run and goes green on the same commit, you own something CI almost never gives
you: **a controlled experiment.** Same commit, same shard, same job, same runner generation — one
variable, the outcome. It is routinely discarded as "the problem went away".

**Method.** Count each candidate tag inside the FAILING test's own log lines, then inside the
PASSING run's lines for the same test, and compare:

```bash
gh run view <run> --repo Systemorph/MeshWeaver --attempt 1 --log > fail.log   # ← see the trap below
gh run view <run> --repo Systemorph/MeshWeaver --log            > pass.log
for t in "TagA" "TagB"; do
  printf '%-34s fail=%s pass=%s\n' "$t" \
    "$(grep '<TestName>' fail.log | grep -c "$t")" \
    "$(grep '<TestName>' pass.log | grep -c "$t")"
done
```

**Measured 2026-08-31 on the `FutuReAnalysisTest` flake:**

| tag | FAIL (50 s) | PASS (7.7 s) |
|---|---:|---:|
| `$type … NOT registered` | 66 | **122** |
| `is not registered` (the upsert refusal) | 0 | 0 |
| `Dropping StreamEndedEvent` | **8** | 2 |

**The suspected cause was MORE frequent in the run that passed.** A separate issue had inferred that
this flake was "the test-visible face" of *its* defect, on the strength of that shared
`NOT registered` wall. The wall is ambient in that suite — heavier in the PASS — so it cannot be
causal, and one table retired a cross-issue link that had stood for days and would have sent someone
to the wrong subsystem.

**The rule:** a tag present in BOTH windows is ambient. Only a tag whose count moves *with* the
outcome is a candidate; absent-in-pass plus present-in-fail is the shape worth chasing.

🚨 **The trap that makes this fail silently:** `gh run view --job <id> --log` returns the **LATEST
attempt**. After a re-run, that is the transcript that **passed** — same job id, no marker saying so.
Pass `--attempt 1` for the failure. The tell is inside the data: the window ends `[PASS]`, or its
span is far shorter than the reported failure duration. Check the span before analysing anything.

**Corollary:** never re-run a flake and move on without first pulling `--attempt 1`. The failing
window has a retention shelf life, and once it is gone the control arm is worthless — there is
nothing left to compare it against.

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
asserts the mirror matches core — **at a PINNED commit**, recorded in
`clients/react/src/i18n/catalog-source.json`, not core's `main`.

🚨 **READ THAT LAST CLAUSE BEFORE THE ROUTINE BELOW — this page said "matches core `main`" until
2026-09-07, and that is the belief #3596 is about.** The pin changes the signal completely, and in
the direction that hurts: a core catalog change reds **nothing**, anywhere, and the mirror goes
stale in silence until somebody moves the pin. Measured 2026-09-07 — 1372 keys at the pin against
1399 on core's `main`, **27 behind**, and value drift over the 1372 shared keys of **exactly zero**.
That zero is the mechanism: the guard compares values, every shared value matched, both repos green,
and the React/RN clients rendered a raw key in both languages for 27 strings. `AGENTS.md` records
the same shape at 70 keys on 2026-09-04, so it recurs.

**What that means for reading a red.** The loud failure below is the *unpinned* behaviour and it
still describes what you see once the pin MOVES — a mirror sync PR reds every open Plugins PR while
it lands. What it no longer describes is the steady state: between pin moves there is no red at all,
so **absence of an i18n red is not evidence the mirror is current**. Check it, do not infer it:

```bash
gh api repos/Systemorph/MeshWeaver.Plugins/contents/clients/react/src/i18n/catalog-source.json \
  --jq '.content' | base64 -d | jq '{ref, keys}'
```
against `jq 'length' src/MeshWeaver.Messaging.Hub/Localization/strings.en.json` on core `main`.
Core's `i18n catalog (mirror sync handed over)` gate now refuses a catalog change that does not
declare the handover, so the debt is at least recorded where it is created — see
[Localization](/Doc/Architecture/Localization).

**When the pin does move, the old routine applies in full.** Measured 2026-08-29: eleven PRs red at
once, on diffs that could not reach the RN app — a lockfile override, a Store C# change. The guard
is correct; the gap between the two merges is the problem.

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

## 🔗 Reading a red that came from ANOTHER repository

The hardest reds to attribute are the ones a repo did not cause. On 2026-09-01 a single carve-out
wave in core produced **five distinct failures in MeshWeaver.Plugins**, each hiding the next, and
only one of them was an API change:

| # | what broke | what a public-API gate would have said |
|---|---|---|
| 1 | a quoted phrase inside a C# `//` comment parsed as a canonical assembly | green — the API is unchanged |
| 2 | a package's `category` disagreeing with its 11 siblings | green — not an API |
| 3 | moved test suites arrived without their Testcontainers pre-pull | green — not code |
| 4 | pin-vs-source skew: a type moved to a new assembly hours after the pinned image was cut | green — the type exists, just not in *that* image |
| 5 | a type moving assemblies without a forwarder | red — the one shape a surface gate catches |

**So the class is not "core changed its public surface." It is "anything downstream reads out of
core's tree, at a version it does not control."** Prose, package data, CI infrastructure and image
timing all belong to it.

### 🚨 A red with ZERO jobs and ZERO contexts is a `startup_failure` — the workflow file is invalid

Every other red on this page names something: a job to open, a log to read, an annotation on the
commit. **A `startup_failure` names nothing.** GitHub rejected the workflow file before it created a
single job, so the run's job list is empty, it posts no check contexts, and the commit shows an
ordinary red tick with nothing behind it. The run's `conclusion` is `failure` like any other; only
`gh run view <id> --json jobs` distinguishes it, by answering `[]`.

Measured 2026-09-03. MeshWeaver#3225 inserted an input into `node-repo-module-pack.yml` between the
previous input's `required:` line and its `default:` line. The orphaned `default` attached to the
**new** input — a duplicate `default` key — and the input above silently lost its default. GitHub
refuses such a workflow outright. But these are `workflow_call` lanes that five satellite repos pin,
so the break was invisible in core (core does not call them) and surfaced **two repos away**, as
MeshWeaver.Plugins#1268 run `33777768374`: `failure`, zero jobs, zero contexts, on a change that
repo did not make. Attributing it took comparing job counts across two heads in two repos — the
prior head ran 29 jobs, this one ran 0 — and then linting the lane by hand.

**The diagnostic, in order:**

```bash
gh run view <run-id> --repo Systemorph/<repo> --json conclusion,jobs --jq '{conclusion, jobs: (.jobs|length)}'
# {"conclusion":"failure","jobs":0}   ⇒ startup_failure: the WORKFLOW FILE is invalid, not the code
# then, in that repo's tree — actionlint takes FILES, not a directory:
actionlint -shellcheck= -pyflakes= -oneline .github/workflows/*.yml   # names the defect at its line
```

🚨 **And the reason nothing caught it upstream: `yaml.safe_load` accepts a duplicate mapping key and
silently keeps the last one.** #3225 validated the edited lane by parsing it with PyYAML and
asserting the required inputs were unchanged; the parse succeeded, the spec read back correctly, and
the assertion passed. **Any workflow validation built on a permissive YAML load is blind to this
class by construction** — the same shape as every other gate on this page that passes on evidence it
could not produce. Core's `CI's own shell` job now runs `actionlint` over every workflow for exactly
this reason (MeshWeaver#3228), with the embedded shellcheck/pyflakes integrations disabled so the
gate is about structure only; `check-workflow-timeouts.py` and `check-workflow-shell.py` both stay
green on the defect, which is the measurement that says the new check is not redundant.

### 🚨 A red with ZERO STEPS in ~2 s is an org BUDGET refusal wearing a workflow-defect costume

The shape above has a twin that is not a defect at all, and the two are easy to confuse because both
produce a red that names nothing. **When the organisation's GitHub Actions spending limit is
reached, GitHub refuses to START jobs**, and the refusal is dressed as a failure:

| Tell | What you see |
|---|---|
| Duration | jobs fail in **~2 seconds** |
| Steps | the job has **zero steps** — not a failed step, none at all |
| Matrix | matrix job names appear **unexpanded** (the literal `${{ matrix.… }}`, because nothing evaluated them) |
| Logs | `GET /actions/jobs/<id>/logs` answers **`BlobNotFound`** — there is no log, because nothing ran |
| Scope | **several repos at once, in one window**, while a repo whose runs started before the window keeps going green |

**The only place the real reason appears is the job's ANNOTATION**, and nothing in the run's own JSON
says it:

```bash
gh api "repos/Systemorph/<repo>/check-runs/<check-run-id>/annotations" \
  --jq '.[] | "\(.annotation_level): \(.message)"'
# failure: The job was not started because an Actions budget is preventing further use.
```

Measured 2026-09-11 across three repos inside one 18-minute window, with core unaffected.

🚨 **Re-running IS the correct action here, once the window has passed** — and that is the one place
on this page where that is true. Everywhere else a re-run without a code change hides a race
(AGENTS.md → "NEVER re-run a test unless code under test has changed"). This is not a flake and not a
race: it is a **refused gate**, a job that never executed, so there is no observation to preserve and
nothing was measured that a re-run could paper over. The distinction is the same one this whole page
turns on — *"it did not run"* and *"it ran and failed"* are different claims — so **read the
annotation before you re-run**, and if it names no budget, treat the red as real.

### A verdict about an unpinned checkout is a function of wall-clock time

The cross-repo gates check core out with **no `ref:`**. Two people therefore measured the same
downstream branch on the same day and got **opposite answers**, both correct — core's tip had moved
between them. Failure #1 above landed in core at 08:01:19Z; every downstream run before that passed
and every run after it failed, with no downstream commit in between.

**The rule:** record the **core tip** alongside any cross-repo verdict, and resolve it once per run
rather than per checkout step. A verdict without that stamp cannot be reproduced or disputed.

### 🚨 A sweep goes blind when its SUBJECT moves repos — not when its detector breaks

The failure this page keeps naming is *"the sink appeared later than the window"*. There is a second
shape, and it is harder to see because nothing about the detector changes: **the suite that carries
the defect leaves the repository, and the sweep keeps returning a confident, calibrated zero.**

Measured on **#890** (a Roslyn `Emit` that poisons a test host process, so every later NodeType
compile NREs). Swept core CI on 2026-09-02 across 958 `dotnet-test.yml` runs:

| window | occurrences |
|---|---|
| 2026-08-30T00:05Z → 2026-09-01T11:34Z | **11** — 1.76 % of non-cancelled runs |
| 2026-09-01T11:34Z → 2026-09-02T10:27Z (201 runs) | **0** |

The clean tail reads like a fix, and at that rate a 201-run gap is a ~4 % coincidence. It is neither.
**PR #2847 merged at 2026-09-01T14:06:31Z and deleted `MeshWeaver.Hosting.Monolith.Test` and
`MeshWeaver.PluginCatalog.Test` from core**; they now live in `MeshWeaver.Plugins/src/`. Every one of
the 11 occurrences fired inside one of those two suites. The last one is 2 h 35 m before the removal.

Be precise about what left, because "the suite moved" and "the repo is now immune" are different
claims. Core still emits NodeType assemblies in `Compiler.Pipeline.Test` and `Graph.Test` — but those
run in **9 s** and **100 s**, against the **13 m 38 s** of compile-heavy integration work that walked
out. What the removal took was not the possibility, it was **the exposure that made the rate
measurable** — which is enough to make a post-removal null uninformative either way. (An earlier
version of this paragraph justified that with *"the onset in every measured occurrence was ~2 minutes
into such an assembly"*. **That is now falsified** — the 2026-09-03 Plugins occurrence fired **33 s**
in — so the argument rests on suite RUNTIME, which is what the 9 s / 100 s vs 13 m 38 s comparison
actually measures, not on a warm-up threshold.)

**The rule:** before believing a null, ask *"does the code that produces this signal still run in
this repository, in this window?"* Confirm it positively — name a run and the suite verdict line it
printed (`Passed! - … - <Suite>.dll`) — rather than inferring it from the sweep's own silence. A
subject that moved and a defect that stopped are indistinguishable from inside one repo, and
[deleted and relocated look identical too](/Doc/Architecture/CrossRepoPairGate).

### The same fault surfaces through a DIFFERENT sink in each repo

Having found where the suites went, the follow-up sweep in the other repo is not the same command.
Core streams a test's log output into the job log as live `[OUTPUT]` lines — **5 852** of them in the
calibrated #890 occurrence. The equivalent MeshWeaver.Plugins job, running the very same suite,
carries **0**: it dumps `Standard Output Messages:` post-hoc, per *failing* test, and keeps the
`_meshweaver-test-trace.log` phase trace only inside a `teardown-stragglers-*` artifact.

A detector keyed on the framing (`[OUTPUT]`, a project-name-plus-`exit=124` string, a `(part N/M)`
suffix) therefore returns a clean, fast, entirely vacuous zero. **Grep the signature the code emits**
— here `canary=`, `PROCESS CANNOT EMIT`, `GetConsolidatedTypeParameters` — **never the harness's
packaging around it**, and prove the sink is alive in the target repo with a positive control before
reporting the null (17 of 43 Plugins trace artifacts carried real `Compile failure for …` records,
which is what made that null worth stating).

#### Two sinks per repo, and only one of them is complete

**Read both, and know which is authoritative.** A post-hoc `dotnet test` job log carries only the
output of tests that **FAILED** — and the fault you are hunting is often captured by a test that
*passed*. In the calibrated core occurrence the very first canary record is printed under
`CreateLayoutAreaIntegrationTest.CreateArea_WithoutTypeParam_ShowsTypeSelection`, which is **not** one
of that shard's five failures: on a `dotnet test`-driven harness that record would never have reached
the job log at all. The complete sink is the phase trace — `_meshweaver-test-trace.log` inside
`teardown-stragglers-<run>-<attempt>-shard<N>` — which takes an unconditional `[FAULT]` record for
every `ILogger` call carrying an exception at Warning or worse, whatever test was running and whether
or not it failed. (It is collected only when a suite failed, which is why it covers this defect: the
poisoned process always reds its suite.)

**Measured 2026-09-04 over 2026-09-03T06:04Z → 2026-09-04T06:34Z**: 122 non-cancelled
`Plugin Catalog CI` runs, all 58 non-success `Portal hosts (shard N)` job logs fetched (0
unfetchable, 50 of them running the exposure suite) plus 57 trace-log artifacts carrying 13,460
`[FAULT]` records — **one occurrence**, run `33760859754` shard 3, on `main`, and it surfaced in
**both** sinks (56 `canary=BELOW-ROSLYN` and 20 `PROCESS CANNOT EMIT` in the job log; 119 signature
lines and 39 `Compile failure for …` records in the trace). That closes the blind spot the previous
sweep recorded as open — *"I have not proved the canary's log line reaches job-log stdout in a
Plugins shard"* — with a real occurrence rather than an argument.

**Calibrate before believing a null.** The four patterns above, run over the known core occurrence
(job `99288463497`), return **77** signature lines and **38** `canary=BELOW-ROSLYN` — the counts that
occurrence's own report published. A detector that cannot reproduce those numbers is not entitled to
report a zero.

🚨 **One of those four patterns was a guaranteed ZERO on the authoritative sink, and the loudest line
was the least durable one.** The trace log takes a record if and only if
`exception is not null && logLevel >= Warning` (`XUnitFileLogger.Log` → `TestTraceLog.AppendFault`).
`PROCESS CANNOT EMIT (#890)` — the `LogError` whose entire job is *"attribute the failures that
follow to this line, not to the change under test"* — was logged with **no exception object**, so it
could never reach the trace **by construction**, while its quieter `LogWarning` sibling
(`Compile failure for {HubPath}`, which passes the error and carries the whole `canary=`/`dissect=`
verdict in brackets) always did. Measured on both 2026-09-05/06 occurrences:

| pattern | job log | `_meshweaver-test-trace.log` |
|---|---|---|
| `PROCESS CANNOT EMIT` | 25 · 25 | **0 · 0** |
| `canary=` | 78 · 62 | 55 · 28 |
| `Compile failure for` | — | 54 · 31 |

Since the job log carries only FAILED tests' output and this defect's first canary record is
routinely logged under a test that PASSED, an occurrence could reach **neither** sink with its
attribution intact. Fixed by passing the exception (the same thing #612 already did to the sibling
call three lines away), so the record now satisfies the trace sink's gate — the general lesson being
that **for a fault the trace log is the sink, and reaching it is a property of the CALL, not of the
level or the wording**. An `ILogger.LogError` about an exception that does not pass the exception is
invisible there.

🚨 **The same blind spot has a SECOND live instance, and it is the reason "the observable emitted
nothing at all" is undiagnosable.** `LayoutAreaHost` carries two diagnostics written for exactly the
wedge where a layout area never delivers a frame — `COMPLETED WITHOUT RENDERING` (`LogError`) and
`was torn down having never rendered` (`LogWarning`). Both pass **no exception**, so both are
refused by the trace sink's `exception is not null && logLevel >= Warning` gate, and both fire on a
pool thread or during teardown, where `outputHelper` is null / `IsInTestMethod()` is false and the
trx sink is closed too. Measured on the #3413 occurrence (run `34034828870`, shard 5): across all
**450** trx results and the whole 4,050-line trace, `never rendered` → **0** and
`COMPLETED WITHOUT` → **0**. In a `HubTestBase` test the disposal one is `LogDebug` regardless — the
`Hub.IsDisposing` branch (#2679) — and mesh teardown is exactly how such a test disposes its layout
host. So a layout area that renders nothing is silent in **every** sink, which is what made #1081
cost four sessions and what leaves #3413 open.

🚨 **The blind spot's THIRD instance disarmed a GATE, which is a strictly worse outcome than losing
one explanation.** `MeshNodeStreamCache` logs `Content for {Path} stayed an untyped JsonElement`
from both read seams, and `check-untyped-content.sh` reds a shard that finds that phrase in
`collected-logs/`. Both warnings passed **no exception**, so neither could reach
`_meshweaver-test-trace.log`; the only other route into that directory (`*/bin/*/test-logs/*.log`)
is opt-in via `MESHWEAVER_TEST_FILE_LOGS`, which `node-repo-module-pack.yml` sets and
`dotnet-test.yml` does not. Measured on a run that really did degrade content: **817** trace records
naming the test class, **0** occurrences of the phrase it emitted. The gate was therefore
permanently green having matched nothing — indistinguishable from a gate that passed, which is the
one reading AGENTS.md singles out as forbidden.

Its control test did not catch it because it asked the wrong question: it pinned that the source
still *emits* the phrase and that the script still *greps* it — both true throughout — and never
whether the record could **reach the directory the script scans**. *A guard whose subject moved and
whose roots did not passes having checked nothing.*

Two things changed. The warnings now carry `MeshNodeContentDegradedException`, an exception object
constructed and never thrown whose whole job is to satisfy the sink's predicate; and the gate keys
on that **type name** first, with the prose phrase kept only as a second net. A message is a
*description* of an event, the type is the event's *identity*, bound by the compiler at every
construction site — so the coupling now has two independent bindings (a rename is a repo-wide
compile change, **and** `UntypedContentDegradationGate` pins the script's key to `nameof(...)`)
where the phrase had only the one. `UntypedContentDegradationReachesTheTraceSinkTest` supplies the
half that was missing: it drives the production converter and evaluates the sink's own condition
against the captured record.

🚨 **The gate has no allow-list, and a test fixture is where the pressure for one comes from.** A
test that proves a write REFUSES unreadable content has to seed unreadable content, and once these
records reach the sink such a fixture reds its own shard. The answer is the fixture, not an
exemption — an exemption here would be a permanently green check wearing a reason, which is the
state this whole section is about. Model *"present but unreadable as `T`"* the way a running mesh
actually produces it: **seed a value of a DIFFERENT, REGISTERED type.** The stream cache types it
happily, so nothing degrades and nothing is recorded, while `ContentAs<T>` / `As<T>` still answers
`null` — it recovers a foreign runtime type ONLY when the short name matches. That is closer to the
production case (a same-named record from another collectible assembly, a foreign type) than
malformed JSON is, so the fixture gets stronger. Malformed JSON with no resolvable `$type` models a
*different* defect — content nothing can read — which is exactly what the gate exists to report.

**Falsified end to end, exit codes read directly.** Same degradation, same real sink
(`XUnitFileLogger` → `TestTraceLog.AppendFault`), three runs: with the exception argument reverted
the trace file was **never created**, and the gate answered `No content-type degradation` with
**exit 0** — the defect, reproduced; with the fix the record landed in the trace and the gate exited
**1** naming `Space/ARenderedEmptyPage` twice; and against a trace carrying a real, unrelated
`[FAULT]` record the gate exited **0**, so the pass is a verdict rather than an empty scan.

🚨 **What the repaired gate found on its FIRST working run — and the classification that matters.**
A gate that fires on the wrong thing is no better than one that cannot fire, so every occurrence was
classified before anything was changed. Two node paths, two opposite verdicts:

| occurrence | verdict |
|---|---|
| `Ops/Modules/{deployment}` (`Hosting/ModuleInventory`) | **TRUE POSITIVE — a live defect in `src/`.** `DeploymentReportService` stamped `$type = "ModuleInventoryContent"`, a literal naming **no CLR type in the fleet**, while the real record `DeploymentReport` was registered nowhere. Its own comment said the stamp existed because *"content without the discriminator … materialises as NOTHING"* — and it materialised as nothing anyway. Every instance's self-reported module inventory read back untyped. Fixed: the record is registered and the constant is `nameof(DeploymentReport)`. |
| `{partition}/Live` (`LateContentTypeRegistrationTest`) | **A true degradation, but NOT the gate's subject.** That test asserts content *stays* untyped when an unrelated type registers; its `$type` is literally `AContentTypeTheMeshNeverCompiled`. The keying was not at fault — the event really happened — but the gate could not distinguish a degradation that is a test's SUBJECT from one nobody intended, nor a transient one from a final one. The second half is answered by #3645 (see *TRANSIENT from FINAL* below); the first is still answered by capture-and-assert. |

🚨 **The guard on the true positive was asserting the defect.** `AnInstanceReportsWhatItRunsTest`
checked `content.GetProperty("$type") == InventoryContentType` — and a `$type` **property** is only
there to read when the content is raw JSON, i.e. when it has *not* materialised. The assertion
passed *because of* the bug. It now asserts the materialised runtime type.

🚨 **And the obvious replacement assertion also could not fail.** `ContentAs<T>` is the bad-data
TOLERANT accessor: handed a raw `JsonElement` it deserialises anyway, so it answers a
`DeploymentReport` whether or not the discriminator resolved. Measured, not assumed — the
`ContentAs` version passed against the reverted fix. The property that actually breaks is
MATERIALISATION at the read seam (`node.Content is DeploymentReport`), which is what every ordinary
reader does and what the degradation warning is about. **When a diagnostic says a value "reads as
absent", assert the runtime TYPE, never a tolerant accessor.**

🚨 **How a test declares that a degradation is its own SUBJECT — answered, and the answer is not an
allow-list.** The fixture advice above (seed a registered foreign type) covers the case where a test
only needs *"unreadable as `T`"*; it fixed `UnreadablePolicyRecordIsNotClobberedTest`, 8 records → 0.
It cannot cover a test whose assertion IS that **nothing can resolve the content**.
`LateContentTypeRegistrationTest` is that case three times over: #2952 is about a NodeType compiled
at RUNTIME into a collectible assembly, so *"registered nowhere yet"* is the premise, and seeding a
resolvable type deletes the subject. Measured on the repaired gate's second working run: that one
class put **3** records into the shared trace and red shard 4; nothing else in core did.

| candidate | verdict |
|---|---|
| An allow-list in the gate | **Refused.** An exemption in the scanner is a permanently green check wearing a reason — and these node paths carry a per-run GUID, so the entry would have to be a *pattern*, i.e. an exemption that widens by itself. |
| Silencing the emitter's category for that test (a level, an `appsettings`) | **Refused twice over.** AGENTS.md forbids dialling a log level for a CI reason, and a suppression matches *nothing* exactly as happily as it matches its subject. |
| **Capture the record in the test and ASSERT it** | **Held.** |

**What it is.** The emitter takes its logger from DI, so a test substitutes the CLOSED
`ILogger<MeshNodeStreamCache>` for its own mesh: a decorator that takes records carrying
`MeshNodeContentDegradedException` and forwards everything else untouched. That is the same move
`UntypedContentDegradationReachesTheTraceSinkTest` already makes one test over — the only new thing
is doing it for a whole mesh instead of a direct call.

**Why it is not a skip-trapdoor**, which is the only question that matters:

1. **Nothing is silenced.** No level is dialled and no category is muted; every other record the
   cache logs reaches the real logger at its own level.
2. 🚨 **The diversion IS an assertion.** Each test asserts that the record it declared as its
   subject *was produced*, that it names the expected node, and that it **satisfies the trace
   sink's own predicate** (`exception is not null && level >= Warning`). A declaration that matches
   nothing is therefore a RED — the one property an allow-list can never have. It is also strictly
   more than these tests asserted before, when the record went to a file nobody read.
3. **The exemption is exactly one node wide.** The same assertion pins that *everything* captured
   names the declared path, so an unintended degradation of any other node in that mesh fails the
   test rather than being swallowed.
4. `UntypedContentDegradationGate.ADivertedDegradationIsAssertedWhereItIsDiverted` is the control
   arm: a test that substitutes that logger without asserting what it caught fails the build, and
   the guard reds if it ever examines **zero** files.

**Falsified, not assumed.** With the `MeshNodeContentDegradedException` argument reverted at the
`GetStream` seam — the #3625 defect itself — all three tests FAIL on *"the platform must REPORT the
degradation this test reproduces"*. So the regression is now caught by a **test**, not only by a
gate that must first see a whole shard's logs. With the fix in place: 3/3 pass, marker count in the
shared trace **3 → 0**, `check-untyped-content.sh` **exit 1 → exit 0**.

🚨 **TRANSIENT from FINAL — the gate's denominator, answered by #3645.** `MeshNodeStreamCache` warns
at the instant of a read, and at that instant it cannot know the type will register moments later —
which is the normal state during portal boot, before the NodeType compiles land, and is exactly the
race #2952 fixed by re-typing every live reader when the registration arrives. The sibling seam one
layer down already said so in its own message: `MeshNodeTypeSource` prints **both** causes — *"(a)
the NodeType's runtime compile has not registered it YET, which is TRANSIENT … (b) no declaration
will ever claim this discriminator"* — and it passes **no exception**, so it never reaches the trace
file and reds nothing. One event, two descriptions, opposite CI consequences: the cache's was a
shard failure, the type source's invisible. Two of `LateContentTypeRegistrationTest`'s three cases
are the transient one, and they degrade *and recover* inside a single test — so a gate hit meant
*"content was unreadable at a read"*, never *"content is unreadable"*.

**What closed it was not a window; it was asking again.** The platform already kept the state
(`ContentDegradationRegistry`, one entry per node type, added for `/health`), and the content-type
registry already answers *"is this resolvable"* as a pure map lookup by either of the two routes
`TryRecoverForNodeType` takes. So the verdict is computed by **re-asking at report time**:
`ContentDegradationRegistry.Unresolved(registry)` drops every entry whose type has since
registered, and `MeshNodeStreamCache.Dispose` logs one `MeshNodeContentUnresolvedException` per
entry that survives. `check-untyped-content.sh` keys on **that** type now, not on the per-read
`MeshNodeContentDegradedException`.

| record | when | meaning | reds a shard? |
|---|---|---|---|
| `MeshNodeContentDegradedException` | at each degraded read | *this read was untyped* — an EVENT, possibly the boot race | **no** (since #3645) |
| `MeshNodeContentUnresolvedException` | once per node type, at cache teardown | *the registry was re-asked and still says no* — a VERDICT | **yes** |

So a boot that reads a runtime-compiled NodeType before its compile lands now produces **no** gate
hit, while a discriminator nothing will ever claim produces exactly one, naming the node type and
the discriminator. The per-read records stay in the logs and stay useful: once the verdict names a
type, they are how you find which reads degraded on it.

🚨 `/health` was wrong the same way and is fixed in the same change: `ContentTypeHealthCheck` fed
`Snapshot()`, so a replica stayed `Degraded` after a boot race until some later read of that type
happened to clear the entry — a probe answering about the past. It reads `Unresolved(...)` now.

**Falsified in both directions, as the issue asked.** With `Unresolved` reduced to `Snapshot()`, the
recovered case is reported and `ARecoveredDegradation_IsNotTheVerdict` fails; with the teardown
report removed, `AnUnrecoverableDegradation_IsTheVerdict_AndReachesTheSink` fails on *"the teardown
must REPORT what never resolved"*. `UntypedContentDegradationGate` pins the script's key to
`nameof(MeshNodeContentUnresolvedException)`, that the emitter constructs it, that the report is
computed from `Unresolved(`, and that both read seams still emit the per-read record the verdict is
derived from.

**The instrument that does answer it** is `MessageTrace`
(`MeshWeaver.Messaging.Hub/MessageService.cs`): `MESHWEAVER_MSG_TRACE=1` makes every delivery write
a `ROUTED` / `DEFERRED gates=[…]` / `GATE_FAILED` / `DROPPED_GATE_STUCK` line to
`Path.Combine(Path.GetTempPath(), "meshweaver-msg-trace.log")` — enough to say whether the sync
sub-hub processed its
`InitializeHubRequest`, whether `PushRenderResult` ever posted an `UpdateStreamRequest`, and whether
anything was deferred. It is off by default (a lock + file append per message). Both
`node-repo-module-pack.yml` **and** `dotnet-test.yml` now copy the file into the shard artifact when
it exists, so turning it on for a run is a one-variable change and needs no workflow edit.

🚨 **Write `Path.GetTempPath()`, never `$TMPDIR`, when you say where it lands.** They coincide on the
runners today and are not the same thing: on Unix `GetTempPath()` returns `$TMPDIR` *when that is
set* and `/tmp` when it is not, and on Windows it is `%TEMP%`. The collector derives the directory
the same way (`"${TMPDIR:-/tmp}"`) rather than hard-coding `/tmp`, because a literal would work by
the coincidence that GitHub's ubuntu images leave `TMPDIR` unset — and on the day something sets it,
the `-f` test answers false, the copy is skipped, and the step stays **green having collected
nothing**. That is the failure class this whole instrument exists to expose, so it must not be the
shape of the collector.

🚨 **And know which questions the sinks cannot answer.** *Neither* Plugins sink carries a compile
**success**: `Compile success for …` is `LogInformation`, so the trace log (faults only) never sees
it and the post-hoc job log never sees it either unless the test that logged it failed. Core's live
`[OUTPUT]` stream does carry it — which is why the "**none** succeeded" half of the
[total-and-permanent measurement](/Doc/Architecture/NodeTypeCompilation) is stated for the core
occurrence and **not** for the Plugins one. Asserting it there would be inferring an absence from a
sink that never records the presence. Before writing "and X never happened", check that the sink you
read would have shown X happening.

### A grep hit is not a binder

Twice in one day, prose was mistaken for the thing it described. A quoted phrase in a `//` comment
became a demanded assembly; and two types looked *bound* by exactly one caller each — one a doc
comment naming an example subscriber, the other a markdown page that `MeshWeaver.Documentation.dll`
embeds as a **resource**, so it appears in a *binary* grep.

**Before concluding a symbol is used, declared or bound, classify each hit.** Strip comments in the
language actually being parsed, not another language's — a `<!--.*?-->` stripper is a complete no-op
on C#, and that is precisely how a comment became a canonical assembly.

### 🚨 Mutually blocking PRs — when "one concern per PR" inverts

Two open PRs each failed a **required** context on exactly what the other fixed:

| PR | required check A | required check B |
|---|---|---|
| the pin bump | ❌ the phantom assembly | ✅ fixes the build |
| the parser fix | ✅ | ❌ the build the pin fixes |

Neither could go green alone, so **a correct PR could not land however correct it was.** The house
rule that a pin bump is *"deliberate, in its own PR"* is right for a healthy trunk and inverts the
moment the pin is itself part of the breakage.

**The rule:** when two required contexts each fail on the other's fix, separation is the thing
preventing the repair — merge one branch into the other and say why in the body. Check for this
explicitly before concluding a PR is "just flaky"; the signature is *two* red PRs whose failures are
each other's subject.

### The container lane sees a WIDER namespace surface than any local build

A build that composes its references from the platform image's `/app` gives source a **strictly
wider** namespace surface than a project-referenced build. Core compiles a project against its
`ProjectReference` graph, so a transitive package's extension methods are visible only where
something references it; the container build composes *everything in `/app`*, so every module sees
them whether it references them or not.

The visible consequence is an ambiguity that **cannot be reproduced locally**:

```
CS0121  The call is ambiguous between
        'System.Linq.Enumerable.TakeLast<T>(IEnumerable<T>, int)' and
        'System.Linq.EnumerableEx.TakeLast<T>(IEnumerable<T>, int)'
```

Two extension methods, same signature, same namespace — one from the BCL, one from Ix.NET, which is
a legitimate pinned platform dependency that in-mesh source uses deliberately. Core never sees it;
the container lane always does.

**The rule:** a green local build is not evidence about the container lane, and "it compiles in core"
is not an argument that a module will compile. When a compile error appears only in that lane,
suspect the reference set's *width* before suspecting the source. And do not "fix" it by pruning the
platform assembly — qualify the call, and only where the receiver's type actually makes it ambiguous
(an `IObservable` receiver resolving through `System.Reactive.Linq` is not).

### Fixing the first red can reveal a second that never ran

A job stops at its first failing step, so every later step reports **SKIPPED** — which reads as
"fine". Repairing failure #1 above let the job reach a step that had never executed on any branch,
and it failed immediately on a real defect. **"Red for a known reason" is the cheapest state in
which to miss a second regression**, and it is an argument for fixing the first red rather than
routing around it. Expect the count of problems to go *up* when you fix one.

### 🔗 A platform break shows up in the DEPENDENT's release-follow run, not on the core pull request

Core emits a `repository_dispatch` to every node repository when it promotes a platform image set;
each dependent builds and tests against that release in ITS repository. A removed member, an
ambiguous overload or a changed envelope therefore reads as a red `repository_dispatch` run in
MeshWeaver.Plugins (or another node repo) shortly after core published — with that repository's
test names — and is fixed by a pull request there. Core carries no context for it by design
(maintainer, 2026-09-03: the integration is event-based; no top-level repository depends on another).

## 🚨 A check that is red on EVERY pull request is not telling you about any of them

A signal carries information only to the extent that it *varies*. A check that fails on every open
PR at the same instant has variance zero: it partitions nothing, exonerates nothing, and the
correct reading of it on any individual PR is "ignore this". That is a strictly worse state than
having no check at all, because the red is still spent — it dilutes every other red on the wall,
and it trains readers to skip the one place they are supposed to look.

**Measured here, 2026-09-01/02.** `auto-arm.yml` asserted that the `meshweaver-cloud` App could
mint a token carrying `Contents: write` + `Pull requests: write`. The grant was missing, so
`Arm auto-merge on this PR` failed — on **every PR in the repository, simultaneously and
permanently**. The wall read as four red PRs. Two of them were entirely green on the required
check; the other two had genuine, unrelated shard failures that the noise was actively hiding.

### The discriminator: is the fact a property of the PR, or of the repository?

That is the whole test, and it is mechanical:

| the assertion is about… | where it belongs | what its red means |
|---|---|---|
| this branch's code, tests, build, contracts | a per-PR check | *this* PR is not ready |
| the org installation, a secret, a registry grant, a quota | **one repo-scoped lane** | the *repository* is degraded; every PR is equally affected |

A repository-scoped fact asserted per-PR is duplicated N times and actionable in none of them —
nobody fixes an org installation from a pull request's Checks tab. Hoisting it does not weaken it:
`arm-credential.yml` fails exactly as red, on a schedule, naming the grant and the acceptance step,
in the one place where the fix is the obvious next action.

### This is NOT the skip-trapdoor exemption, and the difference is worth stating precisely

AGENTS.md forbids `continue-on-error` on a gate's input step, because a verification that silently
does not run is indistinguishable from one that passed. `auto-arm.yml` now carries exactly that
`continue-on-error` — and it is not the forbidden shape, because **arming is an action, not a
gate**. It asserts nothing about the pull request. Every required context still runs, `Consolidate
test results` still decides, and the honest per-PR consequence of a missing grant is "this PR was
not armed, merge it by hand" — a lost convenience, not a lost check. Reporting that as a *failure
of the pull request* was a false statement about the pull request.

The load-bearing part is that the assertion was **moved, not deleted**. A tolerance whose companion
assertion is gone *is* the trapdoor, so the two files are coupled by a test:
`ArmedMergeMustTriggerMainsPushLanesGuard.ToleratingAFailedMintRequiresARepoScopedAssertion` fails
the build if `auto-arm.yml` tolerates a failed mint while `arm-credential.yml` is missing, tolerates
its own failure, or loses its schedule. **When you hoist an assertion out of a hot lane, guard the
hoist** — otherwise the next person sees only the tolerance and reasonably concludes the check was
abandoned.

### Before you conclude "all the PRs are red"

Read *which* context is red, not the rollup colour. The required check is the only one that gates,
and a non-required red that is identical everywhere is the signature of a repo-scoped fact in the
wrong place:

```bash
gh api graphql -f query='query{repository(owner:"Systemorph",name:"MeshWeaver"){
  pullRequests(states:OPEN,first:20){nodes{number mergeStateStatus
    commits(last:1){nodes{commit{statusCheckRollup{contexts(first:80){
      nodes{... on CheckRun{name conclusion}}}}}}}}}}}'
```

If the same check name appears in every PR's failure list, stop triaging PRs and go fix the
repository. `UNSTABLE` means the required set passed and something non-required did not — it is
mergeable, and it is the state a hoistable assertion leaves behind.

### 🚨 The third case: the fact is repo-scoped, but the FIX is per-PR

The table above splits facts into "property of the PR" and "property of the repository", and tells
you that for the second kind you should stop triaging PRs and go fix the repository. That is right
about where the *cause* lives and wrong about what *clearing* it takes, whenever the assertion is
written in a workflow file — because **a gate defined in `.github/workflows/*.yml` evaluates the
version of that file in the pull request's own tree, never `main`'s.** Landing the fix on `main`
changes nothing for anything already open.

Measured twice on 2026-09-07, on two gates with nothing else in common:

**The platform staleness gate.** Three sessions concurrently held the belief that merging the pin
pull request would unblock `MeshWeaver.Plugins`. It would not have: 19 open PRs each carried their
own stale `MW_PLATFORM_REF`, and the gate read each PR's own copy. The remedy was to merge the pin
branch **into all 19**, then verify per branch. A pin move on `main` unblocks exactly zero open PRs.

**The Dependabot secret preflight — the more dangerous shape.** `MeshWeaver.Crm` added its
`MW_REGISTRY_KEY` assertion to the preflight on `main` at 12:45Z. Its five open Dependabot PRs carry
`ci.yml` from before that:

```bash
gh api "repos/Systemorph/MeshWeaver.Crm/contents/.github/workflows/ci.yml?ref=$(
  gh api repos/Systemorph/MeshWeaver.Crm/pulls/59 --jq .head.sha)" --jq .content \
  | base64 -d | grep -c 'MW_REGISTRY_KEY:-'
# 0   ← on main this reads 1
```

So `Required CI inputs` **ran and passed, having never asked about that secret**, and the run died
forty seconds later inside the consuming job with a message that names no secret at all:

```
##[error]compose-sealed-modules.sh: --registry-url needs --registry-key
```

A green preflight reads as *"the inputs are present"*. Here it meant *"this tree's preflight asked
for less"* — and a reader who trusts it concludes the repository is provisioned when it is not.
This is the concrete form of the rule AGENTS.md already states as **the preflight list is not the
denominator**: prove a secret exists from the job that consumes it, never from a preflight's colour.

### The discriminator, and the measurement

| question | how to answer it |
|---|---|
| is the gate's *verdict* stale? | read the gate's **definition at the PR's head sha**, not at `main` |
| will merging the fix help the open PRs? | only if you merge it **into** them — test `git merge-base --is-ancestor <fix> origin/<branch>` per branch |
| did the input actually get provisioned? | read the **consuming** job, not the preflight |

```bash
# the gate's definition as THIS pull request will run it
sha=$(gh api repos/Systemorph/<repo>/pulls/<n> --jq .head.sha)      # full 40 chars, always
gh api "repos/Systemorph/<repo>/contents/.github/workflows/ci.yml?ref=$sha" --jq .content | base64 -d
```

**The rule.** A workflow-defined gate is versioned with the branch it judges, so its fix propagates
like code, not like configuration. Before announcing that a gate fix unblocks anything, name the
branches it reaches — and reach them.

### Reading the wall over REST

The GraphQL query above answers the question, but AGENTS.md reserves GraphQL for what REST cannot
express, because the **secondary** limit it exhausts takes GitHub access away from every concurrent
agent while `/rate_limit` still reads full. The same wall over REST, one PR at a time, pacing:

```bash
gh api "repos/Systemorph/<repo>/pulls?state=open&per_page=100" --jq '.[].number' |
while read -r n; do
  sha=$(gh api "repos/Systemorph/<repo>/pulls/$n" --jq .head.sha)   # full sha; an abbreviated
  sleep 2                                                           # one silently matches NOTHING
  gh api "repos/Systemorph/<repo>/actions/runs?head_sha=$sha&per_page=100" \
    --jq --arg n "$n" '.workflow_runs[]|select(.conclusion!="success" and .conclusion!=null)
                       |"#\($n) \(.name): \(.conclusion)"'
  sleep 2
done
```

🚨 `head_sha=` matches only the **full 40-character** sha. An abbreviated one does not error — it
returns an empty `workflow_runs` array, which is byte-identical to "this commit has no runs" and
reads as *"CI never fired"*. That false negative is about the one thing a PR watcher exists to
detect, so print the sha's length before concluding anything from a zero.

## 🚨 A lane hand-copied into N repos is N lanes, and N−1 of them are stale

The arm lane is a single file, `.github/workflows/auto-arm.yml` in this repository, and every
satellite reaches it through `workflow_call`:

```yaml
# MeshWeaver.<Satellite>/.github/workflows/auto-arm.yml — the whole file
on:
  pull_request_target:
    types: [opened, reopened, ready_for_review, synchronize]
permissions:
  contents: write
  pull-requests: write
jobs:
  arm:
    uses: Systemorph/MeshWeaver/.github/workflows/auto-arm.yml@<sha>
    secrets:
      MESHWEAVER_APP_ID: ${{ secrets.MESHWEAVER_APP_ID }}
      MESHWEAVER_APP_PRIVATE_KEY: ${{ secrets.MESHWEAVER_APP_PRIVATE_KEY }}
```

It was not always. Until 2026-09-02 the file was hand-copied into every repo in the fleet with a
comment at the top asserting the copies were identical, and **they were not** — a comment claiming
"the single implementation, so they cannot drift" is a hypothesis, and this one was false in three
separate ways at once. Core had been moved to a minted App installation token; every satellite copy
was still arming with `secrets.GITHUB_TOKEN`. Only `.Crm` carried the `landed:` read-back branch.
Some copies had no `timeout-minutes` at all. (Figures below are a record of that day's measurement,
not a live inventory of the fleet — the roster is read with
`gh search code --owner Systemorph "auto-arm.yml@"`, never from prose.)

**The consequence is the #2916 outage, running unnoticed in every satellite.** An auto-merge is
performed as the identity that armed it; a push created with `GITHUB_TOKEN` does not trigger
workflow runs; so each satellite's `main` was accumulating merges that started nothing.
MeshWeaver.Reinsurance's `main` had no `push`-event run after 09:11Z that day, while PRs merged all
afternoon. Nothing was red anywhere, because the evidence that would have been red is precisely the
run that never existed.

**How to see it — ask main whether its last commits produced runs, not whether the runs passed:**

```bash
gh api repos/Systemorph/<repo>/actions/runs --jq \
  '[.workflow_runs[] | select(.event=="push" and .head_branch=="main")][0]
   | "\(.created_at)  \(.name)  \(.conclusion)"'
gh api repos/Systemorph/<repo>/commits/main --jq '.commit.committer.date'
```

A last-push-run timestamp older than main's HEAD commit is the signature. `github-actions[bot]` as
the merging identity on recent merges is the cause. Neither is visible from any pull request.

**The rule this generalises to** is already in AGENTS.md and `/ci`: a satellite's CI *calls* this
repository's reusable workflows, it does not copy them. A copied lane costs nothing on the day it
is copied and diverges silently forever after — and the divergence is invisible from inside any one
repo, because each copy is self-consistent. Only a fleet-wide read finds it.

## 🚨 Delivery can stop for hours with every dashboard green — look for CANCELLED, not failed

A cancelled run is not a failed run, and nothing alerts on it. `alert-on-failure` keys on failure;
the delivery verdict never runs; the required check on main is green because the *tests* passed. The
only symptom is a registry that stops receiving digests, which nobody watches minute to minute.

**Measured 2026-09-02.** `main-cd` promoted nothing between 03:33:50 and 07:50 — over four hours —
while the repository looked entirely healthy. In one ten-minute window, six CD runs were created and
**every one was cancelled**:

```
07:35  PR fix/arm-red-is-repo-scoped           -> CD run, cancelled
07:37  PR ci/hard-cap-every-job-at-45-minutes  -> CD run, cancelled
07:39  main push                               -> CD run, cancelled   <- real delivery
07:40  PR fix/oversized-pod-hub-delivery       -> CD run, cancelled
07:41  PR fix/3022-identity-fork-remainder     -> CD run, cancelled
07:43  main push                               -> CD run, cancelled   <- real delivery
```

### The mechanism: an unfiltered `workflow_run` plus a shared concurrency group

`main-cd` triggers on `workflow_run` of *MeshWeaver Build and Test*. That fires on completions from
**every branch**, pull-request builds included. All of those runs key into the same `concurrency`
group, and GitHub keeps exactly **one pending run per group** — each arrival cancels the pending one.
So a genuine push-path delivery run, sitting pending behind whatever is in flight, is evicted by the
next PR build that happens to finish.

**The failure scales with pull-request throughput.** Delivery stops hardest exactly when the repo is
busiest, and it presents as "CD is slow today". Draining a PR queue makes it strictly worse, which
inverts the usual intuition that merging more is progress.

### Why this survives review, and why the run list actively misleads

For a `workflow_run` event, the resulting run's `head_branch` is the **workflow file's** ref — always
the default branch. A CD run started by a PR build therefore reports `head_branch=main`, exactly like
a real one. Querying the CD runs *confirms* the wrong hypothesis. The triggering branch survives in
only one place: the trigger's own `branches:` filter.

To see what actually started them, list the *triggering* workflow's runs in the window instead:

```bash
gh run list --repo <owner>/<repo> --workflow "MeshWeaver Build and Test" --limit 20   --json headBranch,event,conclusion,updatedAt   --jq '.[] | select(.updatedAt >= "<from>" and .updatedAt <= "<to>") |
        "\(.updatedAt[11:19]) \(.headBranch) \(.event)"'
```

### The rule

**A `workflow_run` trigger on a workflow that declares a `concurrency` group MUST carry
`branches:`.** Without the concurrency group an unfiltered trigger is usually harmless and sometimes
intended — `retry-known-transients.yml` deliberately retries transients on PR builds and evicts
nothing. The two together are what makes off-branch triggers destructive.

Guarded by `WorkflowRunTriggerBranchFilterGuard`, which carries a control arm: if its block matcher
ever stops recognising `workflow_run:`, it fails rather than passing having examined nothing.

### 🚨 A CD run cancelled with ZERO jobs was evicted from the pending slot — the delivery it superseded is the run still in flight

The section above is the *unfiltered* trigger, fixed on 2026-09-02. What remains — by design — is
GitHub's own rule inside the filtered group: **one run executes, one waits, and every further
arrival REPLACES the one waiting.** The replaced run reports `cancelled` with `run_attempt: 1` and
**zero jobs**; the run in flight is untouched (`cancel-in-progress: false`). Read as "CD is being
killed", this shape has now held a roll twice — 2026-08-30 on Build and Test (#2412, where it WAS a
defect and got a per-commit group) and 2026-09-08 on `main-cd` (where it is the design) — each time
while the run that mattered was executing normally.

**Measured 2026-09-08, every `main-cd` run created between 09:00Z and 10:09Z** (REST:
`actions/runs/<id>` for the timestamps and attempt, `…/runs/<id>/jobs` → `total_count`):

| run | commit | conclusion | jobs | cancelled at | next arrival, created |
|---|---|---|---|---|---|
| 34207602172 | 3ee8dda6e | cancelled | 0 | 09:01:46 | 34207717540 @ 09:01:45 |
| 34207717540 | 3ee8dda6e | cancelled | 0 | 09:04:10 | 34207938426 @ 09:04:08 |
| 34207938426 | 3ee8dda6e | cancelled | 0 | 09:29:19 | 34210258845 @ 09:29:18 |
| 34210258845 | 4e1a1b633 | cancelled | 0 | 09:33:37 | 34210667593 @ 09:33:36 |
| 34210667593 | 4e1a1b633 | cancelled | 0 | 09:41:59 | 34211432040 @ 09:41:58 |
| 34213195672 | 1c61ed3ae | cancelled | 0 | 10:06:05 | 34213625327 @ 10:06:03 |
| 34213625327 | 6b2fe2a10 | cancelled | 0 | 10:08:46 | 34213872077 @ 10:08:44 |
| 34206854855 | 765fb7f52 | **failure**, at its seal | 25 | — | in flight 08:52–09:45, never cancelled |
| 34211432040 | 60449106f | in progress | > 0 | — | started 09:45:52, the second the run above completed |

Seven cancellations, seven arrivals, each 1–2 s apart, `run_attempt: 1` on every one; the two runs
that were executing ran to their seal. Nothing was starved: the pending slot always held the
**newest** commit, and the commit order on `main` (3ee8dda6e → 4e1a1b633 → 60449106f → 1c61ed3ae →
6b2fe2a10) is the arrival order, so every evicted run was an ancestor of the run that replaced it.
(Three runs for one commit is the merge queue: Build and Test completes on the queue branch and on
`main` for the same sha, and each completion is an arrival — no-ops that still hold the slot,
#2490.)

**The arithmetic under continuous merging.** A run holding the slot seals in roughly 50 minutes
(#8103, 60449106f: created 09:41:58, `completed/success` 10:31:23; the next run, #8108, started its
first job at 10:31:25 — two seconds later, off the group slot, not a runner). In the same window
`main` merged about every seven minutes, so every arrival that landed while a run held the slot was
evicted by the one after it (10:01, 10:06, 10:08, 10:21, 10:38, 10:43 — all `jobs=0`), and only an
arrival that happened to be the LAST before the slot freed got through. The commit that ships is
therefore "newest at the moment the slot frees", and a commit that lands in the middle of a burst
is delivered only as part of a later run's superset — never as its own. **The lever is the merge
gap**, not the workflow: a deliberate merge-quiet window when a specific commit must seal is the
maintainer's call, and cancelling other runs to make room is not a lever at all — the run in flight
is the one doing the delivering.

**The three things a cancelled run can be, told apart from the record alone:**

| cause | `jobs` | timing | corroboration |
|---|---|---|---|
| evicted from the pending slot (this shape) | **0** | `updated_at` = the next arrival's `created_at` ± 2 s | the in-flight run of the same group is untouched |
| the org's Actions budget or job ceiling | > 0, jobs cut mid-flight | no correlation with arrivals; runs in **every** repository of the org stop in the same minutes | a billing banner on the org — and here, `MeshWeaver.Plugins` runs in the same minutes executed normally |
| a session or a person | any | no correlation with arrivals | seven cancellations 1–2 s after seven arrivals is not a hand |

🚨 **The instrument rule — decide "started nothing" from the run's OWN record, never from the list's
`status`.** The list endpoint's `status` is not monotonic: at 10:57Z it reported #8108 and #8109
(both 765cd55e6, created 10:30) as `queued`, which read as "waiting 40 minutes for a runner — the
in-flight slot is starving every seal behind it, the #888 shape by another mechanism". The runs'
own `jobs` endpoint at 11:12Z said 23 jobs, 23 started, 22 completed — 42 minutes of real work
against a 50-minute bake, no starvation, and a retraction. `jobs.total_count`, each job's
`started_at`, and the per-run `conclusion` are the readings; `queued` on a listing is not one.

**What to do with one: nothing.** The commit it would have built is an ancestor of the commit the
slot now holds, and the push lane builds that one next; if the push lane ever falls behind, the
hourly reconcile targets the newest commit CI has vouched for (#3077). A cancelled run with zero
jobs cannot have torn an image set or a publication — it never ran a step. **The conclusion that
matters is the OTHER one in the same list**: a `failure` on a run that DID execute (34206854855
above, red at its seal), which no amount of "the runs keep getting cancelled" explains.

**Why `main-cd` keeps ONE group on the ref while Build and Test gives every main commit its own** —
`MainRunsAreNeverCancelledGuard` holds both shapes, deliberately opposite, each with a control arm
that fires on the other's expression: a test run for a landed commit is evidence nothing else
produces, so evicting it loses the only build of that tree; a delivery run for a superseded commit
produces images nobody will pull and a publication the next run re-seals, and two CD runs
publishing the same framework identity at once is the overlap #3461 describes. So on `main-cd` the
eviction IS the supersede rule, applied by GitHub in arrival order. The one thing GitHub does not
check is ancestry: if Build and Test ever completed out of commit order, an older commit's run
could evict a newer one's — and the reconcile's next tick covers that hour.

### The other direction: a CD red on main that means nothing

The section above is delivery stopping behind green ticks. The inverse cost the same evening: the
hourly `main-cd` reconcile fired **"main `<sha>` is GREEN and has an INCOMPLETE image set, and this
reconcile published nothing — delivery is stuck"** twice inside forty minutes (runs 7963 and 7965,
2026-09-07), on two commits that both sealed shortly afterwards. The set was incomplete because the
reconcile's own `workflow_run` twin was **mid-publish** — a scheduled job reasoning about a state
its sibling was halfway through creating, and reporting the intermediate as a defect.

**A red that means nothing is expensive in the same way a green that means nothing is**: it is
mixed in with reds that mean everything, and people were reading CD conclusions to decide a release
that night. `delivery-verdict` now probes the run list before making the claim and reaches a **third
verdict** — *incomplete because a publication is in flight* — distinct from *incomplete and nobody
is fixing it*; only the second is a defect, and it is still red. Before treating any CD red on main
as an incident, read whether the run named one. Mechanism, and why "skip while a run is in flight"
would have been the wrong fix:
[Continuous Delivery Contract](/Doc/Architecture/ContinuousDeliveryContract).

## 🚨 A queue ejection is not a red on the PR — read the steward's comment, not the PR's checks

With the merge queue on, a pull request's own checks can be entirely green while the PR is *not
landing*: the queue built it on top of the entries ahead of it, that group build failed, and the
entry was ejected. The red lives on a `merge_group` run whose head branch is
`gh-readonly-queue/main/pr-<N>-<sha>`, not on the PR's commit — `gh pr checks` shows nothing.

The merge-queue steward acts on every ejection and leaves a comment saying what it found and did:
re-queued (a catalogued flake, an infrastructure death, a timeout, or a bisect of a multi-PR group),
or left out with the failing assertion, the run URL and the `queue-rejected` label. Read that
comment first. Never re-run the failed queue build and never re-queue by hand — a re-run hides the
bug and destroys the control arm; the steward re-queues on evidence and records the attempt. The
whole protocol is [The Merge Queue](/Doc/Architecture/MergeQueue).

## Related

[The Merge Queue](/Doc/Architecture/MergeQueue) · [Module Versioning](/Doc/Architecture/ModuleVersioning)
· [Modules](/Doc/Architecture/Modules) · [Deploying Plugin Changes](/Doc/Architecture/DeployingPluginChanges)
· [The Dependabot Secret Store](/Doc/Architecture/DependabotSecretStore) — a red that names a secret
you can see provisioned, and why the exemption that would silence it is only safe in core
· [Duplicate Keys in Workflow YAML](/Doc/Architecture/WorkflowDuplicateKeys) — a workflow can
parse, pass every shape gate and run with a value the diff does not show; the loader keeps the LAST
of two identical keys and says nothing
