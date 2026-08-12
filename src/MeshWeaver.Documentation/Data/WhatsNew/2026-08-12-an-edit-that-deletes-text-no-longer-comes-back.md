---
Name: An edit that deletes text no longer comes back
Category: Fix
Description: Deleting a word, a paragraph or a list entry and then saving again could silently restore what you removed.
Icon: Sparkle
Order: -20260812
---

# An edit that deletes text no longer comes back

Every change to a page was being saved twice, by two paths that could arrive out of order. When the
second, older copy landed after a newer edit, the two were merged — and the merge kept whatever text
each side had, which quietly restored words, paragraphs or list entries you had just deleted. Editing
quickly made it more likely, and nothing on screen told you it had happened.

Each change is now written once, so a deletion stays deleted. The alarm that watches for genuinely
conflicting writes is unchanged — it was reporting the duplicate correctly all along, which had made
it noisy enough to ignore. Saving is also a little cheaper now that pages are stored once instead of
twice.
