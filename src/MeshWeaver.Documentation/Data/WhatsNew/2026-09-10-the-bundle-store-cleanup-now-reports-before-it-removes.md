---
Name: The bundle-store cleanup now reports before it removes anything
Category: Fix
Description: The cleanup for the store CI publishes into shipped with removal switched ON, and the switch that would turn it off reached no installation. It is now off everywhere, it writes what it would have removed — including how much it looked at — and turning it on is a decision you make per installation, after reading that.
Icon: Checkmark
Order: -20260910
---

# The bundle-store cleanup now reports before it removes anything

Your installation keeps a store of the ready-built content that arrives with each platform build,
one directory per build. It grows by one directory every time a build is published, and nothing used
to remove any of them. On one installation that store quietly filled a 16 GB volume to **3 MB free**
— and a full volume of that kind does not report an error, it silently truncates whatever is written
next. What that looked like from the outside was content failing to load for no stated reason.

The cleanup written for that has been running since. What was wrong is which way it was pointed.

## What changes

**Removal is off, everywhere.** The cleanup shipped with removal switched **on** by default, and the
setting that would switch it off never reached an installation at all — it was accepted in a
configuration file and then dropped on the way, with nothing reporting the drop. So every
installation with this store mounted has been removing on defaults nobody had checked against that
installation. It is now off by default, and the setting genuinely arrives.

**It still runs, and it still writes down what it would have removed.** This is not the cleanup being
disabled — the pass happens on every boot and daily after that, and it records its findings the same
way. It simply does not remove anything until you say so.

**The record says how much it looked at, not just how much it would remove.** "Nothing to remove out
of 482 directories" and "nothing to remove because the store was not there" are the same number and
opposite facts. The report now leads with the count it examined, so a cleanup pointed at the wrong
place can no longer read as a cleanup that found nothing to do.

**Turning removal on is now a per-installation decision** you make after reading a real report from
that installation — the same rule the equivalent cleanup for compiled content already follows.

## What this does not change

**Nothing is removed by this change.** It removes strictly less than before: previously armed,
now reporting only.

**The store still needs pruning.** Turning removal on is the intended end state, and it is a
deliberate step rather than a default — one taken against a report from your own installation, not
against settings chosen elsewhere.

**One protection is known to be missing**, and is called out rather than papered over: a build that
is only referenced by an automated build check elsewhere is not yet visible to the cleanup as
"in use". That has to be closed before removal is switched on for the installation those checks pull
from.
