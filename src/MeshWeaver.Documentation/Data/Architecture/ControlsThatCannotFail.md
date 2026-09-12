---
Name: Controls That Cannot Fail
Category: Architecture
Description: A control whose green is guaranteed by construction is not a control. Fourteen measured instances — a test, a detector, an identity anchor, a preflight, a watcher, a CD verdict, a git idiom that answers the wrong question, a generator whose failure mode is a success line, a guard handed an input that could not fail, a probe whose defect had already been fixed in its sibling method, and two counters over different populations printed as one measurement — and the one question that catches them all.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M8 12h8"/><path d="M12 8v8" opacity="0.25"/></svg>
---

# Controls That Cannot Fail

A control — a test, a gate, a detector, a preflight, a watcher, a status verdict — earns its green by
being **able to go red**. When it cannot, its green stops carrying information, and the failure mode
is the worst one available: *nothing happens*. No alarm, no red tick, no line in a log. The control
silently disappears and the wall stays green.

> **The diagnostic, in two halves. The second is the one that does the work.**
>
> 1. *"If the subject of this check were broken right now, what would this print?"*
> 2. *"And have I run it against a subject I broke on purpose?"*

**Every one of the eight instances below passes the first question by reasoning, and fails the second.**
That is not a coincidence, and it is the reason the first question alone is not enough: reasoning about
a control is done by the person who built it, in the model they built it from, and a control fails
precisely where that model is wrong. Instance 1 is the cleanest proof — its probe was *adversarially
self-defeating*, actively delaying the state it asserted, which no amount of reading it would predict.

So the second half is not a formality after the first. It is the only one of the two that consults
reality.

This is the general form of two rules stated elsewhere as absolutes — a CI gate must never carry a
skip-trapdoor ([Reading CI Signals](/Doc/Architecture/ReadingCiSignals)), and a regression test is only
a pin if it fails against the defect ([Negative Controls](/Doc/Architecture/NegativeControls)). Both are
instances. The family is larger, and it is worth recognising by shape.

## The shapes

| Shape | What it looks like | Why the green is empty |
|---|---|---|
| **Served by the thing under isolation** | a precondition probe satisfied by the transport the test then removes | green before the isolation exists, so it says nothing about after |
| **Never run against a broken subject** | a detector shipped alongside its own fix | it has only been observed passing on healthy code |
| **Asserting existence, not arrival** | a preflight that checks an input is provisioned | provisioned ≠ passed on to the job that needs it |
| **Green-and-wrong** | an anchor that emits a non-blank but meaningless value | red stops the line; a wrong value travels |
| **Blindness rendered as health** | a watcher whose read failure and whose "no matches" look identical | "I cannot see" is reported as "nothing is wrong" |
| **One word covering three states** | `completed/success` over checked-and-passed, never-ran, and ran-and-did-nothing | the verdict is not the outcome |
| **A tool that collapses several failures into one exit code** | `git cat-file -e "$sha:$path" \|\| echo ABSENT` | absent-path, bad-sha, missing-object and bad-quoting all read the same |
| **A measurement that truncates and answers anyway** | a profiler capping at N objects; `\| head -10` on a sorted sweep | the wrong number is *plausible* — and on ordered output the survivors are biased, so a partial view reads as a clean one |
| **An identity field one value covers** | every session commits as the same git author | the field agrees with whichever hypothesis you brought to it |
| **An EXCULPATORY wrong answer** | "it fails on another branch too" ⇒ pre-existing | *not our change* ends the investigation; nobody re-checks a clean bill |
| **A count over a population whose membership varies** | `grep -c "webhook-url:" ci.yml` → `0` across six repos | a zero means "present and omits it" in five and "the thing does not exist" in the sixth |
| **A writer whose failure mode is a SUCCESS line** | a generator that skips its work and prints a summary anyway | nothing distinguishes "wrote it" from "declined to" |
| **An input the OPERATOR supplies** | `--is-ancestor <commit I chose> <candidate>` where the commit chosen is the branch's own tip | the check is sound; the value handed to it cannot make it print anything but PASS |
| **An observer that becomes the load** | several sessions polling the same PRs until the shared credential 403s | the watch removes the ability to observe; `/rate_limit` still reports a healthy quota |
| **A remedy written for reads, applied to a write** | "retry the failed call" after a timeout on `gh pr create` | a lost response is not a lost request; the blind retry creates the second one |
| **Two counters over different populations** | `78 adopted` beside `baked=5`, ten seconds apart | neither is wrong; the units, denominators and sources differ and nothing says so |
| **One contract serving two consumers with opposite needs** | `[]` from a speculative compile: *no squiggles* to an editor, *approved* to a tool rendering `{ok}` | the code is right for one caller, so reviewing it on its own terms confirms it |

## Fourteen measured instances

The first six were found in a single day, across tests, CI, publication and ops. Instances 7 and 8
were found **while writing this page** — one by its author, one by the session that supplied instances
2–6, independently, within an hour of each other. Each is stated with what was *measured*, not with
what was suspected.

### 1. A control served by the transport the test then destroyed

`PodHubTransportTest.CrossSiloNack_ReachesASenderWhoseStreamSubscriptionIsGone` erases a sender's
stream subscription and asserts a NACK still arrives over the *directed* pod-hub transport. Its
precondition probe posted a message and waited for it to arrive — proving **reachability**, which
during that window is served by the **stream it was about to erase**.

Worse, the probe was *adversarial*: it posts from the other silo in a loop, and each failed directed
call mints a throw-away activation there that the owner's next `Attach` must bounce off, restarting
its backoff. The louder the control ran, the longer the asserted condition took to become true.

