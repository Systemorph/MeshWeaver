---
Name: A delivery alarm no longer fires at a delivery in progress
Category: Fix
Description: The hourly check that watches for a stalled release was reporting a stall whenever a release happened to be halfway through. It now tells the two apart, and the real stall is still reported loudly.
Icon: Alert
Order: -20260907
---

# A delivery alarm no longer fires at a delivery in progress

Every hour, a check asks one question about the platform: *does the newest code have a complete set
of deployable images?* If it does not, nobody's installation can move forward, and that is worth
waking someone for. So when the answer is no, the check goes red and says delivery is stuck.

Building that set takes about twenty minutes and sealing it about eighty. For a stretch of every
such build, the honest answer to the question is "no" — not because anything is wrong, but because
the set is being assembled right then. The hourly check had no way to see that. When a build
straddled the top of an hour, it read a half-finished set as a broken one and reported a stall.

On 7 September it did this twice inside forty minutes, on two commits that both completed normally a
short while later. Nothing was stuck either time.

## Why a red that means nothing costs something

The check is worth having: an installation that quietly stays on yesterday's build is invisible
otherwise, and this is what makes it visible. That value comes entirely from the red being
believable. A red that has to be reasoned away sits in the same list as the reds that must not be —
and it arrived on an evening when people were reading exactly this signal to decide whether to
ship.

## What changed

The obvious repair would have been to stay quiet while a build is running. That trades a false
alarm for a blind spot: a check that says nothing looks precisely like a check that found nothing,
and the minutes while a build is running are the minutes this check exists to watch.

So instead of declining to answer, it now answers a third way. Before reporting a stall it looks at
whether a build is actually under way on that exact code, and reports what it finds:

- **A build is running or waiting to start** — the set is incomplete because it is being created.
  Reported as a normal outcome, naming the build and when it started.
- **No build is running** — nobody is completing the set. Reported as a stall, exactly as before,
  and now saying that it checked.
- **The question could not be answered** — reported as a stall. An unanswered question is never
  treated as reassurance.

Nothing waits, retries or is given more time to settle; the quiet period lasts precisely as long as
a build is visibly running, and a build that hangs is ended by the platform's own limits, after
which the next hourly check reports the stall.

The behaviour of both outcomes is now exercised by the test suite on every change: that a genuinely
incomplete set with no build running is still reported as a stall, and that the two September runs'
conditions no longer are.
