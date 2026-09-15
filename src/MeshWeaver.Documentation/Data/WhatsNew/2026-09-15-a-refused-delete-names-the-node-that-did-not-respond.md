---
Name: A refused delete names the node that did not respond
Category: Fix
Description: When one item inside a folder does not respond, the delete is still refused — but it now names that one item, says it is a temporary failure rather than a permissions one, and stops waiting sooner instead of spending the whole timeout.
Icon: Delete
Order: -20260915
---

# A refused delete names the node that did not respond

Deleting a Space, a course or any folder asks **every** item inside it whether it can be deleted,
before anything is removed. That check is what makes the delete all-or-nothing: if one item cannot
be deleted, nothing is touched, and you never end up with half a folder gone.

**That is unchanged.** One unresponsive item still refuses the whole delete, and nothing is removed.
What changed is what you are told, and how long you wait to be told it.

Until now the whole round of questions shared one stopwatch. If a single item did not answer — its
part of the portal was still starting, or was stuck — the delete waited out the entire budget and
then refused with a count:

> 7 of 83 items did not answer within 30s, so the delete was refused.

Everything else had answered in a fraction of a second, and there was nothing in that sentence you
could act on.

**Each item is now asked on its own stopwatch.** The one that does not answer reports itself, by
name, inside the overall budget:

> Cannot delete 'Courses/AgenticBusiness/02-WishForATool/Exercise/ToolWishChat': that node did not
> answer within 25s — its part of the portal never replied, so the delete was refused and nothing
> was removed.

Two things follow, and both matter when you hit this:

- **It is reported as a temporary failure, not a permissions one.** A node that never answered
  decided nothing about your access — so the delete is worth retrying, and you are no longer sent to
  ask for permissions you already have.
- **Nothing was deleted.** This check runs before any removal, so the folder is exactly as it was.

The round of questions is also capped now, so deleting a very large folder no longer asks thousands
of nodes all at the same instant — which is what used to make a big delete heavy on the portal
rather than merely slow.