**Measured:** with one bounce forced and the claim backoff pinned long, the probe goes green in under
a second while `PodHubClaimSettled` has demonstrably not completed — six runs, six times.
[#3298](https://github.com/Systemorph/MeshWeaver/issues/3298), fixed in #3313. Full treatment:
[Negative Controls](/Doc/Architecture/NegativeControls) §4 and
[Orleans Test Routing Pattern](/Doc/Architecture/OrleansTestRoutingPattern).

### 2. A detector that passes with the production fix removed

`NackReachesTheWaiterDuringTeardownTest.OwnerDisposingUnderMeshTeardown_StillAnswersTheWaitingCaller`
blocked two pull requests and dequeued a merge-queue group. Run with both `HierarchicalRouting` call
sites disabled — the shipped fix removed — the suite printed:

```
Failed! - Failed: 1, Passed: 1
```

That `Passed: 1` **is the field detector**, green with the defect fully present, having itself logged
the refusal it exists to complain about. Fixed in #3309 (tests and docs only; the production fix was
#3302).

**Rule:** a detector must be run against a subject you broke *on purpose*, or you have only measured
that it runs.

🚨 **The same rule decides when a ZERO may be banked.** "No occurrences in 73 runs" and "the detector
is dead" are the *same reading* until you have seen the detector fire — this instance wearing an
absence for a hat. So a null is publishable only with its calibration attached: for #890 the counts
were matched against a known occurrence's own published figures (56 `BELOW-ROSLYN` / 20
`PROCESS CANNOT EMIT`) and the exposure suite was confirmed to have run in the window, *before* the
zero was believed. Even then the arithmetic has to clear: at ~1 % per run, P(0 in 73) ≈ 48 %, so that
null is a coin toss and #890 stays open on it. **Refusing to bank a null is only a virtue if the
detector was known to work; otherwise it is the same mistake as banking one.**

### 3. A fix for a red that replaced it with a silent one, in the same edit

This is the entry that explains why the family deserves a page, so read it as one story rather than
two issues.

#3293 fixed a genuine **RED**: the identity anchor was read from the module's own publish output,
which is absent for any module not transitively referencing `MeshWeaver.Compiler`, so the pack
stopped. The same edit passed `-p:Version="$VERSION"` to the replacement build, "to match the module
build's flags". MSBuild writes `Version` into the assembly, so the anchor became per-module:

```
-p:Version=1.3.18 (AI)         → 34337c31960d47f5b5251d32ef923fc6
-p:Version=1.0.24 (Essentials) → 7c1c4f70de084da78659e6bda495e1c5
no override, run A             → 71cc81badb364c5d8558ac5e7db6a44e
no override, run B             → 71cc81badb364c5d8558ac5e7db6a44e   ← identical
```

