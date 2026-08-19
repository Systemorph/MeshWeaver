---
Name: Agents know what day it is
Category: Fix
Description: Every agent turn now carries today's date and time — in your own time zone — so "tomorrow", "next Tuesday" and "what day is today" are answered from the clock instead of the model's memory.
Icon: CalendarClock
Order: -20260818
---

# Agents know what day it is

Ask an assistant what day it is and it used to answer from whatever its training data suggested —
confidently, and often days off. Nothing in the pipeline ever told it the date, and it had no clock
to consult, so there was no way for it to be right.

Every turn now ships the current date and time as part of the agent's context. That matters well
beyond the question itself: a scheduling or mail assistant resolves "tomorrow", "next Tuesday
afternoon" or "clear my Friday" against an anchor date, and a wrong anchor books meetings on wrong
days without anything looking broken.

The date is shown in **your** time zone, taken from your profile, so "today" is your day rather than
UTC's — which are genuinely different days for anyone whose evening falls after midnight in London.
Alongside it the agent gets the same moment in UTC, in the machine-readable form it should use when
it writes a timestamp back into a calendar entry or a document.

Because the clock is stamped on each turn rather than baked into an assistant when it is first
loaded, a conversation you come back to next week gets next week's date — not the date it was
started on.
