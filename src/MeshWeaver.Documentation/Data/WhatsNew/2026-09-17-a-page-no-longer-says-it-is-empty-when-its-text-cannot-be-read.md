---
Name: A page no longer says it is empty when its text cannot be read
Category: Fix
Description: A markdown page whose stored text was saved in a shape the page cannot read showed "No content yet" and invited you to start editing — over a document that was still there. It now says what is stored, and does not invite the edit that would have replaced it.
Icon: Bug
Order: -20260917
---

# A page no longer says it is empty when its text cannot be read

A markdown page had one way of saying "there is nothing here", and it used it for two different
situations: the page really is empty, and the page has text that it cannot interpret. Both showed
*"No content yet. Use the menu to start editing."*

Only the first of those was true, and the second one cost you your text. The invitation is exactly
what a reader acts on: they start typing, and the first save replaces the document that was still
sitting in the store, unread.

The two are now told apart. A page that genuinely has no content still invites you to write one. A
page whose content cannot be read says so instead, names what is actually stored, and asks you not
to start editing until it has been repaired — so the text stays recoverable. The same situation is
now recorded in the portal's log, which it never was.

Writes that would put a page into that state are refused at the point they are made, so new pages
cannot be born this way.
