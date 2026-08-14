---
Name: Your last edit is no longer thrown away
Category: Fix
Description: Text typed into an inline editor could vanish if the page updated within half a second of the last keystroke — most visibly in course example cells, where an edit did not survive a reload.
Icon: Edit
Order: -20260814
---

# Your last edit is no longer thrown away

Inline editors save on their own, half a second after you stop typing. That pause is what keeps a
document from being written on every keystroke.

If the page updated during that pause, the pending text was **discarded**. Not delayed, not
retried — dropped, with nothing shown and nothing logged. And the pages most likely to update are
exactly the ones you are editing: a view refreshes whenever the thing it displays changes,
including the change your own previous save just made.

It showed up most clearly in course example cells. A learner gets their own editable copy of an
example, types into it, and comes back to find the original text — an editor that looks like yours
and quietly keeps nothing. The same pause guarded every inline markdown editor.

A pending edit is now **written out when the editor stops**, rather than abandoned with it. If the
text was already saved, nothing extra is written.
