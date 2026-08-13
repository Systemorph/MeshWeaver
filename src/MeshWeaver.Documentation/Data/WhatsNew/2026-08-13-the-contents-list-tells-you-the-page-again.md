---
Name: The contents list tells you the page again
Category: Fix
Description: Exported PDFs print the page number beside every table-of-contents entry once more — and each number is checked against the finished document before it is published.
Icon: Sparkle
Order: -20260813
---

# The contents list tells you the page again

When PDF export moved to the browser, the table of contents kept its entries and its links but
lost the `..... 12` column. **The page numbers are back.**

Every entry now shows the page its section starts on, right-aligned at the margin the way a
printed contents list has always looked. Clicking the entry still jumps there — the number is
in addition to the link, not instead of it.

## Why you can trust the number

Browsers genuinely cannot work out, while laying a page out, which page something will land
on. So the export does not guess. It prints the document once, reads out of that finished PDF
which page each section actually begins on, and prints it again with those numbers filled in —
then **reads the finished document back a second time and checks that every number is still
right**. Only a document that passes that check is handed to you.

If anything about it does not add up, the export prints the contents list without numbers
rather than with numbers it cannot stand behind. A contents list that quietly points at the
wrong page would be worse than one that says nothing.

The number column is reserved on both prints, so filling in the digits cannot push the list
onto another page and move everything after it — which is exactly how this sort of feature
usually goes wrong.

Documents without a table of contents are unaffected and are still printed once.
