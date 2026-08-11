---
Name: One person's view of a page can no longer be shown to another
Category: Fix
Description: Rendered views were cached without recording who they were built for, so whoever opened a page first could fix what everyone after them saw — either showing them content they had no right to, or an empty view where they should have seen everything.
Icon: Sparkle
Order: -20260811
---

# One person's view of a page can no longer be shown to another

Almost everything the portal draws — a page, a card, an embedded area inside a document, a table
that updates as data changes — is fed by a live subscription to the node that owns it. Opening that
subscription is the moment access is decided: the owner is told who is asking, checks what that
person is allowed to read, and from then on the subscription carries exactly that person's view of
the data for as long as it stays open.

Those subscriptions are reused, which is what makes a busy page cheap to draw. The reuse was
recorded under what was being read and where it lived, but not under **who it had been opened for**.
So when two people read the same thing through the same connection, the second one silently
inherited the first one's subscription — and with it, the first one's permissions.

That went wrong in both directions, decided by nothing more than who happened to arrive first. If
someone with access opened the view first, a person without access who followed was handed it and
saw content the owner would have refused them. If the person without access arrived first, their
refusal was the thing left behind, and the next reader — fully entitled to the content — got the
refused view and an area that rendered empty. The same page, the same permissions, two different
outcomes depending on the order people opened it, which is exactly why this could sit unnoticed:
most of the time everyone looking at a page is entitled to it, and the reuse is invisible and
correct.

Reuse now records who a subscription was opened for, and hands it back only to that same person.
Everyone else gets their own, checked from scratch against their own permissions. The identity is
taken from the very same place that tells the owner who is asking, so the two can never disagree
about whose view a subscription holds.

Nothing changes for a page whose readers all have the same access, which is the overwhelming
majority — the reuse still happens, so pages draw exactly as quickly as before. What changes is
that being first no longer decides what anyone else is allowed to see.
