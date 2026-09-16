---
Name: A failed create now actually cleans up after itself
Category: Fix
Description: When a create could not be completed, the platform was supposed to remove the half-created item again. On the live database it almost never did — it mistook its own row for somebody else's and left it behind, telling you to delete it by hand. The item is now removed as intended.
Icon: ArrowUndo
Order: -20260916
---

# A failed create now actually cleans up after itself

Creating something is **all or nothing**. If a required follow-up step fails — the one that makes
you the owner of a brand-new space, for instance — the platform removes the row it had just written
and tells you the create failed, so you can simply try again.

Leaving the row behind is the worst possible outcome, which is why that rule exists: a retry answers
*"this already exists"*, and nobody can delete it either, because the step that would have made
someone its owner is exactly the one that failed. The item becomes unusable and unremovable at the
same time.

## What was going wrong

Before removing anything, the clean-up re-reads the row and checks that it is still the one this
request wrote — a deliberate safeguard, so it can never delete an item somebody else created in the
meantime. It does that by comparing the creation timestamp.

The timestamp the platform put on the item was **more precise than the database column that stores
it**. Reading it back therefore returned a very slightly different value — a fraction of a
millionth of a second — and the safeguard read that difference exactly as it was meant to: *this is
not my row.* So it stood down, kept the half-created item, and reported that you should remove it
manually.

The result was that the safeguard almost always fired, on the platform's own rows, and the clean-up
it guards almost never ran.

## What you get now

- **The half-created item is removed**, as it was always meant to be, and the create can be retried
  straight away.
- **Timestamps are recorded at a precision the database actually keeps**, so an item you have just
  created reads back with the same creation date you were shown — not one that differs in the last
  digit.
- **When the clean-up genuinely cannot tell whose row it is**, it now says that, instead of claiming
  the row belongs to someone else. That distinction matters: one is a fact about another person's
  data, the other is an admission that nothing was checked.

## What did not change

The safeguard itself is untouched, and nothing is deleted more eagerly than before. An item whose
creation date really has changed is still left alone and reported; so is one the platform could not
check. The only thing that changed is that the platform can now recognise its own rows.