Four bundles, four identities, one platform. All non-blank, all green, **none matchable by any
consumer**. Fixed in #3306; the class was filed as
[#3308](https://github.com/Systemorph/MeshWeaver/issues/3308).

Three things make this the sharpest instance on the page, and its author states all three plainly:

- **The fix caused it.** This was not a weak check somebody inherited — a correct repair for a red
  introduced a silent failure in the same edit.
- **It was written by someone hunting exactly this defect class that day**, who *noticed* the
  per-module-MVID risk while writing it, reasoned it was pre-existing and out of scope, and shipped
  anyway. Another session measured it hours later.
- **Nothing told anyone.** The red had stopped the pack, so its absence was felt immediately. The
  replacement stopped nothing.

**Rule:** green-and-wrong is worse than red, and that asymmetry is the whole subject of this page — a
red is self-reporting, a plausible wrong value is not. Stated as its author put it: *the fix removed
the symptom and left a better-disguised version of the defect.*

🚨 **The danger scales with how BELIEVABLE the wrong value is, and a measuring tool can supply one.**
Profiling a live 6.8 GB portal pod, `dotnet-gcdump` silently truncated at exactly **10,000,000
objects** and reported a **568 MB** heap. The real figure, from `dumpheap -stat`, was **49,827,806
objects / 5,544,881,838 bytes** — under-reported by a factor of about ten, with no warning of any
kind. And 568 MB is a *perfectly plausible* heap for a portal, so the number does not look wrong; it
looks like an answer. Trusting it produces a confident, wrong conclusion — *the heap is fine, look
elsewhere* — which is strictly worse than no measurement, because it closes the investigation.

Same shape as an anchor emitting a meaningless-but-non-blank value, one layer down: one number
covering *"this is the heap"* and *"this is as much of the heap as the tool chose to walk"*.

🚨 **And it happens in the shell, where the truncation is BIASED toward exactly the answer you were
hoping for.** A sweep for ambient-culture uses in a satellite repo was run as `git grep … | head -10`
and reported **zero** production offenders. The sweep had **19** lines and the real offender was
number **18** — because comments and test files sort early, clustering in doc-heavy and `*.Test`
paths, so the first ten lines were *all* commentary and tests. The truncated view did not look
partial; it looked clean. The same person had been caught by the same shape four hours earlier, in a
different costume: `… | tee list.txt | head -30` let `head` close the pipe, `SIGPIPE` truncated what
`tee` wrote, and a satellite-only package sweep reported **31** where the truth was **49** — with the
tell being that the one package which had just taken CD down was missing from its own sweep.

This is the ordinary case, not an exotic one: `grep`, `git grep`, `ls` and `find` all emit in a sorted
or otherwise structured order, so **a head-truncated view is a systematically unrepresentative
sample, not a random one** — and the bias points toward whichever files sort first, which is usually
the ones you were not looking for. **Count first (`| wc -l`), filter comments and tests INSIDE the
pipeline, and treat `head` as a way to eyeball SHAPE — never as a way to establish ABSENCE.** **The cure
here is not "go break the subject" but a second instrument that can DISAGREE** — which is what caught
it. (A companion from the same session fails the honest way and is worth the contrast: SOS
`dumpobj`/`gcroot` **segfault** on that process, so field-following had to move to ClrMD. A tool that
crashes costs you an hour; a tool that answers plausibly costs you the conclusion.) The measurements
are in [The Portal Heap Is Hubs](/Doc/Architecture/PortalHeapIsHubs).

### 4. A preflight that asserts a credential exists and never passes it on

`main-cd.yml` asserted that `vars.PLATFORM_WEBHOOK_URL` and `secrets.PLATFORM_WEBHOOK_SECRET` were
provisioned — with a comment explaining that without them *"every satellite falls back to its schedule
poll"* — and then called `node-repo-publish-bake.yml` passing **neither**. Every publication sealed
and was never registered. The job's own error read *"provision on the calling repository"*, on a
repository where the variable is set (`len=63`) and the secret exists. Fixed in #3311.

**Rule:** asserting that an input **exists** is not asserting that it **arrives**. Only the second is
what the pipeline depends on.

### 5. Watchers rendering "I cannot see" as "nothing is wrong"

Four successive revisions of a PR watcher, each silent on a state its author had not imagined:

- could not distinguish "queued and building" from "ejected again" — so it reported a pull request as
  merging while it sat rejected for ~15 minutes;
- read latest-`main` while `main` outran CD, so it could never see the run carrying the verification;
- a *queued* run masked the *running* one;
- `gh api --jq` silently rejects `--arg` (`accepts 1 arg(s), received 4`) and returns nothing — which
  reads exactly like "no matching runs".

Only the last was caught quickly, and only because an explicit **blind-detector** — one that refuses
to interpret an empty result — had been added by then. Its author asks that this not read as
foresight: the detector arrived on **revision four**, after three earlier watchers had already misled
both them and their user, one of which reported a pull request as merging while it sat
`queue-rejected` for about fifteen minutes. A remedy that took four attempts is more useful to a
reader than one that looks like instinct.

**Rule:** report *read failure* and *no matches* as distinct states, and give every watcher its own
control arm. A monitor that greps only for the success marker stays silent through a crash.

🚨 **This entry was repeated by the author of this page, against this page, under an hour after
writing it.** A watcher built to follow three pull requests computed `pending = count(status !=
completed)` over the check list and called zero-pending green. On a freshly pushed head the list is
**empty** — no checks created yet — so it reported *ALL CHECKS GREEN* for a pull request whose
required contexts did not exist. Same shape as the four above: an absence rendered as health.

The cure is the one this page already gives for CI verdicts, applied one level in — **evaluate the
required contexts BY NAME**, so a context that is missing reports *not created yet* and can never
read as a passing one:

| state | reported as |
|---|---|
| context absent from the list | `not created yet` |
| context present, not completed | `running` |
| context present, `success` / `skipped` / `neutral` | satisfied |
| the read itself failed | `READ-FAILED` |

**The generalisation is the uncomfortable one:** *the author of an entry is not immune to it, and the
interval between writing it and repeating it can be under an hour.* This defect is not carelessness
and cannot be fixed by care. A control is reasoned about by the person who built it, in the model
they built it from — which is why the second half of this page's diagnostic asks you to go and break
the subject rather than to think harder about it.

🚨 **And a THIRD time, by the same author, four hours later — this one defeats the table above.**
A watcher over three satellite pull requests reported *ALL CHECKS GREEN*. The head carried exactly
two check-runs, `arm / Arm auto-merge` and the Copilot reviewer, both genuinely green; the four
required contexts were absent. The cause was not "not created yet": the workflow had ended in
**`startup_failure`** — a caller passed a reusable workflow an input its pinned lane sha does not
declare, so GitHub rejected the file at load time and created **no job at all**.

```
Social Media Plugins CI   completed/startup_failure   event=pull_request
```

The distinction that matters, and the reason the remedy above is not yet sufficient: a
`startup_failure` produces a check-**suite** whose `latest_check_runs_count` is **0**, and it is
**terminal**. The table's `context absent from the list → not created yet` is the right answer for a
freshly pushed head and the *wrong* answer here — a watcher applying it waits for contexts that will
never arrive, and reports "running" forever. Two absences that look identical in the check-run list
are distinguished only one level up:

| suite state | runs | means |
|---|---|---|
| `queued` / `in_progress` | 0 | not created **yet** — wait |
| `completed` / `startup_failure` | **0** | the workflow never loaded — **terminal, red** |
| `completed` / any conclusion | n | read the runs |

**Rule:** a watcher must read check-**suites** as well as check-runs, and must treat
`startup_failure` as a first-class terminal red. Counting runs cannot see it, and neither can a
required-contexts-by-name check that only knows "absent ⇒ pending".

🚨 The same reading applies to the *pull request's* own summary: `mergeable_state` was `blocked` —
which is correct, and says nothing about why. `blocked` covers "a required context is red", "a
required context has not reported", and "reviews are outstanding". It is the same one-word-covering-
three-states shape as instance 6, on the field a merge decision is most often made from.

### 6. One verdict covering three different outcomes

A green `CD delivered` sat over a **skipped** `bake + seal`, and four consecutive green scheduled CD
runs published nothing. The job list is the undeniable artifact:

```
event=schedule → completed/success
  Promote                            skipped
  pack                               skipped
  bake + seal                        skipped
  Notify platform-update registry    skipped
```

`completed/success` covered all of: *checked and passed*, *never ran*, and *ran and deliberately did
nothing* — indistinguishable without opening that list.

**Rule:** a run's conclusion is not the outcome. Read the job that does the work; see
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) and
[How to tell if CD actually published](/Doc/Architecture/ContinuousDeliveryContract).

### 7. The idiom that checks the check — found while writing this page

The six above were other people's, spread over a day. This one is the page's own author's, made
**inside the verification of a claim about verification**, hours after drafting the entries above. It
is included because a page containing only other people's mistakes is easy to read and easy to
dismiss.

A dependent repository pins the platform at a commit, so "can this dependent use the new API?" is
answered by asking whether the file exists **at the pin**. The check used was:

```bash
git cat-file -e "$sha:$path" && echo PRESENT || echo ABSENT
```

It reported `ABSENT`, correctly, and that verdict was published. Challenged later, the same idiom was
re-run and reported **`PRESENT`** — a shell-quoting artifact, not a fact — which for one turn
persuaded its author that their original, correct measurement had been wrong. Three worktrees and
`git ls-tree` then agreed the file was genuinely absent.

`git cat-file -e` exits non-zero for an absent path, a bad sha, an object not in the local store, and
a malformed argument, **all identically**. That is exactly shape 6 — one word covering several states
— one layer down, in the toolchain rather than in CI. The remedy is a command whose two answers cannot
be confused:

```bash
git ls-tree --name-only "$sha" -- "$path"   # prints the path, or prints nothing
```

**Two lessons, and the second is the general one.**

