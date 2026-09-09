---
Name: A slow deploy is no longer a reverted one
Category: Fix
Description: An upgrade that had landed correctly was rolled back because helm's fixed fifteen-minute wait expired while the new pods were still inside their startup gate — a gate the instance's own record budgets three hours for. helm now applies and nothing more; the rollout is observed separately, against the record's budget.
Icon: ShieldCheckmark
Order: -20260909
---

A reconcile of memex on 2026-09-09 applied its new configuration successfully. Fifteen minutes
later it was undone. An audit taken nine seconds before the rollback showed the intended state in
place — the full render, every object the record describes — and the portal answered normally
throughout. The pods were simply still starting, which is what a gated two-replica roll looks like
while a generation recompiles, and the instance's record allows three hours for it.

## What went wrong

The deploy step asked helm to apply the change *and* wait for it, with a fixed fifteen-minute
budget and an instruction to roll everything back if the wait ran out. helm has no way to read the
instance's record, so the budget could not reflect what this instance was allowed to take. To a
timer, slow and broken look the same — and the response it had been given, reverting, is the right
answer to only one of them.

Raising the number would not have fixed it. A wait inside helm cannot outlast the job running it,
which has its own one-hour ceiling, so no timeout can express a budget the record is permitted to
set higher. It would have moved the cliff, not removed it.

## What it does now

The deploy step applies the change and stops there, reporting which revision it produced. Watching
that revision reach a healthy state is a separate step with its own budget, taken from the record.

Nothing is left half-finished by dropping the rollback: a failed release can be upgraded straight
over, and the states that genuinely block the next attempt were already refused by name — before
helm runs, naming the release and leaving the decision to a person.

A deploy step that reports success now has to have seen what it is reporting. If the applied
revision cannot be read back, the step refuses rather than reporting a release nobody can observe.
