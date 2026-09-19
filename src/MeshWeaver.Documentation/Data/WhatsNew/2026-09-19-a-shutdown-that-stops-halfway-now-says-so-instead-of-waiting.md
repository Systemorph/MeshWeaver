---
Name: A shutdown that stops halfway now says so instead of waiting
Category: Fix
Description: When part of the portal shuts down, it walks through several phases. One handover between phases could be lost with nothing to notice it — leaving that part parked with an empty queue and nothing running, and everything above it waiting behind it until the process ended. It now reports the failure and releases the waiters. The diagnostic that describes such a stall also told readers to look for a cause that cannot exist at that stage.
Icon: PlugDisconnected
Order: -20260919
---

# A shutdown that stops halfway now says so instead of waiting

Tearing down one part of the portal is not one step. It quiesces (stops accepting new work and lets
replies it still owes arrive), then takes down anything it hosts, then runs its own cleanup. Each step
hands over to the next.

The hand-over from the first step to the second could be lost, and nothing was watching it. When that
happened, that part of the portal sat at the first step forever: nothing queued, nothing running,
nothing further written to the log — and everything above it in the tree waited behind it until an
outer deadline ended the process. Measured on 2026-09-19: one such part had been parked for **253
seconds**, against a step that is designed to take at most about forty.

The hand-over one step later had been protected against exactly this from the beginning, with a
comment explaining why. The earlier one had the same comment a few lines above it — and the statement
the comment was about sat just outside the protection. It now reports the failure and releases
everything waiting on it, rather than parking silently.

## The report was sending readers to look for something that cannot be there

The same stall produces a diagnostic, and that diagnostic named the wrong cause. It said a registered
cleanup was blocking — but cleanups only run in the **last** step, so at the first step there are no
cleanups to block, and there never could have been. The line nonetheless said so for a stall at any
step, because it keyed on *what* the pending work was and not on *where in the sequence* it had got
to.

That cost a full investigation: every cleanup registered by the affected component was examined — a
dozen of them — before the step named in the report's own text settled the matter.

There are now two diagnostics. The original keeps its wording for the last step, where it is true. The
new one covers the earlier steps and says what is actually the case: no cleanup has run, so the
finding is the **hand-over**, not a cleanup — together with the ceiling the elapsed time should be
read against and the two log lines that distinguish *"the step never finished"* from *"it finished and
the hand-over was lost"*.

## And the elapsed time could describe work nothing was doing

The same report names the pending work and how long it has been pending. Those two facts were recorded
at the start of the work and cleared at the end, roughly two hundred and sixty lines apart — so if
anything went wrong in between, the record was left set with nothing to clear it. The report then
showed work that had been "running" for four minutes while the same report's other fields said, if you
read them together, that nothing was running at all.

The marker is now set and cleared as a single unit, so there is no path that can set it without
arranging for the clear.

Nothing here was made quieter and no deadline was extended. A shutdown step that cannot finish is
still a defect in that step; these changes mean it is reported honestly, nothing waits forever on it,
and the report points at the thing that is actually wrong.
