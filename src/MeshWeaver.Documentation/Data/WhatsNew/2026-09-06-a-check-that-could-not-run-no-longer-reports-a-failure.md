---
Name: A check that could not run no longer reports a failure
Category: Fix
Description: When a portal pod could reach neither of its two ways of learning whether its build was approved, it reported that the build had not been approved — and held the rollout. It now says it could not tell, and decides on evidence it can actually reach.
Icon: CloudArrowUp
Order: -20260906
---

A portal pod checks, at startup, whether the build for the image it is running has been approved.
It has two ways of asking. One was added [earlier today](../2026-09-06-a-rollout-no-longer-stalls-on-a-build-it-already-approved),
after two pods held a rollout on a build that had in fact already been approved.

Both ways can be blocked by the *same* underlying connectivity fault — they travel over the same
internal channel. When that happened, the second check did not come back with an answer at all; it
came back with nothing. The pod treated "nothing" as "no", refused to come up, and wrote a line
saying the build had not been approved. That sentence was simply not true, and an operator reading
it went looking for a build nobody had refused.

There are now three possible outcomes rather than two: approved, not approved, and **could not
tell** — and the third one is never reported as the second. When a pod genuinely cannot tell, it
does not guess in either direction. It looks at something the fault cannot reach: the compiled
output already sitting on its own storage. If everything the pod needs is there, it comes up and
says it decided on that; if anything is missing, it stays down exactly as before, and says the
check could not be read rather than that the answer was no.

The bar for coming up this way is deliberately *higher* than when the approval is readable, not
lower — a pod that has an approval is allowed some slack that a pod deciding for itself is not.
Nothing was made more permissive: no timeout was lengthened, no retry was added, and the
connectivity fault is still reported in full so it can be found and fixed.
