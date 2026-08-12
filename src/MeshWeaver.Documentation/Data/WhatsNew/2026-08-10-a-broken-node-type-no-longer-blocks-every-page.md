---
Name: A node type that fails to compile stops blocking everything built on it
Category: Fix
Description: When one node type had a code error, every page and every plugin built on it went quiet for a minute and then showed the wrong explanation. Now they say what is actually wrong, immediately.
Icon: Wrench
Order: -20260810
---

# A node type that fails to compile stops blocking everything built on it

When a node type's code fails to compile, the portal deliberately stops rebuilding
it until someone fixes the source. That part was working. What went wrong is what
happened to everything *built on* that type in the meantime.

Opening one of those pages went quiet for a full minute — and then showed a message
saying the type's build "had not settled yet" and would sort itself out. Neither
half was true. The build had already finished, it had failed, and the portal had
already written down why. Nothing was ever going to change on its own.

## Why the wait was worse than slow

A minute of waiting is not just an annoyance here, because other things gave up
sooner. Anything that wanted to *write* to one of those pages waited only thirty
seconds. So the write always ran out of time first, every single time, and reported
that the page had never loaded — which pointed at the wrong problem entirely.

That is how a single broken type took out the plugin install on start-up. Every
plugin's home page is built on the same type. When that type broke, each plugin in
turn failed to install, on every restart, with a message about a page that would
not load — while the real cause, a compile error, sat recorded and un-mentioned.

## What happens now

The portal now checks what it already knows before it starts waiting. If a type has
been set aside after a failed compile, there is nothing to wait for, so the page
comes straight back with the compile error itself — the actual line that failed, and
the honest advice that the source needs fixing rather than "this will heal itself".

Waiting that can still succeed is untouched: a type that is genuinely mid-compile is
still waited for, however long it takes.

## Also fixed: plugins reinstalled themselves on every restart

The record of which plugins the installer had already delivered was never being
saved, because its entry type had not been registered. Each restart therefore
re-ran the whole first-time install instead of only repairing what was missing. The
record now saves, so a restart does the small amount of work it was meant to do.
