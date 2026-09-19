---
Name: A diagnostic stops reporting something nobody can change
Category: Fix
Description: An internal warning flagged one routine cleanup message as a routing problem. It was not one — and there was no way to act on it, because the only change that would have silenced it is the one thing the system must not do. The warning is gone and the reasoning is written down.
Icon: DocumentBulletListOff
Order: -20260916
---

# A diagnostic stops reporting something nobody can change

The platform watches for a specific kind of internal mistake: the component that **routes** messages
between parts of the system doing actual work itself, instead of only passing messages along. That
is worth catching — when the router gets busy, everything behind it waits.

One of the things it flagged was not a mistake.

## What was being reported

When a page stops watching a piece of live data, it sends a short "I'm done" message so the other
side can release what it was keeping for that page. Whoever sends that message matters: the other
side matched it against the "start watching" message from earlier, and it uses the **sender** to know
which watcher is finishing. The two messages come from the same place on purpose.

For pages whose data lives closest to the router, that sender is the router — correctly. The warning
read it as the router doing work.

## Why it could not simply be fixed

The usual fix is to send such a message from somewhere else, which for everything else in this class
is a change with no side effects. Not here:

- send only the "I'm done" message from elsewhere, and the other side is left holding something
  started by one party and finished by another — it no longer matches them up;
- send both from elsewhere, and the "start watching" message changes identity, which is what the
  permission check on the other side reads. That path has a known failure: the check refuses, the
  page retries, and the two loop.

So the report asked for a change that must not be made. A warning nobody may act on is worse than no
warning, because people learn to ignore the whole channel — and then miss the real ones.

## What changed

The message now carries a note, in its own definition, saying that its sender is meaningful rather
than incidental, and the detector skips those. Nothing else is skipped: an ordinary message in the
same position is still reported, and the other half of the pair deliberately still is too. The
reasoning is recorded in the architecture notes rather than in a list of exceptions, so the next
person reads the argument and not just the outcome.

## What did not change

No message moved, no timing changed, and nothing about how pages watch or stop watching data is
different. This is about what gets written to the log, and about one decision now being written down.
