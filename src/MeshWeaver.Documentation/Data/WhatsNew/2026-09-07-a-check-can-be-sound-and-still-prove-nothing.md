---
Name: A check can be sound and still prove nothing
Category: Feature
Description: The Controls That Cannot Fail page gains a twelfth shape — a guard whose input the operator chooses, which can always be handed a value it cannot fail on. The rule it yields is that an operator-supplied input is safe exactly when its absence is a refusal.
Icon: ShieldError
Order: -20260907
---

# A check can be sound and still prove nothing

[Controls That Cannot Fail](/Doc/Architecture/ControlsThatCannotFail) collects the checks whose green
is guaranteed by construction — a test served by the thing it was about to remove, a watcher that
reports "I cannot see" as "nothing is wrong", a verdict that spells three different outcomes the same
way. Every entry so far was about a control that could not see its **subject**.

The twelfth is different: the control was fine, and the **input someone handed it** was the problem.

A guard was written to stop a build pin moving before it carried two required changes. It asked
whether a commit was an ancestor of the candidate pin — a sound question — and it was given the
branch's own tip as the commit. Every candidate contains that, so the check could only ever print
`ok`. It was reported as one of the things that made the pin safe.

## What makes it worth a page entry rather than a footnote

The obvious reading — *that was the wrong check* — is false, and acting on it would have deleted a
check that does real work. Measuring the queue settled it: it merges rather than squashes, so a
change's own commit genuinely is not an ancestor before it lands and genuinely is after. Had it
squashed instead, the same check would have been not merely empty but **wrong** — refusing forever
after a successful merge, while looking exactly like a guard doing its job.

So the check was worth keeping and the input was worth removing, which is the opposite of what the
quick reading suggests.

## The rule

> **An operator-supplied input is safe exactly when its absence is a refusal.**

Removing the input, or requiring it and refusing an empty value, both satisfy that. Accepting a value
and proceeding does not — because then a missing input and a wrong one are spelled the same way, and
that spelling is `PASS`.

The entry also records the sting in the tail: removing an input by hard-coding the answer creates a
one-shot artifact that expires. Run it against a later change and it asserts requirements that are
already met, reporting success having checked nothing — the same shape reached by going stale instead
of by being handed a bad value.

## Two more, from what happened while writing it

The sessions writing the entry then produced two fresh instances of its own subject, an hour apart.

**Watchers armed to prevent a false red exhausted the shared credential and produced a false
UNKNOWN** — for every concurrent session, not only their own. One session had accumulated around
forty backgrounded wait-then-poll processes, one per turn, without stopping the previous one. The
sweep that looked for them found nothing, because such a process is sleeping rather than calling for
nearly all of its life: *"none are running right now"* and *"none exist"* look the same. Count the
waiters, not the calls in flight.

**And the standing advice for a failed request turned out to be advice about reads.** Opening the
pull request for this very page failed on a network timeout. Retrying would have been right for a
read and wrong for a write: a lost response is not a lost request, so the second attempt can create a
second copy of whatever the first one already made. The rule the entry adds is that a write is never
retried without a read that says whether it landed.
