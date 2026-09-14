---
Name: Imports and copies no longer run on the part of the portal that routes traffic
Category: Fix
Description: Bulk imports, node copies and moves could end up executing on the one component whose job is to pass every other message along. A big import therefore competed with everyone else's clicks. They now run on the portal's dedicated work queue, and a new check keeps the next one from drifting back.
Icon: Checkmark
Order: -20260914
---

# Imports and copies no longer run on the part of the portal that routes traffic

Inside a portal, one component acts as the **switchboard**: every message anyone sends passes
through it on the way to whoever should handle it. It is deliberately kept free — a switchboard that
stops to do a job of its own is a switchboard nobody's call gets through.

Some of the portal's own background work was, in effect, calling from the switchboard. A bulk
import, a node copy, a move — each was written to run wherever its caller happened to be standing,
and several of the callers stand on the switchboard: the content importers, the parts that run
before anyone has logged in, the plumbing that sets up a guest. When one of those did a large piece
of work, it queued up on the very component that everyone else's page was waiting on.

The symptom was never "an import failed". It was **the portal going quiet while one was running**,
and, in the worst measured case, a burst of node writes holding up ordinary page subscriptions long
enough for the portal to be restarted underneath its users.

## What changes

**Bulk imports, copies and moves now run on the portal's dedicated work queue**, which exists for
exactly this and has always been where the day-to-day writes go. Nothing about what they do changes:
the same permissions are checked, by the same rules, as the same person.

**For everything that was already off the switchboard, this changes nothing at all** — which is the
useful property. The hop is written so that it is a no-op unless the caller really was standing on
the switchboard, so adopting it is never a behaviour change and never a judgement call.

**And a new check keeps it that way.** The same finding had been reported four separate times over
the past month, each time from a different symptom, because nothing stopped it being reintroduced.
The check names the file and the one-line fix at the moment the code is written, instead of a
production log naming it weeks later.

## What you will notice

- **The portal stays responsive while a large import or copy is running.**
- **A guest session, and the first page after a restart, stop competing with routing** for the few
  writes they need to do.

## For the record

One piece of dead code was removed in the same pass: a second, unused copy of the node create/delete
path that had never been wired to anything and did not take the hop. It could not misbehave while
nothing called it, and it is the shape that would have been reintroduced by the first person who
did.
