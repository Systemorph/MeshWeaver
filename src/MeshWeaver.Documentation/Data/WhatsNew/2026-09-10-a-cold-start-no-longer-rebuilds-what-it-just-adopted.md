---
Name: A cold start no longer rebuilds the content it has just adopted
Category: Fix
Description: A portal starting from cold takes ready-made builds out of the packages it ships, then works out what is left to compile. The second step was reading a catalogue that had not yet caught up with the first, so it rebuilt content whose finished build it had adopted seconds earlier — roughly 200 rebuilds instead of 20 on one measured start. It now consults what it has just done, and both counts say what they are counting.
Icon: Rocket
Order: -20260910
---

# A cold start no longer rebuilds the content it has just adopted

A portal does not compile its content if it does not have to. Ready-made builds travel with the
packages it ships, and the first thing a cold start does is adopt them. Only then does it work out
what is still missing and compile that.

On one measured start those two steps disagreed. Ten seconds apart, the same portal reported that it
had adopted **78** ready-made builds, and then that only **5** of its 209 types were ready — so it
compiled 197 of them. Nothing was wrong with the builds it had adopted. They were on disk, correct,
and complete.

## What went wrong

The second step decides what to compile by reading a catalogue of every type and what each one was
last built from. That catalogue is a summary kept slightly behind the real records — normally by
milliseconds, occasionally by much longer — and the first step had just rewritten those records for
78 types.

So the portal asked a question it had already answered, got the answer from before its own work, and
could not tell the difference. "This type's build is out of date" and "my copy of this type's
information is out of date" looked identical.

The cost was not only time. Content that never needed compiling ended up on the compile path, where
a separate fault could turn four healthy pages into apparent failures and hold a portal out of
service for three hours.

## What changes

The portal now remembers what it adopted during this start, and which version of each record it
wrote over. When the catalogue's copy of a type is demonstrably older than that, the portal trusts
what it did rather than what it is being told.

The reverse case is deliberate too: if the catalogue's copy is *newer* — because something else
changed the type after the adoption — the catalogue wins and the type is rebuilt. The portal only
overrides information it can prove it has already superseded.

## Both counts now say what they are counting

The two numbers were never comparable, and nothing said so. **78** counted ready-made builds placed
on disk. **5** counted types whose stored record and the disk agreed. Different things, different
units, printed as if they were the same measurement.

Each line now names its own population, and a start where the catalogue was behind says that out
loud in the same line that reports the result — so the next person reading a start log is not left
to guess which of two numbers to believe.

Separately, a type whose stored information could not be read at all used to vanish from these
counts entirely, with nothing to indicate it. Such a type is now recovered where possible and named
where it is not, so the totals describe a group the portal can point at.