- A verdict of the form *"blocked at the pin"* is true only of **the sha it was measured against**.
  The original measurement here was correct and then **expired silently** when the pin moved a few
  hours later, with nothing anywhere announcing it. Name the sha in the verdict, and re-measure before
  repeating it — including when repeating it to yourself.
- When two measurements of the same thing disagree, the first question is **"what changed between
  them?"**, not "which mechanism explains the discrepancy?" A mechanism invented to explain a
  disagreement is a story, and a plausible story is harder to dislodge than a wrong number. Here the
  first reviewer's proposed mechanism — that the wrong file had been read — was itself wrong; the
  values had merely coincided until a pin bump separated them.

### 8. A writer whose failure mode is a success line

Found the same evening as instance 7, and independently by two people within the hour — which is why
it is here rather than in a footnote about tooling.

`scripts/gen-manifests.py` derives each module's version from a content hash compared against the
highest published git **tag**. In a checkout with no tags fetched it computes a lower version for
everything, and a deliberate **forward-only guard** then refuses to write a version downward. That
guard is correct. Its consequence is not: **a tagless run does nothing and says so in the voice of
success**, leaving every lock stale while the same tree still fails `gen-manifests.py --check-versions`.
Write mode and check mode appear to contradict each other. They do not — the writer simply had
nothing to compare against.

Two encounters, same evening:

- One run reported 25 stale locks at the gate. Tags fetched first (1063 of them), 25 rewritten, and
  then — **not trusting the writer** — `--check-versions` run as an independent reader: *every
  module's version matches its content*.
- Another session, draining six branches, got the no-op line back on five of them and nearly read it
  as "already current". What saved it was declining to read the writer's report at all: it checked
  `Validate node repos` on each head instead and got `completed/success` five times. On the sixth the
  script genuinely rewrote 16 locks, where the gate had been red on 17 stale ones with the fix named
  in its own output.

**Rule:** never accept a writer's report as evidence that the write happened. Verify with an
independent reader — the tool's own check mode, or the gate that consumes the artifact. A tool that
reports success while doing nothing is the purest form of this page's subject: there is no error to
notice, no red to chase, and the only signal is one you have to go and ask for.

### 9. A sweep whose zero had two causes, and the fix it therefore mis-sized

Six repositories were swept for whether their `publish-bake` caller passes `webhook-url` /
`webhook-secret` through to the reusable lane. The sweep was one number per repo:

```bash
grep -c "webhook-url:" .github/workflows/ci.yml      # → 0 in five of six
```

Every number was correct. The conclusion — *"five callers omit it, so five need the same two-line
fix"* — was wrong, because in one repository the `0` meant something else entirely: it has **no
reusable caller at all**, a local `publish-bake` job with no such step to wire. The fix there is
adopting the shared lane, a behavioural change, not a pass-through.

The sweep had an unstated premise — *every repo in this list calls the lane* — that was false for one
row, and no amount of care in reading the numbers could surface it, because the premise is not
something the numbers are about.

**The cure is a second column, always: measure the DENOMINATOR in the same pass.**

```
                      calls the lane   webhook-url:
Education                   0               0     ← no caller at all      (different fix)
SocialMedia                 1               0     ← caller, unwired       (two-line fix)
Reinsurance                 1               1     ← correct
```

Rows 1 and 2 are indistinguishable in the second column alone, and the difference between them is
*which fix applies*.

🚨 **The identical shape appeared the same afternoon in an unrelated sweep, by a different session:**
a fleet grep for `MW_IMAGE_DIGEST` reported one repository as *"does not pin the platform"*. It pins
under `MW_PORTAL_DIGEST` / `MW_MIGRATION_DIGEST` / `MW_TEST_DIGEST`. Same zero, same false
conclusion, opposite direction — one under-reported a defect, the other over-reported it.

**This is not the truncated-sweep failure** (a listing cut by `head` whose only offender sorted
below the cut) nor a sweep that never ran.
The sweep ran completely and every number was right; the *interpretation* carried a false premise. A
correct measurement under a false premise reads exactly like a correct answer, which is why it
belongs on this page: nothing about the output looks wrong.

**Rule:** whenever a sweep's negative can have more than one cause, the sweep is not finished until
it also measures which cause. If you cannot express the denominator as a column, you do not yet know
what your zero means.

### 10. An identity field that agrees with whatever you already believed

Every agent session on the shared build machine commits as the **same git identity**. On one pull
request touched by three sessions within an hour, all four commits read identically:

```
49ef8b9fe  author=rbuergi  committer=Roland Bürgi   (session A — staged the pin move)
770b6306a  author=rbuergi  committer=Roland Bürgi   (session A)
3cf2b206b  author=rbuergi  committer=Roland Bürgi   (session B, 19:13:56Z, 1 file)
2f1471a96  author=rbuergi  committer=Roland Bürgi   (session C, 20:11:59Z, 2 other files)
```

A guess — *"session B, I assume"* — was relayed as fact, thanked for, and used to aim a
"stop pushing to this branch" constraint. Nobody caught it, because the field anyone would check it
against **agrees with every hypothesis**. It was corrected only when the session it named said "that
is not my commit" and produced the timestamps.

Cost of the near-miss: the real author was never told their change edits the *detector* rather than
the defect, and the actual third pair of hands stayed invisible — the precise coordination failure
the constraint existed to prevent.

**Rule:** never attribute a commit on a shared branch from git metadata. The only discriminators are
the **commit timestamp** and the **file set**. Say *"a commit landed"* and ask. When announcing your
own push, state it as `<sha> <timestamp> <files>` so the next reader has the discriminator without
having to ask anyone.

### 11. "It fails on another branch too" — the wrong answer that is EXCULPATORY

Two tests failed on a pin-move PR. Another pull request **on the old lane pin** failed the same two
by name, so the failure was recorded as pre-existing and independent of the pin. That conclusion was
wrong, and it was published before anyone checked it.

Outcomes are incoherent when read against the lane pin, and exact when read against the core commit
each branch actually compiles against:

| PR | lane pin | outcome | | effective source ref | carries the suspect commit? |
|---|---|---|---|---|---|
| #1417 | old | success | | `main` `bbcb22f25` | **no** (compare: `behind`) |
| #1419 | old | success | | #1410 `7278d3d22` | **YES** |
| #1410 | old | **FAILURE** | | #1415 `1b5350d54` | **YES** |
| #1415 | new | **FAILURE** | | | |

