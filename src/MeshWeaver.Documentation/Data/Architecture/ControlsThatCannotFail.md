---
Name: Controls That Cannot Fail
Category: Architecture
Description: A control whose green is guaranteed by construction is not a control. Six measured instances from one day — a test, a detector, an identity anchor, a preflight, a watcher and a CD verdict — and the one question that catches all six.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><path d="M8 12h8"/><path d="M12 8v8" opacity="0.25"/></svg>
---

# Controls That Cannot Fail

A control — a test, a gate, a detector, a preflight, a watcher, a status verdict — earns its green by
being **able to go red**. When it cannot, its green stops carrying information, and the failure mode
is the worst one available: *nothing happens*. No alarm, no red tick, no line in a log. The control
silently disappears and the wall stays green.

> **The question that catches all six instances below:**
> *"What makes this go red? Have I seen it do that?"*
>
> If the honest answer is "I've never seen it fail" or "it would fail if X, and X can't happen here",
> you have a decoration, not a control.

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

## Six measured instances

All six were found in a single day, across tests, CI, publication and ops. Each is stated with what
was *measured*, not with what was suspected.

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

## What to do about it

1. **Break the subject on purpose, once.** Revert only the fixing lines, or disable the mechanism, and
   watch the control go red with the message you predicted. Record that output — the red run is the
   evidence, the green one is not. The procedure is in
   [Negative Controls](/Doc/Architecture/NegativeControls).
2. **Ask what serves the precondition while the isolation is absent.** If the answer is "the thing I am
   about to remove", the probe cannot fail.
3. **Separate "cannot see" from "nothing there".** Every watcher, every query-backed gate, needs a
   distinct blind state that is loud.
4. **Assert arrival, not existence.** For an input, the property that matters is that it reached the
   consumer — pass it and have the consumer confirm.
5. **Prefer a red that stops the line to a value that travels.** A refusal is recoverable; a
   plausible-looking wrong value is not.
6. **Do not promote an experiment into a guard by default.** An experiment that pins a cause and a test
   that guards against regression are different artifacts. If the experiment's setup depends on an
   ordering you cannot *enforce*, committing it manufactures the next flake — record it in the change
   instead.

## See also

- [Negative Controls](/Doc/Architecture/NegativeControls) — the procedure, and four tests that proved nothing.
- [Reading CI Signals](/Doc/Architecture/ReadingCiSignals) — skipped and absent contexts, and what a green wall does not mean.
- [Cross-Repo Pair Gate](/Doc/Architecture/CrossRepoPairGate) — a gate that sees two of seven break shapes, and says so.
- [Orleans Test Routing Pattern](/Doc/Architecture/OrleansTestRoutingPattern) — the pod-hub claim, and why reachability cannot stand in for it.
- [Writing Tests](/Doc/Architecture/WritingTests) — the golden rules these controls are expressed against.
