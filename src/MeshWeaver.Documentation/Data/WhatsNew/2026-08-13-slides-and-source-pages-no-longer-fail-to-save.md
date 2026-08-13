---
Name: Pages with an invisible control character now save
Category: Fix
Description: A hidden character in a page's text no longer makes it fail to save, and when something genuinely cannot be stored the error now names the page and the field.
Icon: Sparkle
Order: -20260813
---

# Pages with an invisible control character now save

Some pages carried an invisible control character in their text. The database cannot store that
character at all, so saving those pages failed — and the error said only that something was wrong,
without naming the page, the field, or the character, which made it almost impossible to track down.

The two places that produced the character have been fixed, so the pages save normally again. If
content ever does contain a character that genuinely cannot be stored, the save now stops before
anything is written and reports exactly which page and which field to correct, instead of failing
part-way through a batch and leaving you to guess. Nothing is silently trimmed or rewritten to make
it fit.
