---
Name: A Fault Does Not State the Verdict
Category: Architecture
Description: >-
  A log line that reports a FAULT must say what it knows and stop — never the verdict some later
  branch decides. When a recovery path is added downstream, the fault's old claim becomes a false
  assertion that nothing in the pipeline can contradict: the red-log watcher captures Error and
  Critical only, so the Error's claim becomes the permanent record while the Warning that says the
  process recovered is never captured at all. The result is an issue that can never stay closed.
Icon: <svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86 1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><path d="M12 9v4"/><path d="M12 17h.01"/></svg>
---

# A Fault Does Not State the Verdict

**A line that reports a fault may state the fault. It may not state the decision that another
branch makes after it runs.**

This sounds like a style rule about wording. It is not. When the decision moves — when someone adds
a second door, a fallback, a recovery path — the fault's sentence does not move with it, and it
becomes a confident assertion about something that did not happen. Nothing fails to compile, nothing
goes red, and the claim is now the only thing the observability pipeline keeps.

## The shape

```
  ┌─ the fault is detected ────────────────────────────────┐
  │  logs at Error: "X failed, SO THE OUTCOME IS Y"        │  ← states a verdict it cannot know
  └────────────────────────────────────────────────────────┘
                            │  (the exception propagates)
                            ▼
  ┌─ a later branch DECIDES the outcome ───────────────────┐
  │  recovered → logs at Warning: "actually, not Y"        │  ← never captured
  │  refused   → logs at Error:   "Y"                      │
  └────────────────────────────────────────────────────────┘
```

The fault line is written **first**, so at the moment it runs the verdict does not exist yet. On the
recovered path it is simply wrong.

## Why it is not cosmetic: the capture rule turns the claim into the record

Two properties of the red-log pipeline (see [Log Watch Triage](../LogWatchTriage)) combine into the
whole defect:

1. **Only `fail:` and `crit:` are captured** — Error and Critical. A Warning is not collected, not
   fingerprinted, and not ticketed.
2. **The fingerprint is the NORMALIZED MESSAGE.** An incident's identity, and therefore the issue it
   folds into, is the wording.

So on the recovered path:

- the Error that claims the bad outcome **is** captured, fingerprinted, and filed;
- the Warning that says the process recovered is **not captured at all**;
- and there is no third line for anyone to compare them against.

The false claim becomes the permanent, ticketed record of an event that went fine. Worse, because the
fingerprint is the wording, it folds into the issue *about the bad outcome* — so that issue is
reopened by events which are not instances of it, and **can never stay closed**. Closing it correctly,
with evidence, does not help: the next benign blip reopens it.

## The measured instance

The pre-warmer's build handshake is a `SubscribeRequest` to the build coordination root (see
[Build Coordination](../BuildCoordination)). When it goes unanswered,
`BuildProtocolDriver.RetryUnreachableCoordination` exhausts its attempts and logs, at Error:

> BuildProtocol: could not reach the build coordination node 'Admin/Build' in 3 attempt(s) — the
> pre-warm sweep never started, so this process has verified NOTHING about its NodeTypes on this
> image. **This is a refusal, not a pass: readiness stays refused and the rollout holds the previous
> image.** A restart re-attempts.

That last sentence was true while the subscription was the **only** door. Then
`WhenTheSubscriptionDoorIsShut` was added: it catches this exception and asks the durable witness, and
when the witness already carries the GO for this framework it **grants readiness** — reporting that
at Warning.

From then on, every unanswered subscription published one Error asserting that readiness was refused
and a rollout was held, whether or not either happened. Measured consequence: the incident for held
rollouts was correctly closed twice with evidence, and reopened by an occurrence on an image **three
framework builds newer than the door that fixed it** — datable from the log line itself, whose
`Queue(…)` diagnostic carried `handledWhileWaiting` and a `Trail:` block that the original samples do
not have. The reopen was legitimate by the pipeline's own rules; the line it rested on was not.

The cost is paid twice. An operator reading it goes to look at a rollout that is fine, and the issue
queue carries an open item whose condition is not occurring.

