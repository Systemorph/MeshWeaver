---
Name: Shutdown now names the work it could not wait for
Category: Fix
Description: When a portal shuts down it cancels its background I/O and waits for it to stop. If something refuses to stop, shutdown says so — but for three weeks that report named nothing at all, so the one instruction it gave ("fix the work that would not stop") could not be acted on. It now names which kind of work it was and where in the code it was running.
Icon: DocumentBulletListClock
Order: -20260919
---

# Shutdown now names the work it could not wait for

A portal does not stop the instant it is asked to. Work it has already accepted — a file being
written, a query still streaming, a compile in progress — is given a chance to finish, and then, if
it has not finished, it is cancelled and shutdown waits for it to unwind. That wait is deliberately
bounded at thirty seconds, because a portal that never finishes shutting down is worse than one that
finishes loudly.

When the bound expires, shutdown writes an error. It has to: the next thing that happens is that the
process releases the memory that work was still running in, and if a later crash is investigated,
this line is the only clue that will exist.

## What was wrong

The line said this, and only this:

> pooled I/O did not finish within 00:00:30 — the silo is releasing over live work. A leaf ignored
> its cancellation token; fix the leaf, do not widen the budget.

It is good advice with nothing to apply it to. *Which* work? Running *where*? Sixteen portal
restarts over three weeks produced sixteen copies of that sentence, character for character, and
nobody could act on any of them.

The frustrating part is that the machinery to name the culprit had already been written, for exactly
this complaint. It was attached to the wrong moment. Each kind of background work reports what it
left behind **as it finishes unwinding** — which is perfect for work that stops a little late, and
useless for work that never stops at all. The only case the report exists for was the one case that
could not reach it. Meanwhile shutdown had already tidied away its own list of what it was waiting
for, so by the time the clock ran out there was nothing left to look at either.

## What happens now

When the bound expires, shutdown looks directly at what it was waiting for, and says so:

> … do not widen the budget. Did NOT report: Query=1 [MeshQuery.MergeProviderObservables].

That names the resource class the work belonged to, how many operations were still running, and the
method they were started from — enough to go to one place in the code rather than to none.

Two smaller things came with it:

- **If the clock runs out and yet nothing is outstanding**, that is a *different* fault — in the
  waiting itself, not in any piece of work — and the line now says that instead of falling silent
  and reading like a clean shutdown.
- **The thirty-second bound is unchanged**, and is now expressed as a setting rather than a constant
  for one reason: at thirty seconds no automated test could ever observe this report, which is
  precisely how its wording went three weeks without being read by anybody. A test now parks work
  that genuinely refuses to stop and checks that the line names it.

Nothing was made quieter, nothing was reclassified, and no bound was widened — work that will not
stop when asked is still a defect in that work, and the point of the change is that it can now be
found.
