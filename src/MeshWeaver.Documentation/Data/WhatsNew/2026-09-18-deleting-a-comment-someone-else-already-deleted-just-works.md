---
nodeType: WhatsNew
Name: Deleting something that is already gone just works
Category: Fix
Description: "Deleting a comment that had already been deleted — by a colleague on the same document, or by your own double-click — failed with \"Node not found\". Deleting something that is already gone is now simply done."
Icon: Delete
Order: -20260918
---

Deleting a comment that somebody had **already** deleted failed, with an error naming a node that
could not be found.

The two people most likely to see it were the two most likely to be working together. Open a
document alongside a colleague, they delete a comment, and for a moment your page still shows its
marker — the list refreshes a beat later. Click Delete in that beat and you were told the comment
could not be removed **because it had already been removed**. A quick double-click of the delete
button did the same thing.

**Deleting something that is already gone is now simply done.** The outcome you asked for — that
comment, not there any more — is the outcome you get, with no error and nothing to dismiss. The
marker disappears as the list catches up, exactly as it would have done anyway.

This is not a matter of hiding the message. Every other reason a delete can fail still says so, as
plainly as before: something you are not allowed to remove, something another rule is protecting,
something that did not finish. And a deletion that found nothing to remove still records that it
removed nothing — so a clean-up that was expected to clear a whole folder, or a mistyped path, is
still perfectly visible to anyone who goes looking.

The change is in the platform rather than in one screen, so it holds anywhere a delete can arrive a
moment late: the comment you and a colleague both reached for, a page open in two tabs, a menu
clicked twice, a script run a second time.