**The outcome tracks the commit 4/4 and the lane pin 2/4.** The odd PR had moved its
`MW_PLATFORM_REF` while leaving its lane pin alone — it *already had the cause*. Same symptom
elsewhere was not evidence of independence; it was evidence that the other branch had got there
first.

🚨 **This one is the most dangerous in the family because of its DIRECTION.** Every other entry
leaves someone still looking. *"Not our change"* **ends the investigation** — an exculpatory wrong
answer is the one nobody re-checks, because it costs nothing to accept and closes the file.

**Rule:** same-symptom-on-another-branch is evidence of independence ONLY after you establish which
ref that branch compiles against. A repo here carries four that can differ on one branch —
`MW_PLATFORM_REF` (source), `MW_PLATFORM_SET`/`MW_IMAGE_DIGEST` (image), the lane `uses:@sha` refs,
and `clients/react/src/i18n/catalog-source.json` (not a build pin at all). One line settles it:

```bash
gh api "repos/<owner>/<core>/compare/<suspect>...<that branch's MW_PLATFORM_REF>" --jq .status
# ahead|identical ⇒ that branch HAS the cause; behind|diverged ⇒ it does not
```

### 12. A guard handed an input that could not fail — and the queue property that decided it

A cross-repo change needed a pin moved: `MW_PLATFORM_REF` in a satellite's `ci.yml` had to carry two
core commits before a dependent pull request could go green. The guard written for it asserted
ancestry — `git merge-base --is-ancestor <commit> <candidate pin>` — and its dry-run printed:

```
ancestry: 1fceaef56 reachable from main  -> ok
```

**That line could not have printed anything else.** The operator supplies the commit, and the value
supplied was `main`'s own tip — which every candidate pin taken from `main` trivially contains. The
control was sound; the *input it was handed* made its green free.

🚨 **This is NOT instance 2, and collapsing the two takes the wrong lesson.** Instance 2's detector
cannot go red at all — remove the production fix and it still passes. This check **can** go red: a
pull request's head sha is genuinely not an ancestor of `main` before it lands. It could not go red
**on the value it was given**, and the operator chose the value. The lesson is therefore not "make
your detector sensitive to its subject"; it is *an input you supply can be one the check cannot fail
on*.

🚨 **The two-step is the point, and skipping it gets the remedy wrong.** The obvious reading —
*ancestry is the wrong check here* — is false, and acting on it would have deleted a check that does
real work. What makes ancestry discriminate is a property of the queue, so it was measured before
deciding: this queue **merges** rather than squashes.

```
git rev-list --parents -n1 1fceaef56   ->  1fceaef56 7701561f9 57beea61b   # 2 parents = merge, not squash
git merge-base --is-ancestor 57beea61b origin/main  ->  yes   # #3562's head, after it landed
git merge-base --is-ancestor 4f809ca92 origin/main  ->  yes   # #3569's head, after it landed
```

**Counterfactual, and labelled as one — it did not happen here.** Had the queue **squashed**, no head
sha would ever become an ancestor, and ancestry-on-head would have been not vacuous but *wrong*:
refusing forever after a successful merge, while reading exactly like a guard doing its job. This
queue merges, so that failure is not what occurred; it is what the same check does under a different
queue setting, and it is the reason the check's soundness is a measurement rather than an assumption.

The check was therefore worth keeping and the input was worth replacing — the opposite of what the
one-step reading suggests.

**The rule this yields is sharper than "validate your inputs":**

> **An operator-supplied input is safe exactly when its ABSENCE is a refusal.**

🚨 **Absence, not validity — validation would not have caught this.** The supplied sha was forty hex
characters, a real commit, and reachable; every check a validator would run on it passed. It was
still guaranteed to succeed. Three fixes, one property, and the defect is the one where absence and a
wrong value are spelled the same way:

| | absence means | a wrong value means |
|---|---|---|
| the original (`--pin <sha>`, operator picks) | PASS | PASS ← **the defect** |
| remove the input (requirements hardcoded) | *impossible* | *impossible* |
| caller states it, empty list REFUSED | stop | stop |

**And the fix has its own expiry.** Hardcoding the requirement set (`REQUIRED_PRS="3562 3570"`) is
correct for one move and wrong for the next: run later, it asserts two long-merged pull requests —
both trivially merged, both trivially ancestors — and reports PASS having checked nothing about the
change actually being pinned. That is this same shape reached by a *stale requirement set* rather than
a chosen input, and it is the shape
[Transitional Allow Entries](/Doc/Architecture/TransitionalAllowEntries) gives allow-file lines: in
force only in the change that adds them. A one-shot script is deleted after use, or it grows the
refusal.

🚨 **Ancestry is also not the property the dependents need.** It answers whether a *commit* is
reachable; it cannot see a revert landing after it, so a pin can pass ancestry and still not contain
the change. What the dependent needs is asserted against the pinned **tree**:

```bash
git show "<candidate pin>:src/MeshWeaver.Messaging.Hub/Localization/strings.en.json" \
  | grep -q 'onboarding.usernameTaken' || { echo "REFUSE: pin does not carry the keys"; exit 1; }
```

The cleanest single artefact from this instance is the dry-run where the two halves **disagreed** on
one candidate, and the content half did the refusing:

```
PASS    onboarding.usernameTaken in strings.en.json
PASS    onboarding.usernameTaken in strings.de.json
FAIL    StoodDown absent from src/MeshWeaver.Graph.Contract/BuildState.cs
FAIL    ReleaseStoodDownClaim absent from src/MeshWeaver.Graph/Configuration/BuildNodeType.cs
ancestry: reachable from main -> ok
```

Ancestry passed and the pin was not safe. Had the guard carried only the ancestry half it would have
moved the pin that minute, turning one dependent green and leaving the other red on a pin that
carried half the requirement. Per language and per symbol, separately: a pin carrying
`strings.en.json` but not `strings.de.json` passes an English-only assertion and then reds
`LocalizationTest`, and one half of a two-file API change compiles and still fails its test. Both are
the same half-present pin.

