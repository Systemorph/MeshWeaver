---
Name: /health now says what it spent, and on which check
Category: Fix
Description: A new replica could not finish starting although every one of its health verdicts was fine — the endpoint the startup probe reads had grown to 8-10 seconds against a 10 second budget, and the check spending them was the one kind of check nothing in the fleet could name. /health now publishes its total and its slowest checks, on line two, for anyone who can curl it.
Icon: Timer
Order: -20260917
---

A portal replica that has just started is held out of rotation until its `/health` endpoint answers
once. That is deliberate: it keeps the previous version serving while the new one finishes building
its content, so a bad release stalls a rollout instead of reaching users.

It only works while `/health` can answer inside the time the probe allows it.

## What went wrong

On one instance `/health` had grown to **8–10 seconds**, measured on healthy, serving replicas. The
probe waits **five**. `/alive` and `/ready` on the same pod answered in 0.12 s, so the seconds were
entirely in the heavier checks that only `/health` runs.

A replica in that state can never finish starting, whatever its actual health — and a startup
failure is the one that does not recover on its own: the pod is killed when its startup budget runs
out, and the next attempt starts the same clock again. On the instance where this was measured the
budget is three hours, so the loop was slow and silent. The rollout sat at "1 of 2 replicas updated"
for hours with no outage and no explanation.

## Why nobody could say which check

The platform records how long each check took. For a check that is **healthy** it records it at a
level this fleet does not ship to the log store — and the `/health` body only printed the checks
that were *un*healthy. So an expensive check that was perfectly fine appeared nowhere at all: not in
the body, not in the logs. The slowest check on the endpoint was, by construction, the one nobody
could name.

## What is published now

The second line of `/health` is the timing:

```
Degraded
timing: 9412ms total over 17 check(s), slowest first — db_version 9180ms; required_modules 402ms; 15 more under 10ms
```

- the **total** — the number the probe's budget has to cover;
- every check that took **10 ms or more**, slowest first;
- a **count** of the ones that did not, so the line states its own denominator rather than quietly
  dropping what it did not print.

The first line is unchanged: it is still the single status word that every tool reads, so nothing
that consumed the body before consumes it differently now. The timing sits directly under it so that
a reader who only sees the beginning of a truncated body sees the timing first.

## What it deliberately does not do

It does not make `/health` faster and it does not give the probe a bigger timeout. A bigger timeout
moves the cliff rather than removing it, and the next instance to grow past the new number would
fail the same way, just as silently. This makes the cost **attributable** — the fix it points at is
the one worth making.
