---
Name: A compile failure now says how far the damage goes
Category: Fix
Description: When a compile fails for a reason that is not your code, the error on the node now also reports whether anything in that process can still be compiled at all — so you can tell "my type is broken" from "this server stopped being able to build anything a minute ago".
Icon: Wrench
Order: -20260907
---

# A compile failure now says how far the damage goes

Very occasionally a server stops being able to build **anything**. Not because of a mistake in
anyone's code — the fault is underneath the compiler — but the effect is that every type compiled
from that moment on fails, one after another, each with the same unhelpful message. Whoever was
editing at the time sees their own type break and quite reasonably starts looking at their own
change.

The error on the node has said *"this is not about your code"* for a while. What it could not say is
**how much** is not working.

## What changes

When a compile fails this way, the message now also reports the result of building **the simplest
possible thing** — an empty class, nothing in it — in the same server, at the same moment.

That single extra line separates two situations that used to look identical:

- **Even an empty class cannot be built.** The server genuinely cannot compile, and nothing you
  change will help until it is restarted.
- **An empty class builds fine.** The server is not dead. What has broken is narrower than "everything",
  and the message says so instead of overstating it.

Until now the broader claim — *"every later build in this process will fail the same way"* — was
being made without ever having been checked. It may well be right. It had simply never been measured,
and a message that overstates the damage sends people to restart a server when they did not have to,
or stops them retrying when a retry would have worked.

## Why it is phrased as a reading rather than a verdict

The line reports what was observed and refuses to guess past it. If the simple build fails somewhere
*different* from the original one, the message says that too, names both places, and draws no
conclusion — two different failures are two different problems until someone shows otherwise.

## What it does not do

**It does not fix the underlying fault**, which lives below the compiler and is not something a
change here can reach. This is about not misdescribing it: the failure is rare, it is expensive to
diagnose every time it appears, and each occurrence has historically cost someone a fresh
investigation of a change that was never the cause.

**It costs nothing on a healthy server.** The extra build runs only on a path that has already
failed in this specific way — never during a normal compile, and never on a normal compile error.
