---
Name: Deleted text stays deleted
Category: Fix
Description: Under load, a rapid sequence of edits could re-add text you had just deleted. It can't any more.
Icon: Sparkle
Order: -20260814
---

# Deleted text stays deleted

When edits arrived in quick succession, the portal could save the same change twice by two different
internal routes. If the second one was slightly behind, the two versions were merged — and a merge
with no common starting point keeps everything from both sides, so a word or a paragraph you had
just deleted quietly came back.

The two routes are now properly ordered rather than racing, so the duplicate save never happens and
there is nothing to merge. Deletions stick, however fast you type.
