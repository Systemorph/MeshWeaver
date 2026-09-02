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
out, and the onset in every measured occurrence was ~2 minutes into such an assembly. What the
removal took was not the possibility, it was **the exposure that made the rate measurable** — which
is enough to make a post-removal null uninformative either way.

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

## 🚨 A lane that was hand-copied into seven repos is SEVEN lanes, and six of them are stale

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

It was not always. Until 2026-09-02 the file was hand-copied into all seven repos with a comment at
the top asserting the copies were identical, and **they were not** — a comment claiming "the single
implementation, so they cannot drift" is a hypothesis, and this one was false in three separate
ways at once. Core had been moved to a minted App installation token; every satellite copy was
still arming with `secrets.GITHUB_TOKEN`. Only `.Crm` carried the `landed:` read-back branch. Some
copies had no `timeout-minutes` at all.

**The consequence is the #2916 outage, running unnoticed in six repositories.** An auto-merge is
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
