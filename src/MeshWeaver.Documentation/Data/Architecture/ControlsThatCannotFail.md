---
Name: Controls That Cannot Fail
Category: Architecture
Description: A control whose green is guaranteed by construction is not a control. Eight measured instances — a test, a detector, an identity anchor, a preflight, a watcher, a CD verdict, a git idiom that answers the wrong question, and a generator whose failure mode is a success line — and the one question that catches all eight.
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
| **A measurement that truncates and answers anyway** | a profiler capping at N objects and reporting the partial total | the wrong number is *plausible*, so it closes the investigation |
| **A writer whose failure mode is a SUCCESS line** | a generator that skips its work and prints a summary anyway | nothing distinguishes "wrote it" from "declined to" |

## Eight measured instances

The first six were found in a single day, across tests, CI, publication and ops. The last two were
found **while writing this page** — one by its author, one by the session that supplied instances
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
covering *"this is the heap"* and *"this is as much of the heap as the tool chose to walk"*. **The cure
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

## See also

- [Negative Controls](/Doc/Architecture/NegativeControls) — the procedure, and four tests that proved nothing.
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — skipped and absent contexts, and what a green wall does not mean.
- [Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — a gate that sees two of seven break shapes, and says so.
- [Orleans Test Routing Pattern](/Doc/Architecture/OrleansTestRoutingPattern) — the pod-hub claim, and why reachability cannot stand in for it.
- [Writing Tests](/Doc/Architecture/WritingTests) — the golden rules these controls are expressed against.