## The rule, and what it does not say

**State what you know. Name what you could not do. Do not name the consequence unless you are the
code that decides it.**

Concretely, for the fault line:

- ✅ what was attempted, how many times, and against what — `'Admin/Build'`, `3 attempt(s)`;
- ✅ what is consequently unknown — the subscription-borne sweep did not run, so nothing was verified
  *through that door*;
- ✅ that the fault is real and unfixed, because that is the only signal the path is broken;
- ✅ where the verdict will be stated — "whichever door answers says so on its own line";
- ❌ the verdict.

And for the deciding branch: it states the verdict, at the severity the verdict deserves — the
refusing branches at Error, in as many words, so the operator-facing claim about held rollouts is
still made **exactly when it is true**.

🚨 **This is not a severity change and not a visibility reduction.** The fault keeps its `Error`. The
temptation is to "fix" the false ticket by demoting the fault to Warning so the watcher stops seeing
it — that deletes the only evidence that the transport is broken, which is the opposite of the goal.
The fix is what the line **claims**, never whether it is reported.

🚨 **It is also not a reason to stop logging early.** The fault must be logged where it happens, with
its inner exception and its diagnostic detail, because the deciding branch has less context. Both
lines are wanted; only one of them may state the outcome.

## How to tell whether you have this defect

Ask one question of any line that names an outcome:

> **Is this line written before or after the code that decides the outcome?**

Before ⇒ it must not name it. The test is mechanical and does not require judgement about wording.

Two supporting smells:

- A fault message containing "so", "therefore", "this means" or a bare future tense about a verdict.
- A `Catch` added *around* an existing fault path. Adding a recovery downstream of a fault is exactly
  the edit that invalidates the fault's sentence, and the sentence is in a different file from the
  edit — so nothing prompts anyone to revisit it.

## Testing it, with a control on each side

A single assertion here is worthless, because "no line claims a refusal" passes trivially against a
test fixture that supplies its own message. Two things make it real:

1. **Drive the production chain, not a fixture.** Compose the real retry with the real door, so the
   message under assertion is the message minted in production. A fixture that hard-codes the wording
   tests the fixture.
2. **Assert from both sides with ONE predicate.** The recovering case asserts the predicate does
   **not** fire; the refusing case asserts it **does**, on a line at Error that also names the
   witness it read. A predicate that could never match — a typo, a reworded production line — would
   make the recovering case pass having checked nothing, which is this defect's own failure shape one
   level up.

That pair establishes the property that matters: the claim **moved to the code that decides it**,
rather than having been deleted.

Measured against the fix in place, both cases pass; with the pre-fix message restored, the
recovering case fails on exactly that assertion. See
`PreWarmerReadsTheDurableGoTest.TheTransportFaultStatesNoVerdict_WhenTheDurableGoGrants` and
`…TheDoorThatRefuses_DoesStateTheRefusal`.

## The family this belongs to

The same root, in both directions:

- **An undetermined witness rendered as a definitive negative** — a read that never completed
  reported as an answer. [Build Coordination](../BuildCoordination) carries that one; it is the
  reason the second door exists at all.
- **A narrow instrument generalised into a claim it never measured** —
  [Reading CI Signals](../ReadingCiSignals).
- **This page** — a *successful recovery* rendered as a definitive refusal. The inverse, and the one
  that is invisible because the pipeline that would contradict it does not collect Warnings.

All three are one question: *does this line speak for something it actually established?*

## Applying it beyond logs

The rule generalises to anything a later stage can overturn:

- **An exception MESSAGE** carried past a `Catch` — as here. The message travels; the caller's
  decision does not travel back into it.
- **A health-check detail string** composed before a downstream aggregate decides the overall status.
- **A node's stamped status** written by one phase and re-decided by a later one. Where that matters
  for what a running portal actually serves, see
  [Stale State Until Recycle](../StaleStateUntilRecycle).
- **An issue or PR body** asserting an outcome a gate has not yet reported.

In each case the same repair applies: say what happened here, name where the outcome is decided, and
let the deciding code say it.
