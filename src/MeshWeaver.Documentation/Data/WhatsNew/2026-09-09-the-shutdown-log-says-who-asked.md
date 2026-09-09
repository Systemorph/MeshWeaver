---
Name: The shutdown log says who asked
Category: Feature
Description: When a hub tears down, the log line that opens the teardown now names what asked for it — another component, the hub itself recycling, or nothing at all. Until now it named only the hub going down, which left the most consequential question about a stalled shutdown unanswerable from the log.
Icon: PersonQuestionMark
Order: -20260909
---

When something inside the platform shuts down, it opens with a line naming itself and the work still
outstanding. What it never said is **what asked it to shut down** — and in the one incident where
that mattered most, there was no way to find out afterwards.

A package was installing 145 files. Partway through, the thing that owned those files was torn down
underneath the install. Everything it was responsible for went with it, the install sat waiting for
answers that could no longer come, and ten minutes later it gave up. Reading the logs afterwards,
the teardown was plainly visible and its cause was not: nothing recorded who had asked. The
investigation's own note was *"who disposed the root is not in the log at this level"*, and the
leading explanation stayed an explanation.

## What the line says now

Three answers, because there are three genuinely different situations:

- **another component asked** — it is named, so the thing that ordered the shutdown can be found;
- **it asked itself** — the routine self-refresh that happens when something is rebuilt in place,
  said in those words rather than printed as the component naming itself, which reads like a fault
  and is not one;
- **nothing asked over the wire** — an ordinary shutdown from the host, or from whatever owns it.

That last one carries as much information as the other two. It rules out an entire class of cause in
one reading, which is exactly the deduction that could not be made before.

## What it does not do

It does not stop a shutdown from landing in the middle of an install — that is a separate piece of
work, still open. This makes the next occurrence explicable instead of a dead end.