### 12b. The watcher that became the load — found while writing instance 12

The postscript belongs with 12 because it happened *to the two sessions writing it*, an hour after the
guard above was hardened.

Both sessions had armed watchers on the pull requests they were waiting on. Several other sessions
were watching the same handful. At **10:06:02 UTC** the aggregate exhausted the shared credential:

```
403 API rate limit exceeded for user ID 6334612
request id E5AD:48A5A:E5CFC9B:DE61348:6A9E8C8A
```

**The mechanism, established afterwards rather than inferred at the time.** One watcher was polling
7 pull requests × 3 calls every 150 s. The dominant source was a *third* session that had accumulated
roughly **forty** backgrounded `sleep …; gh api …` waiters — **one new one per turn, without stopping
the previous one**. Its own statement of the fix is the crisp one:

> **The waiter you replace is the waiter you stop.**

🚨 **The first conclusion was wrong, in the direction that mattered, and the check that appeared to
support it could not have contradicted it.** The initial reading was a burst of short-lived calls. A
`pkill` sweep matched nothing, and that was reported as evidence the processes *had already exited*.

They had not. A `sleep …; gh api …` waiter is **invisible to a process match on the caller for nearly
all of its life** — it is sleeping, not calling. So "no match" meant *none are calling in this
instant*, which is not *none exist*, and the two render identically. Roughly forty of them did exist,
and the session that owned them had to stop them itself.

> **Count the waiters, not the calls in flight.**

Recorded as a correction rather than softened, because the page's own investigation producing a fresh
instance of the page's own shape is the most convincing thing in it.

**The watchers existed to prevent a false red, and produced a false UNKNOWN — for every concurrent
session, not only their own.** The hardening worked in the narrow sense: the watcher reported the 403
as `UNKNOWN — this is NOT a red` rather than as a failure. But a control that degrades the system it
observes has already cost more than the signal is worth, and the remedy is not a better reading of the
signal:

> **The cheapest way to avoid misreading a signal is to stop generating it.**

Nothing either session was waiting on could be decided faster by asking more often. One had a merged
pull request, one had a draft that moved only on a human-agreed ping, and the pin move belonged to
neither's poll loop.

🚨 **And the instrument for "am I rate-limited" is itself in this family.** `gh api /rate_limit` keeps
reporting `5000/5000 remaining, 0 used` while every call is refused — the primary quota is not the
signal, and *that pairing is what a secondary limit looks like*. So the check you would reach for to
confirm the diagnosis reports health. See
[Reading CI Signals](/Doc/Architecture/ReadingCiSignals) and AGENTS.md's REST-not-GraphQL rule; the
operational half is: on a refusal **stop for at least five minutes**, do not retry, and do not switch
to a different query hoping it is cheaper — fast retries EXTEND the window.

This is instance 1's property at fleet scale. There, the probe's own traffic delayed the condition it
asserted. Here, the watchers' own traffic removed the ability to observe anything at all. In both, the
act of measuring changed what was measured, and in both the control reported something that was not a
red.

### 12c. A remedy that is right for reads and wrong for writes — same error text, opposite response

Minutes after 12b, opening the pull request for this very page failed:

```
Post "https://api.github.com/graphql": net/http: TLS handshake timeout
```

🚨 **The standing remedy is SAFE here and NOT SUFFICIENT — and the difference is the whole entry.**
AGENTS.md's rule for a failed GitHub call — *on a refusal STOP for at least five minutes, do not
retry, do not switch queries* — is correct, hard-won, and its **"do not retry" half is exactly what
prevents the duplicate**. So this is not a correction to that rule. It is an addition to it: for a
write, stopping is necessary and leaves you knowing nothing, because you still cannot say whether the
thing happened. A read can stop and simply be asked again later; a write cannot resume until someone
establishes which of two states the system is in.

The advice that *does* damage is the ordinary reflex the rule exists to suppress — "just try again" —
and nothing in the error text distinguishes the case where it is harmless from the case where it is
not.

> **A timeout on a READ means ask again — the worst case is a wasted call.**
> **A timeout on a WRITE means find out what happened, then decide.**
> **The error text is identical.**

A create is a mutation. A transport failure says the **response** was lost; it does not say the
**request** was. `gh pr create` may have opened the pull request and then failed to tell you. Retry
blind and you get two — and the duplicate reads to everyone else as carelessness, not as a transport
fault, so the evidence of what actually happened is destroyed by the act of recovering from it.

**What was done instead, in three calls:**

```bash
# 1. ONE read that distinguishes the two states — never a blind retry
gh api "repos/<owner>/<repo>/pulls?head=<owner>:<branch>&state=all" --jq '.[] | "#\(.number) \(.state)"'
#    -> empty  ⇒ the mutation did NOT land

# 2. only then create it, over REST rather than the endpoint that just failed
gh api --method POST repos/<owner>/<repo>/pulls --input payload.json
```

REST here is not a workaround for a limit — it is the endpoint this repository prefers for everything
GraphQL is not required for, and it happens to avoid the endpoint that had just timed out.

🚨 **And the claim "I did not create a duplicate" is exactly the one not to accept on assertion.** It
is exculpatory — it closes the file, which instance 11 identifies as the most dangerous direction in
this family. The other session re-ran the same query independently and got one row. That is the check
that would have falsified the claim, run by the party that did not make it.

**Generalise it past pull requests.** Every write with a lost response has this shape: a node create,
a tag push, a `POST` that provisions something, an issue comment. Before any retry of a mutation,
name the read that distinguishes *landed* from *did not land* — and if no such read exists, that is
worth knowing before you need it, not after.

🚨 **But the axis is IDEMPOTENCE, not write-ness — and this entry proved it on itself minutes later.**
Pushing this very section failed with `Could not resolve host: github.com`. That is a *stronger*
failure than the timeout above (DNS resolution precedes any connection, so the request provably never
left) — and it would not have mattered either way, because `git push` of a named ref **is
idempotent**: repeating it converges on the same remote state. `gh pr create` is not; repeating it
creates a second pull request.

So the rule is not "never retry a write". It is:

