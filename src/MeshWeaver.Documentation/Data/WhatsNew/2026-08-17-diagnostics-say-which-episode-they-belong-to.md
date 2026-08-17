---
Name: Diagnostics say which episode they belong to
Category: Fix
Description: A routing overload warning now identifies which episode it belongs to, so a repeat is recognisable as a new event rather than the same one continuing.
Icon: Sparkle
Order: -20260817
---

# Diagnostics say which episode they belong to

When the server reported that message routing was falling behind, it printed the
same headline numbers every time and left the "recovered after" line to say how
long it lasted. If that second line never arrived — which is exactly what happens
when the condition does not clear — there was no way to tell a repeat apart from
a single event still going.

Each report now carries its own identity, so two reports are visibly two events
or visibly one, and the recovery line is no longer the only thing that can answer
the question. The recovery line also ships at a level that reaches the log store,
instead of one that can be filtered away.

Nothing about how routing behaves has changed — only what the diagnostics let an
operator conclude.
