---
Name: Deleting something already gone no longer reports an error
Category: Fix
Description: When two clean-ups removed the same item at once, the one that arrived second failed with a message about storage, even though the item was correctly gone. It now reports success, and an item that genuinely cannot be deleted says so up front instead of part-way through.
Icon: Delete
Order: -20260813
---

# Deleting something already gone no longer reports an error

Deleting an item happens in two steps: first we check it is there, then we remove it. Those two
steps were not asking about quite the same places, and they were not asking at the same moment —
so two things went wrong.

If something else removed the item in between — a bulk clean-up running in parallel, a second
server doing the same tidy-up — the removal step found nothing left and reported a failure that
talked about storage providers. The item was gone, which is exactly what was asked for, but the
operation was recorded as an error and the message pointed at something entirely unrelated. That
now reports success: once the item is gone, the job is done, whoever finished it.

The second problem was more serious. A few kinds of item are supplied by the product itself rather
than stored in your workspace, and those genuinely cannot be deleted. The check step could see
them, so a delete was allowed to start — and because a delete works from the innermost items
outwards, everything beneath such an item was already removed by the time the refusal came. You
were left with a partly emptied item you had been told could not be deleted at all. The refusal now
happens before anything is removed, and it names what is supplying the item so it is clear why.

Nothing new became deletable. The delete still only removes from the places it always could, and
the case that now succeeds is precisely the one where there was nothing left to remove.
