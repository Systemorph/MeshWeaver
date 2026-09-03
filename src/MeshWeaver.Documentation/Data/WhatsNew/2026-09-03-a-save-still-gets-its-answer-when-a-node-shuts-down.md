---
Name: A save still gets its answer when a node shuts down
Category: Fix
Description: When the part of the mesh that owns a node was shutting down mid-save, the answer it had already prepared could be discarded — and the save sat unanswered for half a minute before reporting itself unconfirmed.
Icon: SaveEdit
Order: -20260903
---

# A save still gets its answer when a node shuts down

Every save is confirmed by the part of the mesh that owns the thing being saved. When that owner is
shutting down at the moment a save arrives, it does not simply go quiet: it prepares a specific
answer — *"this did not apply; it is safe to retry"* — which is what lets the save be re-applied
against the fresh owner and land, rather than being lost.

That answer could be thrown away before anyone saw it. A shutdown runs a list of clean-up steps in
order, and the list stopped at the first step that failed. One step failing therefore silently
cancelled every step behind it — including the one that hands over the prepared answer. The save
then heard nothing at all, waited out its full confirmation window of about half a minute, and
reported itself **unconfirmed**: not an error you could act on, just an edit whose fate you were
told nobody could establish.

Two things changed:

- **A failing clean-up step no longer cancels the rest.** Each step now runs on its own, the
  failure is recorded naming the step that raised it, and everything behind it still runs. Nothing
  is hidden — a step that fails is a bug in that step, and it is now reported as one instead of as
  a shutdown that quietly stopped half-way.
- **The step that hands over the answer no longer depends on anything that can be gone by then.**
  It now takes hold of what it needs while the owner is still fully alive, so it cannot be the step
  that fails.

The visible effect is that a save caught by a shutdown gets its answer immediately and is retried
and applied, instead of hanging for thirty seconds and coming back unconfirmed. Saves that were not
caught by a shutdown are unaffected — this changes nothing on the ordinary path.
