---
Name: Your own content no longer quietly reads as missing
Category: Fix
Description: Views that list your own items — your copies, your history, your drafts — could show an empty "nothing here" to someone who did have content. The platform now spots the cause instead of hiding it.
Icon: Sparkle
Order: -20260813
---

# Your own content no longer quietly reads as missing

A page that lists things belonging to *you* — the copies you have made, your saved history, your
drafts — could come back completely empty even though the content was there the whole time.

The cause was a mix-up about who a search was being run for. When nothing told the platform whose
content to look for, it fell back to searching as a signed-out visitor. That is a safe thing to do —
it can never show you somebody else's data — but it produces zero results for anything private, and
zero results is indistinguishable from "you have nothing here". So the page reported absence, and
the real reason left no trace anywhere.

This turned out to be behind several unrelated-looking problems at once: a "continue from your own
copy" link that had never worked since the day it shipped, and a history table that told its owner
there were no records while six sat in storage.

Searches now say who they are for, explicitly. A search meant for a particular person carries that
person; a genuinely public listing — a catalog anyone may browse — says so too, and keeps working
exactly as before. If a search that should be about somebody ends up with nobody, the platform now
reports it by name instead of silently returning an empty page, and a search that would be
meaningless without knowing who is asking refuses to answer rather than guessing.

The change never widens what anyone can see: a person who was not allowed to read something still
cannot. What changes is that "you have nothing" and "we lost track of who you are" have stopped
looking the same.
