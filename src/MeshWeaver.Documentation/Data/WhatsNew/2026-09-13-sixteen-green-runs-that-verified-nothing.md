---
Name: Sixteen green runs that verified nothing
Category: Feature
Description: Combo Gate Wiring now records what the combo verification lane is actually doing - thirty runs, zero instances verified, and more than half of them green - the four inputs nobody provisioned, what being unverified costs today, and the one step that turns out to need no new credential.
Icon: Sparkle
Order: -20260913
---

# Sixteen green runs that verified nothing

The combo gate asks the question an artifact cannot answer: *may this instance be rolled to that
image, given the modules it actually runs?* A portal consults a recorded verdict before it applies
an update. Something off-cluster has to produce that verdict, and a workflow was wired to do it.

It has never produced one. Over its last thirty runs, the job that verifies an instance was
**skipped thirty times out of thirty**, and neither live instance has a single recorded verdict.

## The part that is easy to misread

Fourteen of those thirty runs are red — the lane refusing to run because four inputs were never
provisioned, and naming each one. **Sixteen are green**, and every one of those greens verified
exactly as much as the reds: nothing. They are the *no candidate* case — the delivery run that
would have produced something to verify was cancelled, so there was nothing to check and the lane
says so.

That branch is right, and it is what stops the gate crying wolf on ordinary traffic. But it means
the workflow's colour is not an answer to "is anything being verified", and counting greens is the
one way to get this wrong. The reading that answers it is whether the verification job *ran*, and
the instance count the lane prints beside its verdict — a number that has been zero throughout.

## What it costs today, stated rather than assumed

Nothing gates on it, for two independent reasons, both measured. An absent verdict deliberately
neither clears nor refuses — refusing on it would have frozen every instance in the fleet the day
the gate shipped — so only a recorded **failure** holds a roll; an unverified roll proceeds and says
so in the log. And the one thing that consults a verdict at decision time, the self-update poller,
is switched off on both live instances.

That is an argument about priority, not about whether to finish it: the gate exists so that the day
self-update is switched back on, a candidate a live instance cannot serve is refused rather than
rolled.

## The step that turned out to be cheaper than recorded

Three things close this, and the list has been carrying a dependency that does not hold. The
instance list is a hand-maintained variable, which the platform's no-pins rule turns into debt, and
replacing it with a run-time enumeration was recorded as needing a new credential for reading the
fleet's deployment records. It does not: the retention lane already derives the same roster from the
deployment overlays using a read-only app whose credentials **this lane already asserts and already
mints**. What genuinely cannot be worked around from a build is the per-instance permission that
lets a build record a verdict at all — that is data on each portal, and only an operator can grant
it.

All of it is written up under Combo Gate Wiring, with the measurements, the exact provisioning table
and the dependency order.