> **Never retry a NON-IDEMPOTENT write without the read that says whether it landed.**
> An idempotent one may simply be repeated.

The distinguishing read was cheap and was run anyway, because "provably never left" is a claim worth
one command rather than one inference:

```bash
git rev-parse HEAD                                  # 1ba7eb461…  local
git ls-remote origin refs/heads/<branch> | cut -f1   # b818b4f75…  remote — the push had NOT landed
```

Classify the operation *before* choosing the recovery, and note which of the three the error tells you
about: whether the request was sent, whether it was applied, and whether the operation is safe to
repeat. Most transport errors answer only the first, and only sometimes.

### 13. The same defect, fixed in one method and left in its sibling — two months apart

The instrument was `lsp_check_node`, the pre-flight the `/code` skill's whole edit loop is built on
(*"edit a source file in your head → `lsp_check_node` → if diagnostics, fix → repeat → only then
patch + compile"*). Measured on `memex.systemorph.com`, 2026-09-10:

```
lsp_check_node  nodeTypePath: @BinaryClickerV2/BinaryToggle
                proposedCode: "this is definitely not valid C# ###"
→ {"ok":true,"diagnostics":[]}
```

That second call **is** the second half of the diagnostic, run by accident: a probe handed a subject
broken on purpose — text that cannot parse as C# at all — and it still returned clean. The NodeType
sat in a partition that identity had no read grant on, so nothing was ever compiled; the empty list
was "I could not look", spelled exactly like "I looked and it is fine".

**What makes this one worth its own instance is not the defect. It is that the defect had already
been found, understood, written up and fixed — in the method next door.** #1592/#1618 gave
`GetDiagnostics` a `NodeDiagnosticsOutcome` for precisely this reason, and the type's own XML doc
says it in bold: *"An empty diagnostic list is not evidence of health."* `CheckSpeculative`, four
lines away in the same class, kept returning a bare list. Nothing carried the fix across, because
nothing had named the CLASS the two methods share.

The reason it survived a careful reading is subtler than an oversight, and it is the general lesson:

> **One return type was serving two consumers whose needs are opposite.** The Monaco editor wants
> silence when the owner cannot be resolved — a squiggle computed under the wrong language rules is
> worse than no squiggle. The MCP tool renders a **verdict**, where silence reads as *approved*. The
> code comment said *"stay silent rather than paint squiggles computed under the wrong language
> rules"*, and it was **right for one caller and wrong for the other** — so reviewing the method on
> its own terms could only ever confirm it.

The fix is two surfaces rather than one compromise: `CheckSpeculative` (the editor's, silent by
contract) and `CheckSpeculativeOutcome` (everyone rendering a verdict), the first derived from the
second so they cannot drift. Full account: [Language Services](/Doc/Architecture/LanguageServices)
→ *An empty diagnostic list is not evidence of health*.

**Two transferable rules.** When you fix an instrument that cannot fail, ask *which other callers of
this data have the same shape* — a sibling method, a second tool over the same service, the other
overload — because the fix travels with the CLASS, not with the call site. And when a "silent on
error" contract looks correct, ask **whose** contract it is: if two consumers read the same value
and one of them renders it as a verdict, silence is not a shared default, it is a defect for one of
them.

### 14. Two counters over different populations, printed as if they were one measurement

Not a control this time, but the same property one step over: an **instrument pair** whose readings
were not comparable and whose lines did not say so. memex, 2026-09-08, one cold boot, ten seconds
apart:

```
00:31:08  ShippedPrebuiltBundles: 78 prebuilt assembly(ies) … are backed by the assembly store
00:31:18  DynamicTypePreWarmer:   204 of 209 … need building — 5 already on the share.
```

Filed as *"78 adopted, the sweep's store probe counts 5"*
([#3703](https://github.com/Systemorph/MeshWeaver/issues/3703)) — which sends the next reader to the
assembly store, where nothing is wrong. **78 counts BUNDLE ENTRIES whose bytes were written, in
assemblies. 5 counts NODETYPES whose stored RECORD names a live-framework build and whose bytes
resolve at the version that record names.** The store is asked exactly one question per type, keyed
by the record — so the second number is not a census of the share at all, and the phrase *"already
on the share"* was the whole misreading in four words.

Underneath it sat the defect proper, and it is instance 5's shape: the sweep read a CQRS projection
that its own process had superseded ten seconds earlier, and **a stale projection and a genuinely
stale record are the same bytes** — the classification could not tell them apart, and reported
`frameworkstale=201` with 197 needless compiles behind it.

**Two fixes, and the second is the one this page is about.** The sweep now classifies from the newer
of the two facts, ordered by `MeshNode.Version` rather than by hope. And every line names its own
population: the adoption line says it counts assemblies, the sweep line says it is *"NOT a census of
the assembly store"*, and the report carries `fromlocaladoption=N` so a boot whose input was behind
says so in the same line that reports the verdict.

> **A number that does not carry its population, its unit and its source is not a measurement, and
> two of them side by side are an invitation to a false disagreement.** Before you treat two
> instruments as contradicting each other, make each one state what it counted.

Full account: [Adoption and the Sweep Count Different Things](/Doc/Architecture/AdoptionAndTheSweepCountDifferentThings).

## What the whole family has in common

Instances 9, 10 and 11 were found on one day, alongside a watcher reading a `startup_failure` (a
check-**suite** with **zero** check-**runs**) as *all checks green*. Four instruments, one property:

> **They do not fail to answer. They answer, confidently, wrongly — and the wrong answer is spelled
> exactly like a real negative.**

- git authorship → confirms whatever you already believed
- zero check-runs → *all green*
- a sweep's `0` → *"present and omits it"* **or** *"does not exist"*, indistinguishable
- *"it fails elsewhere too"* → *pre-existing*

**The shared remedy is one question, and it is not "be careful": ask what the instrument answers
when it cannot actually see, and refuse to let that answer be spelled the same way as a real
negative.** Where the two cannot be separated, add the column that separates them — the
denominator, the suite state, the timestamp, the ancestry test.

## The family's edge: a control that fires correctly and hands you an impossible next step

The eight above are all one defect — a control whose green is guaranteed. This last one is
deliberately kept separate, because it is that defect **one step downstream**, in the ACTION rather
than in the verdict, and collapsing the two would blunt both.

`EmitPipeline`'s canary detects its condition correctly. On a `BELOW-ROSLYN` verdict it then told the
reader to **capture a core dump** — and `DOTNET_DbgEnableMiniDump` fires on a **signal**, while this
particular death is the wall-clock cap killing the process as `exit=124`/SIGTERM. No signal, no dump,
ever.

**Nine occurrences carried that instruction and produced zero dumps** (#890, fixed in #3317).

Nothing looked wrong at any point, and that is the whole lesson. A missing dump reads as *"nobody got
round to it"*, never as *"the instruction is impossible"*. The verdict fired, named the right
condition, and routed every investigation into a wall — which is arguably **worse** than staying
silent, because it consumed nine investigations before anyone questioned the advice rather than the
defect.

The repair is the shape to copy: name the followable remedy instead — a split-arm re-run — and put
*"one clean arm proves nothing"* **inside** the instruction, so the remedy carries its own control arm
and the next reader cannot take a single green arm as an answer.

**The sibling finding, same PR:** the verdict also *named a residual it never measured*. Two legs both
asked "can this process emit?"; neither asked whether it could still perform the READ the emit dies
on. A third leg now dissects that directly, so the verdict discriminates between two hypotheses it
previously conflated. **A verdict may only name what one of its legs actually measured** — otherwise
it is a guess wearing a measurement's clothes.

> 🚨 **Ask it of remedies too, not just checks:** *if I followed this instruction right now, could it
> possibly produce what it promises?* Nine people did not, and the instruction was in the tooling the
> whole time.

If this page needs one line to justify existing, it is that the cure for a check that cannot fail is
not a better check but a **habit** — *ask what this would print if the subject were broken, then go
break it* — and that the habit applies to instructions exactly as much as to assertions. A remedy that
carries its own control arm is that thesis applied to itself.

## What to do about it

1. **Break the subject on purpose, once — this is the whole discipline.** Revert only the fixing
   lines, or disable the mechanism, and watch the control go red with the message you predicted.
   Record that output — the red run is the evidence, the green one is not. Reasoning that it *would*
   go red is what all six instances below had. The procedure is in
   [Negative Controls](/Doc/Architecture/NegativeControls).
2. **Ask what serves the precondition while the isolation is absent.** If the answer is "the thing I am
   about to remove", the probe cannot fail.
3. **Separate "cannot see" from "nothing there".** Every watcher, every query-backed gate, needs a
   distinct blind state that is loud.
4. **Assert arrival, not existence.** For an input, the property that matters is that it reached the
   consumer — pass it and have the consumer confirm.
5. **Prefer a red that stops the line to a value that travels.** A refusal is recoverable; a
   plausible-looking wrong value is not.
6. 🚨 **Never trust a `--no-build` result after a build you did not just watch succeed — in EITHER
   direction.** The familiar warning is that `--no-build` on an unbuilt project exits 0 having run
   nothing, a false PASS. It also produces false FAILS: a stale mutation binary left in the output
   directory failed a full suite that was actually fine. The rule is about the staleness, not about
   the polarity of the answer.
7. **Do not promote an experiment into a guard by default.** An experiment that pins a cause and a test
   that guards against regression are different artifacts. If the experiment's setup depends on an
   ordering you cannot *enforce*, committing it manufactures the next flake — record it in the change
   instead.
8. **Give every count a denominator.** A zero that can mean two things is not a measurement. Before
   acting on a sweep, name the premise that makes its negatives comparable, and measure that too — as
   a second column, in the same pass.
9. **Read check-SUITES, not only check-runs.** `startup_failure` is terminal and produces zero runs,
   so a run-count filter and an absent-context filter both mis-report it — one as green, one as
   pending-forever.

10. **Never attribute a commit on a shared branch from git metadata.** One identity covers every
   session; only the timestamp and the file set discriminate. Announce your own pushes as
   `<sha> <timestamp> <files>`.
11. **Before calling a failure pre-existing, check which ref the other branch compiles against.** An
   exculpatory wrong answer ends the investigation, so it is the one to distrust most.
12. **Make absence a refusal wherever an operator supplies an input.** A value you choose can always
   be one the check cannot fail on, and nothing in the output distinguishes that from a real pass.
   Remove the input, or require it and refuse the empty value — never accept it and proceed. If you
   removed it by hardcoding, the hardcoded set is now a transitional artifact: delete it with the
   change, or it goes stale into the same shape.
13. **Never retry a NON-IDEMPOTENT write without a read that says whether it landed.** Retry advice —
   this page's included — is written for reads, where the cost of asking again is a wasted call. A
   mutation whose response was lost may well have succeeded, and the blind retry produces the
   duplicate *and* destroys the evidence. Classify first: an idempotent write (`git push` of a named
   ref, a `PUT` to a fixed path) may simply be repeated; a non-idempotent one (`gh pr create`, a
   `POST` that mints an id) may not. Then name the distinguishing read; if there is none, learn that
   before you need it.
14. **Stop the waiter you replace.** A backgrounded `sleep …; poll` waiter spawned once per turn
   accumulates silently, and a process match on the caller cannot see it — it is sleeping, not
   calling, for nearly all of its life. Count the waiters, not the calls in flight.

## See also

- [Negative Controls](/Doc/Architecture/NegativeControls) — the procedure, and four tests that proved nothing.
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — skipped and absent contexts, and what a green wall does not mean.
- [Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — a gate that sees two of seven break shapes, and says so.
- [Orleans Test Routing Pattern](/Doc/Architecture/OrleansTestRoutingPattern) — the pod-hub claim, and why reachability cannot stand in for it.
- [Transitional Allow Entries](/Doc/Architecture/TransitionalAllowEntries) — in force only in the change that adds them, which is why instance 12's hardcoded set expires.
- [Language Services](/Doc/Architecture/LanguageServices) — instance 13 in full: why one return type could not serve both the editor and the pre-flight tool.
- [Writing Tests](/Doc/Architecture/WritingTests) — the golden rules these controls are expressed against.
