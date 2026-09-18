---
Name: A save refused because the page was restarting now says so
Category: Fix
Description: Saving a page could fail with a message that named no cause, when what had actually happened was that the page's own previous save was still restarting it in the background. The refusal now says that is what happened, that nothing was written, and that the save is worth repeating.
Icon: ArrowSync
Order: -20260916
---

# A save refused because the page was restarting now says so

Changing the TYPE of a page is the supported way to repair one whose type has gone missing. Doing it
restarts that page in the background — and if a second save arrived while the restart was still
finishing, it was refused with a message that named nothing at all.

The refusal was correct. The wording was not usable: it said only that something had gone wrong,
with no cause, so the obvious reading was "my change was rejected" — when in fact **nothing had been
written and simply doing it again would have worked**.

Now that case says what it is:

> The owner of '…' is recycling, so the write was refused on arrival and NOTHING was written — the
> address reactivates, so this is worth retrying.

Nothing else changes: a refusal that really is about your change — no permission, a validation
failure, a bad path — reads exactly as before, and a save that fails for any other reason is still
reported as the unclassified fault it is. Only the restart case, which is temporary and safe to
repeat, is named separately.

The underlying timing — a repair arriving while the previous one is still restarting the page — is
a separate matter and is still being worked on; this change makes it something you can act on rather
than something you have to guess at.
